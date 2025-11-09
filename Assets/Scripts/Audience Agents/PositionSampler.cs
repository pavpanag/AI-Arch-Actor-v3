// Assets/Scripts/PositionSampler.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace LivePositions
{
    [Serializable]
    public struct Sample2D
    {
        public string id;
        public Vector2 xz;   // world XZ sampled (with optional smoothing)
        public Color color;
        public bool isExtra; // true for extras like lights/markers
    }

    [Serializable]
    public struct LoggedSample
    {
        public int uid;    // stable numeric id within run
        public float t;
        public string id;
        public Vector2 xz;
        public bool isExtra;
        public Color color; // color at sample time
        public string colorTag; // optional human-readable color/material name
    }

    [DisallowMultipleComponent]
    public class PositionSampler : MonoBehaviour
    {
        public static PositionSampler Instance { get; private set; }

        [Header("World bounds for mapping (XZ)")]
        [Tooltip("If set, bounds are taken from this BoxCollider AABB")]
        public BoxCollider boundsFromCollider;
        public Vector2 worldMin = new Vector2(-10, -10);
        public Vector2 worldMax = new Vector2(10, 10);

        [Header("Sampling")]
        [Tooltip("Samples per second")]
        public float sampleHz = 10f;
        [Range(0f, 1f), Tooltip("EMA smoothing. 0 no smoothing, 1 frozen")]
        public float smoothingAlpha = 0.2f;

        [Header("Who to track")]
        [Tooltip("Auto-filled at runtime by agents registering themselves")]
        public List<TrackedAgent> trackedAgents = new List<TrackedAgent>();

        [Tooltip("Optional extras you also want drawn and logged")]
        public List<Transform> extraObjects = new List<Transform>();
        public Color extraColor = new Color(1f, 0.85f, 0.2f);

        [Header("Rolling log")]
        [Tooltip("Keep only the last N samples in memory")]
        public int maxLogEntries = 20000;

        public event Action<List<Sample2D>> OnSampled;

        readonly Dictionary<string, Vector2> _ema = new Dictionary<string, Vector2>();
    readonly Dictionary<string, int> _uids = new Dictionary<string, int>();
    int _nextUid = 1;
        readonly List<LoggedSample> _log = new List<LoggedSample>();
        float _accum;
        Bounds _worldBounds;

        public IReadOnlyList<LoggedSample> Log => _log;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("Multiple PositionSampler instances found. Destroying extra PositionSampler on " + gameObject.name);
                Destroy(gameObject);
                return;
            }
            Instance = this;
            RecomputeBounds();
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void Start()
        {
            // Safety net for agents already active before sampler initialized
            var existing = FindObjectsByType<TrackedAgent>(FindObjectsSortMode.None);
            foreach (var a in existing)
                RegisterAgent(a);
        }

        void Update()
        {
            _accum += Time.deltaTime;
            float interval = 1f / Mathf.Max(1f, sampleHz);
            while (_accum >= interval)
            {
                _accum -= interval;
                TickSample();
            }
        }

        public void RegisterAgent(TrackedAgent agent)
        {
            if (agent == null) return;
            if (!trackedAgents.Contains(agent))
                trackedAgents.Add(agent);
        }

        public void UnregisterAgent(TrackedAgent agent)
        {
            if (agent == null) return;
            trackedAgents.Remove(agent);
            // attempt to remove any EMA cache keyed by this agent's id/name
            var id = agent != null && !string.IsNullOrEmpty(agent.agentId) ? agent.agentId : (agent != null ? agent.gameObject.name : null);
            if (!string.IsNullOrEmpty(id))
                _ema.Remove(id);

            // remove UID mapping (keyed by instance id)
            var uidKey = AgentKey(agent);
            _uids.Remove(uidKey);
        }

        public void RegisterExtra(Transform t)
        {
            if (t == null) return;
            if (!extraObjects.Contains(t))
                extraObjects.Add(t);
        }

        public void UnregisterExtra(Transform t)
        {
            if (t == null) return;
            extraObjects.Remove(t);
            // remove EMA cache for this extra (use same key used during sampling)
            var key = ExtraKey(t);
            _ema.Remove(key);
            _uids.Remove(key);
        }

        public void RecomputeBounds()
        {
            if (boundsFromCollider != null)
            {
                _worldBounds = boundsFromCollider.bounds;
                worldMin = new Vector2(_worldBounds.min.x, _worldBounds.min.z);
                worldMax = new Vector2(_worldBounds.max.x, _worldBounds.max.z);
            }
            else
            {
                _worldBounds = new Bounds(
                    new Vector3((worldMin.x + worldMax.x) * 0.5f, 0f, (worldMin.y + worldMax.y) * 0.5f),
                    new Vector3(Mathf.Abs(worldMax.x - worldMin.x), 1f, Mathf.Abs(worldMax.y - worldMin.y))
                );
            }
        }

        void TickSample()
        {
            var outList = new List<Sample2D>(trackedAgents.Count + extraObjects.Count);

            // Agents
            for (int i = trackedAgents.Count - 1; i >= 0; i--)
            {
                var a = trackedAgents[i];
                if (a == null) { trackedAgents.RemoveAt(i); continue; }

                string id = string.IsNullOrEmpty(a.agentId) ? a.gameObject.name : a.agentId;
                Vector2 raw = new Vector2(a.transform.position.x, a.transform.position.z);
                Vector2 filtered = ApplyEMA(id, raw);
                int uid = (a.numericId > 0) ? a.numericId : GetOrAssignUid(AgentKey(a));

                var color = a.mapColor;
                outList.Add(new Sample2D { id = id, xz = filtered, color = color, isExtra = false });
                AppendLog(id, filtered, false, uid, color, a.colorTag);
            }

            // Extras
            foreach (var tr in extraObjects)
            {
                if (tr == null) continue;

                string id = tr.name;
                Vector2 raw = new Vector2(tr.position.x, tr.position.z);
                var emaKey = ExtraKey(tr);
                Vector2 filtered = ApplyEMA(emaKey, raw);
                int uid = GetOrAssignUid(emaKey);

                var colorE = extraColor;
                outList.Add(new Sample2D { id = id, xz = filtered, color = colorE, isExtra = true });
                AppendLog(id, filtered, true, uid, colorE, null);
            }

            OnSampled?.Invoke(outList);
        }

        Vector2 ApplyEMA(string id, Vector2 current)
        {
            if (smoothingAlpha <= 0f) return current;

            if (_ema.TryGetValue(id, out var prev))
            {
                float a = Mathf.Clamp01(smoothingAlpha);
                var val = a * prev + (1f - a) * current;
                _ema[id] = val;
                return val;
            }
            else
            {
                _ema[id] = current;
                return current;
            }
        }

        string ExtraKey(Transform t)
        {
            // Stable unique key per Transform instance for EMA map
            return "extra:" + t.GetInstanceID().ToString();
        }

        void AppendLog(string id, Vector2 xz, bool isExtra, int uid, Color color, string colorTag)
        {
            _log.Add(new LoggedSample { uid = uid, t = Time.time, id = id, xz = xz, isExtra = isExtra, color = color, colorTag = colorTag });
            if (_log.Count > maxLogEntries)
            {
                int remove = _log.Count - maxLogEntries;
                _log.RemoveRange(0, remove);
            }
        }

        public Vector2 WorldTo01(Vector2 worldXZ)
        {
            float u = Mathf.InverseLerp(worldMin.x, worldMax.x, worldXZ.x);
            float v = Mathf.InverseLerp(worldMin.y, worldMax.y, worldXZ.y);
            return new Vector2(u, v);
        }

        string AgentKey(TrackedAgent a)
        {
            return "agent:" + a.GetInstanceID().ToString();
        }

        int GetOrAssignUid(string key)
        {
            if (_uids.TryGetValue(key, out int val)) return val;
            int assigned = _nextUid++;
            _uids[key] = assigned;
            return assigned;
        }
    }
}
