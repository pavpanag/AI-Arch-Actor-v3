using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

public sealed class BlueprintChatController : MonoBehaviour
{
    [Header("Scene refs")]
    public OpenAIClient OpenAI;
    public TMP_InputField UserInput;
    public TMP_InputField DirectingInput;
    public TMP_Text ConversationLog;
    // new realtime overview fields
    public TMP_Text BlueprintDisplay;
    public TMP_Text DirectingDisplay;

    [Header("Config")]
    public string Model = "gpt-4o-mini";

    [Header("UI")]
    [Tooltip("Max number of lines to keep in the on-screen ConversationLog (rolling)")]
    public int MaxDisplayLines = 12;

    [Header("Lighting")]
    [Tooltip("Optional: assign specific scene Lights to color. If empty, script will auto-find scene Lights.")]
    public Light[] TargetLights;
    [Tooltip("Number of lights (from TargetLights or auto-found list) to set when assistant replies with a color.")]
    public int NumLightsToSet = 1;

    // intensity buckets (same concept as reference)
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

    private sealed class ChatMsg
    {
        public string Role;
        public string Content;
        public ChatMsg(string role, string content) { Role = role; Content = content; }
    }

    private string LatestBlueprintPath =>
        Path.Combine(Application.persistentDataPath, "latest_blueprint.json");

    private string _blueprintPretty = "";
    private string _memorySummary = "";
    private readonly List<ChatMsg> _history = new();

    private string _lastColor = "";
    private string _lastReply = "";
    private string _lastExplanation = "";
    private string _lastUserText = "";

    private readonly Queue<string> _pendingAppends = new Queue<string>();
    private readonly List<string> _displayLines = new List<string>();
    private string _directingNotes = "";
    private string _directingPreview = "";

    // external input mode (inspector)
    public enum ExternalInputMode { Microphone, Typed }
    [Tooltip("Select how external speech is provided (Microphone = external bridge/OSC, Typed = simulate speech from inspector)")]
    public ExternalInputMode InputMode = ExternalInputMode.Microphone;

    [Tooltip("If false and InputMode=Microphone, manual typing into UserInput will be ignored (speech-only mode).")]
    public bool AllowTypedUserInputWhenMicrophone = true;

    [Tooltip("Simulated/transcribed text to send when using Typed mode")]
    [TextArea(1,4)]
    public string SimulatedSpeechText = "";

    private void Awake()
    {
        // optional convenience; safe if you prefer button hooks instead
        if (UserInput != null)
            UserInput.onSubmit.AddListener(_ => SubmitChat());

        // live preview and Enter-to-add for directing input
        if (DirectingInput != null)
        {
            DirectingInput.onValueChanged.AddListener(s => OnDirectingChanged(s));
            DirectingInput.onSubmit.AddListener(_ => AddDirectingNote());
        }

        // auto-find ConversationLog if not set
        if (ConversationLog == null)
        {
            var byName = Array.Find(UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                t => t.name.Equals("ConversationLog", StringComparison.OrdinalIgnoreCase));
            if (byName != null) ConversationLog = byName;
            else
            {
                var all = UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (all.Length > 0) ConversationLog = all[0];
            }

            if (ConversationLog == null)
                Debug.LogWarning("[BlueprintChatController] ConversationLog not assigned and none found in scene.");
            else
                Debug.Log($"[BlueprintChatController] Auto-attached ConversationLog: {ConversationLog.name}");
        }

        // auto-find optional BlueprintDisplay and DirectingDisplay by name if not assigned
        if (BlueprintDisplay == null)
        {
            var bd = Array.Find(UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                t => t.name.Equals("BlueprintDisplay", StringComparison.OrdinalIgnoreCase));
            if (bd != null) BlueprintDisplay = bd;
        }
        if (DirectingDisplay == null)
        {
            var dd = Array.Find(UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                t => t.name.Equals("DirectingDisplay", StringComparison.OrdinalIgnoreCase));
            if (dd != null) DirectingDisplay = dd;
        }
    }

