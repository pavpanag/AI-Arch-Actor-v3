using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class HueController : MonoBehaviour
{
    [Header("Hue Bridge")]
    [Tooltip("IP address of the Philips Hue bridge")]
    public string bridgeIP = "192.168.1.106";
    [Tooltip("User API token (whitelist)")]
    public string userApi = "";
    [Tooltip("Bulb id to control (the numeric id in Hue)")]
    public int bulbId = 1;

    [Header("Unity source (optional)")]
    [Tooltip("If set and 'useUnityLightColor' is enabled, this Light's color is sent to Hue")]
    public Light unityLight;

    [Header("Target Color (when not sampling unityLight)")]
    public Color targetColor = Color.white;

    [Header("Runtime Options (TestHue-style)")]
    [Tooltip("Send once on Start.")]
    public bool sendOnStart = true;

    [Tooltip("If true, send when inspector values change (bridge config / targetColor / toggles).")]
    public bool applyOnChange = true;

    [Tooltip("If true, periodically re-sends the last state while scene runs.")]
    public bool autoRepeat = false;

    [Tooltip("Seconds between periodic re-sends.")]
    public float repeatInterval = 1f;

    [Tooltip("Minimum seconds between PUT requests (prevents spamming / overlap issues).")]
    public float minSendInterval = 0.08f;

    [Tooltip("If true, continuously samples unityLight.color and sends when it changes.")]
    public bool useUnityLightColor = false;

    [Header("Debug")]
    public bool verbose = true;

    // ---- URL normalization ----
    string BridgeBaseUrl
    {
        get
        {
            var ip = (bridgeIP ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(ip)) return string.Empty;
            if (ip.StartsWith("http://") || ip.StartsWith("https://")) return ip.TrimEnd('/');
            return $"http://{ip}".TrimEnd('/');
        }
    }
    string UserApiTrimmed => (userApi ?? string.Empty).Trim();
    string BuildUrl(string relativePath) => $"{BridgeBaseUrl}/{relativePath.TrimStart('/')}";

    // ---- runtime state ----
    bool _isSending;
    bool _sendQueued;
    float _repeatTimer;
    float _nextAllowedSendTime;

    // last-known values for change detection
    string _lastBridgeIP;
    string _lastUserApi;
    int _lastBulbId;
    bool _lastUseUnityLightColor;
    Color _lastTargetColor;
    Color _lastUnityLightColor;

    void Start()
    {
        _lastBridgeIP = bridgeIP;
        _lastUserApi = userApi;
        _lastBulbId = bulbId;
        _lastUseUnityLightColor = useUnityLightColor;
        _repeatTimer = repeatInterval;

        _lastTargetColor = targetColor;
        _lastUnityLightColor = unityLight != null ? unityLight.color : Color.black;

        // If sampling is enabled, initialize targetColor from unityLight (optional but practical)
        if (useUnityLightColor && unityLight != null)
            targetColor = unityLight.color;

        if (sendOnStart)
            QueueSend();
    }

    void Update()
    {
        // Optional continuous sampling of a Unity Light
        if (useUnityLightColor && unityLight != null)
        {
            var c = unityLight.color;
            if (c != _lastUnityLightColor)
            {
                _lastUnityLightColor = c;
                targetColor = c; // drive Hue from unityLight
                QueueSend();
            }
        }

        // apply-on-change: detect inspector edits at runtime
        if (applyOnChange)
        {
            if (bridgeIP != _lastBridgeIP || userApi != _lastUserApi || bulbId != _lastBulbId ||
                useUnityLightColor != _lastUseUnityLightColor || targetColor != _lastTargetColor)
            {
                _lastBridgeIP = bridgeIP;
                _lastUserApi = userApi;
                _lastBulbId = bulbId;
                _lastUseUnityLightColor = useUnityLightColor;
                _lastTargetColor = targetColor;

                QueueSend();
            }
        }

        // auto-repeat: periodic resend
        if (autoRepeat)
        {
            _repeatTimer -= Time.deltaTime;
            if (_repeatTimer <= 0f)
            {
                _repeatTimer = Mathf.Max(0.01f, repeatInterval);
                QueueSend();
            }
        }

        TrySendIfDue();
    }

    void QueueSend()
    {
        // Coalesce triggers; Update() will enforce throttling
        _sendQueued = true;
    }

    void TrySendIfDue()
    {
        if (!_sendQueued) return;
        if (_isSending) return;

        if (minSendInterval > 0f && Time.unscaledTime < _nextAllowedSendTime)
            return;

        _sendQueued = false;
        _nextAllowedSendTime = Time.unscaledTime + Mathf.Max(0f, minSendInterval);

        StartCoroutine(SendColorToHue(targetColor));
    }

    IEnumerator SendColorToHue(Color color)
    {
        _isSending = true;

        if (string.IsNullOrEmpty(BridgeBaseUrl) || string.IsNullOrWhiteSpace(UserApiTrimmed) || bulbId <= 0)
        {
            Debug.LogError("HueController: invalid bridgeIP/userApi/bulbId.");
            _isSending = false;
            yield break;
        }

        Color.RGBToHSV(color, out float h, out float s, out float v);

        int hueVal = Mathf.Clamp(Mathf.RoundToInt(h * 65535f), 0, 65535);
        int satVal = Mathf.Clamp(Mathf.RoundToInt(s * 254f), 0, 254);
        int briVal = Mathf.Clamp(Mathf.RoundToInt(v * 254f), 0, 254);

        string url = BuildUrl($"/api/{UserApiTrimmed}/lights/{bulbId}/state");
        string json = (briVal > 0)
            ? $"{{\"on\":true,\"bri\":{briVal},\"hue\":{hueVal},\"sat\":{satVal}}}"
            : "{\"on\":false}";

        if (verbose) Debug.Log($"HueController: PUT {url} payload={json}");

        using (var req = new UnityWebRequest(url, "PUT"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw) { contentType = "application/json" };
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "application/json");
            req.useHttpContinue = false;
            req.timeout = 10;

#if UNITY_2020_1_OR_NEWER
            yield return req.SendWebRequest();
            bool isError = req.result != UnityWebRequest.Result.Success;
#else
            yield return req.Send();
            bool isError = req.isNetworkError || req.isHttpError;
#endif

            string respText = req.downloadHandler != null ? req.downloadHandler.text : "<no body>";
            if (isError) Debug.LogWarning($"HueController: request failed: error={req.error} responseCode={req.responseCode} body={respText}");
            else if (verbose) Debug.Log($"HueController: request success responseCode={req.responseCode} body={respText}");
        }

        _isSending = false;

        // if something changed while we were sending, Update() will queue again via applyOnChange / sampling / repeat
    }
}
