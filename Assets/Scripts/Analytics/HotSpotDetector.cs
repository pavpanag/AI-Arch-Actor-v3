using System;
using System.Collections.Generic;
using UnityEngine;
using LivePositions; // for PositionSampler and Sample2D

/// <summary>
/// Runtime hotspot detector that accumulates where agents tend to go.
/// - Maintains a decaying heat grid over a bounded XZ area
/// - Adjustable sensitivity (decay half-life, splat radius, resolution)
/// - Logs top hotspots to Console and exposes values in Inspector
/// Attach this to any GameObject in the scene. If Sampler is left empty, it will use PositionSampler.Instance.
/// </summary>
[DisallowMultipleComponent]
public class HotSpotDetector : MonoBehaviour
{
    [Header("Sampler Reference")]
    [Tooltip("If left empty, will use PositionSampler.Instance in the scene.")]
    public PositionSampler sampler;

    [Header("Bounds (XZ)")]
    [Tooltip("If true, use PositionSampler.worldMin/worldMax. If false, use fixedMin/fixedMax. Useful if you want to force a fixed 10x10 area.")]
    public bool useSamplerBounds = true;

    [Tooltip("World-space min (x,z) used when useSamplerBounds = false (lower-left corner of the tracked area)")] 
    public Vector2 fixedMin = new Vector2(-5f, -5f); // default to a 10x10 centered area

    [Tooltip("World-space max (x,z) used when useSamplerBounds = false (upper-right corner of the tracked area)")] 
    public Vector2 fixedMax = new Vector2(5f, 5f);

    [Header("Grid Settings")]
    [Tooltip("Number of cells in X (columns). Higher = finer resolution but more memory/compute.")] 
    [Range(4, 512)] public int gridX = 50;
    [Tooltip("Number of cells in Y (rows). Higher = finer resolution but more memory/compute.")] 
    [Range(4, 512)] public int gridY = 50;

    [Header("Accumulation/Sensitivity")]
    [Tooltip("Base weight deposited per second of presence (scaled by the sampling dt). If set to 1 and no decay and no splat, values approximate agent-seconds in cell.")] 
    public float weightPerSecond = 1f;

    [Tooltip("Half-life (seconds) for exponential decay. Lower = more sensitive to recent visits. 0 = no decay. Example: 10s means old contributions halve every 10 seconds.")]
    [Min(0f)] public float decayHalfLife = 10f;

    [Tooltip("Extra spread in cells for each sample. 0 = deposit to a single cell only. Use >0 to smooth heat across neighbors.")] 
    [Range(0, 10)] public int splatRadiusCells = 1;

    [Tooltip("Gaussian sigma (in cells) used when splatRadiusCells > 0. Lower sigma concentrates near center; higher spreads more.")] 
    [Range(0.1f, 5f)] public float splatSigma = 1f;

    [Tooltip("Include extras (lights/markers) reported by PositionSampler. Useful if you want to count static objects or event markers.")] 
    public bool includeExtras = false;

    [Tooltip("Clamp out-of-bounds samples to the nearest grid edge. If false, samples outside bounds are ignored.")]
    public bool clampToBounds = true;

    [Header("Output")]
    [Tooltip("How many hotspots to report when logging. Top K highest-value cells after normalization.")]
    [Range(1, 50)] public int topK = 5;

    [Tooltip("Minimum separation between reported hotspots (world units). Prevents listing several neighbouring cells as separate peaks.")]
    [Min(0f)] public float minPeakSeparation = 1.0f;

    public enum NormalizationMode { None, SecondsPerAgent, FractionOfAgentTime }

    [Header("Normalization")]
    [Tooltip("How to interpret reported cell values.\nNone = raw accumulated value.\nSecondsPerAgent = estimated agent-seconds in cell (divide by total agents sampled).\nFractionOfAgentTime = fraction of total sampled agent-time spent in cell (0..1).")]
    public NormalizationMode normalization = NormalizationMode.None;