    private void OnDirectingChanged(string s)
    {
        _directingPreview = (s ?? "").Trim();
        Debug.Log($"[DIRECTING PREVIEW] {_directingPreview}");
        if (DirectingDisplay != null)
        {
            DirectingDisplay.text = string.IsNullOrWhiteSpace(_directingNotes) ? _directingPreview : _directingNotes + "\n" + _directingPreview;
        }
    }

    public void LoadLatestBlueprint()
    {
        if (!File.Exists(LatestBlueprintPath))
        {
            Append($"[error] No blueprint found at: {LatestBlueprintPath}");
            return;
        }

        var json = File.ReadAllText(LatestBlueprintPath);
        // avoid System.Text.Json dependency; keep stored JSON as-is
        _blueprintPretty = string.IsNullOrWhiteSpace(json) ? "{}" : json;

        Append($"\nLoaded blueprint: {LatestBlueprintPath}");
    }

    public void StartChat()
    {
        if (OpenAI == null) { Append("[error] OpenAIClient not assigned."); return; }
        if (UserInput == null || ConversationLog == null) { Append("[error] TMP refs missing."); return; }

        if (string.IsNullOrWhiteSpace(_blueprintPretty))
            LoadLatestBlueprint();

        if (string.IsNullOrWhiteSpace(_blueprintPretty))
            return;

        Append("\n=== BLUEPRINT CHAT ===");
        Append("Commands: /exit (no-op), /reset, /blueprint\n");

        // do not auto-clear; keep append-only log as requested
    }

    // Show concise usage instructions in the ConversationLog and Console.
    public void ShowUsage()
    {
        var msg = "BlueprintChatController usage: StartChat(); then type and SubmitChat(). Commands: /blueprint, /reset, /exit. Use AddDirectingNote() to save director notes.";
        Append(msg);
        Debug.Log("[BlueprintChatController] " + msg);
    }

