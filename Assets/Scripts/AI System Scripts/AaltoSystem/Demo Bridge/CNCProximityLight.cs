using System.Collections.Generic;
using UnityEngine;

namespace CNCDemo
{
    /// <summary>
    /// A light that answers presence: put this on (or next to) any object of interest, give it a
    /// Light, and when a tracked person comes within Radius the light fades up; when they leave,
    /// it fades out. Watches the "TrackedPerson_XX" agents that ReceivePosition spawns from the
    /// camera tracker — plus anything you drag into AlsoWatch (a player rig, a test cube...).
    ///
    /// Entirely in-Unity: no OSC, no network, no coupling. If tracking is off it simply stays dark.
    /// </summary>
    public sealed class CNCProximityLight : MonoBehaviour
    {
        [Header("Trigger")]
        [Tooltip("How close (meters, in the floor plane) someone must come to wake the light.")]
        public float Radius = 2f;
        [Tooltip("Objects whose names start with this are watched — ReceivePosition names its spawns this way.")]
        public string WatchNamePrefix = "TrackedPerson_";
        [Tooltip("Extra transforms to watch besides the tracked people (optional).")]
        public List<Transform> AlsoWatch = new List<Transform>();

        [Header("Light")]
        [Tooltip("The Light to drive. Blank = the first Light on this object or its children.")]
        public Light TargetLight;
        public Color OnColor = new Color(1f, 0.72f, 0.35f);   // warm ember
        [Range(0f, 12f)] public float OnIntensity = 3f;
        [Tooltip("How fast the light fades in/out, per second (1 = one second full fade).")]
        [Range(0.2f, 10f)] public float FadeSpeed = 3f;

        [Header("Also glow a material (optional)")]
        [Tooltip("Renderer whose emission should light up with the same color — makes the OBJECT itself glow, " +
                 "not just cast light. Leave empty to drive only the Light.")]
        public Renderer GlowRenderer;

        [Header("Status (read-only)")]
        public bool SomeoneClose;
        public string NearestName;

        private readonly List<Transform> _watched = new List<Transform>();
        private float _level;          // 0..1 current fade level
        private float _nextScan;
        private MaterialPropertyBlock _mpb;
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        private void OnEnable()
        {
            if (TargetLight == null) TargetLight = GetComponentInChildren<Light>();
            if (TargetLight != null)
            {
                TargetLight.color = OnColor;
                TargetLight.intensity = 0f;
                TargetLight.enabled = false;

                // Warn if the Hue projector is mirroring this same Light — both would write it
                // every frame and the color would fight. Give this proximity light its OWN Light.
                foreach (var proj in FindObjectsByType<CNCHueProjector>(FindObjectsSortMode.None))
                    if (proj != null && proj.TargetLight == TargetLight)
                        Debug.LogWarning("[CNCProximityLight] This Light is also driven by CNCHueProjector on '" +
                            proj.name + "'. They will fight over its color. Point the projector at a different " +
                            "Light, or give this proximity trigger its own dedicated Light.");
            }
            if (GlowRenderer != null && GlowRenderer.sharedMaterial != null)
                GlowRenderer.sharedMaterial.EnableKeyword("_EMISSION");
        }

        private void Update()
        {
            // Rescan for tracked people once a second — they spawn/despawn as the camera sees them.
            if (Time.time >= _nextScan)
            {
                _nextScan = Time.time + 1f;
                RefreshWatched();
            }

            // Nearest watched thing, measured in the floor plane.
            float best = float.MaxValue;
            Transform nearest = null;
            var here = transform.position;
            foreach (var t in _watched)
            {
                if (t == null) continue;
                var d = t.position - here;
                var planar = new Vector2(d.x, d.z).sqrMagnitude;
                if (planar < best) { best = planar; nearest = t; }
            }

            SomeoneClose = nearest != null && best <= Radius * Radius;
            NearestName = nearest != null ? nearest.name : "";

            // Fade toward presence.
            var target = SomeoneClose ? 1f : 0f;
            _level = Mathf.MoveTowards(_level, target, FadeSpeed * Time.deltaTime);

            if (TargetLight != null)
            {
                TargetLight.color = OnColor;
                TargetLight.intensity = _level * OnIntensity;
                TargetLight.enabled = _level > 0.01f;
            }

            // Optionally glow the object's own material (emission), via a property block so we
            // never create per-instance material leaks.
            if (GlowRenderer != null)
            {
                _mpb ??= new MaterialPropertyBlock();
                GlowRenderer.GetPropertyBlock(_mpb);
                _mpb.SetColor(EmissionColorId, OnColor * (_level * Mathf.Max(0.01f, OnIntensity)));
                GlowRenderer.SetPropertyBlock(_mpb);
            }
        }

        private void RefreshWatched()
        {
            _watched.Clear();
            if (!string.IsNullOrEmpty(WatchNamePrefix))
            {
                // Tracked people carry NavMeshAgents (ReceivePosition spawns them), so this scan
                // is narrow — we never walk the whole hierarchy.
                foreach (var nav in FindObjectsByType<UnityEngine.AI.NavMeshAgent>(FindObjectsSortMode.None))
                    if (nav != null && nav.name.StartsWith(WatchNamePrefix, System.StringComparison.Ordinal))
                        _watched.Add(nav.transform);
            }
            foreach (var t in AlsoWatch)
                if (t != null) _watched.Add(t);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.72f, 0.35f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, Radius);
        }
    }
}