    // Internal tracking to support normalization
    double _totalAgentSampledSeconds = 0.0; // sum of dt for each agent sample seen (approx)
    int _lastSampleAgentCount = 0;

    [Tooltip("If true, draws Gizmos for heat and peaks in Scene view (editor only)")]
    public bool drawGizmos = true;

    [Tooltip("Gizmo intensity scale for drawing squares")]
    [Range(0.1f, 5f)] public float gizmoIntensityScale = 1f;
    [Tooltip("Gizmo sample height above ground for drawing cubes/spheres in Scene view")]
    [Range(0f, 2f)] public float gizmoHeight = 0.05f;
    [Tooltip("If true, draw hotspot labels (value) in Scene view")]
    public bool drawGizmoLabels = true;

    [Header("Runtime Visualization (Game View)")]
    [Tooltip("If true, show simple runtime markers (small GameObjects) at top hotspots during Play mode.")]
    public bool showRuntimeMarkers = false;

    [Tooltip("Prefab used to mark hotspot positions. If null, a small sphere will be created.")]
    public GameObject peakMarkerPrefab;

    [Tooltip("Height above ground to place runtime markers (world units)")]
    public float peakMarkerHeight = 0.05f;

    [Tooltip("Scale factor applied to the marker relative to a single grid cell size")]
    [Range(0.05f, 5f)] public float peakMarkerScale = 0.6f;

    [Tooltip("Color tint for generated markers (prefab color is used if provided)")]
    public Color peakMarkerColor = Color.yellow;

    // runtime marker cache
    List<GameObject> _peakMarkers = new List<GameObject>();

    // Internal state
    float[,] _heat;
    float _lastSampleTime;
    bool _subscribed;

    // Cached gaussian kernel for splatting
    float[,] _kernel;
    int _kernelRadiusCached = -1;
    float _kernelSigmaCached = -1f;

    // For quick inspector view
    [SerializeField, ReadOnlyIfInspector] float _currentMax;
    [SerializeField, ReadOnlyIfInspector] float _currentMin;
    [SerializeField] List<Hotspot> _lastTopPeaks = new List<Hotspot>();

    void Awake()
    {
        AllocateGridIfNeeded();
    }

    void OnEnable()
    {
        if (sampler == null) sampler = PositionSampler.Instance;
        Subscribe();
    }

    void OnDisable()
    {
        Unsubscribe();
    }

    void OnValidate()
    {
        gridX = Mathf.Max(4, gridX);
        gridY = Mathf.Max(4, gridY);
        if (!Application.isPlaying)
        {
            AllocateGridIfNeeded(force: true);
        }
    }

    void Subscribe()
    {
        if (_subscribed) return;
        if (sampler == null) return;
        sampler.OnSampled += OnSampled;
        _subscribed = true;
        _lastSampleTime = Time.time;
    }

    void Unsubscribe()
    {
        if (!_subscribed) return;
        if (sampler != null)
            sampler.OnSampled -= OnSampled;
        _subscribed = false;
    }

    void Update()
    {
        if (!_subscribed)
        {
            if (sampler == null) sampler = PositionSampler.Instance;
            Subscribe();
        }

        // runtime visualization: spawn/destroy markers when enabled (Play mode only)
        if (Application.isPlaying && showRuntimeMarkers)
            UpdateRuntimeMarkers();
    }

