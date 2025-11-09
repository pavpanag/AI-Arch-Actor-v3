// Assets/Scripts/MinimapView.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using LivePositions;

/// <summary>
/// Lightweight UI minimap that listens to PositionSampler and draws moving dots as UI Images.
/// Attach this to a GameObject under a Canvas. Assign a RectTransform area (usually the same object)
/// and a small circular sprite for markers.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class MinimapView : MonoBehaviour
{
    [Header("References")]
    [Tooltip("PositionSampler to subscribe to. If left empty, will use PositionSampler.Instance at runtime.")]
    public PositionSampler sampler;

    [Tooltip("The RectTransform area that represents the map (normalized 0..1 across its width/height). If null, uses own RectTransform.")]
    public RectTransform mapArea;

    [Tooltip("UI Image prefab used for each dot. If null, a basic Image will be created at runtime.")]
    public Image markerPrefab;

    [Header("Appearance")]
    [Tooltip("Pixel size of each dot/marker.")]
    public float markerSize = 6f;

    [Tooltip("Optional background image for map; not required for functionality.")]
    public Image mapBackground;

    [Tooltip("If true and no background is assigned, a simple Image will be created behind the markers.")]
    public bool autoCreateBackground = true;

    [Tooltip("Color of the auto-created background (ignored if a background Image is assigned).")]
    public Color autoBackgroundColor = new Color(0f, 0f, 0f, 0.25f);

    [Header("Grid Overlay")]
    [Tooltip("Draw a simple grid overlay to make the map area clear.")]
    public bool showGrid = true;

    [Tooltip("Number of vertical divisions (columns). Lines = divisions + 1.")]
    public int gridDivisionsX = 10;

    [Tooltip("Number of horizontal divisions (rows). Lines = divisions + 1.")]
    public int gridDivisionsY = 10;

    [Tooltip("Grid line thickness in pixels.")]
    public float gridThickness = 1f;

    [Tooltip("Grid line color.")]
    public Color gridColor = new Color(1f, 1f, 1f, 0.25f);

    [Header("Debug")]
    [Tooltip("If true, draws a few test markers so you can confirm marker visibility and alignment even when no samples are received.")]
    public bool drawTestMarkers = false;

    [Tooltip("If true, prints sample counts and a few sample entries to the Console for debugging.")]
    public bool verboseLogging = false;

    [Tooltip("If true, force all agent markers to white (ignores sample color).")]
    public bool forceWhiteMarkers = false;

    [Tooltip("If true, attempt to use the agent's shared material color for the marker (falls back to sample.color). This looks up GameObjects by name, so agentId/name must match the GameObject name.")]
    public bool useMaterialColors = false;

    // cache material-derived colors by agent id (name)
    readonly Dictionary<string, Color> _materialColorCache = new Dictionary<string, Color>();

    [Header("Bounds (Simplified Mode)")]
    [Tooltip("If true, uses the fixed min/max below to map world XZ to the map, bypassing PositionSampler.worldMin/worldMax.")]
    public bool useFixedBounds = false;

    [Tooltip("World-space min (x,z). For a 10x10 floor starting at (0,0), set to (0,0). For a centered -5..+5, set to (-5,-5).")]
    public Vector2 fixedMin = new Vector2(0f, 0f);

    [Tooltip("World-space max (x,z). For a 10x10 floor starting at (0,0), set to (10,10). For a centered -5..+5, set to (5,5).")]
    public Vector2 fixedMax = new Vector2(10f, 10f);

    // Pool of UI markers reused each frame
    readonly List<Image> _pool = new List<Image>();
    RectTransform _rect;
    RectTransform _gridLayer;
    int _lastDivX = -1, _lastDivY = -1;
    float _lastThickness = -1f;
    Color _lastGridColor = new Color(0,0,0,0);
    bool _subscribed = false;

    void Awake()
    {
        _rect = GetComponent<RectTransform>();
        if (mapArea == null) mapArea = _rect;
        EnsureBackground();
        EnsureGrid();
    }

    void OnEnable()
    {
        if (sampler == null) sampler = PositionSampler.Instance;
        if (sampler != null && !_subscribed)
        {
            sampler.OnSampled += OnSampled;
            _subscribed = true;
        }
        EnsureGrid();
    }

    void OnDisable()
    {
        if (sampler != null && _subscribed)
        {
            sampler.OnSampled -= OnSampled;
            _subscribed = false;
        }
    }

    void Update()
    {
        // If the sampler wasn't available at OnEnable, try to find and subscribe to it now.
        if (!_subscribed)
        {
            if (sampler == null)
                sampler = PositionSampler.Instance;

            if (sampler != null)
            {
                sampler.OnSampled += OnSampled;
                _subscribed = true;
            }
        }
    }

    void OnSampled(List<Sample2D> samples)
    {
        if (samples == null || samples.Count == 0)
        {
            // Hide all markers
            for (int i = 0; i < _pool.Count; i++)
                _pool[i].gameObject.SetActive(false);

            if (drawTestMarkers)
                DrawTestMarkers();

            if (verboseLogging)
                Debug.Log("MinimapView: received 0 samples");

            return;
        }

        if (verboseLogging)
        {
            Debug.Log($"MinimapView: received {samples.Count} samples");
            for (int ii = 0; ii < Mathf.Min(3, samples.Count); ii++)
            {
                var s = samples[ii];
                Debug.Log($" sample[{ii}] id={s.id} x={s.xz.x:F2} z={s.xz.y:F2} color={s.color}");
            }
        }

        EnsurePool(samples.Count);

        // Convert each sample's world XZ to normalized 0..1 and then to anchored position in mapArea
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            Vector2 uv;
            if (useFixedBounds)
            {
                uv = WorldTo01Fixed(s.xz);
            }
            else if (sampler != null)
            {
                uv = sampler.WorldTo01(s.xz);
            }
            else
            {
                // Fallback if no sampler is available
                uv = new Vector2(0.5f, 0.5f);
            }
            Vector2 local = UVToLocal(uv);

            var img = _pool[i];
            if (!img.gameObject.activeSelf) img.gameObject.SetActive(true);
            var rt = img.rectTransform;
            rt.anchoredPosition = local;
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, markerSize);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, markerSize);
            // determine desired color: optionally use material color, then sample color, then forced white
            Color desired = s.color;
            if (useMaterialColors)
            {
                if (!_materialColorCache.TryGetValue(s.id, out desired))
                {
                    // attempt lookup by GameObject name
                    var go = GameObject.Find(s.id);
                    if (go != null)
                    {
                        if (TryGetMaterialColor(go, out var matC))
                        {
                            desired = matC;
                            _materialColorCache[s.id] = matC;
                        }
                        else
                        {
                            // cache negative result to avoid repeated lookups
                            _materialColorCache[s.id] = s.color;
                            desired = s.color;
                        }
                    }
                    else
                    {
                        desired = s.color;
                    }
                }
            }

            img.color = desired;
            if (forceWhiteMarkers) img.color = Color.white;
            if (verboseLogging)
            {
                Debug.Log($"MinimapView: sample id={s.id} sampleColor={s.color} desiredColor={desired} displayedColor={img.color}");
            }
            // Make sure markers are above background
            img.transform.SetAsLastSibling();
        }

        // Hide any unused pooled markers
        for (int i = samples.Count; i < _pool.Count; i++)
        {
            if (_pool[i].gameObject.activeSelf)
                _pool[i].gameObject.SetActive(false);
        }
    }

    void EnsureBackground()
    {
        if (mapBackground != null) return;
        if (!autoCreateBackground || mapArea == null) return;

        // Create a child Image that stretches to the map area
        var go = new GameObject("MinimapBackground", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(mapArea, false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;

        var img = go.GetComponent<Image>();
        img.raycastTarget = false;
        img.color = autoBackgroundColor;

        // Put it behind all other children
        go.transform.SetAsFirstSibling();
        mapBackground = img;
    }

    void EnsureGrid()
    {
        if (mapArea == null) return;

        if (!showGrid)
        {
            if (_gridLayer != null) _gridLayer.gameObject.SetActive(false);
            return;
        }

        if (_gridLayer == null)
        {
            var go = new GameObject("MinimapGrid", typeof(RectTransform));
            go.transform.SetParent(mapArea, false);
            _gridLayer = go.GetComponent<RectTransform>();
            _gridLayer.anchorMin = Vector2.zero;
            _gridLayer.anchorMax = Vector2.one;
            _gridLayer.pivot = new Vector2(0.5f, 0.5f);
            _gridLayer.anchoredPosition = Vector2.zero;
            _gridLayer.sizeDelta = Vector2.zero;
        }

        _gridLayer.gameObject.SetActive(true);

        // Ensure background is behind, grid above background
        if (mapBackground != null)
        {
            mapBackground.transform.SetAsFirstSibling();
            _gridLayer.SetSiblingIndex(1);
        }
        else
        {
            _gridLayer.SetAsFirstSibling();
        }

        RebuildOrRefreshGrid();
    }

    void RebuildOrRefreshGrid()
    {
        int divX = Mathf.Max(1, gridDivisionsX);
        int divY = Mathf.Max(1, gridDivisionsY);
        float thickness = Mathf.Max(0.5f, gridThickness);

        bool needsRebuild = (_gridLayer.childCount != (divX + 1) + (divY + 1))
                            || divX != _lastDivX || divY != _lastDivY;

        if (needsRebuild)
        {
            // Clear existing
            for (int i = _gridLayer.childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(_gridLayer.GetChild(i).gameObject);
            }

            // Vertical lines (x divisions)
            for (int i = 0; i <= divX; i++)
            {
                float t = divX == 0 ? 0f : (float)i / divX;
                CreateGridLine(vertical: true, t, thickness, gridColor, i);
            }

            // Horizontal lines (y divisions)
            for (int j = 0; j <= divY; j++)
            {
                float t = divY == 0 ? 0f : (float)j / divY;
                CreateGridLine(vertical: false, t, thickness, gridColor, j);
            }

            _lastDivX = divX;
            _lastDivY = divY;
        }
        else
        {
            // Refresh thickness/color
            for (int k = 0; k < _gridLayer.childCount; k++)
            {
                var line = _gridLayer.GetChild(k) as RectTransform;
                var img = line.GetComponent<Image>();
                if (img != null)
                {
                    img.color = gridColor;
                    bool isVertical = line.name.StartsWith("VLine");
                    if (isVertical)
                        line.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, thickness);
                    else
                        line.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, thickness);
                }
            }
        }

        _lastThickness = thickness;
        _lastGridColor = gridColor;
    }

    void CreateGridLine(bool vertical, float t, float thickness, Color color, int index)
    {
        var go = new GameObject(vertical ? $"VLine_{index}" : $"HLine_{index}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(_gridLayer, false);
        var rt = go.GetComponent<RectTransform>();
        var img = go.GetComponent<Image>();
        img.raycastTarget = false;
        img.color = color;

        if (vertical)
        {
            rt.anchorMin = new Vector2(t, 0f);
            rt.anchorMax = new Vector2(t, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(thickness, 0f);
        }
        else
        {
            rt.anchorMin = new Vector2(0f, t);
            rt.anchorMax = new Vector2(1f, t);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, thickness);
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        gridDivisionsX = Mathf.Max(1, gridDivisionsX);
        gridDivisionsY = Mathf.Max(1, gridDivisionsY);
        gridThickness = Mathf.Max(0.5f, gridThickness);
        if (isActiveAndEnabled)
        {
            EnsureBackground();
            EnsureGrid();
        }
    }
#endif

    Vector2 WorldTo01Fixed(Vector2 worldXZ)
    {
        float u = Mathf.InverseLerp(fixedMin.x, fixedMax.x, worldXZ.x);
        float v = Mathf.InverseLerp(fixedMin.y, fixedMax.y, worldXZ.y);
        return new Vector2(u, v);
    }

    Vector2 UVToLocal(Vector2 uv)
    {
        // Clamp to [0,1] to keep dots inside the map
        uv.x = Mathf.Clamp01(uv.x);
        uv.y = Mathf.Clamp01(uv.y);

        // Map to RectTransform's size with pivot in account
        var size = mapArea.rect.size;
        // We treat uv.y as bottom->top. In UI, anchoredPosition y increases up if pivot is center (default).
        Vector2 pos = new Vector2(uv.x * size.x, uv.y * size.y);
        // Shift for pivot (anchoredPosition is relative to pivot)
        pos -= Vector2.Scale(size, mapArea.pivot);
        return pos;
    }

    void DrawTestMarkers()
    {
        // Simple layout: four markers in each corner of the map area
        EnsurePool(4);
        Vector2[] corners = new Vector2[] { new Vector2(0,0), new Vector2(1,0), new Vector2(0,1), new Vector2(1,1) };
        Color[] cols = new Color[] { Color.green, Color.blue, Color.magenta, new Color(1f,0.6f,0f) };
        for (int i = 0; i < 4; i++)
        {
            var img = _pool[i];
            if (!img.gameObject.activeSelf) img.gameObject.SetActive(true);
            var rt = img.rectTransform;
            rt.anchoredPosition = UVToLocal(corners[i]);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, markerSize * 2f);
            rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, markerSize * 2f);
            img.color = forceWhiteMarkers ? Color.white : cols[i];
            img.transform.SetAsLastSibling();
        }
        // hide extras
        for (int i = 4; i < _pool.Count; i++)
            _pool[i].gameObject.SetActive(false);
    }

    bool TryGetMaterialColor(GameObject go, out Color c)
    {
        c = default;
        if (go == null) return false;

        var rend = go.GetComponentInChildren<Renderer>();
        if (rend == null) return false;

        var mat = rend.sharedMaterial;
        if (mat == null) return false;

        try
        {
            if (mat.HasProperty("_BaseColor")) { c = mat.GetColor("_BaseColor"); return true; }
            if (mat.HasProperty("_Color")) { c = mat.GetColor("_Color"); return true; }
            // common fallback - try mainColor-like names
            if (mat.HasProperty("_MainColor")) { c = mat.GetColor("_MainColor"); return true; }
        }
        catch { }

        return false;
    }

    void EnsurePool(int needed)
    {
        while (_pool.Count < needed)
        {
            _pool.Add(CreateMarker());
        }
    }

    Image CreateMarker()
    {
        Image prefab = markerPrefab;
        if (prefab == null)
        {
            // Create a basic Image if none provided
            var go = new GameObject("MinimapMarker", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            // Simple white square; user can assign a circular sprite in inspector later
            prefab = img;
        }

        // Instantiate under mapArea
        Image marker;
        if (prefab.transform.parent == null)
        {
            marker = Instantiate(prefab, mapArea);
        }
        else
        {
            // Avoid cloning existing instance in scene hierarchy; create a new GO with same settings
            var go = new GameObject("MinimapMarker", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            marker = go.GetComponent<Image>();
            marker.sprite = prefab.sprite;
            marker.type = prefab.type;
            marker.pixelsPerUnitMultiplier = prefab.pixelsPerUnitMultiplier;
            marker.raycastTarget = false;
            marker.transform.SetParent(mapArea, false);
        }

        var rt = marker.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.zero;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(markerSize, markerSize);

        // default marker color (will be overridden each sample); use white so markers are visible
        marker.color = Color.white;
        marker.gameObject.SetActive(false);
        return marker;
    }
}
