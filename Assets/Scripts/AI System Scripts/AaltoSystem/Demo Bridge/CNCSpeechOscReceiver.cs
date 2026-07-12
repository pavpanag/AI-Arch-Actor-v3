using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using AaltoSystemV3;

namespace CNCDemo
{
    /// <summary>
    /// Turns spoken words into the lamp's actor input. The external Vosk transcriber
    /// (Vosk SpeechToText.py) sends each finalized transcript as an OSC "/speech" packet over UDP;
    /// this receiver parses it and calls the SAME Performer.SubmitTurn the web console's "Speak"
    /// box uses. So a spoken line drives the lamp and appears in the Stage feed exactly like a typed
    /// one — with no connection between the transcriber and the web app in either direction.
    ///
    /// Fully additive and self-contained: its own UDP socket (like CNCLampActuator's trigger
    /// listeners), no shared Aalto code touched. Point the transcriber's OSC IP/Port at this port.
    /// </summary>
    public sealed class CNCSpeechOscReceiver : MonoBehaviour
    {
        [Header("Wiring")]
        [Tooltip("The lamp's performer. Auto-found in the scene if left empty.")]
        public AaltoDirectedRoomPerformerController Performer;

        [Header("OSC")]
        public bool EnableListener = true;
        [Tooltip("UDP port to listen on. Match the transcriber's OSC Port (its default is 3333).")]
        public int SpeechPort = 3333;
        [Tooltip("OSC address the transcriber sends on (its default is /speech).")]
        public string ExpectedAddress = "/speech";

        [Header("Debug (read-only)")]
        [TextArea(1, 2)] public string Status;
        [TextArea(1, 3)] public string LastHeard;
        public int TotalReceived;

        private readonly ConcurrentQueue<string> _pending = new ConcurrentQueue<string>();
        private UdpClient _udp;
        private Thread _thread;
        private volatile bool _running;

        private void OnEnable()
        {
            if (Performer == null) Performer = FindObjectOfType<AaltoDirectedRoomPerformerController>();
            if (EnableListener) StartListener();
        }

        private void OnDisable() => StopListener();
        private void OnDestroy() => StopListener();

        private void Update()
        {
            // Drain on the main thread — SubmitTurn touches Unity/performer state.
            while (_pending.TryDequeue(out var text))
            {
                if (Performer == null) Performer = FindObjectOfType<AaltoDirectedRoomPerformerController>();
                if (Performer == null) { Status = "Heard speech but no performer assigned."; continue; }
                Performer.SubmitTurn(text);
                Status = "Spoke to the lamp: " + text;
            }
        }

        private void StartListener()
        {
            if (_running) return;
            try
            {
                _udp = new UdpClient(SpeechPort);
                _running = true;
                _thread = new Thread(ListenLoop) { IsBackground = true };
                _thread.Start();
                Status = "Listening for speech on UDP " + SpeechPort + " (" + ExpectedAddress + ").";
                Debug.Log("[CNCSpeechOscReceiver] " + Status);
            }
            catch (Exception ex)
            {
                Status = "Failed to listen on UDP " + SpeechPort + ": " + ex.Message;
                Debug.LogWarning("[CNCSpeechOscReceiver] " + Status);
            }
        }

        private void StopListener()
        {
            _running = false;
            try { _udp?.Close(); } catch { }
            _udp = null;
        }

        private void ListenLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try { data = _udp.Receive(ref remote); }
                catch { break; } // socket closed
                var text = ExtractSpeechText(data);
                if (string.IsNullOrWhiteSpace(text)) continue;
                TotalReceived++;
                LastHeard = text;
                _pending.Enqueue(text.Trim());
            }
        }

        /// <summary>
        /// Pulls the transcript out of a "/speech" OSC packet. The transcriber sends either
        /// ",is" (int channel, string text) or ",s" (string text) — we ignore the channel (one
        /// participant, one lamp) and return the string argument. Falls back to raw UTF8.
        /// </summary>
        private string ExtractSpeechText(byte[] data)
        {
            try
            {
                int pos = 0;
                var address = ReadOscString(data, ref pos);
                if (address == null || !address.StartsWith("/", StringComparison.Ordinal))
                    return Encoding.UTF8.GetString(data).Trim();

                // If an address is expected, ignore packets to other addresses.
                if (!string.IsNullOrWhiteSpace(ExpectedAddress) &&
                    !string.Equals(address, ExpectedAddress, StringComparison.Ordinal))
                    return string.Empty;

                var typeTag = ReadOscString(data, ref pos);
                if (typeTag == null || !typeTag.StartsWith(",", StringComparison.Ordinal))
                    return string.Empty;

                // Walk the type tags; return the FIRST string argument, skipping ints/floats.
                for (int i = 1; i < typeTag.Length; i++)
                {
                    switch (typeTag[i])
                    {
                        case 's': return ReadOscString(data, ref pos);
                        case 'i':
                        case 'f': pos += 4; break;   // 32-bit arg, already 4-aligned
                        default: return string.Empty;
                    }
                }
                return string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string ReadOscString(byte[] data, ref int pos)
        {
            if (pos >= data.Length) return null;
            int start = pos;
            while (pos < data.Length && data[pos] != 0) pos++;
            var s = Encoding.UTF8.GetString(data, start, pos - start);
            pos = ((pos / 4) + 1) * 4; // skip null terminator + pad to 4 bytes
            return s;
        }
    }
}
