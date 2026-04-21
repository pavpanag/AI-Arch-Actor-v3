using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using AaltoSystemV3;

public sealed class MultiSpeakerOscSpeechReceiver : MonoBehaviour
{
    [Serializable]
    private sealed class PendingSpeech
    {
        public string speakerId;
        public string text;
        public int channel;
    }

    [Header("Wiring")]
    public AaltoMultiSpeakerDirectedRoomPerformerController MultiSpeakerPerformer;

    [Header("OSC")]
    [Tooltip("Reference to the OSC MonoBehaviour that owns the UDP receiver thread.")]
    public MonoBehaviour osc;
    [Tooltip("Expected OSC address. Match sender address.")]
    public string ExpectedAddress = "/speech";

    [Header("Channel Filter")]
    public bool AcceptAnyChannel = true;
    public List<int> AllowedChannels = new List<int> { 1, 2, 3, 4 };

    [Header("Channel -> Speaker Mapping")]
    public string Channel1SpeakerId = "actor_1";
    public string Channel2SpeakerId = "actor_2";
    public string Channel3SpeakerId = "actor_3";
    public string Channel4SpeakerId = "actor_4";

    [Header("Debug")]
    [TextArea(2, 6)]
    public string LastStatus;
    [TextArea(1, 3)]
    public string LastReceivedSpeaker;
    [TextArea(1, 4)]
    public string LastReceivedText;
    public int TotalReceived;
    public int TotalDispatched;
    public int DroppedEmptyTranscript;
    public int DroppedByChannelFilter;
    public int DroppedNoPerformer;
    public int Speaker1Count;
    public int Speaker2Count;
    public int Speaker3Count;
    public int Speaker4Count;
    public int UnknownSpeakerCount;

    private readonly Queue<PendingSpeech> _pending = new Queue<PendingSpeech>();
    private readonly object _lock = new object();
    private bool _registered;
    private MethodInfo _setAddressHandlerMethod;
    private MethodInfo _removeAddressHandlerMethod;
    private Delegate _addressHandlerDelegate;

    private void Awake()
    {
        ResolveTargetIfNeeded();
    }

    private void OnEnable()
    {
        ResolveTargetIfNeeded();
        StartReceiver();
    }

    private void OnDisable()
    {
        StopReceiver();
    }

    private void OnDestroy()
    {
        StopReceiver();
    }

    private void Update()
    {
        lock (_lock)
        {
            while (_pending.Count > 0)
            {
                var item = _pending.Dequeue();
                ResolveTargetIfNeeded();

                if (MultiSpeakerPerformer == null)
                {
                    DroppedNoPerformer++;
                    EmitReceiverEvent(
                        "receiver.dispatch_dropped",
                        "{\"speaker_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(item.speakerId ?? string.Empty) + "\"," +
                        "\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(item.text ?? string.Empty) + "\"," +
                        "\"reason\":\"no_performer\"}");
                    SetStatus("Dropped transcript: MultiSpeakerPerformer is not assigned.");
                    continue;
                }

                MultiSpeakerPerformer.ReceiveExternalSpeech(item.speakerId, item.text);
                TotalDispatched++;
                EmitReceiverEvent(
                    "receiver.dispatched",
                    "{\"speaker_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(item.speakerId ?? string.Empty) + "\"," +
                    "\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(item.text ?? string.Empty) + "\"," +
                    "\"channel\":" + item.channel + "}");
                SetStatus("Dispatched speech from " + item.speakerId + ": " + item.text);
            }
        }
    }

    public void StartReceiver()
    {
        if (_registered)
            return;

        if (osc == null)
        {
            SetStatus("OSC reference is null. Assign OSC component.");
            enabled = false;
            return;
        }

        if (string.IsNullOrWhiteSpace(ExpectedAddress))
        {
            SetStatus("ExpectedAddress is empty.");
            enabled = false;
            return;
        }

        try
        {
            if (!TryPrepareReflectionBinding(out var bindError))
            {
                SetStatus("Failed to prepare OSC binding: " + bindError);
                return;
            }

            _setAddressHandlerMethod.Invoke(osc, new object[] { ExpectedAddress, _addressHandlerDelegate });
            _registered = true;
            SetStatus("Registered OSC handler for " + ExpectedAddress + ".");
        }
        catch (Exception ex)
        {
            SetStatus("Failed to register handler: " + ex.Message);
        }
    }

