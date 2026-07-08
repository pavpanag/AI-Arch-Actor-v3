using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace CNCDemo
{
    /// <summary>What happens after a behavior is performed.</summary>
    public enum CNCActionStateMode
    {
        [InspectorName("Choose & Keep")]
        ChooseAndKeep = 0,
        [InspectorName("Act & Return To Neutral")]
        ActAndReturnToNeutral = 1
    }

    /// <summary>
    /// One behavior of the lamp's expressive vocabulary: a label the model can choose,
    /// a light condition (colour/brightness/pulse), and an optional sound.
    /// </summary>
    [Serializable]
    public sealed class CNCBehaviorSpec
    {
        [Tooltip("The action label the model chooses — a short first-person verb, e.g. 'i say yes'.")]
        public string label;
        [Tooltip("Light colour as hex, e.g. #FFC073.")]
        public string colorHex = "#FFC073";
        [Range(0f, 1f)] public float brightness = 1f;
        [Tooltip("If true, the light pulses (sinusoidal breathing) instead of holding steady.")]
        public bool pulse = false;
        [Tooltip("How many pulses. 0 = keep pulsing until another behavior takes over.")]
        public int pulseCount = 0;
        [Tooltip("Total seconds the pulses take (with count 0: seconds per single pulse).")]
        public float pulseSeconds = 2f;
        [Tooltip("Sound file for this behavior (in the demo-sounds folder). Empty = light only.")]
        public string soundFile = "";
    }

    /// <summary>
    /// Unity-local actuation endpoint for the one-lamp demo. Speaks the SAME OSC contract as the
    /// external Python controllers — "memory N" on the light port, "sound N" on the sound port —
    /// so the label -> trigger seam is unchanged and the Python rig remains a drop-in alternative
    /// (run the Python apps instead and disable the listeners here).
    ///
    /// Renders a behavior to: the virtual lamp Light in the scene (the source of truth — the
    /// physical rig is mirrored from it by CNCHueProjector) and an AudioSource for the single
    /// speaker. Pulse is a sinusoidal brightness breathe, mirroring the Python implementation.
    /// </summary>
    public sealed class CNCLampActuator : MonoBehaviour
    {
        [Header("Virtual Lamp")]
        [Tooltip("The Unity Light representing the lamp (the digital twin).")]
        public Light VirtualLamp;
        [Range(0.1f, 12f)] public float MaxIntensity = 4f;

        [Header("OSC Listeners (same contract as the Python controllers)")]
        public bool EnableOscListeners = true;
        [Tooltip("Port for 'memory N' triggers (lights controller uses 4444).")]
        public int LightPort = 4444;
        [Tooltip("Port for 'sound N' triggers (sound controller uses 5555).")]
        public int SoundPort = 5555;

        // Physical output happens one layer down: CNCHueProjector watches the VirtualLamp Light
        // (the source of truth) and mirrors it to the bulbs. This component drives virtual only.

        [Header("Behaviors (runtime; loaded from the preset by the bridge)")]
        public List<CNCBehaviorSpec> Behaviors = new List<CNCBehaviorSpec>();

        [Header("Pulse")]
        [Range(0f, 1f)] [Tooltip("Brightness floor of the pulse, as a fraction of the behavior brightness.")]
        public float PulseFloor = 0.15f;

        [Header("Action State Mode (settable from the Expression tab)")]
        [Tooltip("Choose & Keep: the response stays. Act & Return To Neutral: steady responses hold for HoldSeconds then fade to neutral; finite pulses return to neutral after the last pulse.")]
        public CNCActionStateMode ActionStateMode = CNCActionStateMode.ChooseAndKeep;
        [Tooltip("How long a NON-pulsing response is held before returning to neutral.")]
        public float HoldSeconds = 4f;
        [Tooltip("The neutral light: colour...")]
        public string NeutralColorHex = "#FFB45A";
        [Range(0f, 1f)] [Tooltip("...and brightness.")]
        public float NeutralBrightness = 0.12f;

        [Header("Status (read-only)")]
        [TextArea(1, 3)] public string LastApplied;
        [TextArea(1, 3)] public string LastSoundStatus;

        private AudioSource _audio;
        private readonly ConcurrentQueue<Action> _mainQueue = new ConcurrentQueue<Action>();
        private UdpClient _lightUdp, _soundUdp;
        private Thread _lightThread, _soundThread;
        private volatile bool _running;
        private Coroutine _pulseRoutine;
        private readonly Dictionary<int, AudioClip> _clips = new Dictionary<int, AudioClip>();

        public static string SoundsDirectory => Path.Combine(Application.streamingAssetsPath, "demo-sounds");

        private void Awake()
        {
            _audio = GetComponent<AudioSource>();
            if (_audio == null) _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f; // one speaker, plain stereo out
        }

        private void Start()
        {
            Directory.CreateDirectory(SoundsDirectory);
            if (EnableOscListeners) StartListeners();
            _ = ReloadAllClipsAsync();
        }

        private void OnDestroy()
        {
            _running = false;
            try { _lightUdp?.Close(); } catch { }
            try { _soundUdp?.Close(); } catch { }
        }

        private void Update()
        {
            while (_mainQueue.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception ex) { Debug.LogError("[CNCLampActuator] " + ex.Message); }
            }
        }

        // --- Public API (used by the bridge) -----------------------------------

        public void SetBehaviors(List<CNCBehaviorSpec> specs)
        {
            Behaviors = specs ?? new List<CNCBehaviorSpec>();
            _ = ReloadAllClipsAsync();
        }

        /// <summary>Applies behavior index (0-based): light now, sound if it has one.</summary>
        public void TestBehavior(int index)
        {
            ApplyBehaviorLight(index);
            PlayBehaviorSound(index);
        }

        public void ApplyBehaviorLight(int index)
        {
            var b = At(index);
            if (b == null) return;

            if (_pulseRoutine != null) { StopCoroutine(_pulseRoutine); _pulseRoutine = null; }
            _pulseRoutine = StartCoroutine(RunBehaviorLight(b));

            LastApplied = "behavior " + (index + 1) + " (" + (b.label ?? "?") + ")";
        }

        /// <summary>
        /// Performs a behavior's light under the current mode.
        /// Keep: steady holds; a finite pulse settles at its full colour/brightness.
        /// Return: steady holds HoldSeconds then goes neutral; a finite pulse goes neutral after
        /// the last pulse. An endless pulse (count 0) runs until the next behavior either way.
        /// </summary>
        private System.Collections.IEnumerator RunBehaviorLight(CNCBehaviorSpec b)
        {
            var color = ParseColor(b.colorHex);
            var brightness = Mathf.Clamp01(b.brightness);
            var returnToNeutral = ActionStateMode == CNCActionStateMode.ActAndReturnToNeutral;

            if (b.pulse)
            {
                var seconds = Mathf.Max(0.3f, b.pulseSeconds);
                var floor = brightness * Mathf.Clamp01(PulseFloor);
                var period = b.pulseCount > 0 ? seconds / b.pulseCount : seconds;
                var total = b.pulseCount > 0 ? seconds : float.PositiveInfinity;
                var t0 = Time.time;

                while (Time.time - t0 < total)
                {
                    var phase = (Time.time - t0) / period * 2f * Mathf.PI;
                    var normalized = (Mathf.Sin(phase) + 1f) * 0.5f;
                    ApplyLightState(color, floor + normalized * (brightness - floor));
                    yield return null;
                }

                // Finite pulse finished: settle at full value, or hand over to neutral.
                if (returnToNeutral) ApplyNeutral();
                else ApplyLightState(color, brightness);
            }
            else
            {
                ApplyLightState(color, brightness);
                if (returnToNeutral)
                {
                    yield return new WaitForSeconds(Mathf.Max(0.1f, HoldSeconds));
                    ApplyNeutral();
                }
            }

            _pulseRoutine = null;
        }

        private void ApplyNeutral()
        {
            ApplyLightState(ParseColor(NeutralColorHex), Mathf.Clamp01(NeutralBrightness));
        }

        public void PlayBehaviorSound(int index)
        {
            var b = At(index);
            if (b == null || string.IsNullOrWhiteSpace(b.soundFile)) return;

            if (_clips.TryGetValue(index, out var clip) && clip != null)
            {
                _audio.Stop();
                _audio.clip = clip;
                _audio.Play();
                LastSoundStatus = "playing " + b.soundFile;
            }
            else
            {
                LastSoundStatus = "clip not loaded: " + b.soundFile;
            }
        }

        /// <summary>Names of the available microphones ("sound actuators" for recording).</summary>
        public string[] GetMicrophones() => Microphone.devices ?? Array.Empty<string>();

        /// <summary>Records from the chosen microphone (empty = system default) into behavior index's slot, saves WAV, loads it.</summary>
        public async Task<string> RecordSoundAsync(int index, float seconds, string micName = null)
        {
            var b = At(index);
            if (b == null) return "no such behavior";
            seconds = Mathf.Clamp(seconds, 0.5f, 15f);

            if (Microphone.devices == null || Microphone.devices.Length == 0)
                return "no microphone found";

            var mic = string.IsNullOrWhiteSpace(micName) ? null : micName;
            if (mic != null && Array.IndexOf(Microphone.devices, mic) < 0) mic = null; // unknown -> default

            var recording = Microphone.Start(mic, false, Mathf.CeilToInt(seconds), 44100);
            LastSoundStatus = "recording " + seconds + "s…";
            await Task.Delay(TimeSpan.FromSeconds(seconds + 0.15f));
            Microphone.End(mic);

            if (recording == null) return "recording failed";

            var samples = new float[recording.samples * recording.channels];
            recording.GetData(samples, 0);

            var fileName = "sound_" + (index + 1) + ".wav";
            var path = Path.Combine(SoundsDirectory, fileName);
            WriteWav(path, samples, recording.channels, recording.frequency);

            b.soundFile = fileName;
            await LoadClipAsync(index, path);
            LastSoundStatus = "recorded " + fileName;
            return "ok";
        }

        /// <summary>Saves uploaded audio bytes into behavior index's slot and loads it.</summary>
        public async Task<string> SaveUploadedSoundAsync(int index, byte[] data, string ext)
        {
            var b = At(index);
            if (b == null) return "no such behavior";
            if (data == null || data.Length == 0) return "empty file";

            ext = (ext ?? "wav").Trim('.').ToLowerInvariant();
            if (ext != "wav" && ext != "ogg" && ext != "mp3") ext = "wav";

            var fileName = "sound_" + (index + 1) + "." + ext;
            var path = Path.Combine(SoundsDirectory, fileName);
            File.WriteAllBytes(path, data);

            b.soundFile = fileName;
            var ok = await LoadClipAsync(index, path);
            LastSoundStatus = ok ? "loaded " + fileName : "could not decode " + fileName;
            return ok ? "ok" : "could not decode audio (wav recommended)";
        }

        public bool HasSound(int index)
        {
            var b = At(index);
            return b != null && !string.IsNullOrWhiteSpace(b.soundFile);
        }

        // --- OSC listeners (same wire contract as the Python apps) -------------

        private void StartListeners()
        {
            _running = true;
            _lightThread = StartListener(LightPort, u => _lightUdp = u, text => HandleTrigger(text, "memory", i => ApplyBehaviorLight(i)));
            _soundThread = StartListener(SoundPort, u => _soundUdp = u, text => HandleTrigger(text, "sound", i => PlayBehaviorSound(i)));
        }

        private Thread StartListener(int port, Action<UdpClient> keep, Action<string> onText)
        {
            try
            {
                var udp = new UdpClient(port);
                keep(udp);
                var t = new Thread(() =>
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    while (_running)
                    {
                        byte[] data;
                        try { data = udp.Receive(ref remote); }
                        catch { break; }
                        var text = ExtractTriggerText(data);
                        if (!string.IsNullOrWhiteSpace(text)) onText(text);
                    }
                })
                { IsBackground = true };
                t.Start();
                Debug.Log("[CNCLampActuator] Listening on UDP " + port);
                return t;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CNCLampActuator] Could not bind UDP " + port + " (is the Python controller running?): " + ex.Message);
                return null;
            }
        }

        /// <summary>Parses "memory N"/"sound N" and dispatches on the main thread (0-based index).</summary>
        private void HandleTrigger(string text, string keyword, Action<int> apply)
        {
            var t = text.Trim().ToLowerInvariant();
            var parts = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[parts.Length - 2] != keyword) return;
            if (!int.TryParse(parts[parts.Length - 1], out var n) || n < 1) return;
            _mainQueue.Enqueue(() => apply(n - 1));
        }

        /// <summary>Reads the string argument out of an OSC packet, or falls back to raw UTF8.</summary>
        private static string ExtractTriggerText(byte[] data)
        {
            try
            {
                // OSC: address (padded string), ",s" typetag (padded), then the string arg.
                int pos = 0;
                var address = ReadOscString(data, ref pos);
                if (address != null && address.StartsWith("/", StringComparison.Ordinal))
                {
                    var typeTag = ReadOscString(data, ref pos);
                    if (typeTag != null && typeTag.StartsWith(",", StringComparison.Ordinal) && typeTag.Contains("s"))
                    {
                        var arg = ReadOscString(data, ref pos);
                        if (!string.IsNullOrWhiteSpace(arg)) return arg;
                    }
                    // Raw UTF8 fallback shape: "/memory memory 3"
                    var raw = Encoding.UTF8.GetString(data);
                    var space = raw.IndexOf(' ');
                    return space > 0 ? raw.Substring(space + 1) : string.Empty;
                }
                return Encoding.UTF8.GetString(data);
            }
            catch { return string.Empty; }
        }

        private static string ReadOscString(byte[] data, ref int pos)
        {
            if (pos >= data.Length) return null;
            int start = pos;
            while (pos < data.Length && data[pos] != 0) pos++;
            var s = Encoding.UTF8.GetString(data, start, pos - start);
            pos = ((pos / 4) + 1) * 4; // skip null + pad to 4
            return s;
        }

        // --- Light rendering ----------------------------------------------------

        /// <summary>Drives the virtual lamp only — the Light is the source of truth; CNCHueProjector mirrors it to the physical rig.</summary>
        private void ApplyLightState(Color color, float brightness01)
        {
            if (VirtualLamp == null) return;
            VirtualLamp.color = color;
            VirtualLamp.intensity = brightness01 * MaxIntensity;
            VirtualLamp.enabled = brightness01 > 0.005f;
        }

        // --- Clip loading / WAV ------------------------------------------------

        private async Task ReloadAllClipsAsync()
        {
            for (int i = 0; i < (Behaviors?.Count ?? 0); i++)
            {
                var b = Behaviors[i];
                if (b == null || string.IsNullOrWhiteSpace(b.soundFile)) continue;
                var path = Path.Combine(SoundsDirectory, b.soundFile);
                if (File.Exists(path)) await LoadClipAsync(i, path);
            }
        }

        private async Task<bool> LoadClipAsync(int index, string path)
        {
            var type = AudioType.WAV;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ogg") type = AudioType.OGGVORBIS;
            else if (ext == ".mp3") type = AudioType.MPEG;

            using (var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path.Replace('\\', '/'), type))
            {
                var op = req.SendWebRequest();
                while (!op.isDone) await Task.Yield();
                if (req.result != UnityWebRequest.Result.Success) return false;
                var clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null) return false;
                _clips[index] = clip;
                return true;
            }
        }

        private static void WriteWav(string path, float[] samples, int channels, int sampleRate)
        {
            using (var fs = new FileStream(path, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                int byteCount = samples.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + byteCount);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)1);                    // PCM
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * channels * 2);   // byte rate
                w.Write((short)(channels * 2));       // block align
                w.Write((short)16);                   // bits
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(byteCount);
                foreach (var f in samples)
                    w.Write((short)(Mathf.Clamp(f, -1f, 1f) * short.MaxValue));
            }
        }

        // --- Helpers -------------------------------------------------------------

        private CNCBehaviorSpec At(int index) =>
            (Behaviors != null && index >= 0 && index < Behaviors.Count) ? Behaviors[index] : null;

        private static Color ParseColor(string hex)
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorUtility.TryParseHtmlString(hex.Trim(), out var c)) return c;
            return new Color(1f, 0.75f, 0.45f); // warm default
        }
    }
}
