using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class HueUnityTest : MonoBehaviour
{
    [Header("Hue Bridge (v1 local API)")]
    public string bridgeIp = "192.168.1.85";
    public string username = "YOUR_HUE_USERNAME";
    public int lightId = 3;

    [Header("Test State")]
    public bool turnOn = true;
    [Range(1, 254)] public int brightness = 254;

    [Header("Runtime Options")]
    public bool applyOnChange = true;    // send when inspector values change
    public bool autoRepeat = false;      // periodically resend current state
    public float repeatInterval = 1f;    // seconds

    void Start()
    {
        StartCoroutine(SetLightState(lightId, turnOn, brightness));
        // initialize last-known values so Update-change detection works
        _lastBridgeIp = bridgeIp;
        _lastUsername = username;
        _lastLightId = lightId;
        _lastTurnOn = turnOn;
        _lastBrightness = brightness;
        _repeatTimer = repeatInterval;
    }

    // --- added fields for change detection / repeat ---
    string _lastBridgeIp;
    string _lastUsername;
    int _lastLightId;
    bool _lastTurnOn;
    int _lastBrightness;
    float _repeatTimer;
    bool _isSending = false;

    void Update()
    {
        // apply-on-change: detect inspector edits at runtime
        if (applyOnChange)
        {
            if (bridgeIp != _lastBridgeIp || username != _lastUsername || lightId != _lastLightId ||
                turnOn != _lastTurnOn || brightness != _lastBrightness)
            {
                _lastBridgeIp = bridgeIp;
                _lastUsername = username;
                _lastLightId = lightId;
                _lastTurnOn = turnOn;
                _lastBrightness = brightness;

                if (!_isSending) StartCoroutine(SetLightState(lightId, turnOn, brightness));
            }
        }

        // auto-repeat: periodic resend
        if (autoRepeat)
        {
            _repeatTimer -= Time.deltaTime;
            if (_repeatTimer <= 0f)
            {
                _repeatTimer = Mathf.Max(0.01f, repeatInterval);
                if (!_isSending) StartCoroutine(SetLightState(lightId, turnOn, brightness));
            }
        }
    }

    IEnumerator SetLightState(int id, bool on, int bri)
    {
        _isSending = true;

        string url = $"http://{bridgeIp}/api/{username}/lights/{id}/state";
        string json = $"{{\"on\":{on.ToString().ToLower()},\"bri\":{bri}}}";

        using (var req = new UnityWebRequest(url, "PUT"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            Debug.Log($"Hue PUT -> {url}\nBody: {json}");

#if UNITY_2020_1_OR_NEWER
            yield return req.SendWebRequest();
            bool ok = req.result == UnityWebRequest.Result.Success;
#else
            yield return req.Send();
            bool ok = !(req.isNetworkError || req.isHttpError);
#endif

            Debug.Log($"Hue response ok={ok} status={req.responseCode}\n{req.downloadHandler.text}");

            if (!ok)
            {
                Debug.LogError($"Hue error: {req.error}");
            }
        }

        _isSending = false;
    }
}
