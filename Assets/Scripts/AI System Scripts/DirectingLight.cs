using UnityEngine;
using TMPro;
using UnityEngine.Networking;
using System.Text;
using System;
using System.Collections;

namespace DirectingSystem
{
    [Serializable] 
    public class LightBehavior {
        public int hue;
        public int brightness;   // 0–100
        public int saturation;   // 0–100
        public bool on;
        public string effect;
    }

    [Serializable] 
    public class ModelResponse {
        public LightBehavior light_behavior;
        public string reasoning;
    }

    [Serializable]                          // ADDED
    public class ChoiceMessage {            // now serializable for JsonUtility
        public string role;
        public string content;
    }

    [Serializable]                          // ADDED
    public class Choice {                   // now serializable for JsonUtility
        public ChoiceMessage message;
    }

    [Serializable]                          // ADDED
    public class ChatResponse {             // now serializable for JsonUtility
        public Choice[] choices;
    }

    public class DirectingLight : MonoBehaviour
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

        [Header("UI References")]
        public TMP_InputField dramaturgyInput;   // General dramaturgy (world rules)
        public TMP_InputField directingInput;    // Directorial instructions (strict)
        public TMP_InputField userInput;         // Audience input
        public TMP_Text conversationLog;         // Log output

        [Header("Scene Light")]
        public Light sceneLight;

        // NEW: inspector controls for intensity examples / buckets
        [Header("Intensity Examples")]
        [Tooltip("Scene light intensity to use for LOW brightness bucket.")]
        public float intensityLow = 2f;
        [Tooltip("Scene light intensity to use for MEDIUM brightness bucket.")]
        public float intensityMedium = 5f;
        [Tooltip("Scene light intensity to use for HIGH brightness bucket.")]
        public float intensityHigh = 10f;
        [Tooltip("When true, use the low/medium/high buckets instead of linear mapping.")]
        public bool useExampleIntensityBuckets = true;
        [Tooltip("Upper brightness (0-100) threshold for the LOW bucket.")]
        [Range(0,100)]
        public int lowUpper = 33;
        [Tooltip("Upper brightness (0-100) threshold for the MEDIUM bucket.")]
        [Range(0,100)]
        public int mediumUpper = 66;

        [Header("OpenAI")]
        public string apiKey = "";           
        [InspectorName("Model (OpenAI Dropdown)")]
        public OpenAIModelPreset model = OpenAIModelPreset.Gpt4oMini; 

        private string currentDramaturgy = "";
        private string currentDirecting = "";

        void Start()
        {
            if (dramaturgyInput != null)
                dramaturgyInput.onSubmit.AddListener(OnSubmitDramaturgy);

            if (directingInput != null)
                directingInput.onSubmit.AddListener(OnSubmitDirector);

            if (userInput != null)
                userInput.onSubmit.AddListener(OnSubmitUser);
        }

        private void OnSubmitDramaturgy(string text)
        {
            currentDramaturgy = text;
            if (conversationLog)
                conversationLog.text = $"[Dramaturgy set: {text}]";
        }

        private void OnSubmitDirector(string text)
        {
            currentDirecting = text;
            if (conversationLog)
                conversationLog.text = $"[Directorial instructions updated: {text}]";
        }