    void UpdateRuntimeMarkers()
    {
        // compute peaks now
        _lastTopPeaks = GetTopHotspots(topK, minPeakSeparation);

        GetBounds(out var min, out var max);
        float width = Mathf.Max(1e-5f, max.x - min.x);
        float height = Mathf.Max(1e-5f, max.y - min.y);
        int nx = Mathf.Max(1, gridX);
        int ny = Mathf.Max(1, gridY);

        // ensure pool size
        for (int i = 0; i < _lastTopPeaks.Count; i++)
        {
            if (i >= _peakMarkers.Count)
            {
                GameObject go;
                if (peakMarkerPrefab != null)
                {
                    go = Instantiate(peakMarkerPrefab);
                }
                else
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    var col = go.GetComponent<Collider>();
                    if (col != null) Destroy(col);
                }
                go.name = "HotspotMarker" + _peakMarkers.Count;
                go.hideFlags = HideFlags.DontSave;
                _peakMarkers.Add(go);
            }

            var marker = _peakMarkers[i];
            var hp = _lastTopPeaks[i];

            float cellSizeX = width / nx;
            float cellSizeY = height / ny;
            float markerScale = Mathf.Min(cellSizeX, cellSizeY) * peakMarkerScale;

            marker.transform.position = new Vector3(hp.worldXZ.x, peakMarkerHeight, hp.worldXZ.y);
            marker.transform.localScale = Vector3.one * markerScale;
            marker.SetActive(true);

            // color the marker if there's a Renderer and we generated it
            var rend = marker.GetComponent<Renderer>();
            if (rend != null)
            {
                if (peakMarkerPrefab == null)
                {
                    // for generated spheres: create a simple material instance
                    if (rend.sharedMaterial == null)
                        rend.sharedMaterial = new Material(Shader.Find("Standard"));
                    rend.sharedMaterial.color = peakMarkerColor;
                }
            }
        }

