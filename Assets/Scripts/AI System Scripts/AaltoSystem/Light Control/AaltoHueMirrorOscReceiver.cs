using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// Receives per-light state from the Python controller over UDP OSC.
/// Expected OSC message: /aalto/light_state [slot:int, bri:int, hue:int, sat:int]
/// This implementation is self-contained so it works inside AaltoSystem.asmdef.
/// </summary>
public class AaltoHueMirrorOscReceiver : MonoBehaviour
{
    [Header("UDP")]
    public int listenPort = 6969;
    public string expectedAddress = "/aalto/light_state";

    [Header("Debug")]
    public bool verbose = false;

    [SerializeField]
    [Tooltip("Slots seen at runtime from incoming packets.")]
    private List<int> knownSlots = new List<int>();

    private readonly Dictionary<int, HueState> stateBySlot = new Dictionary<int, HueState>();
    private readonly HashSet<int> knownSlotSet = new HashSet<int>();
    private readonly object stateLock = new object();
    private readonly List<int> pendingAddedSlots = new List<int>();

    private Thread receiveThread;
    private UdpClient udpClient;
    private volatile bool running;

    public struct HueState
    {
        public int brightness;
        public int hue;
        public int saturation;

        public HueState(int bri, int h, int sat)
        {
            brightness = bri;
            hue = h;
            saturation = sat;
        }
    }

    public IReadOnlyList<int> KnownSlots => knownSlots;

    /// <summary>
    /// Fired on the Unity main thread when a new slot is discovered from incoming OSC packets.
    /// </summary>
    public event Action<int> OnSlotAdded;

    private void Update()
    {
        // Invoke any pending slot-added notifications on the main thread.
        List<int> toNotify = null;
        lock (stateLock)
        {
            if (pendingAddedSlots.Count > 0)
            {
                toNotify = new List<int>(pendingAddedSlots);
                pendingAddedSlots.Clear();
            }
        }

        if (toNotify != null)
        {
            foreach (var s in toNotify)
            {
                try { OnSlotAdded?.Invoke(s); }
                catch (Exception ex) { Debug.LogWarning($"[AaltoHueMirrorOscReceiver] OnSlotAdded handler threw: {ex}"); }
            }
        }
    }

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

    private void StartReceiver()
    {
        if (running) return;

        try
        {
            udpClient = new UdpClient(listenPort);
            running = true;
            receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
            receiveThread.Start();
            if (verbose) Debug.Log($"[AaltoHueMirrorOscReceiver] Listening on UDP {listenPort}");
        }
        catch (Exception ex)
        {
            running = false;
            Debug.LogWarning($"[AaltoHueMirrorOscReceiver] Failed to open UDP {listenPort}: {ex.Message}");
        }
    }

    private void StopReceiver()
    {
        running = false;

        try { udpClient?.Close(); }
        catch { }
        udpClient = null;

        if (receiveThread != null && receiveThread.IsAlive)
            receiveThread.Join(250);

        receiveThread = null;
    }

    private void ReceiveLoop()
    {
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (running)
        {
            try
            {
                byte[] data = udpClient.Receive(ref remote);
                if (verbose)
                    Debug.Log($"[AaltoHueMirrorOscReceiver] Received UDP {data.Length} bytes from {remote}");

                // Try to read the OSC address for better diagnostics even when parsing fails.
                int probeIdx = 0;
                if (TryReadOscString(data, ref probeIdx, out string probeAddr))
                {
                    if (verbose)
                        Debug.Log($"[AaltoHueMirrorOscReceiver] OSC address: '{probeAddr}'");
                }

                if (TryParseLightStatePacket(data, out int slot, out int bri, out int hue, out int sat))
                {
                    lock (stateLock)
                    {
                        stateBySlot[slot] = new HueState(bri, hue, sat);
                        if (knownSlotSet.Add(slot))
                        {
                            knownSlots.Add(slot);
                            knownSlots.Sort();
                            // Queue a main-thread notification so subscribers can react safely.
                            pendingAddedSlots.Add(slot);
                        }
                    }

                    if (verbose)
                        Debug.Log($"[AaltoHueMirrorOscReceiver] slot={slot} bri={bri} hue={hue} sat={sat}");
                }
                else
                {
                    if (verbose)
                        Debug.LogWarning($"[AaltoHueMirrorOscReceiver] Unhandled/invalid OSC packet from {remote} (len={data.Length})");
                }
            }
            catch (SocketException)
            {
                if (!running) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (verbose)
                    Debug.LogWarning($"[AaltoHueMirrorOscReceiver] Receive error: {ex.Message}");
            }
        }
    }

    private bool TryParseLightStatePacket(byte[] data, out int slot, out int bri, out int hue, out int sat)
    {
        slot = 0;
        bri = 0;
        hue = 0;
        sat = 0;

        if (data == null || data.Length < 8) return false;

        int idx = 0;
        if (!TryReadOscString(data, ref idx, out string address)) return false;
        if (!string.Equals(address, expectedAddress, StringComparison.Ordinal)) return false;

        if (!TryReadOscString(data, ref idx, out string typeTag)) return false;

        // python-osc sends ints as ",iiii" for this packet.
        if (string.IsNullOrEmpty(typeTag) || !typeTag.StartsWith(",")) return false;
        if (typeTag.Length < 5) return false;

        if (!TryReadOscInt(data, ref idx, out slot)) return false;
        if (!TryReadOscInt(data, ref idx, out bri)) return false;
        if (!TryReadOscInt(data, ref idx, out hue)) return false;
        if (!TryReadOscInt(data, ref idx, out sat)) return false;

        bri = Mathf.Clamp(bri, 0, 254);
        hue = Mathf.Clamp(hue, 0, 65535);
        sat = Mathf.Clamp(sat, 0, 254);

        return true;
    }

    private static bool TryReadOscString(byte[] data, ref int idx, out string value)
    {
        value = null;
        if (idx >= data.Length) return false;

        int start = idx;
        int end = start;
        while (end < data.Length && data[end] != 0) end++;
        if (end >= data.Length) return false;

        value = System.Text.Encoding.UTF8.GetString(data, start, end - start);

        int lengthWithNull = (end - start) + 1;
        int padded = ((lengthWithNull + 3) / 4) * 4;
        idx += padded;
        return idx <= data.Length;
    }

    private static bool TryReadOscInt(byte[] data, ref int idx, out int value)
    {
        value = 0;
        if (idx + 4 > data.Length) return false;

        value = (data[idx] << 24) |
                (data[idx + 1] << 16) |
                (data[idx + 2] << 8) |
                data[idx + 3];

        idx += 4;
        return true;
    }

    public bool TryGetState(int slot, out HueState state)
    {
        lock (stateLock)
        {
            return stateBySlot.TryGetValue(slot, out state);
        }
    }
}
