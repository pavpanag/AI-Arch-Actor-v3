using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Utility functions for trace logging: JSON parsing, pretty-printing, validation.
/// </summary>
public static class TraceUtils
{
	/// <summary>
	/// Attempt to parse a JSON string and return a prettified version (indented).
	/// If parsing fails, return null.
	/// </summary>
	public static string TryPrettyPrintJson(string json)
	{
		if (string.IsNullOrWhiteSpace(json)) return null;

		try
		{
			// simple pretty-print: add indentation for { [ and newlines for ,
			var sb = new StringBuilder();
			int indent = 0;
			bool inString = false;
			bool escaped = false;

			foreach (var ch in json)
			{
				if (escaped)
				{
					sb.Append(ch);
					escaped = false;
					continue;
				}

				if (ch == '\\' && inString)
				{
					sb.Append(ch);
					escaped = true;
					continue;
				}

				if (ch == '\"')
				{
					inString = !inString;
					sb.Append(ch);
					continue;
				}

				if (!inString)
				{
					switch (ch)
					{
						case '{':
						case '[':
							sb.Append(ch);
							indent++;
							sb.AppendLine();
							sb.Append(new string(' ', indent * 2));
							break;
						case '}':
						case ']':
							indent--;
							sb.AppendLine();
							sb.Append(new string(' ', indent * 2));
							sb.Append(ch);
							break;
						case ',':
							sb.Append(ch);
							sb.AppendLine();
							sb.Append(new string(' ', indent * 2));
							break;
						case ':':
							sb.Append(ch);
							sb.Append(' ');
							break;
						case ' ':
						case '\n':
						case '\r':
						case '\t':
							// skip whitespace outside strings
							break;
						default:
							sb.Append(ch);
							break;
					}
				}
				else
				{
					sb.Append(ch);
				}
			}

			return sb.ToString();
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Try to parse assistantContentRaw as JSON and return stringified parsed object.
	/// Return null if parsing fails or input is not an object.
	/// </summary>
	public static string TryParseJsonToString(string rawJson)
	{
		if (string.IsNullOrWhiteSpace(rawJson)) return null;

		try
		{
			// simple validation: check if it's a JSON object or array
			var trimmed = rawJson.Trim();
			if ((trimmed.StartsWith("{") && trimmed.EndsWith("}")) ||
				(trimmed.StartsWith("[") && trimmed.EndsWith("]")))
			{
				// it looks like JSON; return as-is for now
				// in a real scenario, you'd use Unity.Serialization or Newtonsoft.Json
				return trimmed;
			}

			return null;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Count words in a string (simple split on whitespace).
	/// </summary>
	public static int CountWords(string s)
	{
		if (string.IsNullOrWhiteSpace(s)) return 0;
		return s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
	}

	/// <summary>
	/// Validate a chat JSON response and return warnings.
	/// </summary>
	public static List<string> ValidateChatJson(string jsonText, string contextTag = "")
	{
		var warnings = new List<string>();

		if (string.IsNullOrWhiteSpace(jsonText))
		{
			warnings.Add("assistantContentRaw is empty or null");
			return warnings;
		}

		var trimmed = jsonText.Trim();
		if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
		{
			warnings.Add("root is not a JSON object");
			return warnings;
		}

		var lowerJson = jsonText.ToLowerInvariant();

		// check for required keys based on context
		if (contextTag.StartsWith("Chat:", StringComparison.OrdinalIgnoreCase))
		{
			if (!lowerJson.Contains("\"color\""))
				warnings.Add("missing required key: color");
			if (!lowerJson.Contains("\"reply\""))
				warnings.Add("missing required key: reply");
			if (!lowerJson.Contains("\"explanation\""))
				warnings.Add("missing required key: explanation");
		}
		else if (contextTag.Contains("Blueprint", StringComparison.OrdinalIgnoreCase))
		{
			if (!lowerJson.Contains("\"blueprint\""))
				warnings.Add("missing required key: blueprint");
		}

		return warnings;
	}

	/// <summary>
	/// Validate blueprint structure and return warnings.
	/// </summary>
	public static List<string> ValidateBlueprintJson(string jsonText)
	{
		var warnings = new List<string>();

		if (string.IsNullOrWhiteSpace(jsonText))
		{
			warnings.Add("blueprint JSON is empty");
			return warnings;
		}

		var trimmed = jsonText.Trim();
		if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
		{
			warnings.Add("blueprint root is not a JSON object");
			return warnings;
		}

		// naive check for blueprint structure
		var lowerJson = jsonText.ToLowerInvariant();
		if (!lowerJson.Contains("\"blueprint\""))
		{
			warnings.Add("missing 'blueprint' key");
		}
		else if (!lowerJson.Contains("\"core_character\""))
		{
			warnings.Add("blueprint missing 'core_character'");
		}

		// very naive word-count check for blueprint entries (if we can extract them)
		// This is a placeholder; a real implementation would parse JSON and check each field.
		var entries = new[] { "backstory", "objectives", "obstacles", "conditions", "given_circumstances" };
		foreach (var entry in entries)
		{
			var key = $"\"{entry}\"";
			if (jsonText.Contains(key, StringComparison.OrdinalIgnoreCase))
			{
				// found the field; would need real JSON parsing to validate words per entry
				// For now, just log that it exists
			}
		}

		return warnings;
	}
}
