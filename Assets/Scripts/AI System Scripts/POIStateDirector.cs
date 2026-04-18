using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using LivePositions;

/// <summary>
/// POIStateDirector
/// - Define up to 5 Points-of-Interest (POIs) in the Inspector (Transform + radius)
/// - Collect samples from PositionSampler and count how many agents are within each POI
/// - Periodically send a compact summary to an LLM (OpenAI Chat Completions) and log/forward the response
/// - Draw POIs in Scene view for operator clarity
/// </summary>
[DisallowMultipleComponent]
public class POIStateDirector : MonoBehaviour
{
    public enum OpenAIModelPreset
    {
        [InspectorName("GPT-5.4")]
        Gpt54 = 7,
        [InspectorName("GPT-5.4 mini")]
        Gpt54Mini = 6,
        [InspectorName("GPT-4.1")]
        Gpt41 = 3,
        [InspectorName("GPT-4.1 mini")]
        Gpt41Mini = 2,
        [InspectorName("GPT-4o mini")]
        Gpt4oMini = 0
    }

    [Serializable]
    public class POI
    {
        public string id = "poi";
        public Transform target;
        [Tooltip("Radius (world units) considered 'near' the POI")] public float radius = 1f;
        public bool enabled = true;
    }

    [Header("POIs (max 5)")]
    [Tooltip("Define up to 5 POIs. The script will trim the list to 5 if more are added.")]
    public List<POI> pois = new List<POI>() { new POI { id = "poi1" } };

    [Header("Sampling & Send")]
    [Tooltip("How often (seconds) to send the POI summary to the LLM")]
    public float sendIntervalSeconds = 3f;

    [Tooltip("If true, start the periodic sender automatically on Enable")] public bool autoStart = true;

    [Tooltip("If true, count extras reported by PositionSampler as " +
        "agents when computing proximity. Otherwise extras are ignored.")]
    public bool includeExtras = false;
    [Tooltip("If true, include the list of agent IDs within each POI in the JSON payload sent to the LLM.")]
    public bool includeAgentIds = false;

    [Header("LLM / OpenAI Settings")]
    [Tooltip("Your OpenAI API key (kept local).")]
    public string apiKey = "";
    [InspectorName("Model (OpenAI Dropdown)")]
    public OpenAIModelPreset model = OpenAIModelPreset.Gpt4oMini;

    [TextArea(3, 6), Tooltip("Directorial instructions passed as system text to the LLM. Use clear rules like: 'If more than 2 people near lamp A, suggest a light behavior.'")]
    public string directorialInstructions = "";
    [Tooltip("If true, augment the directorial instructions with a small few-shot template/examples so the model understands less-specific phrasing.")]
    public bool augmentWithExamples = true;

    [Tooltip("If true, automatically forward LLM responses to a ConversationWithLight receiver (optional)")]
    public bool forwardToConversation = false;
    public ConversationWithLight conversationTarget;
        [Header("Response Safety / Retry")]
        [Tooltip("Maximum number of attempts to request/parse a valid LLM response. 0 = no attempts (skip).")]
        [Range(0, 5)] public int maxRetries = 2;
        [Tooltip("Seconds to wait between retry attempts when a malformed response is received.")]
        public float retryDelaySeconds = 0.5f;
        [Tooltip("If true and all retries fail, apply this safe fallback light behavior (optional)")]
        public bool fallbackToSafeBehavior = false;
        [Tooltip("Safe fallback light behavior to apply when the LLM fails repeatedly")]
        public LightBehavior fallbackLightBehavior = new LightBehavior { hue = 0, brightness = 0, saturation = 0, on = false, effect = "" };

    [Header("Visualization")]
    public bool drawGizmos = true;
    [Tooltip("Gizmo color for POI spheres")] public Color poiColor = new Color(0f, 0.7f, 1f, 0.25f);
    [Tooltip("Gizmo label color")] public Color labelColor = Color.white;

    // internals
    PositionSampler _sampler;
    List<Sample2D> _lastSamples = new List<Sample2D>();
    Coroutine _senderCoroutine;

