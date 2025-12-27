using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class OscSpeechReceiver : MonoBehaviour
{
    [Header("Wiring")]
    public BlueprintChatController Chat;

    [Header("OSC (via OSC.cs)")]
    [Tooltip("Reference to the OSC MonoBehaviour that owns the UDP receiver thread.")]
    public OSC osc;
    [Tooltip("Expected OSC address (e.g. /speech). Leave empty to accept any address (not recommended).")]
    public string ExpectedAddress = "/speech";

    [Header("Channel Filter")]
    [Tooltip("If true, accept any channel. If false, only accept channels listed in AllowedChannels.")]
    public bool AcceptAnyChannel = true;
    public List<int> AllowedChannels = new List<int> { 1 };

    private readonly Queue<string> _pending = new Queue<string>();
    private readonly object _lock = new object();

    private bool _registered;

    private void OnEnable()
    {
        Debug.Log($"[{nameof(OscSpeechReceiver)}] OnEnable");
        StartReceiver();
    }

    private void OnDisable()
    {
        Debug.Log($"[{nameof(OscSpeechReceiver)}] OnDisable");
        StopReceiver();
    }

    private void OnDestroy()
    {
        Debug.Log($"[{nameof(OscSpeechReceiver)}] OnDestroy");
        StopReceiver();
    }

    private void Update()
    {
        // pump transcripts onto Unity main thread
        lock (_lock)
        {
            while (_pending.Count > 0)
            {
                var t = _pending.Dequeue();
                Debug.Log($"[{nameof(OscSpeechReceiver)}] Dispatching transcript to Chat: \"{t}\"");
                if (Chat != null) Chat.ReceiveExternalSpeech(t);
                if (Chat == null) Debug.LogWarning($"[{nameof(OscSpeechReceiver)}] Chat is null, dropped transcript: \"{t}\"");
            }
        }
    }

    public void StartReceiver()
    {
        if (_registered)
        {
            Debug.Log($"[{nameof(OscSpeechReceiver)}] StartReceiver called but already registered.");
            return;
        }

        if (osc == null)
        {
            Debug.LogError($"[{nameof(OscSpeechReceiver)}] OSC reference is null. Assign the OSC component.");
            enabled = false;
            return;
        }

        // If empty, you can’t safely RemoveAddressHandler later (and you’ll receive nothing specific),
        // so treat empty as "do nothing" rather than registering a bad handler.
        if (string.IsNullOrWhiteSpace(ExpectedAddress))
        {
            Debug.LogError($"[{nameof(OscSpeechReceiver)}] ExpectedAddress is empty. Set it to something like '/speech'.");
            enabled = false;
            return;
        }

        try
        {
            osc.SetAddressHandler(ExpectedAddress, OnReceiveSpeech);
            _registered = true;
            Debug.Log($"[{nameof(OscSpeechReceiver)}] Registered '{ExpectedAddress}' on osc='{osc.name}' inPort={osc.inPort} AcceptAnyChannel={AcceptAnyChannel}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{nameof(OscSpeechReceiver)}] Failed to register OSC handler for '{ExpectedAddress}': {ex}");
        }
    }

    public void StopReceiver()
    {
        if (!_registered)
        {
            Debug.Log($"[{nameof(OscSpeechReceiver)}] StopReceiver called but not registered.");
            return;
        }

        Debug.Log($"[{nameof(OscSpeechReceiver)}] Unregistering OSC handler for '{ExpectedAddress}'");
        try
        {
            if (osc != null && !string.IsNullOrWhiteSpace(ExpectedAddress))
                osc.RemoveAddressHandler(ExpectedAddress, OnReceiveSpeech);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[{nameof(OscSpeechReceiver)}] Exception while removing handler: {ex}");
        }

        _registered = false;
        Debug.Log($"[{nameof(OscSpeechReceiver)}] Unregistered.");
    }

    private void OnReceiveSpeech(OscMessage message)
    {
        Debug.Log($"[{nameof(OscSpeechReceiver)}] OnReceiveSpeech called. Message present: {message != null}");
        if (message == null || message.values == null)
        {
            Debug.LogWarning($"[{nameof(OscSpeechReceiver)}] Message or message.values is null.");
            return;
        }

        Debug.Log($"[{nameof(OscSpeechReceiver)}] Message values count: {message.values.Count}");
        for (int i = 0; i < message.values.Count; i++)
        {
            var val = message.values[i];
            Debug.Log($"[{nameof(OscSpeechReceiver)}] value[{i}] type={val?.GetType().Name ?? "null"} value={val}");
        }

        // Expected payloads:
        //  - /speech "hello world"
        //  - /speech <int channel> "hello world"
        if (message.values.Count == 0)
        {
            Debug.LogWarning($"[{nameof(OscSpeechReceiver)}] Empty message values, ignoring.");
            return;
        }

        int channel = 0;
        string transcript = null;

        if (message.values.Count >= 2 && (message.values[0] is int || message.values[0] is float))
        {
            try
            {
                channel = message.GetInt(0);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{nameof(OscSpeechReceiver)}] Failed to parse channel as int: {ex}");
            }
            transcript = message.values[1] as string ?? message.values[1]?.ToString();
            Debug.Log($"[{nameof(OscSpeechReceiver)}] Parsed channel={channel}, transcript=\"{transcript}\"");
        }
        else
        {
            // treat first arg as transcript
            transcript = message.values[0] as string ?? message.values[0]?.ToString();
            Debug.Log($"[{nameof(OscSpeechReceiver)}] Parsed transcript from first arg=\"{transcript}\"");
        }

        if (!AcceptAnyChannel && (AllowedChannels == null || !AllowedChannels.Contains(channel)))
        {
            Debug.Log($"[{nameof(OscSpeechReceiver)}] Channel {channel} not allowed. AcceptAnyChannel={AcceptAnyChannel}. AllowedChannels={string.Join(",", AllowedChannels ?? new List<int>())}");
            return;
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            Debug.LogWarning($"[{nameof(OscSpeechReceiver)}] Transcript is empty or whitespace, ignoring.");
            return;
        }

        lock (_lock)
        {
            _pending.Enqueue(transcript);
            Debug.Log($"[{nameof(OscSpeechReceiver)}] Enqueued transcript: \"{transcript}\" (channel={channel})");
        }
    }
}
