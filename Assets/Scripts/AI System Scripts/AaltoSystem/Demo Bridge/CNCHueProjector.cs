using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CNCDemo
{
    /// <summary>
    /// Projects a Unity Light onto the physical Hue rig: the light is the source of truth, and
    /// whatever changes it — the actuator, an animation, a shader, a manual tweak — is mirrored
    /// to EVERY bulb the bridge reports (discovered like lights controller v15's mapping step).
    ///
    /// Direction is strictly virtual -> physical. Do not run the old physical->virtual mirror
    /// (AaltoHueMirrorOscReceiver) on the same light at the same time, or you get a feedback loop.
    /// Sends are change-detected and throttled because the Hue bridge is rate-limited.
    /// </summary>
    public sealed class CNCHueProjector : MonoBehaviour
    {
        [Header("Source of Truth")]
        [Tooltip("The Unity Light to project — same Light the actuator drives.")]
        public Light TargetLight;
        [Tooltip("Light.intensity that counts as full brightness (matches the actuator's MaxIntensity).")]
        public float IntensityForFullBrightness = 4f;

        [Header("Hue Bridge")]
        public bool EnableProjection = true;
        public string BridgeIP = "192.168.1.106";
        [Tooltip("Hue bridge API username (same one lights controller v15.py uses).")]
        public string UserApi = "0fLeSuFEFbk1UV2ehHFZKAyOBDL7dlSbE2szNqwR";
        [Tooltip("\"auto\" = discover every light on the bridge and drive them all. Or a fixed list like \"1,3\".")]
        public string BulbIds = "auto";

        [Header("Rate Limiting")]
        [Tooltip("Seconds between sends. ~0.1 (10/sec) is the Hue max for ONE bulb and gives the smoothest pulse. If you drive several bulbs at once, raise this (total commands/sec across all bulbs should stay near 10).")]
        [Range(0.08f, 2f)] public float MinSendInterval = 0.1f;

        [Header("Status (read-only)")]
        [TextArea(1, 2)] public string HueStatus;

        private readonly List<int> _discoveredBulbs = new List<int>();
        private Color _lastColor = Color.clear;
        private float _lastBrightness = -1f;
        private float _lastSendTime = -999f;
        private bool _sending;

        private void Start()
        {
            if (TargetLight == null)
                Debug.LogWarning("[CNCHueProjector] No TargetLight assigned — nothing to project.");
            if (!string.IsNullOrWhiteSpace(UserApi))
                StartCoroutine(DiscoverBulbs());
        }

        /// <summary>The bulb IDs the bridge reports (for the Technical tab to display).</summary>
        public List<int> DiscoveredBulbs() => new List<int>(_discoveredBulbs);

        /// <summary>Re-query the bridge for its lights (e.g. after plugging one in).</summary>
        public void Rediscover()
        {
            if (!string.IsNullOrWhiteSpace(UserApi)) StartCoroutine(DiscoverBulbs());
        }

        private void Update()
        {
            if (!EnableProjection || TargetLight == null) return;
            if (_sending) return;                                   // never overlap sends (bridge floods = clunky)
            if (Time.time - _lastSendTime < MinSendInterval) return;

            var color = TargetLight.color;
            var brightness = TargetLight.enabled
                ? Mathf.Clamp01(TargetLight.intensity / Mathf.Max(0.01f, IntensityForFullBrightness))
                : 0f;

            // Send whenever the light moved at all — small threshold so pulses aren't flattened.
            var colorDelta = Mathf.Abs(color.r - _lastColor.r) + Mathf.Abs(color.g - _lastColor.g) + Mathf.Abs(color.b - _lastColor.b);
            if (colorDelta < 0.008f && Mathf.Abs(brightness - _lastBrightness) < 0.008f) return;

            _lastColor = color;
            _lastBrightness = brightness;
            _lastSendTime = Time.time;
            StartCoroutine(SendHueState(color, brightness));
        }

        private System.Collections.IEnumerator SendHueState(Color color, float brightness01)
        {
            _sending = true;

            Color.RGBToHSV(color, out var h, out var s, out _);
            var bri = Mathf.RoundToInt(brightness01 * 254f);
            // Transition ≈ the send interval, so the bulb ramps smoothly from one sample to the next
            // and arrives just as the next command comes — continuous motion, no stair-step, minimal lag.
            var tt = Mathf.Max(1, Mathf.RoundToInt(MinSendInterval * 10f));
            string body = bri > 0
                ? "{\"on\":true,\"bri\":" + Mathf.Max(1, bri) +
                  ",\"hue\":" + Mathf.RoundToInt(h * 65535f) +
                  ",\"sat\":" + Mathf.RoundToInt(s * 254f) +
                  ",\"transitiontime\":" + tt + "}"
                : "{\"on\":false,\"transitiontime\":" + tt + "}";

            // One lamp, whatever its ID: broadcast the state to every resolved bulb.
            foreach (var id in ResolveBulbIds())
            {
                var url = "http://" + BridgeIP + "/api/" + UserApi + "/lights/" + id + "/state";
                using (var req = UnityWebRequest.Put(url, body))
                {
                    req.SetRequestHeader("Content-Type", "application/json");
                    yield return req.SendWebRequest();
                }
            }

            _sending = false;
        }

        private List<int> ResolveBulbIds()
        {
            var csv = (BulbIds ?? "auto").Trim();
            if (!string.Equals(csv, "auto", StringComparison.OrdinalIgnoreCase))
            {
                var manual = new List<int>();
                foreach (var part in csv.Split(','))
                    if (int.TryParse(part.Trim(), out var id) && id > 0) manual.Add(id);
                if (manual.Count > 0) return manual;
            }

            if (_discoveredBulbs.Count > 0) return _discoveredBulbs;

            // Discovery hasn't answered (yet): broadcast IDs 1-8, matching the controller's slot range.
            return new List<int> { 1, 2, 3, 4, 5, 6, 7, 8 };
        }

        /// <summary>Asks the bridge for its lights (same as lights controller v15's mapping step).</summary>
        private System.Collections.IEnumerator DiscoverBulbs()
        {
            var url = "http://" + BridgeIP + "/api/" + UserApi + "/lights";
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 5;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    HueStatus = "Bridge not reachable (" + req.error + ") — broadcasting to IDs 1-8.";
                    yield break;
                }

                _discoveredBulbs.Clear();
                foreach (var key in TopLevelJsonKeys(req.downloadHandler.text))
                    if (int.TryParse(key, out var id)) _discoveredBulbs.Add(id);
                _discoveredBulbs.Sort();

                HueStatus = _discoveredBulbs.Count > 0
                    ? "Projecting onto bridge light IDs: " + string.Join(", ", _discoveredBulbs)
                    : "Bridge answered but reported no lights — broadcasting to IDs 1-8.";
                Debug.Log("[CNCHueProjector] " + HueStatus);
            }
        }

        /// <summary>Extracts the top-level object keys of a JSON object like {"1":{...},"4":{...}}.</summary>
        private static List<string> TopLevelJsonKeys(string json)
        {
            var keys = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return keys;

            int depth = 0;
            bool inString = false, escaped = false;
            var current = new StringBuilder();
            bool capturing = false;

            for (int i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (escaped) { escaped = false; if (capturing) current.Append(c); }
                    else if (c == '\\') { escaped = true; }
                    else if (c == '"')
                    {
                        inString = false;
                        if (capturing)
                        {
                            int j = i + 1;
                            while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                            if (j < json.Length && json[j] == ':') keys.Add(current.ToString());
                            capturing = false;
                        }
                    }
                    else if (capturing) current.Append(c);
                    continue;
                }

                if (c == '"') { inString = true; if (depth == 1) { capturing = true; current.Length = 0; } }
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }

            return keys;
        }
    }
}
