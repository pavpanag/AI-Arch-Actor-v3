using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>
/// Chat trace logger: captures every OpenAI API request/response for offline analysis.
/// 
/// Logs are written to: Application.persistentDataPath/chat_traces/
/// File format: JSON lines (JSONL) - one ChatTraceEntry per line
/// File name: trace_<sessionId>_<yyyyMMdd_HHmms>.jsonl
/// 
/// Each entry contains:
/// - sessionId: GUID per app session
/// - requestId: GUID per API call
/// - contextTag: phase label (e.g., "Chat:Turn", "Interview:SynthesizeBlueprint")
/// - ok: true if no error; false on HTTP/exception failure
/// - httpStatus: HTTP response code (0 if never completed)
/// - request: sent payload (messages, model, endpoint, raw JSON)
/// - response: received payload
///   - rawResponseJson: exact JSON from API (for debugging)
///   - assistantContentRaw: exact content from choices[0].message.content (no corruption)
///   - assistantContentPretty: prettified version if JSON-parseable, else null
///   - parsedJson: stringified parsed object if it's an object, else null
/// - error: null on success; on failure: { summary, httpStatus, responseBody, exceptionType, stackTrace }
/// - warnings: array of validation warnings (non-fatal)
/// - timestampUtc: ISO-8601 UTC timestamp
/// 
/// To find logs:
///   Debug.Log will print the path on every session start and save.
///   Manually: Application.persistentDataPath/chat_traces/trace_*.jsonl
/// </summary>
public sealed class ChatTraceLogger : MonoBehaviour
{
	private static ChatTraceLogger _instance;
	private static readonly object _lock = new object();
	
	private readonly List<ChatTraceEntry> _entries = new List<ChatTraceEntry>();
	private string _sessionId = Guid.NewGuid().ToString();
	private string _traceFolderPath;
	private string _lastSavePath;

	public static ChatTraceLogger Instance => EnsureInstance();

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
	private static void ResetStatics() => _instance = null;

	private static ChatTraceLogger EnsureInstance()
	{
		if (_instance != null) return _instance;
		var go = new GameObject(nameof(ChatTraceLogger)) { hideFlags = HideFlags.HideAndDontSave };
		DontDestroyOnLoad(go);
		_instance = go.AddComponent<ChatTraceLogger>();
		return _instance;
	}

	private void Awake()
	{
		if (_instance != null && _instance != this) { Destroy(gameObject); return; }
		_instance = this;
		DontDestroyOnLoad(gameObject);

		_traceFolderPath = Path.Combine(Application.persistentDataPath, "chat_traces");
		try { Directory.CreateDirectory(_traceFolderPath); }
		catch (Exception ex) { Debug.LogWarning($"[ChatTraceLogger] Failed to create trace folder: {ex.Message}"); }

		Debug.Log($"[ChatTraceLogger] Session {_sessionId} started. Traces → {_traceFolderPath}");
	}

	public static void StartNewSession() => Instance.InternalStartNewSession();

	public static Guid LogRequest(
		string contextTag,
		string endpoint,
		string model,
		IReadOnlyList<OpenAIClient.Msg> messages,
		string rawRequestJson)
		=> Instance.InternalLogRequest(contextTag, endpoint, model, messages, rawRequestJson);

	public static void LogResponse(Guid? requestId, string rawResponseJson, string assistantContentRaw)
		=> Instance.InternalLogResponse(requestId, rawResponseJson, assistantContentRaw, 200);

	public static void LogResponse(Guid? requestId, string rawResponseJson, string assistantContentRaw, int httpStatus)
		=> Instance.InternalLogResponse(requestId, rawResponseJson, assistantContentRaw, httpStatus);

	public static void LogError(Guid? requestId, string errorSummary, int httpStatus, string responseBody, string exceptionType = null, string stackTrace = null)
		=> Instance.InternalLogError(requestId, errorSummary, httpStatus, responseBody, exceptionType, stackTrace);

	public static void SaveSessionToFile() => Instance.InternalSaveSessionToFile();

	private void InternalStartNewSession()
	{
		lock (_lock)
		{
			_sessionId = Guid.NewGuid().ToString();
			_entries.Clear();
			_lastSavePath = null;
		}
		Debug.Log($"[ChatTraceLogger] New session started: {_sessionId}");
	}

