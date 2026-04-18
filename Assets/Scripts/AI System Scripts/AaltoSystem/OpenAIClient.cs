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

namespace AaltoSystemV3
{

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

    [Header("Debug Logging")]
    [Tooltip("Logs request summary, roles order, and system prompt preview.")]
    public bool LogRequestSummary = true;
    [Tooltip("Logs a payload JSON preview (use with caution in production).")]
    public bool LogPayloadPreview = true;
    [Tooltip("Logs the complete response body from OpenAI.")]
    public bool LogFullResponse = true;
    [Range(200, 50000)]
    public int PayloadPreviewChars = 20000;
    [Range(200, 20000)]
    public int SystemPreviewChars = 10000;

    public string LastRawRequestJson { get; private set; }
    public string LastRawResponseJson { get; private set; }
    public string LastAssistantContent { get; private set; }
    public string LastContextTag { get; private set; }
    public int LastResponseCode { get; private set; }
    public string LastTraceSessionId { get; private set; }
    public string LastTraceRequestId { get; private set; }
    public string LastTraceContextTag { get; private set; }

    public readonly struct Msg
    {
        public readonly string role;
        public readonly string content;
        public Msg(string role, string content) { this.role = role; this.content = content; }
    }

    public async Task<string> ChatCompletionsJsonAsync(
        IReadOnlyList<Msg> messages,
        string model = null,
        float? temperature = null,
        string contextTag = "Unspecified")
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new Exception("OpenAIClient.ApiKey is empty (set it in the Inspector).");

        var usedModel = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        var usedTemp = (temperature ?? Temperature);
        var payload = BuildPayloadJson(usedModel, usedTemp, messages);

        LastContextTag = contextTag;
        LastRawRequestJson = payload;
        LastRawResponseJson = null;
        LastAssistantContent = null;
        LastResponseCode = 0;
        LastTraceSessionId = null;
        LastTraceRequestId = null;
        LastTraceContextTag = contextTag;

        if (LogRequestSummary)
        {
            var roles = BuildRolesString(messages);
            var sys = FindFirstSystem(messages);
            var sysPreview = Preview(sys, SystemPreviewChars);
            Debug.Log($"[OpenAIClient] Request {contextTag}: model={usedModel}, temp={usedTemp}, roles={roles}");
            Debug.Log($"[OpenAIClient] System preview: {sysPreview}");
        }
        if (LogPayloadPreview)
        {
            Debug.Log($"[OpenAIClient] === FULL REQUEST PAYLOAD ===\n{payload}\n=== END PAYLOAD ===");
        }

        Guid? traceId = null;
        UnityWebRequest req = null;
        string responseBody = null;
        int responseCode = 0;
        bool errorLogged = false;

        try
        {
            try
            {
                traceId = ChatTraceLogger.LogRequest(contextTag, "https://api.openai.com/v1/chat/completions", usedModel, messages, payload);
                LastTraceSessionId = ChatTraceLogger.CurrentSessionId;
                LastTraceRequestId = traceId?.ToString();
                LastTraceContextTag = contextTag;
            }
            catch (Exception logEx) { Debug.LogWarning($"[OpenAIClient] Trace log request failed: {logEx.Message}"); }

            req = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", $"Bearer {ApiKey.Trim()}");

            await Send(req);

            responseCode = (int)req.responseCode;
            responseBody = req.downloadHandler.text ?? "";

            LastResponseCode = responseCode;
            LastRawResponseJson = responseBody;

            if (req.result != UnityWebRequest.Result.Success)
            {
                TryLogError(traceId, $"HTTP {responseCode}: {req.error}", responseCode, responseBody);
                errorLogged = true;
                throw new Exception($"HTTP {responseCode}: {req.error}\n{responseBody}");
            }

            // check for error message (naive but sufficient for typical OpenAI responses)
            if (TryExtractJsonString(responseBody, new[] { "error", "message" }, out var errMsg) && !string.IsNullOrWhiteSpace(errMsg))
                throw new Exception(errMsg);

            // extract choices[0].message.content (naive extractor)
            if (!TryExtractFirstChoiceContent(responseBody, out var content))
                throw new Exception("Malformed response: missing choices[0].message.content.");

            var text = (content ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new Exception("Model returned empty content.");

            LastAssistantContent = text;

            if (LogFullResponse)
            {
                Debug.Log($"[OpenAIClient] === FULL RESPONSE BODY ===\n{responseBody}\n=== END RESPONSE ===");
                Debug.Log($"[OpenAIClient] Extracted content: {text}");
            }

            TryLogResponse(traceId, responseBody, content, responseCode); // pass raw content + status
            return text;
        }
        catch (Exception ex)
        {
            LastResponseCode = responseCode;
            if (responseBody != null)
                LastRawResponseJson = responseBody;
            if (!errorLogged)
                TryLogError(traceId, ex.Message, responseCode, responseBody, ex.GetType().Name, ex.StackTrace);
            throw;
        }
        finally
        {
            req?.Dispose();
        }
    }

