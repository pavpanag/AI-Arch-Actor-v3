using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AaltoSystemV3
{
    /// <summary>
    /// Receives transcript-complete OSC payloads from the Python audio-note sidecar
    /// and re-emits them into the launch session logger as structured events.
    /// </summary>
    public sealed class AaltoAudioNoteTranscriptOscReceiver : MonoBehaviour
    {
        [Header("OSC")]
        [Tooltip("Reference to the OSC MonoBehaviour that owns the UDP receiver thread.")]
        public MonoBehaviour Osc;

        [Tooltip("Expected OSC address from the sidecar script.")]
        public string ExpectedAddress = "/audio_note_transcript";

        [Header("Behavior")]
        [Tooltip("If true, expects first OSC arg to be a JSON string payload.")]
        public bool ExpectJsonStringPayload = true;

        [Header("Debug")]
        [TextArea(2, 6)]
        public string LastStatus;

        [TextArea(2, 6)]
        public string LastPayload;

        public int TotalReceived;
        public int TotalLogged;
        public int TotalDropped;

        private readonly Queue<string> _pendingPayloads = new Queue<string>();
        private readonly object _gate = new object();

        private bool _registered;
        private MethodInfo _setAddressHandlerMethod;
        private MethodInfo _removeAddressHandlerMethod;
        private Delegate _addressHandlerDelegate;

        private void OnEnable()
        {
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
            lock (_gate)
            {
                while (_pendingPayloads.Count > 0)
                {
                    var payload = _pendingPayloads.Dequeue();
                    var payloadJson = NormalizePayloadToJson(payload);
                    LastPayload = payload;

                    AaltoLaunchSessionLogger.EmitEvent("audio_note_sidecar", "audio_note.transcript_ready", payloadJson);
                    TotalLogged++;
                    SetStatus("Logged transcript payload from OSC sidecar.");
                }
            }
        }

        public void StartReceiver()
        {
            if (_registered)
                return;

            if (Osc == null)
            {
                SetStatus("OSC reference is null. Assign an OSC component.");
                return;
            }

            if (string.IsNullOrWhiteSpace(ExpectedAddress))
            {
                SetStatus("ExpectedAddress is empty.");
                return;
            }

            try
            {
                if (!TryPrepareReflectionBinding(out var bindError))
                {
                    SetStatus("Failed to prepare OSC binding: " + bindError);
                    return;
                }

                _setAddressHandlerMethod.Invoke(Osc, new object[] { ExpectedAddress, _addressHandlerDelegate });
                _registered = true;
                SetStatus("Registered OSC handler for " + ExpectedAddress + ".");
            }
            catch (Exception ex)
            {
                SetStatus("Failed to register OSC handler: " + ex.Message);
            }
        }

        public void StopReceiver()
        {
            if (!_registered)
                return;

            try
            {
                if (Osc != null && !string.IsNullOrWhiteSpace(ExpectedAddress) &&
                    _removeAddressHandlerMethod != null && _addressHandlerDelegate != null)
                {
                    _removeAddressHandlerMethod.Invoke(Osc, new object[] { ExpectedAddress, _addressHandlerDelegate });
                }
            }
            catch (Exception ex)
            {
                SetStatus("Exception while removing OSC handler: " + ex.Message);
            }

            _registered = false;
        }

        private void OnReceiveDynamic(object message)
        {
            var values = TryGetMessageValues(message);
            if (values == null || values.Count == 0)
            {
                TotalDropped++;
                return;
            }

            string payload;
            if (ExpectJsonStringPayload)
            {
                payload = values[0] as string;
                if (string.IsNullOrWhiteSpace(payload))
                {
                    TotalDropped++;
                    return;
                }
            }
            else
            {
                payload = BuildFallbackPayloadJson(values);
            }

            lock (_gate)
            {
                _pendingPayloads.Enqueue(payload);
                TotalReceived++;
            }
        }

        private bool TryPrepareReflectionBinding(out string error)
        {
            error = null;

            if (Osc == null)
            {
                error = "OSC MonoBehaviour is null.";
                return false;
            }

            var oscType = Osc.GetType();
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
            var callbackMethod = GetType().GetMethod(nameof(OnReceiveDynamic), BindingFlags.NonPublic | BindingFlags.Instance);
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

        private static string BuildFallbackPayloadJson(IList values)
        {
            var parts = new List<string>();
            for (int i = 0; i < values.Count; i++)
            {
                var v = values[i] == null ? string.Empty : values[i].ToString();
                parts.Add("\"" + AaltoLaunchSessionLogger.EscapeJson(v) + "\"");
            }

            return "{\"raw_values\":[" + string.Join(",", parts.ToArray()) + "]}";
        }

        private static string NormalizePayloadToJson(string payload)
        {
            var trimmed = (payload ?? string.Empty).Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
                return trimmed;

            return "{\"raw\":\"" + AaltoLaunchSessionLogger.EscapeJson(trimmed) + "\"}";
        }

        [ContextMenu("Audio Note Transcript Receiver/Clear Counters")]
        public void ClearCounters()
        {
            TotalReceived = 0;
            TotalLogged = 0;
            TotalDropped = 0;
            LastPayload = string.Empty;
            SetStatus("Counters cleared.");
        }

        private void SetStatus(string status)
        {
            LastStatus = string.IsNullOrWhiteSpace(status) ? string.Empty : status;
            Debug.Log("[AaltoAudioNoteTranscriptOscReceiver] " + LastStatus);
        }
    }
}
