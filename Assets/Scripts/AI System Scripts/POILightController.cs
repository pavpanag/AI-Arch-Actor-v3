using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using LivePositions;

/// <summary>
/// POILightController
/// - Independent runtime component that watches a list of POIs (Transform + radius)
/// - Counts agents from PositionSampler and directly controls a Light or Renderer per-POI
/// - Simple fade on/off and optional color/brightness mapping
/// - Does not use any LLM or ConversationWithLight integration (keeps lighting deterministic)
/// </summary>
[DisallowMultipleComponent]
public class POILightController : MonoBehaviour
{
    [System.Serializable]
    public class POIEntry
    {
        public string id = "poi";
        public Transform target;
        [Tooltip("World-space radius to consider someone 'near' this POI")] public float radius = 1f;
        [Tooltip("Light to control when someone is near this POI. If empty, rendererEmission will be used.")] public Light lightToControl;
        [Tooltip("Optional Renderer whose emission color/intensity will be used when no Light is assigned.")] public Renderer rendererEmission;
        [Tooltip("When true the POI is considered active and will be evaluated")] public bool enabled = true;
        [Header("Behavior")]
        [Tooltip("Intensity value to use when at least one person is in the POI (Light.intensity scale)")] public float intensityOn = 1f;
        [Tooltip("Intensity value when no people are near")] public float intensityOff = 0f;
        [Tooltip("Seconds to fade between on/off intensities")][Min(0f)] public float fadeSeconds = 0.5f;
        [Tooltip("If true, set the light enabled state when intensity > 0.001")] public bool enableLightWhenOn = true;
        [Tooltip("Optional color to apply to the Light or Renderer when on")] public Color colorOn = Color.white;
    }

    [Header("POIs (add entries in the Inspector)")]
    public List<POIEntry> pois = new List<POIEntry>() { new POIEntry { id = "poi1" } };

    [Header("Sampling")]
    [Tooltip("Use extras reported by PositionSampler when counting agents")] public bool includeExtras = false;
    [Tooltip("How frequently (seconds) to recompute counts and update lights. Use low values for responsiveness.")] public float updateInterval = 0.1f;

    [Header("Debug / Visualization")]
    public bool drawGizmos = true;
    public Color gizmoColor = new Color(0f, 0.7f, 1f, 0.2f);

    // internals
    PositionSampler _sampler;
    List<Sample2D> _lastSamples = new List<Sample2D>();
    float[] _currentIntensities;

    void Awake()
    {
        _sampler = PositionSampler.Instance;
        if (_sampler != null)
            _sampler.OnSampled += OnSampled;

        if (pois == null) pois = new List<POIEntry>();
        _currentIntensities = new float[pois.Count];
        for (int i = 0; i < _currentIntensities.Length; i++) _currentIntensities[i] = 0f;
    }

    void OnEnable()
    {
        if (_sampler == null) _sampler = PositionSampler.Instance;
        if (_sampler != null)
            _sampler.OnSampled += OnSampled;
        StartCoroutine(UpdateLoop());
    }

    void OnDisable()
    {
        if (_sampler != null)
            _sampler.OnSampled -= OnSampled;
        StopAllCoroutines();
    }

    void OnValidate()
    {
        if (pois == null) pois = new List<POIEntry>();
        if (_currentIntensities == null || _currentIntensities.Length != pois.Count)
        {
            _currentIntensities = new float[pois.Count];
            for (int i = 0; i < _currentIntensities.Length; i++) _currentIntensities[i] = 0f;
        }
    }

    void OnSampled(List<Sample2D> samples)
    {
        _lastSamples.Clear();
        if (samples != null)
        {
            for (int i = 0; i < samples.Count; i++) _lastSamples.Add(samples[i]);
        }
    }

    IEnumerator UpdateLoop()
    {
        while (true)
        {
            UpdatePOIStates();
            yield return new WaitForSeconds(Mathf.Max(0.01f, updateInterval));
        }
    }

    void UpdatePOIStates()
    {
        // Ensure arrays match
        if (_currentIntensities == null || _currentIntensities.Length != pois.Count)
        {
            _currentIntensities = new float[pois.Count];
            for (int i = 0; i < _currentIntensities.Length; i++) _currentIntensities[i] = 0f;
        }

        int totalAgents = 0;
        foreach (var s in _lastSamples) if (includeExtras || !s.isExtra) totalAgents++;

        for (int i = 0; i < pois.Count; i++)
        {
            var p = pois[i];
            if (p == null || p.target == null || !p.enabled)
            {
                // fade to off
                UpdateIntensityForIndex(i, p != null ? p.intensityOff : 0f, p != null ? p.fadeSeconds : 0.2f);
                ApplyIntensityToOutput(i);
                continue;
            }

            int count = 0;
            Vector2 poiXZ = new Vector2(p.target.position.x, p.target.position.z);
            float r2 = p.radius * p.radius;
            foreach (var s in _lastSamples)
            {
                if (!includeExtras && s.isExtra) continue;
                float dx = s.xz.x - poiXZ.x;
                float dz = s.xz.y - poiXZ.y;
                if (dx * dx + dz * dz <= r2) count++;
            }

            float target = (count > 0) ? p.intensityOn : p.intensityOff;
            UpdateIntensityForIndex(i, target, p.fadeSeconds);
            ApplyIntensityToOutput(i);
        }
    }

    void UpdateIntensityForIndex(int index, float target, float fadeSeconds)
    {
        if (index < 0 || index >= _currentIntensities.Length) return;
        float current = _currentIntensities[index];
        if (fadeSeconds <= 0f)
        {
            _currentIntensities[index] = target;
            return;
        }
        float delta = Mathf.Abs(target - current);
        float maxDelta = (delta > 0f) ? (Mathf.Abs(target - current) / fadeSeconds) * Time.deltaTime : 0f;
        // Use MoveTowards for stable behaviour
        _currentIntensities[index] = Mathf.MoveTowards(current, target, Mathf.Abs(target - current) * (Time.deltaTime / Mathf.Max(0.0001f, fadeSeconds)));
    }

    void ApplyIntensityToOutput(int index)
    {
        if (index < 0 || index >= pois.Count) return;
        var p = pois[index];
        float intensity = _currentIntensities[index];
        if (p == null) return;

        if (p.lightToControl != null)
        {
            // Turn Light on/off and set intensity/color
            p.lightToControl.intensity = intensity;
            p.lightToControl.color = p.colorOn;
            if (p.enableLightWhenOn)
                p.lightToControl.enabled = intensity > 0.0005f;
        }
        else if (p.rendererEmission != null)
        {
            // If Renderer has a material with emission, set color/intensity on _EmissionColor if available
            var mat = p.rendererEmission.material;
            if (mat != null)
            {
                Color c = p.colorOn * Mathf.Clamp01(intensity);
                if (mat.HasProperty("_EmissionColor"))
                {
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", c);
                }
                else if (mat.HasProperty("_Color"))
                {
                    mat.SetColor("_Color", c);
                }
            }
        }
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos || pois == null) return;
        Gizmos.color = gizmoColor;
        for (int i = 0; i < pois.Count; i++)
        {
            var p = pois[i];
            if (p == null || p.target == null) continue;
            Vector3 pos = new Vector3(p.target.position.x, 0.01f, p.target.position.z);
            Gizmos.DrawSphere(pos, p.radius);
            UnityEditor.Handles.Label(pos + Vector3.up * 0.05f, $"{p.id} ({p.radius:F2})");
        }
    }

    [ContextMenu("Force Recompute Now")]
    public void ForceRecomputeNow()
    {
        UpdatePOIStates();
    }
}