    public void SubmitChat()
    {
        if (InputMode == ExternalInputMode.Microphone && !AllowTypedUserInputWhenMicrophone)
        {
            Append("[input] Typed input disabled (InputMode=Microphone). Use OSC speech instead.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_blueprintPretty))
        {
            Append("[error] No blueprint loaded. Call LoadLatestBlueprint() first.");
            return;
        }

        var userText = (UserInput.text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(userText)) return;

        UserInput.text = "";
        UserInput.ActivateInputField();

        _ = HandleChatAsync(userText);
    }

    private async Task HandleChatAsync(string userText)
    {
        try
        {
            _lastUserText = userText;

            if (userText.Equals("/blueprint", StringComparison.OrdinalIgnoreCase))
            {
                Append("\n--- BLUEPRINT ---");
                Append(_blueprintPretty);
                Append("---------------\n");
                return;
            }

            if (userText.Equals("/reset", StringComparison.OrdinalIgnoreCase))
            {
                _memorySummary = "";
                _history.Clear();
                _lastColor = "";
                _lastReply = "";
                _lastExplanation = "";
                _lastUserText = "";
                Append("[reset: cleared conversation memory]\n");
                return;
            }

            // /exit has no meaning in Unity button-driven loop; treat as informational
            if (userText.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                Append("[note] /exit is a terminal command; ignore in Unity.\n");
                return;
            }

            Append($"You: {userText}");
            _history.Add(new ChatMsg("user", userText));

            // summarize earlier for continuity
            if (_history.Count > 10)
            {
                var chunkSize = 6;
                _memorySummary = await SummarizeMemoryAsync(_memorySummary, _history.Take(chunkSize).ToList());
                _history.RemoveRange(0, chunkSize);
            }

            var directing = GetDirecting();
            var system = BuildSystemPrompt(_blueprintPretty, _memorySummary, _lastUserText, _lastColor, _lastReply, _lastExplanation, directing);

            var messages = new List<OpenAIClient.Msg> { new OpenAIClient.Msg("system", system) };
            messages.AddRange(_history.Select(h => new OpenAIClient.Msg(h.Role, h.Content)));

            // REMOVE: enforcement as separate user message (move into system prompt instead)
            // messages.Add(new OpenAIClient.Msg("user", enforcement));

            // first attempt
            var (ok, parsed, jsonText, errors) = await CallParseValidateAsync(messages, "Chat:Turn");
            if (!ok)
            {
                // single retry (keep contextTag explicit)
                var reasons = string.Join("; ", errors);
                messages.Add(new OpenAIClient.Msg("user",
$@"Your last JSON violated constraints: {reasons}.
Re-emit JSON that fully complies. Output JSON only.
Previous JSON was:
{jsonText}"));

                (ok, parsed, jsonText, errors) = await CallParseValidateAsync(messages, "Chat:RetryValidation");
                if (!ok)
                {
                    Append($"[validation failed] {string.Join("; ", errors)}\n");
                    return;
                }
            }

            Append($"Color: {parsed.color}");
            Append($"Room:  {parsed.reply}");
            Append($"Why:   {parsed.explanation}\n");

            // Do NOT add formatted assistant summary to history; store reply only (reduces token bloat)
            _history.Add(new ChatMsg("assistant", parsed.reply));

            _lastColor = parsed.color;
            _lastReply = parsed.reply;
            _lastExplanation = parsed.explanation;

            // attempt to extract structured light_behavior from the assistant JSON; prefer structured behavior if present
            if (TryExtractLightBehaviorFromJson(jsonText, out var lb))
            {
                Append($"LightBehavior → Hue:{lb.hue}, Brightness:{lb.brightness}, Saturation:{lb.saturation}, On:{lb.on}, Effect:{lb.effect}");
                try { ApplyLightBehavior(lb, Math.Max(0, NumLightsToSet)); } catch (Exception e) { Append($"[lights] {e.Message}"); }
            }
            else
            {
                // apply semantic color fallback
                try { ApplyColorToLights(_lastColor, Math.Max(0, NumLightsToSet)); } catch (Exception e) { Append($"[lights] {e.Message}"); }
            }
        }
        catch (Exception e)
        {
            Append($"[error] {e.Message}\n");
        }
    }

    private async Task<(bool ok, (string color, string reply, string explanation) parsed, string jsonText, List<string> errors)>
        CallParseValidateAsync(List<OpenAIClient.Msg> messages, string contextTag = "Chat:Turn")
    {
        var jsonText = await OpenAI.ChatCompletionsJsonAsync(messages, model: Model, contextTag: contextTag);

        (string color, string reply, string explanation) parsed = ParseColorReply(jsonText);
        var ok = ValidateChatJson(jsonText, parsed, out var errors);

        return (ok, parsed, jsonText, errors);
    }

    private static string BuildSystemPrompt(
        string blueprintPretty,
        string memorySummary,
        string lastUserText,
        string lastColor,
        string lastReply,
        string lastExplanation,
        string directing)
    {
        var memoryBlock = string.IsNullOrWhiteSpace(memorySummary)
            ? "Memory: (none yet)"
            : "Memory (summary of earlier dialogue; treat as true):\n" + memorySummary;

        var lastTurnState =
$@"LAST TURN STATE (treat as true):
- last_user: ""{lastUserText}""
- last_color: ""{(string.IsNullOrWhiteSpace(lastColor) ? "none" : lastColor)}""
- last_room_reply: ""{lastReply}""
- last_explanation: ""{lastExplanation}""";

        var colorSemantics =
@"Color Semantics (fixed mapping):
- red: anger
- blue: calm
- green: growth
- yellow: alert
- purple: ambition
- white: clarity
- black: despair";

        var directingBlock =
            "DIRECTOR INSTRUCTIONS (always obey if compatible):\n" +
            (string.IsNullOrWhiteSpace(directing) ? "(none)" : directing);

        // Embed enforcement schema/rules here (system prompt, once per request)
        var schemaBlock =
@"Respond with JSON only using this schema (no extra keys):
{
  ""color"": string,
  ""reply"": string,
  ""explanation"": string
}

Rules:
- color must follow Color Semantics.
- reply: theatrical, in-character, max 2 sentences.
- explanation MUST be 1–2 short sentences, <= 18 words total, and use ONE of these templates:
  - ""Stayed <color> because '<blueprint quote>' and you said '<user quote>'.""
  - ""Shifted " + SafeColor(lastColor) + @"-><color> because '<blueprint quote>' and you said '<user quote>'.""
- <blueprint quote>: exact/near-exact phrase from the blueprint text.
- <user quote>: exact/near-exact snippet from the latest user message.
- Use single ' around both quotes. Be concrete; avoid abstract filler words without evidence.";

        return
$@"You are a ROOM portrayed as an ACTOR.
Stay consistent with the character blueprint and the conversation.
Output MUST follow the JSON schema requested by the user message (no extra keys).

{schemaBlock}

{directingBlock}

CHARACTER BLUEPRINT (dramaturgy):
{blueprintPretty}

{memoryBlock}

{lastTurnState}

{colorSemantics}

Constraints:
- explanation must be concrete, 1–2 sentences, <= 18 words, include 2 short quotes (blueprint + user).
- Avoid vague filler like: chaos, destiny, energy, vibes, symbolic.";
    }

    private async Task<string> SummarizeMemoryAsync(string priorSummary, List<ChatMsg> chunk)
    {
        var directing = (DirectingInput != null ? (DirectingInput.text ?? "").Trim() : "");

        var system =
$@"You compress dialogue into durable memory for a roleplaying ROOM-ACTOR.
Keep it short and concrete. Do not invent new facts.
Output JSON only: {{ ""memory_summary"": string }}.

DIRECTOR INSTRUCTIONS (always obey if compatible):
{(string.IsNullOrWhiteSpace(directing) ? "(none)" : directing)}

Blueprint:
{_blueprintPretty}

Prior memory (may be empty):
{priorSummary}";

        var convoText = string.Join("\n", chunk.Select(m => $"{m.Role.ToUpperInvariant()}: {m.Content}"));

        var user =
$@"Summarize and merge into updated memory_summary.
Focus on: stakes, relationships, recurring motifs, unresolved tensions, current objective/obstacle shifts.

DIALOGUE CHUNK:
{convoText}";

        var jsonText = await OpenAI.ChatCompletionsJsonAsync(new[]
        {
            new OpenAIClient.Msg("system", system),
            new OpenAIClient.Msg("user", user)
        }, model: Model);

        var memory = ExtractStringPropertyFromJson(jsonText, "memory_summary");
        return memory ?? priorSummary ?? "";
    }

    private static (string color, string reply, string explanation) ParseColorReply(string jsonText)
    {
        var color = ExtractStringPropertyFromJson(jsonText, "color") ?? "unknown";
        var reply = ExtractStringPropertyFromJson(jsonText, "reply") ?? "unknown";
        var explanation = ExtractStringPropertyFromJson(jsonText, "explanation") ?? "unknown";

        if (string.IsNullOrWhiteSpace(color)) color = "unknown";
        if (string.IsNullOrWhiteSpace(reply)) reply = "unknown";
        if (string.IsNullOrWhiteSpace(explanation)) explanation = "unknown";

        return (color.Trim(), reply.Trim(), explanation.Trim());
    }

    private static string ExtractStringPropertyFromJson(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName)) return null;
        var key = $"\"{propertyName}\"";
        var pos = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (pos < 0) return null;
        var after = json.Substring(pos + key.Length);
        var colon = after.IndexOf(':');
        if (colon < 0) return null;
        var rest = after.Substring(colon + 1).TrimStart();
        if (!rest.StartsWith("\"")) return null;
        // extract quoted string
        var sb = new StringBuilder();
        bool esc = false;
        for (int i = 1; i < rest.Length; i++)
        {
            var ch = rest[i];
            if (esc) { sb.Append(ch); esc = false; continue; }
            if (ch == '\\') { esc = true; continue; }
            if (ch == '\"') break;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static bool ValidateChatJson(
        string jsonText,
        (string color, string reply, string explanation) parsed,
        out List<string> errors)
    {
        errors = new List<string>();

        // basic presence check only; remove rigid explanation validation rules
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            errors.Add("root is not an object or response empty");
            return false;
        }

        var lowered = jsonText.ToLowerInvariant();
        if (!lowered.Contains("\"color\"") || !lowered.Contains("\"reply\"") || !lowered.Contains("\"explanation\""))
        {
            errors.Add("must contain keys: color, reply, explanation");
            return false;
        }

        return true;
    }

    private static int WordCount(string s) =>
        string.IsNullOrWhiteSpace(s) ? 0 : s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static int CountSentences(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var terminators = s.Count(ch => ch == '.' || ch == '!' || ch == '?');
        return Math.Max(1, terminators);
    }

    private static string SafeColor(string c) => string.IsNullOrWhiteSpace(c) ? "none" : c.Trim();

    private void Update()
    {
        if (_pendingAppends.Count == 0 && string.IsNullOrWhiteSpace(_directingPreview)) return;
        lock (_pendingAppends)
        {
            while (_pendingAppends.Count > 0)
            {
                var line = _pendingAppends.Dequeue();
                _displayLines.Add(line);
                while (_displayLines.Count > MaxDisplayLines)
                    _displayLines.RemoveAt(0);
            }
        }

        if (ConversationLog != null)
        {
            var text = string.Join("\n", _displayLines);
            if (!string.IsNullOrWhiteSpace(_directingPreview))
                text = string.IsNullOrWhiteSpace(text) ? $"DIRECTING: {_directingPreview}" : text + "\n" + $"DIRECTING: {_directingPreview}";
            ConversationLog.text = text;
        }

        // update blueprint and directing overview panels
        if (BlueprintDisplay != null)
            BlueprintDisplay.text = string.IsNullOrWhiteSpace(_blueprintPretty) ? "(blueprint: none yet)" : _blueprintPretty;
        if (DirectingDisplay != null)
            DirectingDisplay.text = string.IsNullOrWhiteSpace(_directingNotes) && string.IsNullOrWhiteSpace(_directingPreview)
                ? "(directing: none)" : (_directingNotes + (string.IsNullOrWhiteSpace(_directingPreview) ? "" : (_directingNotes.Length > 0 ? "\n" : "") + _directingPreview));
    }

    private void Append(string line)
    {
        Debug.Log(line);
        lock (_pendingAppends)
        {
            _pendingAppends.Enqueue(line);
        }
    }

    // Add directing-note helper
    public void AddDirectingNote()
    {
        if (DirectingInput == null) return;
        var txt = (DirectingInput.text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(txt)) return;
        if (!string.IsNullOrWhiteSpace(_directingNotes)) _directingNotes += "\n";
        _directingNotes += txt;
        DirectingInput.text = "";
        Append($"[DIRECTOR NOTE ADDED] {txt}");
        if (DirectingDisplay != null) DirectingDisplay.text = _directingNotes;
    }

    private string GetDirecting()
    {
        var cur = (DirectingInput != null ? (DirectingInput.text ?? "").Trim() : "");
        if (string.IsNullOrWhiteSpace(_directingNotes)) return cur ?? "";
        if (string.IsNullOrWhiteSpace(cur)) return _directingNotes;
        return _directingNotes + "\n" + cur;
    }

    // Map semantic color names to Unity Color and apply to N lights.
    private void ApplyColorToLights(string colorName, int count)
    {
        if (count <= 0) return;
        if (string.IsNullOrWhiteSpace(colorName)) return;

        // ensure targets
        Light[] lights = TargetLights != null && TargetLights.Length > 0
            ? TargetLights
            : UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        if (lights == null || lights.Length == 0)
        {
            Append("[lights] No Light objects found in scene.");
            return;
        }

        var name = (colorName ?? "").Trim().ToLowerInvariant();
        Color col = name switch
        {
            "red" => Color.red,
            "blue" => Color.blue,
            "green" => Color.green,
            "yellow" => Color.yellow,
            "purple" => new Color(0.6f, 0.2f, 0.8f),
            "white" => Color.white,
            "black" => Color.black,
            "orange" => new Color(1f, 0.5f, 0f),
            "pink" => new Color(1f, 0.4f, 0.7f),
            _ => ParseHexOrDefault(colorName)
        };

        int applied = 0;
        for (int i = 0; i < lights.Length && applied < count; i++)
        {
            var L = lights[i];
            if (L == null) continue;

            // ensure the light is enabled so color/intensity are visible
            L.enabled = true;

            // apply color
            L.color = col;

            // set intensity: black -> 0, otherwise pick a visible intensity
            if (name == "black")
            {
                L.intensity = 0f;
            }
            else
            {
                // use configured buckets if requested, otherwise use medium as sensible default
                if (!useExampleIntensityBuckets)
                {
                    // no brightness info available for semantic color -> use medium as default
                    L.intensity = Mathf.Max(0f, intensityMedium);
                }
                else
                {
                    // semantic mapping: use medium intensity for visible color
                    L.intensity = Mathf.Max(0f, intensityMedium);
                }
            }

            applied++;
        }

        Append($"[lights] Applied color '{colorName}' to {applied} light(s).");
    }

    private static Color ParseHexOrDefault(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return Color.white;
        var s = input.Trim();
        if (s.StartsWith("#")) s = s.Substring(1);
        if (s.Length == 6)
        {
            if (byte.TryParse(s.Substring(0,2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(s.Substring(2,2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(s.Substring(4,2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                return new Color32(r, g, b, 255);
            }
        }
        return Color.white;
    }

    // new: structured light behavior representation
    private struct LightBehavior
    {
        public int hue;
        public int brightness;   // 0–100
        public int saturation;   // 0–100
        public bool on;
        public string effect;
    }

    // naive extractor for a nested "light_behavior" object with numeric and boolean fields
    private static bool TryExtractLightBehaviorFromJson(string json, out LightBehavior lb)
    {
        lb = new LightBehavior { hue = 0, brightness = 100, saturation = 100, on = true, effect = "" };
        if (string.IsNullOrWhiteSpace(json)) return false;
        var key = "\"light_behavior\"";
        var pos = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (pos < 0) return false;
        // find opening brace
        var brace = json.IndexOf('{', pos);
        if (brace < 0) return false;
        // find matching closing brace (simple depth counter)
        int depth = 0;
        int end = -1;
        for (int i = brace; i < json.Length; i++)
        {
            if (json[i] == '{') depth++;
            else if (json[i] == '}')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
        }
        if (end < 0) return false;
        var sub = json.Substring(brace, end - brace + 1);

        bool any = false;
        if (TryExtractIntPropertyFromJson(sub, "hue", out var hue)) { lb.hue = hue; any = true; }
        if (TryExtractIntPropertyFromJson(sub, "brightness", out var br)) { lb.brightness = br; any = true; }
        if (TryExtractIntPropertyFromJson(sub, "saturation", out var sat)) { lb.saturation = sat; any = true; }
        if (TryExtractBoolPropertyFromJson(sub, "on", out var onv)) { lb.on = onv; any = true; }
        var eff = ExtractStringPropertyFromJson(sub, "effect");
        if (!string.IsNullOrWhiteSpace(eff)) { lb.effect = eff; any = true; }

        return any;
    }

    private static bool TryExtractIntPropertyFromJson(string json, string prop, out int value)
    {
        value = 0;
        var key = $"\"{prop}\"";
        var pos = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (pos < 0) return false;
        var after = json.Substring(pos + key.Length);
        var colon = after.IndexOf(':');
        if (colon < 0) return false;
        var rest = after.Substring(colon + 1).TrimStart();
        var sb = new StringBuilder();
        int i = 0;
        // collect numeric characters, sign allowed
        while (i < rest.Length && (char.IsDigit(rest[i]) || rest[i] == '-' || rest[i] == '+')) { sb.Append(rest[i]); i++; }
        if (sb.Length == 0) return false;
        return int.TryParse(sb.ToString(), out value);
    }

    private static bool TryExtractBoolPropertyFromJson(string json, string prop, out bool value)
    {
        value = false;
        var key = $"\"{prop}\"";
        var pos = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (pos < 0) return false;
        var after = json.Substring(pos + key.Length);
        var colon = after.IndexOf(':');
        if (colon < 0) return false;
        var rest = after.Substring(colon + 1).TrimStart().ToLowerInvariant();
        if (rest.StartsWith("true")) { value = true; return true; }
        if (rest.StartsWith("false")) { value = false; return true; }
        return false;
    }

    // apply structured LightBehavior to first 'count' lights
    private void ApplyLightBehavior(LightBehavior lb, int count)
    {
        if (count <= 0) return;
        Light[] lights = TargetLights != null && TargetLights.Length > 0
            ? TargetLights
            : UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        if (lights == null || lights.Length == 0)
        {
            Append("[lights] No Light objects found in scene.");
            return;
        }

        Color col = Color.white;
        try
        {
            col = Color.HSVToRGB(Mathf.Clamp01(lb.hue / 360f), Mathf.Clamp01(lb.saturation / 100f), 1f);
        }
        catch { col = Color.white; }

        int applied = 0;
        for (int i = 0; i < lights.Length && applied < count; i++)
        {
            var L = lights[i];
            if (L == null) continue;
            L.enabled = lb.on;
            if (!lb.on) { applied++; continue; }
            L.color = col;

            // intensity mapping
            if (!useExampleIntensityBuckets)
            {
                float norm = Mathf.Clamp01(lb.brightness / 100f);
                L.intensity = Mathf.Lerp(0f, 10f, norm);
            }
            else
            {
                int b = Mathf.Clamp(lb.brightness, 0, 100);
                float appliedIntensity = intensityHigh;
                if (b <= lowUpper) appliedIntensity = Mathf.Max(0f, intensityLow);
                else if (b <= mediumUpper) appliedIntensity = Mathf.Max(0f, intensityMedium);
                else appliedIntensity = Mathf.Max(0f, intensityHigh);
                L.intensity = appliedIntensity;
            }

            applied++;
        }

        Append($"[lights] Applied structured behavior to {applied} light(s).");
    }

    // New: receive transcribed/external speech and route into the chat flow
    public void ReceiveExternalSpeech(string text)
    {
        if (InputMode != ExternalInputMode.Microphone)
        {
            Append("[input] Ignored external speech (InputMode=Typed).");
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        // If a TMP_InputField is assigned, route through the UI submission path for consistent behavior
        if (UserInput != null)
        {
            UserInput.text = text;
            SubmitChat();
            return;
        }

        // Fallback: call the handler directly
        _ = HandleChatAsync(text);
    }

    // New: helper for inspector button / UI to submit the SimulatedSpeechText
    public void SubmitSimulatedSpeech()
    {
        if (InputMode != ExternalInputMode.Typed)
        {
            Append("[input] Ignored simulated speech (InputMode=Microphone).");
            return;
        }
        ReceiveTypedSimulatedSpeech(SimulatedSpeechText ?? "");
    }

    // Keep simulated speech separate so ReceiveExternalSpeech can be gated to Microphone only.
    private void ReceiveTypedSimulatedSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (UserInput != null)
        {
            UserInput.text = text;
            SubmitChat();
            return;
        }

        _ = HandleChatAsync(text);
    }
}
