using System.Collections;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

public class HueLightSampler : MonoBehaviour
{
	[Header("Hue Bridge")]
	public string bridgeIP = "192.168.1.85";
	[Tooltip("Hue API user token / whitelist key")]
	public string userApi = "";

	[Header("Target Hue Lights")]
	public bool useLight1 = true;
	public bool useLight2 = false;
	public bool useLight3 = false;
	public bool useLight4 = false;
	public bool useLight5 = false;
	public bool useLight6 = false;
	public bool useLight7 = false;
	public bool useLight8 = false;

	[Header("Unity Light to sample")]
	public Light unityLight;
	[Tooltip("If true, sample linear color (recommended). If false, sample gamma-space color.")]
	public bool sampleLinearColor = true;
	[Tooltip("Unity intensity that maps to Hue full brightness (254). Increase if your lights use >1 intensity.")]
	public float maxUnityIntensity = 1f;

	[Header("Runtime Options")]
	public bool sendOnStart = true;
	public bool applyOnChange = true;
	public bool autoRepeat = false;
	public float repeatInterval = 1f;
	public float minSendInterval = 0.08f;

	[Header("Debug")]
	public bool verbose = true;

	// URL helpers
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

	// runtime state
	bool _isSending;
	bool _sendQueued;
	float _repeatTimer;
	float _nextAllowedSendTime;

	// last-known sampled values for change detection
	Color _lastColor = Color.black;
	float _lastIntensity = -1f;
	bool _lastOn = false;

	void Start()
	{
		_repeatTimer = repeatInterval;
		if (unityLight != null)
		{
			_lastColor = GetSampledColor();
			_lastIntensity = GetSampledIntensity();
			_lastOn = unityLight.enabled;
		}

		if (sendOnStart)
			QueueSend();
	}

	void Update()
	{
		// continuous sampling and change detection
		if (unityLight != null)
		{
			Color c = GetSampledColor();
			float inten = GetSampledIntensity();
			bool on = unityLight.enabled;

			if (applyOnChange && (c != _lastColor || Mathf.Abs(inten - _lastIntensity) > 1e-4f || on != _lastOn))
			{
				_lastColor = c;
				_lastIntensity = inten;
				_lastOn = on;
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

		TrySendIfDue();
	}

	Color GetSampledColor()
	{
		if (unityLight == null) return Color.black;
		return sampleLinearColor ? unityLight.color.linear : unityLight.color;
	}

	float GetSampledIntensity()
	{
		if (unityLight == null) return 0f;
		return unityLight.intensity;
	}

	void QueueSend() => _sendQueued = true;

	void TrySendIfDue()
	{
		if (!_sendQueued || _isSending) return;
		if (minSendInterval > 0f && Time.unscaledTime < _nextAllowedSendTime) return;

		_sendQueued = false;
		_nextAllowedSendTime = Time.unscaledTime + Mathf.Max(0f, minSendInterval);

		// compute values from current unityLight (or defaults if missing)
		Color c = GetSampledColor();
		float inten = GetSampledIntensity();
		bool on = unityLight != null ? unityLight.enabled : true;

		// convert to HSV and Hue ranges
		Color.RGBToHSV(c, out float h01, out float s01, out float v01);

		int hueVal = Mathf.Clamp(Mathf.RoundToInt(h01 * 65535f), 0, 65535);
		int satVal = Mathf.Clamp(Mathf.RoundToInt(s01 * 254f), 0, 254);

		// map intensity to bri (0..254), clamp
		float norm = maxUnityIntensity > 0f ? Mathf.Clamp01(inten / maxUnityIntensity) : Mathf.Clamp01(v01);
		int briVal = Mathf.Clamp(Mathf.RoundToInt(norm * 254f), 0, 254);

		_startSend(hueVal, satVal, briVal, on);
	}

	void _startSend(int hueVal, int satVal, int briVal, bool on)
	{
		List<int> ids = GetSelectedBulbIds();
		if (ids.Count == 0)
		{
			if (verbose) Debug.LogWarning("HueLightSampler: no target lights selected (1-8).");
			return;
		}

		StartCoroutine(SendStateToSelectedCoroutine(ids, hueVal, satVal, briVal, on));
	}

	List<int> GetSelectedBulbIds()
	{
		var ids = new List<int>(8);
		if (useLight1) ids.Add(1);
		if (useLight2) ids.Add(2);
		if (useLight3) ids.Add(3);
		if (useLight4) ids.Add(4);
		if (useLight5) ids.Add(5);
		if (useLight6) ids.Add(6);
		if (useLight7) ids.Add(7);
		if (useLight8) ids.Add(8);
		return ids;
	}

	IEnumerator SendStateToSelectedCoroutine(List<int> ids, int hueVal, int satVal, int briVal, bool on)
	{
		_isSending = true;

		if (string.IsNullOrEmpty(BridgeBaseUrl) || string.IsNullOrWhiteSpace(UserApiTrimmed))
		{
			Debug.LogError("HueLightSampler: invalid bridgeIP/userApi.");
			_isSending = false;
			yield break;
		}

		string json = on
			? $"{{\"on\":true,\"bri\":{briVal},\"hue\":{hueVal},\"sat\":{satVal}}}"
			: "{\"on\":false}";

		for (int i = 0; i < ids.Count; i++)
		{
			int bulbId = ids[i];
			if (bulbId <= 0) continue;

			string url = BuildUrl($"/api/{UserApiTrimmed}/lights/{bulbId}/state");
			if (verbose) Debug.Log($"HueLightSampler: PUT {url} payload={json}");

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

				string resp = req.downloadHandler != null ? req.downloadHandler.text : "<no body>";
				if (isError) Debug.LogWarning($"HueLightSampler: request failed: error={req.error} responseCode={req.responseCode} body={resp}");
				else if (verbose) Debug.Log($"HueLightSampler: request success responseCode={req.responseCode} body={resp}");
			}
		}

		_isSending = false;
	}
}