	private Guid InternalLogRequest(
		string contextTag,
		string endpoint,
		string model,
		IReadOnlyList<OpenAIClient.Msg> messages,
		string rawRequestJson)
	{
		var id = Guid.NewGuid();
		var traceMessages = new List<ChatTraceEntry.TraceMessage>();
		if (messages != null)
		{
			for (int i = 0; i < messages.Count; i++)
				traceMessages.Add(new ChatTraceEntry.TraceMessage
				{
					role = messages[i].role,
					content = messages[i].content
				});
		}

		var entry = new ChatTraceEntry
		{
			sessionId = _sessionId,
			requestId = id.ToString(),
			contextTag = string.IsNullOrWhiteSpace(contextTag) ? "Unspecified" : contextTag,
			timestampUtc = DateTime.UtcNow.ToString("o"),
			ok = false, // assume failure until LogResponse is called
			httpStatus = 0,
			request = new ChatTraceEntry.RequestPayload
			{
				endpoint = endpoint,
				model = model,
				responseFormatType = "json_object",
				messages = traceMessages,
				rawRequestJson = rawRequestJson
			},
			response = null,
			error = null,
			warnings = new List<string>()
		};

		lock (_lock) _entries.Add(entry);
		return id;
	}

	private void InternalLogResponse(Guid? requestId, string rawResponseJson, string assistantContentRaw, int httpStatus)
	{
		lock (_lock)
		{
			var entry = FindOrCreate(requestId);
			if (entry == null) return;

			// preserve raw content exactly (only trim trailing \r)
			var contentRaw = assistantContentRaw ?? "";
			if (contentRaw.EndsWith("\r"))
				contentRaw = contentRaw.Substring(0, contentRaw.Length - 1);

			var contentPretty = TraceUtils.TryPrettyPrintJson(contentRaw);
			var parsedJson = TraceUtils.TryParseJsonToString(contentRaw);

			entry.response = new ChatTraceEntry.ResponsePayload
			{
				rawResponseJson = rawResponseJson,
				assistantContentRaw = contentRaw,
				assistantContentPretty = contentPretty,
				parsedJson = parsedJson,
				assistantContent = contentRaw // backward-compat mirror
			};

			entry.ok = true;
			entry.httpStatus = httpStatus;
			entry.error = null;

			var warnings = TraceUtils.ValidateChatJson(contentRaw, entry.contextTag);
			entry.warnings.AddRange(warnings);
		}
	}

	private void InternalLogError(Guid? requestId, string errorSummary, int httpStatus, string responseBody, string exceptionType = null, string stackTrace = null)
	{
		lock (_lock)
		{
			var entry = FindOrCreate(requestId);
			if (entry == null) return;

			entry.ok = false;
			entry.httpStatus = httpStatus;
			entry.error = new ChatTraceEntry.ErrorPayload
			{
				summary = errorSummary,
				httpStatus = httpStatus,
				responseBody = responseBody,
				exceptionType = exceptionType,
				stackTrace = stackTrace
			};
		}
	}

	private ChatTraceEntry FindOrCreate(Guid? requestId)
	{
		if (requestId == null) return null;
		var id = requestId.ToString();
		var entry = _entries.FirstOrDefault(e => e.requestId == id);
		if (entry != null) return entry;

		// not found, create a stub entry (for error reporting)
		entry = new ChatTraceEntry
		{
			sessionId = _sessionId,
			requestId = id,
			contextTag = "Unspecified",
			timestampUtc = DateTime.UtcNow.ToString("o"),
			ok = false,
			httpStatus = 0,
			request = null,
			response = null,
			error = null,
			warnings = new List<string>()
		};
		_entries.Add(entry);
		return entry;
	}

	private void InternalSaveSessionToFile()
	{
		lock (_lock)
		{
			if (_entries.Count == 0) return;
			if (_lastSavePath != null)
			{
				Debug.Log($"[ChatTraceLogger] Session {_sessionId} already saved to {_lastSavePath}");
				return;
			}

			var fileName = $"trace_{_sessionId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jsonl";
			var filePath = Path.Combine(_traceFolderPath, fileName);
			try
			{
				using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
				using (var writer = new StreamWriter(stream))
				{
					foreach (var entry in _entries)
					{
						var json = JsonUtility.ToJson(entry);
						writer.WriteLine(json);
					}
				}
				_lastSavePath = filePath;
				Debug.Log($"[ChatTraceLogger] Session {_sessionId} saved to {_lastSavePath}");
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[ChatTraceLogger] Failed to save session to file: {ex.Message}");
			}
		}
	}

	private void OnDisable() => TryAutoSave("[ChatTraceLogger] OnDisable auto-save");
	private void OnApplicationQuit() => TryAutoSave("[ChatTraceLogger] OnApplicationQuit auto-save");

	private void TryAutoSave(string reason)
	{
		if (_entries.Count == 0) return;
		if (_lastSavePath != null) return; // already saved

		Debug.Log($"[ChatTraceLogger] {reason}: attempting auto-save...");
		InternalSaveSessionToFile();
	}
}