    public void StopReceiver()
    {
        if (!_registered)
            return;

        try
        {
            if (osc != null && !string.IsNullOrWhiteSpace(ExpectedAddress) &&
                _removeAddressHandlerMethod != null && _addressHandlerDelegate != null)
            {
                _removeAddressHandlerMethod.Invoke(osc, new object[] { ExpectedAddress, _addressHandlerDelegate });
            }
        }
        catch (Exception ex)
        {
            SetStatus("Exception while removing handler: " + ex.Message);
        }

        _registered = false;
    }

    private void OnReceiveSpeechDynamic(object message)
    {
        var values = TryGetMessageValues(message);
        if (values == null || values.Count == 0)
            return;

        // Payload options:
        // 1) /speech <int channel> <string text>
        // 2) /speech <string speakerId> <string text>
        // 3) /speech <string text>
        int channel = 0;
        string speakerId;
        string transcript;

        if (values.Count >= 2 && (values[0] is int || values[0] is float))
        {
            channel = ToInt(values[0]);
            transcript = values[1] as string ?? values[1]?.ToString();
            speakerId = ResolveSpeakerIdForChannel(channel);
        }
        else if (values.Count >= 2 && values[0] is string)
        {
            speakerId = (values[0] as string ?? string.Empty).Trim();
            transcript = values[1] as string ?? values[1]?.ToString();
        }
        else
        {
            speakerId = "unknown";
            transcript = values[0] as string ?? values[0]?.ToString();
        }

        transcript = (transcript ?? string.Empty).Trim();
        speakerId = string.IsNullOrWhiteSpace(speakerId) ? "unknown" : speakerId.Trim();

        if (string.IsNullOrWhiteSpace(transcript))
        {
            DroppedEmptyTranscript++;
            EmitReceiverEvent("receiver.ingress_dropped", "{\"reason\":\"empty_transcript\"}");
            return;
        }

        if (!AcceptAnyChannel && (AllowedChannels == null || !AllowedChannels.Contains(channel)))
        {
            DroppedByChannelFilter++;
            EmitReceiverEvent(
                "receiver.ingress_dropped",
                "{\"reason\":\"channel_filter\",\"channel\":" + channel + ",\"speaker_id\":\"" +
                AaltoLaunchSessionLogger.EscapeJson(speakerId) + "\",\"text\":\"" +
                AaltoLaunchSessionLogger.EscapeJson(transcript) + "\"}");
            return;
        }

        lock (_lock)
        {
            TotalReceived++;
            LastReceivedSpeaker = speakerId;
            LastReceivedText = transcript;
            IncrementSpeakerCounter(speakerId);
            EmitReceiverEvent(
                "receiver.ingress_received",
                "{\"speaker_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(speakerId) + "\"," +
                "\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(transcript) + "\"," +
                "\"channel\":" + channel + "}");
            _pending.Enqueue(new PendingSpeech
            {
                speakerId = speakerId,
                text = transcript,
                channel = channel
            });
        }
    }

    [ContextMenu("Multi Speaker Receiver/Clear Debug Counters")]
    public void ClearDebugCounters()
    {
        TotalReceived = 0;
        TotalDispatched = 0;
        DroppedEmptyTranscript = 0;
        DroppedByChannelFilter = 0;
        DroppedNoPerformer = 0;
        Speaker1Count = 0;
        Speaker2Count = 0;
        Speaker3Count = 0;
        Speaker4Count = 0;
        UnknownSpeakerCount = 0;
        LastReceivedSpeaker = string.Empty;
        LastReceivedText = string.Empty;
        SetStatus("Debug counters cleared.");
    }

