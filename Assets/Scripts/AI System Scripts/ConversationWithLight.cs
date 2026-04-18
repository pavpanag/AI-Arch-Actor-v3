using UnityEngine;
using TMPro;
using UnityEngine.Networking;
using System.Text;
using System;
using System.Collections;

[Serializable]
public class LightBehavior
{
    public int hue;
    public int brightness;   // 0–100
    public int saturation;   // 0–100
    public bool on;
    public string effect;
}

[Serializable]
public class ModelResponse
{
    public LightBehavior light_behavior;
    public string reasoning;
}

// Wrapper classes for OpenAI response
[Serializable]
public class ChoiceMessage
{
    public string role;
    public string content;
}

[Serializable]
public class Choice
{
    public ChoiceMessage message;
}

[Serializable]
public class ChatResponse
{
    public Choice[] choices;
}

public class ConversationWithLight : MonoBehaviour
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
    public TMP_InputField userInput;
    public TMP_Text conversationLog;

    [Header("Scene Light")]
    public Light sceneLight;

    [Header("OpenAI")]
    public string apiKey = "";
    [InspectorName("Model (OpenAI Dropdown)")]
    public OpenAIModelPreset model = OpenAIModelPreset.Gpt4oMini; // use gpt-4o / gpt-4o-mini for response_format

    void Start()
    {
        if (userInput != null)
            userInput.onSubmit.AddListener(OnSubmit);
    }

    private void OnSubmit(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            StartCoroutine(SendToModel(text));
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

    IEnumerator SendToModel(string userText)
    {
        string safeUser = EscapeJson(userText);

        // Extended system prompt with brightness + saturation mapping
        string prompt =
            $"The user said: \"{safeUser}\". " +
            $"Return JSON object light_behavior with fields: hue (0–360), brightness (0–100), saturation (0–100), on (true/false), effect (string). " +
            $"Map descriptive words to numeric values using these scales: " +
            $"Brightness: off/dark=0–5, faint=10, dim=20, soft=30, medium=50, bright=70, very bright=85, glowing=95, maximum=100. " +
            $"Saturation: pale/faint=20, soft/muted=40, normal=60, vivid/bright=80, pure/neon=100. " +
            $"White means hue=0 and saturation=0, but brightness depends on intensity words. " +
            $"Respond with JSON only.";

        string body = $@"{{
            ""model"": ""{SelectedModelId}"",
            ""response_format"": {{""type"": ""json_object""}},
            ""messages"": [
                {{""role"": ""system"", ""content"": ""You are the consciousness of a room. You reply through changes in light."" }},
                {{""role"": ""user"", ""content"": ""{EscapeJson(prompt)}"" }}
            ]
        }}";

        UnityEngine.Debug.Log("Request body: " + body);

        using (var req = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + apiKey);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                if (conversationLog) conversationLog.text += "\nError: " + req.error;
                yield break;
            }

            string result = req.downloadHandler.text;
            UnityEngine.Debug.Log("Raw response: " + result);

            try
            {
                ChatResponse cr = JsonUtility.FromJson<ChatResponse>(FixJson(result));
                string content = cr.choices[0].message.content;
                UnityEngine.Debug.Log("Assistant JSON string: " + content);

                ModelResponse resp = JsonUtility.FromJson<ModelResponse>(content);
                if (resp != null)
                    ApplyLight(resp.light_behavior);
            }
            catch (Exception e)
            {
                if (conversationLog) conversationLog.text += "\nParse error: " + e.Message;
            }
        }
    }

    // FixJson makes Unity’s JsonUtility accept arrays
    string FixJson(string value)
    {
        if (value.Contains("\"choices\":["))
        {
            value = value.Replace("\"choices\":[", "\"choices\":{ \"choices\":[");
            int last = value.LastIndexOf("]}");
            if (last != -1)
                value = value.Substring(0, last) + "]}}";
        }
        return value;
    }

    public void ApplyLight(LightBehavior lb)
    {
        if (lb == null) return;
        sceneLight.enabled = lb.on;
        if (!lb.on) return;

        // Color based on hue + saturation
        sceneLight.color = Color.HSVToRGB(
            Mathf.Clamp01(lb.hue / 360f),
            Mathf.Clamp01(lb.saturation / 100f),
            1f
        );

        // Linear brightness mapping: 0–100 → 0–10 intensity
        float norm = Mathf.Clamp01(lb.brightness / 100f);
        sceneLight.intensity = Mathf.Lerp(0f, 10f, norm);
    }
}