    private static object[] BuildMessages(IReadOnlyList<Msg> messages)
    {
        var arr = new object[messages.Count];
        for (int i = 0; i < messages.Count; i++)
            arr[i] = new { role = messages[i].role, content = messages[i].content };
        return arr;
    }

    private static string BuildRolesString(IReadOnlyList<Msg> messages)
    {
        if (messages == null || messages.Count == 0) return "(none)";
        var sb = new StringBuilder();
        for (int i = 0; i < messages.Count; i++)
        {
            if (i > 0) sb.Append(" > ");
            sb.Append(messages[i].role);
        }
        return sb.ToString();
    }

    private static string FindFirstSystem(IReadOnlyList<Msg> messages)
    {
        if (messages == null) return "";
        for (int i = 0; i < messages.Count; i++)
        {
            if (string.Equals(messages[i].role, "system", StringComparison.OrdinalIgnoreCase))
                return messages[i].content ?? "";
        }
        return "";
    }

    private static string Preview(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(empty)";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
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
            var colon = cur.IndexOf(':', pos + name.Length);
            if (colon < 0) return false;
            cur = cur.Substring(colon + 1).TrimStart();
            if (p == path.Length - 1)
            {
                if (cur.StartsWith("\""))
                {
                    var sb = new StringBuilder();
                    bool esc = false;
                    for (int i = 1; i < cur.Length; i++)
                    {
                        var ch = cur[i];
                        if (esc)
                        {
                            // append escaped char as literal, keep backslash already appended
                            sb.Append(ch);
                            esc = false;
                        }
                        else if (ch == '\\')
                        {
                            // preserve backslash so '\n' stays intact for later unescape
                            sb.Append('\\');
                            esc = true;
                        }
                        else if (ch == '\"') break;
                        else sb.Append(ch);
                    }
                    value = sb.ToString();
                    return true;
                }
                return false;
            }

            if (cur.StartsWith("{"))
            {
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

        if (!TryExtractJsonString(afterMsg, new[] { "message", "content" }, out var rawExtracted))
            return false;

        // Unescape JSON escape sequences to preserve actual newlines, etc.
        content = UnescapeJsonString(rawExtracted);
        return true;
    }

    private static string UnescapeJsonString(string escaped)
    {
        if (string.IsNullOrWhiteSpace(escaped)) return escaped;
        var sb = new StringBuilder();
        bool esc = false;
        for (int i = 0; i < escaped.Length; i++)
        {
            if (esc)
            {
                var ch = escaped[i];
                switch (ch)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '\\': sb.Append('\\'); break;
                    case '\"': sb.Append('\"'); break;
                    case '/': sb.Append('/'); break;
                    case 'u':
                        // unicode escape: \uXXXX
                        if (i + 4 < escaped.Length)
                        {
                            var hex = escaped.Substring(i + 1, 4);
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
                            {
                                sb.Append((char)code);
                                i += 4;
                                esc = false;
                                continue;
                            }
                        }
                        sb.Append('\\').Append(ch);
                        break;
                    default:
                        sb.Append('\\').Append(ch);
                        break;
                }
                esc = false;
            }
            else if (escaped[i] == '\\')
            {
                esc = true;
            }
            else
            {
                sb.Append(escaped[i]);
            }
        }
        if (esc) sb.Append('\\');
        return sb.ToString();
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

    private static void TryLogResponse(Guid? traceId, string rawResponse, string assistantContent, int httpStatus)
    {
        try { ChatTraceLogger.LogResponse(traceId, rawResponse, assistantContent, httpStatus); }
        catch (Exception ex) { Debug.LogWarning($"[OpenAIClient] Trace log response failed: {ex.Message}"); }
    }

    private static void TryLogError(Guid? traceId, string summary, int responseCode, string responseBody, string exceptionType = null, string stackTrace = null)
    {
        try { ChatTraceLogger.LogError(traceId, summary, responseCode, responseBody, exceptionType, stackTrace); }
        catch (Exception ex) { Debug.LogWarning($"[OpenAIClient] Trace log error failed: {ex.Message}"); }
    }
}
}
