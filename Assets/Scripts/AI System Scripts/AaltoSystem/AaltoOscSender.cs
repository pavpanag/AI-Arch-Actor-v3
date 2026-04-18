using System;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace AaltoSystemV3
{
    /// <summary>
    /// Phase 4.2 bridge: sends OSC UDP messages to Python light controller.
    /// Default target matches lights controller v15 (UDP 4444).
    /// </summary>
    public sealed class AaltoOscSender : MonoBehaviour
    {
        [Header("OSC Endpoint")]
        public string Host = "127.0.0.1";
        public int Port = 4444;
        public string Address = "/memory";
        [Tooltip("If true, send plain UTF8 '<address> <message>' payload instead of OSC packet. Keep false for pythonosc server.")]
        public bool UseRawUtf8Fallback = false;

        [Header("Debug")]
        public bool LogSends = true;

        public string EndpointInfo => $"{Host}:{Port} {NormalizeAddress(Address)}";

        private UdpClient _udp;

        private void Awake()
        {
            _udp = new UdpClient();
        }

        private void OnDestroy()
        {
            try { _udp?.Close(); }
            catch { }
            _udp = null;
        }

        public bool TrySendMemoryTrigger(string trigger, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(trigger))
            {
                error = "Trigger is empty.";
                return false;
            }

            try
            {
                if (_udp == null)
                    _udp = new UdpClient();

                var normalizedAddress = NormalizeAddress(Address);
                var normalizedTrigger = trigger.Trim();

                byte[] data;
                string debugPayload;
                if (UseRawUtf8Fallback)
                {
                    debugPayload = normalizedAddress + " " + normalizedTrigger;
                    data = Encoding.UTF8.GetBytes(debugPayload);
                }
                else
                {
                    debugPayload = normalizedAddress + " [arg:string] " + normalizedTrigger;
                    data = BuildOscPacket(normalizedAddress, normalizedTrigger);
                }

                _udp.Send(data, data.Length, Host, Port);

                if (LogSends)
                    Debug.Log($"[AaltoOscSender] Sent -> {Host}:{Port} '{debugPayload}'");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.LogWarning("[AaltoOscSender] Send failed: " + error);
                return false;
            }
        }

        public void OnScenicReplayTrigger(string trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger))
            {
                Debug.LogWarning("[AaltoOscSender] Scenic replay trigger ignored: empty trigger.");
                return;
            }

            var normalizedTrigger = trigger.Trim();
            if (TrySendMemoryTrigger(normalizedTrigger, out var error))
            {
                Debug.Log("[AaltoOscSender] Scenic replay trigger sent successfully: " + normalizedTrigger);
                return;
            }

            var details = string.IsNullOrWhiteSpace(error) ? "Unknown error." : error;
            Debug.LogWarning("[AaltoOscSender] Scenic replay trigger failed: " + normalizedTrigger + " | " + details);
        }

        private static string NormalizeAddress(string address)
        {
            var a = string.IsNullOrWhiteSpace(address) ? "/memory" : address.Trim();
            if (!a.StartsWith("/")) a = "/" + a;
            return a;
        }

        private static byte[] BuildOscPacket(string address, string stringArg)
        {
            // OSC message = address + typeTag + args; each OSC-string is null-terminated and 4-byte padded.
            var addr = PackOscString(address);
            var typeTag = PackOscString(",s");
            var arg = PackOscString(stringArg ?? "");

            var data = new byte[addr.Length + typeTag.Length + arg.Length];
            Buffer.BlockCopy(addr, 0, data, 0, addr.Length);
            Buffer.BlockCopy(typeTag, 0, data, addr.Length, typeTag.Length);
            Buffer.BlockCopy(arg, 0, data, addr.Length + typeTag.Length, arg.Length);
            return data;
        }

        private static byte[] PackOscString(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            var withNull = bytes.Length + 1;
            var pad = (4 - (withNull % 4)) % 4;

            var outBytes = new byte[withNull + pad];
            Buffer.BlockCopy(bytes, 0, outBytes, 0, bytes.Length);
            // trailing bytes are already zeroed, including null terminator + padding
            return outBytes;
        }
    }
}
