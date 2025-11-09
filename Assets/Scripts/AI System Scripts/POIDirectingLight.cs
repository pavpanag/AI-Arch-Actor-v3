using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using LivePositions;

/// <summary>
/// POIDirectingLight
/// - Deterministic director that applies simple "Directing" instructions to lights based on POI proximity
/// - Does NOT use the LLM or ConversationWithLight. Keeps behavior local and predictable.
/// - Supported instruction (natural language-ish):
///   "If anyone is within the radius of poi1, turn the light for poi1 on; otherwise turn it off."
///   Variants that mention a POI id (poi1, poi2...) are supported. If parsing fails, the controller falls back
///   to default behaviour: each POI's light is ON when at least one agent is in radius, OFF otherwise.
/// </summary>
[DisallowMultipleComponent]
public class POIDirectingLight : MonoBehaviour
{
    [Serializable]
    public class POIEntry
    {
        public string id = "poi";
        public Transform target;
        public float radius = 1f;
        [Header("Light Output")]
        public Light sceneLight;
        [Tooltip("Default hue for ON state (0-360)")] public int hueOn = 0;
        [Tooltip("Default brightness for ON state (0-100)")] public int brightnessOn = 80;
        [Tooltip("Default saturation for ON state (0-100)")] public int saturationOn = 90;
        [Tooltip("Turn light on when a person is detected (if true) - default behavior")]
        public bool defaultOnWhenPresent = true;
    }

    [Header("POIs")]
    public List<POIEntry> pois = new List<POIEntry>() { new POIEntry { id = "poi1" } };

    [Header("Instruction")]
    [TextArea(2,4)]
    [Tooltip("Directing instruction (strict directorial phrasing). Example: 'If anyone is within the radius of poi1, turn the light for poi1 on; otherwise turn it off.'")]
    public string directingInstruction = "";

    [Header("Sampling")]
    [Tooltip("Use extras reported by PositionSampler when counting agents")] public bool includeExtras = false;
    [Tooltip("How often to evaluate the instruction (seconds)")] public float evaluateInterval = 0.2f;

    // internals
    PositionSampler _sampler;
    List<Sample2D> _lastSamples = new List<Sample2D>();

    // compiled instruction representation
    // supports simple ON/OFF rules targeted at a POI id
    struct ParsedRule { public string poiId; public bool onWhenTrue; public bool offWhenFalse; public bool valid; }

    void Awake()
    {
        _sampler = PositionSampler.Instance;
        if (_sampler != null) _sampler.OnSampled += OnSampled;
        NormalizePOIIds();
    }

    void OnEnable()
    {
        if (_sampler == null) _sampler = PositionSampler.Instance;
        if (_sampler != null) _sampler.OnSampled += OnSampled;
        StartCoroutine(EvalLoop());
    }

    void OnDisable()
    {
        if (_sampler != null) _sampler.OnSampled -= OnSampled;
        StopAllCoroutines();
    }

    void OnValidate()
    {
        NormalizePOIIds();
    }

    void NormalizePOIIds()
    {
        if (pois == null) pois = new List<POIEntry>();
        for (int i = 0; i < pois.Count; i++)
        {
            if (string.IsNullOrEmpty(pois[i].id)) pois[i].id = "poi" + (i + 1);
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

    IEnumerator EvalLoop()
    {
        while (true)
        {
            EvaluateInstruction();
            yield return new WaitForSeconds(Mathf.Max(0.01f, evaluateInterval));
        }
    }

    void EvaluateInstruction()
    {
        if (pois == null || pois.Count == 0) return;

        // Try to parse the directing instruction into a simple rule
        ParsedRule rule = ParseInstruction(directingInstruction);

        if (rule.valid)
        {
            // find the poi with that id
            for (int i = 0; i < pois.Count; i++)
            {
                var p = pois[i];
                if (p == null || p.target == null) continue;
                bool isTarget = string.Equals(p.id, rule.poiId, StringComparison.OrdinalIgnoreCase);
                if (!isTarget) continue;
                int count = CountAgentsInPOI(p);
                bool condTrue = count > 0;
                bool shouldOn = condTrue ? rule.onWhenTrue : !rule.offWhenFalse ? false : false;
                // If rule says turn on when true we apply ON; else OFF
                ApplyDirectOnOff(p, condTrue ? rule.onWhenTrue : !rule.offWhenFalse ? false : false);
            }
        }
        else
        {
            // fallback: for each POI, if anyone inside -> on, else off (or use defaultOnWhenPresent)
            for (int i = 0; i < pois.Count; i++)
            {
                var p = pois[i];
                if (p == null || p.target == null) continue;
                int count = CountAgentsInPOI(p);
                bool shouldBeOn = (count > 0) ? p.defaultOnWhenPresent : !p.defaultOnWhenPresent ? false : false;
                ApplyDirectOnOff(p, shouldBeOn);
            }
        }
    }

    int CountAgentsInPOI(POIEntry p)
    {
        if (p == null || p.target == null) return 0;
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
        return count;
    }

    void ApplyDirectOnOff(POIEntry p, bool on)
    {
        if (p == null) return;
        if (p.sceneLight == null) return;
        p.sceneLight.enabled = on;
        if (!on) return;

        // set color and intensity based on POI defaults
        p.sceneLight.color = Color.HSVToRGB(Mathf.Clamp01(p.hueOn / 360f), Mathf.Clamp01(p.saturationOn / 100f), 1f);
        float norm = Mathf.Clamp01(p.brightnessOn / 100f);
        p.sceneLight.intensity = Mathf.Lerp(0f, 10f, norm);
    }

    ParsedRule ParseInstruction(string text)
    {
        var parsed = new ParsedRule { valid = false, poiId = null, onWhenTrue = false, offWhenFalse = false };
        if (string.IsNullOrWhiteSpace(text)) return parsed;

        // Normalize whitespace and lower-case for matching
        string t = text.Trim();

        // Try pattern: If anyone .* poiX .* turn .* on .* otherwise .* off
        // We'll look for a poi id like 'poi' followed by digits
        var mPoi = Regex.Match(t, @"poi\s*\d+", RegexOptions.IgnoreCase);
        if (!mPoi.Success) return parsed;
        string poiId = mPoi.Value.Replace(" ", "").ToLower();

        // Determine actions for true/false by searching for 'on' and 'off' near clauses
        bool onWhenTrue = Regex.IsMatch(t, @"on", RegexOptions.IgnoreCase) && Regex.IsMatch(t, @"if.*poi", RegexOptions.IgnoreCase);
        bool offWhenFalse = Regex.IsMatch(t, @"off", RegexOptions.IgnoreCase) && Regex.IsMatch(t, @"otherwise|else", RegexOptions.IgnoreCase);

        // Basic sanity: if we found poi and saw 'on' somewhere, consider it valid
        if (mPoi.Success && (onWhenTrue || offWhenFalse))
        {
            parsed.valid = true;
            parsed.poiId = poiId;
            parsed.onWhenTrue = onWhenTrue;
            parsed.offWhenFalse = offWhenFalse;
        }

        return parsed;
    }

    [ContextMenu("Force Evaluate Now")]
    public void ForceEvaluateNow()
    {
        EvaluateInstruction();
    }
}