        // disable extras
        for (int i = _lastTopPeaks.Count; i < _peakMarkers.Count; i++)
            if (_peakMarkers[i] != null) _peakMarkers[i].SetActive(false);
    }

    [ContextMenu("Clear Runtime Peak Markers")]
    public void ClearRuntimeMarkers()
    {
        for (int i = 0; i < _peakMarkers.Count; i++)
            if (_peakMarkers[i] != null) Destroy(_peakMarkers[i]);
        _peakMarkers.Clear();
    }

    void AllocateGridIfNeeded(bool force = false)
    {
        if (_heat == null || force || _heat.GetLength(0) != gridX || _heat.GetLength(1) != gridY)
        {
            _heat = new float[gridX, gridY];
            _currentMax = 0f;
            _currentMin = 0f;
        }
    }

    // Public access to heat (read-only copy warning: returns reference)
    public float[,] Heat => _heat;

    // Compute world bounds currently used
    void GetBounds(out Vector2 min, out Vector2 max)
    {
        if (useSamplerBounds && sampler != null)
        {
            min = sampler.worldMin;
            max = sampler.worldMax;
        }
        else
        {
            min = fixedMin;
            max = fixedMax;
        }
    }

    // Main sampling callback
    void OnSampled(List<Sample2D> samples)
    {
        if (_heat == null || _heat.GetLength(0) != gridX || _heat.GetLength(1) != gridY)
            AllocateGridIfNeeded(force: true);

        float now = Time.time;
        float dt = Mathf.Max(0f, now - _lastSampleTime);
        _lastSampleTime = now;

    // 1) Global decay
        if (decayHalfLife > 0f && dt > 0f)
        {
            float lambda = Mathf.Log(2f) / decayHalfLife; // per second
            float factor = Mathf.Exp(-lambda * dt);
            int nx = _heat.GetLength(0);
            int ny = _heat.GetLength(1);
            for (int iy = 0; iy < ny; iy++)
            {
                for (int ix = 0; ix < nx; ix++)
                {
                    _heat[ix, iy] *= factor;
                }
            }
        }

    if (samples == null || samples.Count == 0) { UpdateMinMax(); return; }

        // 2) Deposit per agent sample
        GetBounds(out var min, out var max);
        float width = Mathf.Max(1e-5f, max.x - min.x);
        float height = Mathf.Max(1e-5f, max.y - min.y);

        // Prepare kernel if needed
        EnsureKernel();

    float deposit = weightPerSecond * (dt > 0f ? dt : (sampler != null ? 1f / Mathf.Max(1f, sampler.sampleHz) : Time.deltaTime));

    // track agent-sampled seconds for normalization: each sample represents ~dt seconds for each agent included
    int countedAgents = 0;

        // Filter what to include
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (!includeExtras && s.isExtra)
                continue;

            countedAgents++;

            float u = (s.xz.x - min.x) / width;
            float v = (s.xz.y - min.y) / height;

            if (clampToBounds)
            {
                u = Mathf.Clamp01(u);
                v = Mathf.Clamp01(v);
            }
            else
            {
                if (u < 0f || u > 1f || v < 0f || v > 1f) continue; // skip out of bounds
            }

            int cx = Mathf.Clamp(Mathf.FloorToInt(u * gridX), 0, gridX - 1);
            int cy = Mathf.Clamp(Mathf.FloorToInt(v * gridY), 0, gridY - 1);

            if (splatRadiusCells <= 0)
            {
                _heat[cx, cy] += deposit;
            }
            else
            {
                int r = splatRadiusCells;
                for (int dy = -r; dy <= r; dy++)
                {
                    int yy = cy + dy;
                    if (yy < 0 || yy >= gridY) continue;
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int xx = cx + dx;
                        if (xx < 0 || xx >= gridX) continue;
                        float w = _kernel[dx + r, dy + r];
                        _heat[xx, yy] += deposit * w;
                    }
                }
            }
        }

        // update normalization totals
        if (dt > 0f && countedAgents > 0)
        {
            _totalAgentSampledSeconds += (double)dt * (double)countedAgents;
            _lastSampleAgentCount = countedAgents;
        }

        UpdateMinMax();
    }

    void EnsureKernel()
    {
        if (splatRadiusCells <= 0)
        {
            _kernel = null;
            _kernelRadiusCached = -1;
            _kernelSigmaCached = -1f;
            return;
        }
        if (_kernel != null && _kernelRadiusCached == splatRadiusCells && Mathf.Approximately(_kernelSigmaCached, splatSigma))
            return;

        int r = splatRadiusCells;
        _kernel = new float[2 * r + 1, 2 * r + 1];
        float twoSigma2 = 2f * splatSigma * splatSigma;
        float sum = 0f;
        for (int y = -r; y <= r; y++)
        {
            for (int x = -r; x <= r; x++)
            {
                float d2 = x * x + y * y;
                float val = Mathf.Exp(-d2 / twoSigma2);
                _kernel[x + r, y + r] = val;
                sum += val;
            }
        }
        // Normalize kernel to 1
        if (sum > 1e-6f)
        {
            for (int y = 0; y < 2 * r + 1; y++)
                for (int x = 0; x < 2 * r + 1; x++)
                    _kernel[x, y] /= sum;
        }
        _kernelRadiusCached = r;
        _kernelSigmaCached = splatSigma;
    }

    void UpdateMinMax()
    {
        float minv = float.MaxValue;
        float maxv = float.MinValue;
        int nx = _heat.GetLength(0);
        int ny = _heat.GetLength(1);
        for (int y = 0; y < ny; y++)
        {
            for (int x = 0; x < nx; x++)
            {
                float v = _heat[x, y];
                if (v < minv) minv = v;
                if (v > maxv) maxv = v;
            }
        }
        if (minv == float.MaxValue) minv = 0f;
        if (maxv == float.MinValue) maxv = 0f;
        _currentMin = minv;
        _currentMax = maxv;
    }

    [ContextMenu("Log Top Hotspots")] 
    public void LogTopHotspots()
    {
        var peaks = GetTopHotspots(topK, minPeakSeparation);
        if (peaks.Count == 0)
        {
            Debug.Log("[HotSpotDetector] No hotspots found yet.");
            return;
        }
        Debug.Log($"[HotSpotDetector] Top {peaks.Count} hotspots (value, worldXZ):");
        for (int i = 0; i < peaks.Count; i++)
        {
            var p = peaks[i];
            Debug.Log($"  #{i+1}: {p.value:F3} @ ({p.worldXZ.x:F2}, {p.worldXZ.y:F2})");
        }
    }

    [Serializable]
    public struct Hotspot
    {
        public int ix, iy;
        public float value;
        public Vector2 worldXZ;
    }

    public List<Hotspot> GetTopHotspots(int k, float minSeparationWorld)
    {
        List<Hotspot> result = new List<Hotspot>();
        if (_heat == null) return result;

        GetBounds(out var min, out var max);
        float width = Mathf.Max(1e-5f, max.x - min.x);
        float height = Mathf.Max(1e-5f, max.y - min.y);

        int nx = _heat.GetLength(0);
        int ny = _heat.GetLength(1);

        // Flatten into candidates and apply normalization mapping to an interpreted `valueForRanking`
        List<(int x, int y, float v)> cand = new List<(int, int, float)>(nx * ny);
        for (int y = 0; y < ny; y++)
        {
            for (int x = 0; x < nx; x++)
            {
                float raw = _heat[x, y];
                float interpreted = raw;
                if (normalization == NormalizationMode.SecondsPerAgent)
                {
                    // estimate seconds per agent: total agent-seconds portion in this cell divided by approximate agent count
                    if (_totalAgentSampledSeconds > 1e-9)
                        interpreted = (float)(raw * (_totalAgentSampledSeconds > 0 ? 1.0f : 1.0f));
                    else
                        interpreted = raw;
                }
                else if (normalization == NormalizationMode.FractionOfAgentTime)
                {
                    if (_totalAgentSampledSeconds > 1e-9)
                        interpreted = (float)(raw / (float)_totalAgentSampledSeconds);
                    else
                        interpreted = 0f;
                }
                cand.Add((x, y, interpreted));
            }
        }
        cand.Sort((a, b) => b.v.CompareTo(a.v));

        float minSepCellsX = (minSeparationWorld <= 0f) ? 0f : (minSeparationWorld * nx / width);
        float minSepCellsY = (minSeparationWorld <= 0f) ? 0f : (minSeparationWorld * ny / height);

        for (int i = 0; i < cand.Count && result.Count < k; i++)
        {
            var c = cand[i];
            if (c.v <= 0f) break;
            bool farEnough = true;
            for (int j = 0; j < result.Count; j++)
            {
                float dx = (c.x - result[j].ix) / Mathf.Max(1f, minSepCellsX);
                float dy = (c.y - result[j].iy) / Mathf.Max(1f, minSepCellsY);
                if ((dx * dx + dy * dy) < 1f) // within min sep ellipse
                {
                    farEnough = false;
                    break;
                }
            }
            if (!farEnough) continue;

            Vector2 worldXZ = new Vector2(
                min.x + (c.x + 0.5f) / nx * width,
                min.y + (c.y + 0.5f) / ny * height
            );
            // For display, include the raw stored value, but `value` will hold the interpreted (normalized) value
            result.Add(new Hotspot { ix = c.x, iy = c.y, value = c.v, worldXZ = worldXZ });
        }
        return result;
    }

    [ContextMenu("Compute Top Hotspots (Inspector)")]
    public void ComputeTopHotspotsInspector()
    {
        _lastTopPeaks = GetTopHotspots(topK, minPeakSeparation);
    }

    [ContextMenu("Clear Heatmap")]
    public void ClearHeatmap()
    {
        if (_heat == null) return;
        int nx = _heat.GetLength(0);
        int ny = _heat.GetLength(1);
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
                _heat[x, y] = 0f;
        UpdateMinMax();
        _lastTopPeaks.Clear();
    }

    [ContextMenu("Print Parameter Explanations")]
    public void PrintParameterExplanations()
    {
        Debug.Log("HotSpotDetector parameter explanations:\n" +
            "Sampler: Reference to PositionSampler. Leave empty to auto-use PositionSampler.Instance.\n" +
            "useSamplerBounds: If true uses PositionSampler.worldMin/worldMax; otherwise uses fixedMin/fixedMax.\n" +
            "fixedMin/fixedMax: World XZ corners used when not using sampler bounds (lower-left and upper-right). For a centered 10x10 floor use (-5,-5) to (5,5).\n" +
            "gridX/gridY: Grid resolution (columns/rows). More cells = finer heatmap but more CPU/memory.\n" +
            "weightPerSecond: How much " +
            "weight each agent deposits per second in their current cell. Setting to 1 with no decay approximates agent-seconds.\n" +
            "decayHalfLife: Exponential half-life in seconds; lower values make the map emphasize recent activity. 0 disables decay.\n" +
            "splatRadiusCells/splatSigma: Spread each deposit to neighboring cells using a Gaussian kernel (radius in cells, sigma controls spread).\n" +
            "includeExtras: Count PositionSampler extras (lights/markers) as samples.\n" +
            "clampToBounds: Clamp out-of-bounds samples into the edge cell; otherwise samples outside the defined bounds are ignored.\n" +
            "normalization: None (raw), SecondsPerAgent (interpreted occupancy in agent-seconds), FractionOfAgentTime (0..1 fraction of total sampled agent-time).\n" +
            "topK/minPeakSeparation: Controls how many hotspots are reported and how close reported peaks can be.\n" +
            "drawGizmos/gizmoIntensityScale: Visual debugging options to draw heat and peaks in the Scene view.");
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!drawGizmos || _heat == null) return;
        GetBounds(out var min, out var max);
        float width = Mathf.Max(1e-5f, max.x - min.x);
        float height = Mathf.Max(1e-5f, max.y - min.y);

        int nx = _heat.GetLength(0);
        int ny = _heat.GetLength(1);
        float maxv = Mathf.Max(1e-6f, _currentMax);

        // Draw cells as squares with intensity
        for (int y = 0; y < ny; y++)
        {
            for (int x = 0; x < nx; x++)
            {
                float v = _heat[x, y] / maxv * gizmoIntensityScale;
                v = Mathf.Clamp01(v);
                if (v <= 0f) continue;
                Vector3 center = new Vector3(
                    min.x + (x + 0.5f) / nx * width,
                    gizmoHeight,
                    min.y + (y + 0.5f) / ny * height
                );
                // use a slightly thicker cube so it is visible above flat geometry
                Vector3 size = new Vector3(width / nx, Mathf.Max(0.01f, gizmoHeight), height / ny);
                Gizmos.color = new Color(1f, 0f, 0f, v * 0.6f);
                Gizmos.DrawCube(center, size);
            }
        }

        // Draw top peaks
        var peaks = GetTopHotspots(Mathf.Min(topK, 10), minPeakSeparation);
        Gizmos.color = Color.yellow;
        foreach (var p in peaks)
        {
            Vector3 pos = new Vector3(p.worldXZ.x, Mathf.Max(0.01f, gizmoHeight + 0.01f), p.worldXZ.y);
            Gizmos.DrawSphere(pos, Mathf.Min(width, height) / Mathf.Max(nx, ny) * 0.5f);
            if (drawGizmoLabels)
            {
                UnityEditor.Handles.color = Color.yellow;
                UnityEditor.Handles.Label(pos + Vector3.up * 0.02f, $"{p.value:F3}");
            }
        }
    }
#endif
}

// Utility attribute to render a read-only field in Inspector without custom editor
// Note: purely cosmetic; safe to leave if not needed.
public class ReadOnlyIfInspectorAttribute : PropertyAttribute { }

#if UNITY_EDITOR
[UnityEditor.CustomPropertyDrawer(typeof(ReadOnlyIfInspectorAttribute))]
public class ReadOnlyIfInspectorDrawer : UnityEditor.PropertyDrawer
{
    public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label)
    {
        bool prev = GUI.enabled;
        GUI.enabled = false;
        UnityEditor.EditorGUI.PropertyField(position, property, label, true);
        GUI.enabled = prev;
    }
}
#endif