        private void OnSubmitUser(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                StartCoroutine(SendToModel(text, currentDirecting, currentDramaturgy));
                userInput.text = string.Empty;
            }
        }

        string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        string SelectedModelId => model switch
        {
            OpenAIModelPreset.Gpt54 => "gpt-5.4",
            OpenAIModelPreset.Gpt54Mini => "gpt-5.4-mini",
            OpenAIModelPreset.Gpt41 => "gpt-4.1",
            OpenAIModelPreset.Gpt41Mini => "gpt-4.1-mini",
            OpenAIModelPreset.Gpt4oMini => "gpt-4o-mini",
            _ => "gpt-4o-mini"
        };

        IEnumerator SendToModel(string userText, string directingText, string dramaturgyText)
        {
            string safeUser = EscapeJson(userText);
            string safeDirector = EscapeJson(directingText);
            string safeDramaturgy = EscapeJson(dramaturgyText);

            string userPrompt = 
                $"The audience said: {safeUser}. " +
                $"Respond with a JSON object with exactly two top-level keys: " +
                $"light_behavior and reasoning. " +
                $"light_behavior must be an object with fields: hue (0–360), brightness (0–100), saturation (0–100), on (true/false), effect (string). " +
                $"reasoning must be a string explaining how dramaturgy, directorial instructions, and audience input shaped the decision. " +
                $"Keep the reasoning concise (1–2 sentences). " +
                $"Do not return flat fields. Do not omit reasoning. Respond with JSON only.";

            string body = $@"{{
                ""model"": ""{SelectedModelId}"",
                ""response_format"": {{""type"": ""json_object""}},
                ""messages"": [
                    {{""role"": ""system"", ""content"": ""You are the consciousness of a room. Always respond with a single JSON object containing light_behavior and reasoning. Keep the reasoning concise (1–2 sentences)."" }},
                    {{""role"": ""system"", ""content"": ""Dramaturgy (world rules): {safeDramaturgy}"" }},
                    {{""role"": ""system"", ""content"": ""Directorial instructions (must always be followed): {safeDirector}"" }},
                    {{""role"": ""user"", ""content"": ""{EscapeJson(userPrompt)}"" }}
                ]
            }}";

            // Debug outgoing request
            UnityEngine.Debug.Log("=== REQUEST TO OPENAI ===");
            UnityEngine.Debug.Log(body);
            UnityEngine.Debug.Log("=========================");

            using (var req = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + apiKey);

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    UnityEngine.Debug.LogError("OpenAI Request Failed: " + req.error);
                    if (conversationLog) conversationLog.text = "Error: " + req.error;
                    yield break;
                }

                string result = req.downloadHandler.text;

                // Debug incoming response
                UnityEngine.Debug.Log("=== RESPONSE FROM OPENAI ===");
                UnityEngine.Debug.Log(result);
                UnityEngine.Debug.Log("============================");

                try
                {
                    // Parse the API response directly; no "fixing" needed
                    ChatResponse cr = JsonUtility.FromJson<ChatResponse>(result);   // CHANGED (removed FixJson)
                    if (cr == null || cr.choices == null || cr.choices.Length == 0 || cr.choices[0].message == null)
                    {
                        if (conversationLog) conversationLog.text = "Parse error: choices missing or malformed";
                        yield break;
                    }

                    string content = cr.choices[0].message.content;
                    if (string.IsNullOrEmpty(content))
                    {
                        if (conversationLog) conversationLog.text = "Parse error: empty assistant content";
                        yield break;
                    }

                    // Debug raw assistant content
                    UnityEngine.Debug.Log("=== ASSISTANT RAW CONTENT ===");
                    UnityEngine.Debug.Log(content);
                    UnityEngine.Debug.Log("================================");

                    ModelResponse resp = JsonUtility.FromJson<ModelResponse>(content);

                    if (resp != null && resp.light_behavior != null)
                    {
                        ApplyLight(resp.light_behavior);

                        if (conversationLog) 
                        {
                            conversationLog.text =
                                $"Light → Hue:{resp.light_behavior.hue}, " +
                                $"Brightness:{resp.light_behavior.brightness}, " +
                                $"Saturation:{resp.light_behavior.saturation}, On:{resp.light_behavior.on}, " +
                                $"Effect:{resp.light_behavior.effect}" +
                                $"\nReasoning: {resp.reasoning}";
                        }
                    }
                    else
                    {
                        if (conversationLog) 
                            conversationLog.text = "Parse error: ModelResponse was null or malformed.";
                    }
                }
                catch (Exception e)
                {
                    if (conversationLog) conversationLog.text = "Parse exception: " + e.Message;
                }
            }
        }

        void ApplyLight(LightBehavior lb)
        {
            if (lb == null) return;
            sceneLight.enabled = lb.on;
            if (!lb.on) return;

            sceneLight.color = Color.HSVToRGB(
                Mathf.Clamp01(lb.hue / 360f),
                Mathf.Clamp01(lb.saturation / 100f),
                1f
            );

            // use either the new example buckets or the original linear mapping
            if (!useExampleIntensityBuckets)
            {
                float norm = Mathf.Clamp01(lb.brightness / 100f);
                sceneLight.intensity = Mathf.Lerp(0f, 10f, norm);
            }
            else
            {
                int b = Mathf.Clamp(lb.brightness, 0, 100);
                float applied = intensityHigh; // default
                if (b <= lowUpper) applied = Mathf.Max(0f, intensityLow);
                else if (b <= mediumUpper) applied = Mathf.Max(0f, intensityMedium);
                else applied = Mathf.Max(0f, intensityHigh);
                sceneLight.intensity = applied;
            }
        }
    }   // closes DirectingLight
}       // closes namespace