    private string ResolveSpeakerIdForChannel(int channel)
    {
        return channel switch
        {
            1 => string.IsNullOrWhiteSpace(Channel1SpeakerId) ? "actor_1" : Channel1SpeakerId.Trim(),
            2 => string.IsNullOrWhiteSpace(Channel2SpeakerId) ? "actor_2" : Channel2SpeakerId.Trim(),
            3 => string.IsNullOrWhiteSpace(Channel3SpeakerId) ? "actor_3" : Channel3SpeakerId.Trim(),
            4 => string.IsNullOrWhiteSpace(Channel4SpeakerId) ? "actor_4" : Channel4SpeakerId.Trim(),
            _ => "channel_" + channel
        };
    }

    private void ResolveTargetIfNeeded()
    {
        if (MultiSpeakerPerformer == null)
            MultiSpeakerPerformer = FindObjectOfType<AaltoMultiSpeakerDirectedRoomPerformerController>();
    }

    private bool TryPrepareReflectionBinding(out string error)
    {
        error = null;

        if (osc == null)
        {
            error = "OSC MonoBehaviour is null.";
            return false;
        }

        var oscType = osc.GetType();
        _setAddressHandlerMethod = oscType.GetMethod("SetAddressHandler", BindingFlags.Public | BindingFlags.Instance);
        _removeAddressHandlerMethod = oscType.GetMethod("RemoveAddressHandler", BindingFlags.Public | BindingFlags.Instance);

        if (_setAddressHandlerMethod == null || _removeAddressHandlerMethod == null)
        {
            error = "OSC component does not expose SetAddressHandler/RemoveAddressHandler.";
            return false;
        }

        var parameters = _setAddressHandlerMethod.GetParameters();
        if (parameters.Length != 2)
        {
            error = "SetAddressHandler signature is unexpected.";
            return false;
        }

        var handlerType = parameters[1].ParameterType;
        var callbackMethod = GetType().GetMethod(nameof(OnReceiveSpeechDynamic), BindingFlags.NonPublic | BindingFlags.Instance);
        if (callbackMethod == null)
        {
            error = "Receiver callback method not found.";
            return false;
        }

        try
        {
            _addressHandlerDelegate = Delegate.CreateDelegate(handlerType, this, callbackMethod);
        }
        catch (Exception ex)
        {
            error = "Failed to create OSC handler delegate: " + ex.Message;
            return false;
        }

        return true;
    }

    private static IList TryGetMessageValues(object message)
    {
        if (message == null)
            return null;

        var type = message.GetType();
        var valuesField = type.GetField("values", BindingFlags.Public | BindingFlags.Instance);
        if (valuesField == null)
            return null;

        return valuesField.GetValue(message) as IList;
    }

    private static int ToInt(object value)
    {
        if (value is int i)
            return i;
        if (value is float f)
            return (int)f;

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private void IncrementSpeakerCounter(string speakerId)
    {
        var id = (speakerId ?? string.Empty).Trim();
        if (string.Equals(id, (Channel1SpeakerId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Speaker1Count++;
            return;
        }

        if (string.Equals(id, (Channel2SpeakerId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Speaker2Count++;
            return;
        }

        if (string.Equals(id, (Channel3SpeakerId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Speaker3Count++;
            return;
        }

        if (string.Equals(id, (Channel4SpeakerId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Speaker4Count++;
            return;
        }

        UnknownSpeakerCount++;
    }

    private void SetStatus(string status)
    {
        LastStatus = status ?? string.Empty;
        Debug.Log("[MultiSpeakerOscSpeechReceiver] " + LastStatus);
        EmitReceiverEvent("receiver.status", "{\"status\":\"" + AaltoLaunchSessionLogger.EscapeJson(LastStatus) + "\"}");
    }

    private static void EmitReceiverEvent(string eventType, string payloadJson)
    {
        AaltoLaunchSessionLogger.EmitEvent("MultiSpeakerOscSpeechReceiver", eventType, payloadJson);
    }
}
