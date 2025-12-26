using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
#if UNITY_EDITOR
using UnityEditor;
using System.IO;
#endif

#if UNITY_EDITOR
// Editor-only: ensure Unity's External Script Editor path is valid; try common defaults if missing.
[InitializeOnLoad]
static class ExternalEditorPathFixer
{
	static ExternalEditorPathFixer() => EnsureExternalEditor();

	private static void EnsureExternalEditor()
	{
		const string key = "kScriptsDefaultApp";
		var path = EditorPrefs.GetString(key, "");
		if (!string.IsNullOrWhiteSpace(path) && (Directory.Exists(path) || File.Exists(path)))
			return;

		string[] candidates;
		if (Application.platform == RuntimePlatform.OSXEditor)
			candidates = new[] {
				"/Applications/Visual Studio Code.app",
				"/Applications/Visual Studio Code - Insiders.app",
				"/Applications/Visual Studio.app",
				"/Applications/Sublime Text.app"
			};
		else if (Application.platform == RuntimePlatform.WindowsEditor)
			candidates = new[] {
				@"C:\Program Files\Microsoft VS Code\Code.exe",
				@"C:\Program Files (x86)\Microsoft VS Code\Code.exe",
				@"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"
			};
		else
			candidates = Array.Empty<string>();

		foreach (var c in candidates)
		{
			if (Directory.Exists(c) || File.Exists(c))
			{
				EditorPrefs.SetString(key, c);
				Debug.Log($"[ExternalEditorFix] Set external script editor to: {c}");
				return;
			}
		}

		Debug.LogWarning("[ExternalEditorFix] External Code Editor application path does not exist. Set Preferences -> External Tools -> External Script Editor.");
	}
}
#endif

public sealed class OpenAIClient : MonoBehaviour
{
    [Header("Auth (temporary: Inspector)")]
    [Tooltip("OpenAI API key (starts with sk-...). Stored in scene/prefab; replace later.")]
    public string ApiKey;

    [Header("Defaults")]
    public string DefaultModel = "gpt-4o-mini";
    [Range(0f, 2f)] public float Temperature = 0.7f;

    public readonly struct Msg
    {
        public readonly string role;
        public readonly string content;
        public Msg(string role, string content) { this.role = role; this.content = content; }
    }

    public async Task<string> ChatCompletionsJsonAsync(
        IReadOnlyList<Msg> messages,
        string model = null,
        float? temperature = null)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new Exception("OpenAIClient.ApiKey is empty (set it in the Inspector).");

        var usedModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        var usedTemp = (temperature ?? Temperature);

        // build payload manually to avoid System.Text.Json dependency
        var payload = BuildPayloadJson(usedModel, usedTemp, messages);

        using var req = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {ApiKey.Trim()}");

        await Send(req);

        var body = req.downloadHandler.text ?? "";
        if (req.result != UnityWebRequest.Result.Success)
            throw new Exception($"HTTP {(long)req.responseCode}: {req.error}\n{body}");

        // check for error message (naive but sufficient for typical OpenAI responses)
        if (TryExtractJsonString(body, new[] { "error", "message" }, out var errMsg) && !string.IsNullOrWhiteSpace(errMsg))
            throw new Exception(errMsg);

        // extract choices[0].message.content (naive extractor)
        if (!TryExtractFirstChoiceContent(body, out var content))
            throw new Exception("Malformed response: missing choices[0].message.content.");

        var text = (content ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new Exception("Model returned empty content.");

        return text;
    }

    private static object[] BuildMessages(IReadOnlyList<Msg> messages)
    {
        var arr = new object[messages.Count];
        for (int i = 0; i < messages.Count; i++)
            arr[i] = new { role = messages[i].role, content = messages[i].content };
        return arr;
    }

    // Helper: build JSON payload string (simple, escapes content)
    private static string BuildPayloadJson(string model, float temperature, IReadOnlyList<Msg> messages)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"model\":\"{EscapeJson(model)}\",");
        sb.Append($"\"temperature\":{temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
        sb.Append("\"response_format\":{\"type\":\"json_object\"},");
        sb.Append("\"messages\":[");
        for (int i = 0; i < messages.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{");
            sb.Append($"\"role\":\"{EscapeJson(messages[i].role)}\",");
            sb.Append($"\"content\":\"{EscapeJson(messages[i].content)}\"");
            sb.Append("}");
        }
        sb.Append("]");
        sb.Append("}");
        return sb.ToString();
    }

    // naive extractor for nested string properties like { "error": { "message": "..." } }
    private static bool TryExtractJsonString(string json, string[] path, out string value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(json) || path == null || path.Length == 0) return false;
        var cur = json;
        for (int p = 0; p < path.Length; p++)
        {
            var name = "\"" + path[p] + "\"";
            var pos = cur.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return false;
            // move to colon after property name
            var colon = cur.IndexOf(':', pos + name.Length);
            if (colon < 0) return false;
            // take rest after colon
            cur = cur.Substring(colon + 1);
            cur = cur.TrimStart();
            // if last path element and starts with quote, extract string value
            if (p == path.Length - 1)
            {
                if (cur.StartsWith("\""))
                {
                    // find closing unescaped quote
                    var sb = new StringBuilder();
                    bool esc = false;
                    for (int i = 1; i < cur.Length; i++)
                    {
                        var ch = cur[i];
                        if (esc)
                        {
                            sb.Append(ch);
                            esc = false;
                        }
                        else if (ch == '\\') esc = true;
                        else if (ch == '\"') break;
                        else sb.Append(ch);
                    }
                    value = sb.ToString();
                    return true;
                }
                return false;
            }

            // otherwise try to extract inner object text to continue searching
            if (cur.StartsWith("{"))
            {
                // attempt to find matching closing brace (simple counter)
                int depth = 0;
                int start = cur.IndexOf('{');
                if (start < 0) return false;
                for (int i = start; i < cur.Length; i++)
                {
                    if (cur[i] == '{') depth++;
                    else if (cur[i] == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            cur = cur.Substring(start, i - start + 1);
                            break;
                        }
                    }
                }
            }
            else
            {
                // cannot descend
                return false;
            }
        }
        return false;
    }

    // extract choices[0].message.content
    private static bool TryExtractFirstChoiceContent(string json, out string content)
    {
        content = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        var choicesKey = "\"choices\"";
        var choicesPos = json.IndexOf(choicesKey, StringComparison.OrdinalIgnoreCase);
        if (choicesPos < 0) return false;
        var afterChoices = json.Substring(choicesPos);
        var messageKey = "\"message\"";
        var msgPos = afterChoices.IndexOf(messageKey, StringComparison.OrdinalIgnoreCase);
        if (msgPos < 0) return false;
        var afterMsg = afterChoices.Substring(msgPos);
        return TryExtractJsonString(afterMsg, new[] { "message", "content" }, out content);
    }

    private static string EscapeJson(string s)
    {
        if (s == null) return "";
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 32)
                        sb.AppendFormat("\\u{0:X4}", (int)ch);
                    else
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static async Task Send(UnityWebRequest req)
    {
        var op = req.SendWebRequest();
        while (!op.isDone) await Task.Yield();
    }
}
