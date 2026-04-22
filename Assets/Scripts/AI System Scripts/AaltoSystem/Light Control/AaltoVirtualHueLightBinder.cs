using UnityEngine;

/// <summary>
/// Attach to a Unity Light to mirror one Hue slot from AaltoHueMirrorOscReceiver.
/// </summary>
[RequireComponent(typeof(Light))]
public class AaltoVirtualHueLightBinder : MonoBehaviour
{
    [Header("Binding")]
    public AaltoHueMirrorOscReceiver receiver;
    [Tooltip("Hue logical slot to follow (1..8 in current Python controller mapping).")]
    public int slotId = 1;
    public bool autoFindReceiver = true;

    [Header("Light Mapping")]
    [Tooltip("Multiplies brightness mapping to Unity intensity.")]
    public float intensityMultiplier = 4.0f;
    [Tooltip("Clamp maximum Unity intensity.")]
    public float maxIntensity = 8.0f;
    [Tooltip("If true, disable Light component when incoming brightness is 0.")]
    public bool disableWhenOff = true;

    [Header("Debug")]
    public bool verbose = false;

    private Light targetLight;
    private int lastBri = -1;
    private int lastHue = -1;
    private int lastSat = -1;

    private void Awake()
    {
        targetLight = GetComponent<Light>();
    }

    private void Start()
    {
        ResolveReceiver();
    }

    private void Update()
    {
        ResolveReceiver();
        if (receiver == null) return;

        if (!receiver.TryGetState(slotId, out var state)) return;
        if (state.brightness == lastBri && state.hue == lastHue && state.saturation == lastSat) return;

        lastBri = state.brightness;
        lastHue = state.hue;
        lastSat = state.saturation;

        ApplyState(state.brightness, state.hue, state.saturation);
    }

    private void ResolveReceiver()
    {
        if (receiver == null && autoFindReceiver)
            receiver = FindFirstObjectByType<AaltoHueMirrorOscReceiver>();
    }

    private void ApplyState(int bri, int hue, int sat)
    {
        float h = Mathf.Clamp01(hue / 65535f);
        float s = Mathf.Clamp01(sat / 254f);
        float v = Mathf.Clamp01(bri / 254f);

        var color = Color.HSVToRGB(h, s, v);
        targetLight.color = color;

        float mappedIntensity = Mathf.Clamp(v * intensityMultiplier, 0f, Mathf.Max(0f, maxIntensity));
        targetLight.intensity = mappedIntensity;

        if (disableWhenOff)
            targetLight.enabled = bri > 0;
        else
            targetLight.enabled = true;

        if (verbose)
            Debug.Log($"[AaltoVirtualHueLightBinder] slot={slotId} bri={bri} hue={hue} sat={sat} -> intensity={mappedIntensity:F2}");
    }
}
