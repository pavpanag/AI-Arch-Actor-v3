using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CNCDemo
{
    /// <summary>One correspondence: a Unity Light (by GameObject name) mirrored to a Hue bulb ID.</summary>
    [Serializable]
    public sealed class CNCLightMap
    {
        public string lightName;
        public int bulbId;
    }

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
        [Tooltip("\"auto\" = discover every light on the bridge and drive them all. Or a fixed list like \"1,3\". Ignored when Mappings are set.")]
        public string BulbIds = "auto";

        [Header("Light → Bulb mapping (optional; overrides the broadcast above)")]
        [Tooltip("Map specific Unity lights (by name) to specific bridge bulb IDs. When any mapping is set, ONLY these are driven — each Unity light mirrored to its bulb. Empty = broadcast TargetLight to BulbIds.")]
        public List<CNCLightMap> Mappings = new List<CNCLightMap>();

        [Header("Rate Limiting")]
        [Tooltip("Seconds between sends. ~0.1 (10/sec) is the Hue max for ONE bulb and gives the smoothest pulse. If you drive several bulbs at once, raise this (total commands/sec across all bulbs should stay near 10).")]
        [Range(0.08f, 2f)] public float MinSendInterval = 0.1f;

        [Header("Status (read-only)")]
        [TextArea(1, 2)] public string HueStatus;

        private readonly List<int> _discoveredBulbs = new List<int>();
        private readonly Dictionary<int, Color> _lastColorByBulb = new Dictionary<int, Color>();
        private readonly Dictionary<int, float> _lastBriByBulb = new Dictionary<int, float>();
        private readonly Dictionary<string, Light> _lightsByName = new Dictionary<string, Light>();
        private float _lastSendTime = -999f;
        private bool _sending;

        private struct BulbState { public int bulbId; public Color color; public float bri; }

        private void Start()
        {
            RefreshSceneLights();
            if (TargetLight == null && (Mappings == null || Mappings.Count == 0))
                Debug.LogWarning("[CNCHueProjector] No TargetLight and no Mappings — nothing to project.");
            if (!string.IsNullOrWhiteSpace(UserApi))
                StartCoroutine(DiscoverBulbs());
        }

        /// <summary>The bulb IDs the bridge reports (for the Technical tab to display).</summary>
        public List<int> DiscoveredBulbs() => new List<int>(_discoveredBulbs);

        /// <summary>Names of the Unity Lights in the scene (for the Technical tab's mapping dropdowns).</summary>
        public List<string> GetSceneLightNames()
        {
            RefreshSceneLights();
            var names = new List<string>(_lightsByName.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public void SetMappings(List<CNCLightMap> maps)
        {
            Mappings = maps ?? new List<CNCLightMap>();
            RefreshSceneLights();
        }

        /// <summary>Re-query the bridge for its lights (e.g. after plugging one in).</summary>
        public void Rediscover()
        {
            RefreshSceneLights();
            if (!string.IsNullOrWhiteSpace(UserApi)) StartCoroutine(DiscoverBulbs());
        }

        private void RefreshSceneLights()
        {
            _lightsByName.Clear();
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l != null && !string.IsNullOrEmpty(l.name)) _lightsByName[l.name] = l;
        }

        private void Update()
        {
            if (!EnableProjection) return;
            if (_sending) return;                                   // never overlap sends (bridge floods = clunky)
            if (Time.time - _lastSendTime < MinSendInterval) return;

            var batch = new List<BulbState>();

            if (Mappings != null && Mappings.Count > 0)
            {
                // Explicit mapping: each Unity light mirrored to its own bulb.
                foreach (var m in Mappings)
                {
                    if (m == null || string.IsNullOrEmpty(m.lightName)) continue;
                    if (!_lightsByName.TryGetValue(m.lightName, out var light) || light == null) continue;
                    AddIfChanged(batch, m.bulbId, light);
                }
            }
            else if (TargetLight != null)
            {
                // Fallback: broadcast the one TargetLight to every resolved bulb.
                foreach (var id in ResolveBulbIds())
                    AddIfChanged(batch, id, TargetLight);
            }

            if (batch.Count == 0) return;
            _lastSendTime = Time.time;
            StartCoroutine(SendBatch(batch));
        }

        private void AddIfChanged(List<BulbState> batch, int bulbId, Light light)
        {
            var color = light.color;
            var bri = light.enabled ? Mathf.Clamp01(light.intensity / Mathf.Max(0.01f, IntensityForFullBrightness)) : 0f;

            _lastColorByBulb.TryGetValue(bulbId, out var lc);
            _lastBriByBulb.TryGetValue(bulbId, out var lb);
            var delta = Mathf.Abs(color.r - lc.r) + Mathf.Abs(color.g - lc.g) + Mathf.Abs(color.b - lc.b);
            if (delta < 0.008f && Mathf.Abs(bri - lb) < 0.008f) return;

            _lastColorByBulb[bulbId] = color;
            _lastBriByBulb[bulbId] = bri;
            batch.Add(new BulbState { bulbId = bulbId, color = color, bri = bri });
        }

        private System.Collections.IEnumerator SendBatch(List<BulbState> batch)
        {
            _sending = true;

            // Transition ≈ the send interval, so the bulb ramps smoothly between samples.
            var tt = Mathf.Max(1, Mathf.RoundToInt(MinSendInterval * 10f));
            var requests = new List<UnityWebRequest>();
            foreach (var st in batch)
            {
                Color.RGBToHSV(st.color, out var h, out var s, out _);
                var bri = Mathf.RoundToInt(st.bri * 254f);
                string body = bri > 0
                    ? "{\"on\":true,\"bri\":" + Mathf.Max(1, bri) +
                      ",\"hue\":" + Mathf.RoundToInt(h * 65535f) +
                      ",\"sat\":" + Mathf.RoundToInt(s * 254f) +
                      ",\"transitiontime\":" + tt + "}"
                    : "{\"on\":false,\"transitiontime\":" + tt + "}";

                var url = "http://" + BridgeIP + "/api/" + UserApi + "/lights/" + st.bulbId + "/state";
                var req = UnityWebRequest.Put(url, body);
                req.SetRequestHeader("Content-Type", "application/json");
                req.SendWebRequest();
                requests.Add(req);
            }

            // Wait for all in parallel (sequential round trips stalled the pulse).
            bool allDone = false;
            while (!allDone)
            {
                allDone = true;
                foreach (var req in requests)
                    if (!req.isDone) { allDone = false; break; }
                if (!allDone) yield return null;
            }
            foreach (var req in requests) req.Dispose();

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