    // Simple response wrapper types
    [Serializable]
    public class ChoiceMessage { public string role; public string content; }
    [Serializable] public class Choice { public ChoiceMessage message; }
    [Serializable] public class ChatResponse { public Choice[] choices; }

    // Use the global ModelResponse / LightBehavior defined in ConversationWithLight.cs

    private string SelectedModelId => model switch
    {
        OpenAIModelPreset.Gpt54 => "gpt-5.4",
        OpenAIModelPreset.Gpt54Mini => "gpt-5.4-mini",
        OpenAIModelPreset.Gpt41 => "gpt-4.1",
        OpenAIModelPreset.Gpt41Mini => "gpt-4.1-mini",
        OpenAIModelPreset.Gpt4oMini => "gpt-4o-mini",
        _ => "gpt-4o-mini"
    };

    void Awake()
    {
        _sampler = PositionSampler.Instance;
        TrimPOIs();
    }

    void OnValidate()
    {
        TrimPOIs();
    }

    void TrimPOIs()
    {
        if (pois == null) pois = new List<POI>();
        if (pois.Count > 5) pois.RemoveRange(5, pois.Count - 5);
        // auto-generate ids if missing
        for (int i = 0; i < pois.Count; i++)
            if (string.IsNullOrEmpty(pois[i].id)) pois[i].id = "poi" + (i + 1);
    }

    void OnEnable()
    {
        if (_sampler == null) _sampler = PositionSampler.Instance;
        if (_sampler != null) _sampler.OnSampled += OnSampled;
        if (autoStart) StartSender();
    }

    void OnDisable()
    {
        if (_sampler != null) _sampler.OnSampled -= OnSampled;
        StopSender();
    }

    void OnSampled(List<Sample2D> samples)
    {
        // keep a local snapshot (shallow copy) for periodic reports
        _lastSamples.Clear();
        if (samples != null)
        {
            for (int i = 0; i < samples.Count; i++)
                _lastSamples.Add(samples[i]);
        }
    }

    void StartSender()
    {
        if (_senderCoroutine == null)
            _senderCoroutine = StartCoroutine(SenderLoop());
    }

    void StopSender()
    {
        if (_senderCoroutine != null)
        {
            StopCoroutine(_senderCoroutine);
            _senderCoroutine = null;
        }
    }

