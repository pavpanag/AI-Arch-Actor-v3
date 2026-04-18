using System;
using System.Collections.Generic;

namespace AaltoSystemV3
{

/// <summary>
/// POCO for a single traced OpenAI API call + response.
/// All fields are serialized to JSONL for offline analysis.
/// 
/// Fields:
/// - sessionId: GUID string identifying the play session (new GUID per app start)
/// - requestId: GUID string identifying this individual API call
/// - contextTag: phase label (e.g., "Chat:Turn", "Interview:SynthesizeBlueprint")
/// - timestampUtc: ISO-8601 UTC timestamp of the request
/// - ok: true if no HTTP/exception error occurred
/// - httpStatus: HTTP response code (0 if request never completed)
/// - request: sent payload (model, messages, etc.)
/// - response: received payload (assistantContentRaw, assistantContentPretty, parsedJson, etc.)
/// - error: null on success; on failure: { summary, httpStatus, responseBody, exceptionType, stackTrace }
/// - warnings: array of validation warnings (non-fatal issues)
/// </summary>
[Serializable]
public class ChatTraceEntry
{
	[Serializable]
	public class RequestPayload
	{
		public string endpoint;
		public string model;
		public string responseFormatType;
		public List<TraceMessage> messages;
		public string rawRequestJson;
	}

	[Serializable]
	public class TraceMessage
	{
		public string role;
		public string content;
	}

	[Serializable]
	public class ResponsePayload
	{
		public string rawResponseJson;
		public string assistantContentRaw;    // exact string from choices[0].message.content
		public string assistantContentPretty; // prettified JSON if parseable, else null
		public string parsedJson;             // stringified parsed object if object, else null
		public string assistantContent;        // backward-compat mirror
	}

	[Serializable]
	public class ErrorPayload
	{
		public string summary;
		public int httpStatus;
		public string responseBody;
		public string exceptionType;
		public string stackTrace;
	}

	public string sessionId;
	public string requestId;
	public string contextTag;
	public string timestampUtc;
	public bool ok;
	public int httpStatus;
	public RequestPayload request;
	public ResponsePayload response;
	public ErrorPayload error;
	public List<string> warnings;
}
}
