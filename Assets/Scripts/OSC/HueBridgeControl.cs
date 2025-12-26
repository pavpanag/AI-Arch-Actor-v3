using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;

public class HueBridgeControl : MonoBehaviour
{
    [Header("Bridge (default set for your request)")]
    public string bridgeIP = "192.168.1.85";
    [Tooltip("Hue API user token / whitelist key")]
    public string userApi = "";

    [Header("Light")]
    [Tooltip("Numeric bulb id as reported by the bridge")]
    public int bulbId = 1;

    [Header("Color (Philips Hue ranges)")]
    [Range(0, 65535)] public int hue = 0;
    [Range(0, 254)] public int sat = 254;
    [Range(0, 254)] public int bri = 254;
    public bool lightOn = true;

    [Header("Optional Unity light to sample color from")]
    public Light unityLight;

    [Header("Debug")]
    public bool verbose = true;

    [Header("Runtime Options")]
    public bool sendOnStart = true;
    public bool applyOnChange = true;
    public bool autoRepeat = false;
    public float repeatInterval = 1f;
    public float minSendInterval = 0.08f;
    public bool useUnityLightColor = false;

    // Normalize input so Inspector can contain either "192.168.1.85" or "http://192.168.1.85"
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

    string BuildUrl(string relativePath)
        => $"{BridgeBaseUrl}/{relativePath.TrimStart('/')}";

    // No runtime OnGUI. Use inspector to set fields and these context-menu methods to invoke.
    [ContextMenu("Send State to Bridge")]
    public void SendState()
    {
        if (ValidateConfig()) StartCoroutine(SendStateCoroutine());
    }

    [ContextMenu("Get State from Bridge")]
    public void GetState()
    {
        if (ValidateConfig()) StartCoroutine(GetStateCoroutine());
    }

    [ContextMenu("Test Flash")]
    public void TestFlash()
    {
        if (ValidateConfig()) StartCoroutine(TestFlashCoroutine());
    }

    [ContextMenu("Sample Unity Light Color")]
    public void SampleUnityLightContext()
    {
        SampleUnityLight();
    }