    IEnumerator SenderLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(Mathf.Max(0.1f, sendIntervalSeconds));
            yield return StartCoroutine(SendPOISummaryToLLM());
        }
    }

    [ContextMenu("Force Send POI Summary Now")]
    public void ForceSendNow()
    {
        if (Application.isPlaying) StartCoroutine(SendPOISummaryToLLM());
        else Debug.Log("POIStateDirector: Force send can only run in Play mode.");
    }

    IEnumerator SendPOISummaryToLLM()
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogWarning("POIStateDirector: apiKey is empty, skipping send.");
            yield break;
        }

        // Build summary
        var samples = _lastSamples;
        int totalAgents = 0;
        foreach (var s in samples) if (includeExtras || !s.isExtra) totalAgents++;

        StringBuilder human = new StringBuilder();
        human.AppendLine("POI summary:");

        // Build a structured JSON payload for the LLM so it receives exact numeric data
        StringBuilder json = new StringBuilder();
        json.Append("{\"totalAgents\":").Append(totalAgents).Append(",\"pois\":[");

        for (int i = 0; i < pois.Count; i++)
        {
            var p = pois[i];
            if (p == null || p.target == null || !p.enabled) continue;
            int count = 0;
            Vector2 poiXZ = new Vector2(p.target.position.x, p.target.position.z);
            float r2 = p.radius * p.radius;
            List<string> idsInPoi = null;
            foreach (var s in samples)
            {
                if (!includeExtras && s.isExtra) continue;
                float dx = s.xz.x - poiXZ.x;
                float dz = s.xz.y - poiXZ.y;
                if (dx * dx + dz * dz <= r2) count++;
                if (includeAgentIds)
                {
                    if (dx * dx + dz * dz <= r2)
                    {
                        if (idsInPoi == null) idsInPoi = new List<string>();
                        idsInPoi.Add(s.id);
                    }
                }
            }
            float frac = (totalAgents > 0) ? (float)count / totalAgents : 0f;
            human.AppendLine($"- {p.id}: count={count}, fraction={(frac * 100f):F1}% at pos=({poiXZ.x:F2},{poiXZ.y:F2}), radius={p.radius:F2}");

            // append JSON entry
            if (json[json.Length - 1] != '[') json.Append(',');
            json.Append('{');
            json.Append("\"id\":\"").Append(EscapeJson(p.id)).Append("\"");
            json.Append(",\"x\":").Append(poiXZ.x.ToString("F3")).Append(",\"z\":").Append(poiXZ.y.ToString("F3"));
            json.Append(",\"radius\":").Append(p.radius.ToString("F3"));
            json.Append(",\"count\":").Append(count);
            json.Append(",\"fraction\":").Append((frac).ToString("F4"));
            if (includeAgentIds && idsInPoi != null)
            {
                json.Append(",\"agentIds\":[");
                for (int ai = 0; ai < idsInPoi.Count; ai++)
                {
                    if (ai > 0) json.Append(',');
                    json.Append('"').Append(EscapeJson(idsInPoi[ai])).Append('"');
                }
                json.Append(']');
            }
            json.Append('}');
        }

        json.Append("]}");

        string humanSummary = human.ToString();
        string jsonPayload = json.ToString();

        // Compose prompt: system = directorialInstructions; user = summary + instruction to respond with JSON (light_behavior + reasoning)
    // Include both a human-readable summary and a structured JSON payload so the LLM has exact numeric inputs
    string userPrompt = EscapeJson(humanSummary + "\n\nJSON payload:\n" + jsonPayload + "\n\nDirectorial instructions:\n" + directorialInstructions + "\n\nRespond with a JSON object containing light_behavior and reasoning. light_behavior should have: hue (0-360), brightness (0-100), saturation (0-100), on (true/false), effect (string). Reasoning is short.");

                string systemPrompt = augmentWithExamples ? BuildSystemPrompt() : directorialInstructions;

                string body = $@"{{
    ""model"": ""{SelectedModelId}"",
    ""response_format"": {{""type"": ""json_object""}},
    ""messages"": [
        {{""role"": ""system"", ""content"": ""{EscapeJson(systemPrompt)}""}},
        {{""role"": ""user"", ""content"": ""{userPrompt}""}}
    ]
}}";

        int attempt = 0;
        bool succeeded = false;
        while (attempt <= maxRetries && !succeeded)
        {
            attempt++;
            bool needDelay = false;
            using (var req = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + apiKey);

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"POIStateDirector LLM request failed (attempt {attempt}): " + req.error);
                    needDelay = true;
                }
                else
                {
                    string raw = req.downloadHandler.text;
                    Debug.Log("POIStateDirector raw response: " + raw);

                    try
                    {
                        var fixedJson = FixJson(raw);
                        ChatResponse cr = JsonUtility.FromJson<ChatResponse>(fixedJson);
                        if (cr != null && cr.choices != null && cr.choices.Length > 0 && cr.choices[0].message != null)
                        {
                            string content = cr.choices[0].message.content;
                            Debug.Log("POIStateDirector assistant content: " + content);

                            ModelResponse m = null;
                            try { m = JsonUtility.FromJson<ModelResponse>(content); } catch { m = null; }

                            if (ValidateModelResponse(m))
                            {
                                Debug.Log($"POIStateDirector parsed light_behavior: brightness={m.light_behavior.brightness} hue={m.light_behavior.hue} sat={m.light_behavior.saturation}");
                                if (forwardToConversation && conversationTarget != null)
                                {
                                    conversationTarget.ApplyLight(m.light_behavior);
                                }
                                succeeded = true;
                            }
                            else
                            {
                                Debug.LogWarning($"POIStateDirector: invalid model response (attempt {attempt}).");
                                needDelay = true;
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"POIStateDirector: empty choices in response (attempt {attempt}).");
                            needDelay = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("POIStateDirector parse error: " + ex.Message);
                        needDelay = true;
                    }
                }
            }

            if (succeeded) break;
            if (attempt > maxRetries) break;
            if (needDelay)
                yield return new WaitForSeconds(retryDelaySeconds);
        }

        if (!succeeded)
        {
            Debug.LogWarning("POIStateDirector: all retries failed or invalid responses received.");
            if (fallbackToSafeBehavior)
            {
                Debug.Log("POIStateDirector: applying safe fallback behavior.");
                if (conversationTarget != null)
                    conversationTarget.ApplyLight(fallbackLightBehavior);
            }
        }
    }

    bool ValidateModelResponse(ModelResponse m)
    {
        if (m == null) return false;
        if (m.light_behavior == null) return false;
        // Validate numeric ranges
        if (m.light_behavior.hue < 0 || m.light_behavior.hue > 360) return false;
        if (m.light_behavior.brightness < 0 || m.light_behavior.brightness > 100) return false;
        if (m.light_behavior.saturation < 0 || m.light_behavior.saturation > 100) return false;
        // 'on' can be true/false; effect can be any string
        return true;
    }

    string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    string BuildSystemPrompt()
    {
        // A compact few-shot instruction set to help the model interpret vague directives like
        // "if anyone near poi1, do something". Keep examples minimal and strictly require JSON output.
        StringBuilder t = new StringBuilder();
        t.AppendLine("You are the room director. You receive numeric POI data and direct lights.\n");
        t.AppendLine("Rules:");
        t.AppendLine("- Always return exactly one JSON object with top-level keys: \"light_behavior\" and \"reasoning\".");
        t.AppendLine("- light_behavior must contain: hue (0-360), brightness (0-100), saturation (0-100), on (true/false), effect (string).\n");
        t.AppendLine("Examples:");
        t.AppendLine("1) Instruction: 'If there is anyone near poi1, turn the light red.'");
        t.AppendLine("Input summary: - poi1: count=1, fraction=10% at pos=(1.00,1.00), radius=1.00");
        t.AppendLine("Expected JSON:");
        t.AppendLine("{\"light_behavior\":{\"hue\":0,\"brightness\":80,\"saturation\":90,\"on\":true,\"effect\":\"\"},\"reasoning\":\"One person is within poi1, so set light to red.\"}");
        t.AppendLine();
        t.AppendLine("2) Instruction: 'If nobody is near poi2, turn it off.'");
        t.AppendLine("Input summary: - poi2: count=0, fraction=0% at pos=(2.00,0.50), radius=1.00");
        t.AppendLine("Expected JSON:");
        t.AppendLine("{\"light_behavior\":{\"hue\":0,\"brightness\":0,\"saturation\":0,\"on\":false,\"effect\":\"\"},\"reasoning\":\"No people near poi2; turning off as requested.\"}");
        t.AppendLine();
        t.AppendLine("Now follow the directorial instructions exactly. Do not return any extra text.");
        t.AppendLine();
        t.AppendLine(directorialInstructions);
        return t.ToString();
    }

    // FixJson identical helper to handle 'choices' array for JsonUtility
    string FixJson(string value)
    {
        if (value.Contains("\"choices\":[]")) return value;
        if (value.Contains("\"choices\":["))
        {
            value = value.Replace("\"choices\":[", "\"choices\":{ \"choices\":[");
            int last = value.LastIndexOf("]}");
            if (last != -1)
                value = value.Substring(0, last) + "]}}";
        }
        return value;
    }

    void OnDrawGizmos()
    {
        if (!drawGizmos || pois == null) return;
        for (int i = 0; i < pois.Count; i++)
        {
            var p = pois[i];
            if (p == null || p.target == null) continue;
            Gizmos.color = poiColor;
            Vector3 pos = new Vector3(p.target.position.x, 0.01f, p.target.position.z);
            Gizmos.DrawSphere(pos, p.radius);
            UnityEditor.Handles.color = labelColor;
            UnityEditor.Handles.Label(pos + Vector3.up * 0.05f, $"{p.id} ({p.radius:F2})");
        }
    }
}