    bool ValidateConfig()
    {
        // ContextMenu is often clicked in Edit Mode; coroutines/UnityWebRequest won't run there.
        if (!Application.isPlaying)
        {
            Debug.LogError("[HueBridgeControl] Enter Play Mode before using these ContextMenu actions (Unity coroutines/web requests won't run in Edit Mode).");
            return false;
        }

        if (string.IsNullOrWhiteSpace(bridgeIP))
        {
            Debug.LogError("Bridge IP is empty.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(UserApiTrimmed))
        {
            Debug.LogError("User API token is empty. Paste the Hue whitelist token (same one your Python script uses).");
            return false;
        }

        if (bulbId <= 0)
        {
            Debug.LogError("Invalid bulbId. Must be > 0.");
            return false;
        }

        if (string.IsNullOrEmpty(BridgeBaseUrl))
        {
            Debug.LogError("Bridge base URL could not be built. Check bridgeIP.");
            return false;
        }

        return true;
    }

    void SampleUnityLight()
    {
        if (unityLight == null)
        {
            Debug.LogWarning("unityLight is not assigned.");
            return;
        }
        Color c = unityLight.color.linear;
        Color.RGBToHSV(c, out float h01, out float s01, out float v01);
        hue = Mathf.Clamp(Mathf.RoundToInt(h01 * 65535f), 0, 65535);
        sat = Mathf.Clamp(Mathf.RoundToInt(s01 * 254f), 0, 254);
        bri = Mathf.Clamp(Mathf.RoundToInt(v01 * 254f), 0, 254);
        lightOn = unityLight.enabled;
        if (verbose) Debug.Log($"Sampled unityLight '{unityLight.name}' -> RGB({c.r:F2},{c.g:F2},{c.b:F2}) HSV({h01:F3},{s01:F3},{v01:F3}) -> hue={hue} sat={sat} bri={bri} on={lightOn}");
    }

    // runtime state for throttling / change-detection
    bool _isSending = false;
    bool _sendQueued = false;
    float _repeatTimer = 0f;
    float _nextAllowedSendTime = 0f;

    // last-known values
    string _lastBridgeIP;
    string _lastUserApi;
    int _lastBulbId;
    int _lastHue;
    int _lastSat;
    int _lastBri;
    bool _lastLightOn;
    Color _lastUnityColor;

    void Start()
    {
        // ...existing init if any...

        _lastBridgeIP = bridgeIP;
        _lastUserApi = userApi;
        _lastBulbId = bulbId;
        _lastHue = hue;
        _lastSat = sat;
        _lastBri = bri;
        _lastLightOn = lightOn;
        _repeatTimer = repeatInterval;
        _lastUnityColor = unityLight != null ? unityLight.color : Color.black;

        if (useUnityLightColor && unityLight != null)
        {
            SampleUnityLight();
        }

        if (sendOnStart)
            QueueSend();
    }

    void Update()
    {
        // optional continuous sampling of unityLight
        if (useUnityLightColor && unityLight != null)
        {
            var c = unityLight.color;
            if (c != _lastUnityColor)
            {
                _lastUnityColor = c;
                SampleUnityLight(); // updates hue/sat/bri/lightOn
                QueueSend();
            }
        }

        // detect inspector/runtime changes
        if (applyOnChange)
        {
            if (bridgeIP != _lastBridgeIP || userApi != _lastUserApi || bulbId != _lastBulbId ||
                hue != _lastHue || sat != _lastSat || bri != _lastBri || lightOn != _lastLightOn)
            {
                _lastBridgeIP = bridgeIP;
                _lastUserApi = userApi;
                _lastBulbId = bulbId;
                _lastHue = hue;
                _lastSat = sat;
                _lastBri = bri;
                _lastLightOn = lightOn;
                QueueSend();
            }
        }

        // auto-repeat
        if (autoRepeat)
        {
            _repeatTimer -= Time.deltaTime;
            if (_repeatTimer <= 0f)
            {
                _repeatTimer = Mathf.Max(0.01f, repeatInterval);
                QueueSend();
            }
        }

        // driving sends from Update to avoid overlapping coroutines
        TrySendIfDue();
    }

    void QueueSend()
    {
        _sendQueued = true;
    }

    void TrySendIfDue()
    {
        if (!_sendQueued || _isSending) return;
        if (minSendInterval > 0f && Time.unscaledTime < _nextAllowedSendTime) return;

        _sendQueued = false;
        _nextAllowedSendTime = Time.unscaledTime + Mathf.Max(0f, minSendInterval);
        StartCoroutine(SendStateCoroutine());
    }

    IEnumerator SendStateCoroutine()
    {
        // mark sending so Update doesn't start another
        _isSending = true;

        string url = BuildUrl($"/api/{UserApiTrimmed}/lights/{bulbId}/state");
        string json = lightOn
            ? $"{{\"on\":true,\"bri\":{bri},\"hue\":{hue},\"sat\":{sat}}}"
            : "{\"on\":false}";

        if (verbose) Debug.Log($"[HueBridgeControl] PUT {url} payload={json}");
        if (verbose) Debug.Log($"[HueBridgeControl] Sending HTTP PUT to bridge {BridgeBaseUrl} for bulb {bulbId}. Content-Type=application/json");

        using (var req = new UnityWebRequest(url, "PUT"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw)
            {
                contentType = "application/json"
            };
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

            string resp = req.downloadHandler != null ? req.downloadHandler.text : "<no body>";

            if (isError)
            {
                Debug.LogWarning($"[HueBridgeControl] PUT failed: error={req.error} code={req.responseCode} body={resp}");
            }
            else
            {
                if (verbose) Debug.Log($"[HueBridgeControl] PUT success code={req.responseCode} body={resp}");
                if (!string.IsNullOrEmpty(resp) && resp.Contains("\"error\""))
                    Debug.LogError($"[HueBridgeControl] Hue API returned an error: {resp}");
            }
        }

        _isSending = false;
    }

    IEnumerator GetStateCoroutine()
    {
        string url = BuildUrl($"/api/{UserApiTrimmed}/lights/{bulbId}");
        if (verbose) Debug.Log($"[HueBridgeControl] GET {url}");

        using (var req = UnityWebRequest.Get(url))
        {
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
            string resp = req.downloadHandler != null ? req.downloadHandler.text : "<no body>";

            if (isError)
            {
                Debug.LogWarning($"[HueBridgeControl] GET failed: error={req.error} code={req.responseCode} body={resp}");
            }
            else
            {
                Debug.Log($"[HueBridgeControl] GET success code={req.responseCode} body={resp}");
                if (!string.IsNullOrEmpty(resp))
                {
                    if (resp.Contains("\"state\""))
                        Debug.Log("[HueBridgeControl] GET response contains 'state' — bridge reachable and responded.");
                    else if (resp.Contains("\"error\""))
                        Debug.LogError($"[HueBridgeControl] GET response contains error: {resp}");
                }
            }
        }
    }

    IEnumerator TestFlashCoroutine()
    {
        // Simple test: turn on -> wait -> turn off -> restore
        bool prevOn = lightOn;
        int prevBri = bri;
        int prevHue = hue;
        int prevSat = sat;

        // send bright red
        hue = 0; sat = 254; bri = 254; lightOn = true;
        yield return StartCoroutine(SendStateCoroutine());

        yield return new WaitForSeconds(0.7f);

        // send green
        hue = 21845; sat = 254; bri = 200; lightOn = true; // ~120deg
        yield return StartCoroutine(SendStateCoroutine());

        yield return new WaitForSeconds(0.7f);

        // restore
        hue = prevHue; sat = prevSat; bri = prevBri; lightOn = prevOn;
        yield return StartCoroutine(SendStateCoroutine());
    }
}
