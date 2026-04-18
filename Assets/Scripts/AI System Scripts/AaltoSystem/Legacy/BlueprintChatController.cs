using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace AaltoSystemV3
{

public sealed class BlueprintChatController : MonoBehaviour
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

    [Header("Scene refs")]
    public OpenAIClient OpenAI;
    public TMP_InputField UserInput;
    public TMP_InputField DirectingInput;
    public TMP_Text ConversationLog;
    // new realtime overview fields
    public TMP_Text BlueprintDisplay;
    public TMP_Text DirectingDisplay;

    [Header("Config")]
    [InspectorName("Model (OpenAI Dropdown)")]
    public OpenAIModelPreset Model = OpenAIModelPreset.Gpt4oMini;

    [Header("UI")]
    [Tooltip("Max number of lines to keep in the on-screen ConversationLog (rolling)")]
    public int MaxDisplayLines = 12;

    [Header("Lighting")]
    [Tooltip("Optional: assign specific scene Lights to color. If empty, script will auto-find scene Lights.")]
    public Light[] TargetLights;
    [Tooltip("Number of lights (from TargetLights or auto-found list) to set when assistant replies with a color.")]
    public int NumLightsToSet = 1;
    [Tooltip("Duration (seconds) to turn lights off before applying new color. Creates visual break between responses.")]
    [Range(0f, 3f)]
    public float LightTransitionBlackoutDuration = 0.5f;

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

    // Color variety tracking
    private readonly Queue<string> _recentColors = new Queue<string>();
    private const int MaxColorHistory = 5;

    private readonly Queue<string> _pendingAppends = new Queue<string>();
    private readonly List<string> _displayLines = new List<string>();
    private string _directingNotes = ""; // local cache; persisted in DirectingNotesStore
    private string _directingPreview = "";

    private string SelectedModelId => Model switch
    {
        OpenAIModelPreset.Gpt54 => "gpt-5.4",
        OpenAIModelPreset.Gpt54Mini => "gpt-5.4-mini",
        OpenAIModelPreset.Gpt41 => "gpt-4.1",
        OpenAIModelPreset.Gpt41Mini => "gpt-4.1-mini",
        OpenAIModelPreset.Gpt4oMini => "gpt-4o-mini",
        _ => "gpt-4o-mini"
    };

    private CancellationTokenSource _cts;

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
        _cts = new CancellationTokenSource();

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

        // sync local cache from shared store at startup
        _directingNotes = DirectingNotesStore.Notes;
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void OnDirectingChanged(string s)
    {
        _directingPreview = (s ?? "").Trim();
        Debug.Log($"[DIRECTING] Preview (typing): {_directingPreview}");
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

        var raw = File.ReadAllText(LatestBlueprintPath, Encoding.UTF8);

        // Normalize any escaped/newline artifacts that slipped into the stored file
        var cleaned = TraceUtils.NormalizeEmbeddedJson(raw);

        // basic validation: require an object root
        if (string.IsNullOrWhiteSpace(cleaned) || !cleaned.TrimStart().StartsWith("{"))
        {
            Append($"[error] Blueprint file contains invalid JSON.");
            return;
        }

        // Optional: if normalization changed the content, overwrite the file to repair it
        if (cleaned != raw)
        {
            try
            {
                var pretty = TraceUtils.TryPrettyPrintJson(cleaned) ?? cleaned;
                File.WriteAllText(LatestBlueprintPath, pretty, Encoding.UTF8);
                Append($"[blueprint] Auto-repaired and saved normalized version.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BlueprintChatController] Auto-repair save failed: {ex.Message}");
            }
        }

        _blueprintPretty = cleaned;
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

        // NOTE: Sending chat must NOT modify directing notes. Use AddDirectingNote() to commit.

        _ = HandleChatAsync(userText);
    }

    private async Task HandleChatAsync(string userText)
    {
        if (_cts == null || _cts.IsCancellationRequested) return;

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
            var recentColorsText = GetRecentColorsText();
            var system = BuildSystemPrompt(_blueprintPretty, _memorySummary, _lastUserText, _lastColor, _lastReply, _lastExplanation, directing, recentColorsText);

            var messages = new List<OpenAIClient.Msg> { new OpenAIClient.Msg("system", system) };
            messages.AddRange(_history.Select(h => new OpenAIClient.Msg(h.Role, h.Content)));
            var dialogueWindowUsed = BuildDialogueWindowUsed(messages);

            // first attempt
            var (ok, parsed, jsonText, errors) = await CallParseValidateAsync(messages, "Chat:Turn", directing, system);
            
            if (_cts.IsCancellationRequested) return;

            if (!ok)
            {
                // single retry (keep contextTag explicit)
                var reasons = string.Join("; ", errors);
                messages.Add(new OpenAIClient.Msg("user",
$@"Your last JSON violated constraints: {reasons}.
Re-emit JSON that fully complies. Output JSON only.
Previous JSON was:
{jsonText}"));

                (ok, parsed, jsonText, errors) = await CallParseValidateAsync(messages, "Chat:RetryValidation", directing, system);
                
                if (_cts.IsCancellationRequested) return;
                
                if (!ok)
                {
                    Append($"[validation failed] {string.Join("; ", errors)}\n");
                    return;
                }
            }

            // Remap disallowed/empty colors to a visible, steady color (last stable or white)
            if (string.IsNullOrWhiteSpace(parsed.color) || parsed.color.Equals("black", StringComparison.OrdinalIgnoreCase))
                parsed.color = string.IsNullOrWhiteSpace(_lastColor) ? "white" : _lastColor;

            // Track color for variety
            TrackColor(parsed.color);

            Append($"Color: {parsed.color}");
            Append($"Room:  {parsed.reply}");
            Append($"Why:   {parsed.explanation}\n");

            _history.Add(new ChatMsg("assistant", parsed.reply));

            _lastColor = parsed.color;
            _lastReply = parsed.reply;
            _lastExplanation = parsed.explanation;

            // Apply blackout before new color
            if (LightTransitionBlackoutDuration > 0f)
            {
                TurnOffLights(Math.Max(0, NumLightsToSet));
                await Task.Delay((int)(LightTransitionBlackoutDuration * 1000), _cts.Token);
            }

            if (_cts.IsCancellationRequested) return;

            if (TryExtractLightBehaviorFromJson(jsonText, out var lb))
            {
                Append($"LightBehavior → Hue:{lb.hue}, Brightness:{lb.brightness}, Saturation:{lb.saturation}, On:{lb.on}, Effect:{lb.effect}");
                bool executionSucceeded = true;
                string executionResult = "Applied structured light behavior.";
                try { ApplyLightBehavior(lb, Math.Max(0, NumLightsToSet)); }
                catch (Exception e)
                {
                    executionSucceeded = false;
                    executionResult = e.Message;
                    Append($"[lights] {e.Message}");
                }

                LogScenicDecision(
                    latestMoveText: userText,
                    dialogueWindowUsed: dialogueWindowUsed,
                    intendedAction: parsed.reply,
                    selectedActionLabel: string.IsNullOrWhiteSpace(lb.effect) ? parsed.color : lb.effect,
                    reason: parsed.explanation,
                    executionSucceeded: executionSucceeded,
                    executionResult: executionResult);
            }
            else
            {
                bool executionSucceeded = true;
                string executionResult = "Applied color fallback lighting.";
                try { ApplyColorToLights(_lastColor, Math.Max(0, NumLightsToSet)); }
                catch (Exception e)
                {
                    executionSucceeded = false;
                    executionResult = e.Message;
                    Append($"[lights] {e.Message}");
                }

                LogScenicDecision(
                    latestMoveText: userText,
                    dialogueWindowUsed: dialogueWindowUsed,
                    intendedAction: parsed.reply,
                    selectedActionLabel: parsed.color,
                    reason: parsed.explanation,
                    executionSucceeded: executionSucceeded,
                    executionResult: executionResult);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during domain reload or component destruction
            Debug.Log("[BlueprintChatController] Operation cancelled (expected during cleanup).");
        }
        catch (Exception e)
        {
            Append($"[error] {e.Message}\n");
        }
    }

    private async Task<(bool ok, (string color, string reply, string explanation) parsed, string jsonText, List<string> errors)>
        CallParseValidateAsync(List<OpenAIClient.Msg> messages, string contextTag, string directing, string systemPrompt)
    {
        LogRequestDebug(contextTag, directing, systemPrompt, messages);
        var jsonText = await OpenAI.ChatCompletionsJsonAsync(messages, model: SelectedModelId, contextTag: contextTag);

        (string color, string reply, string explanation) parsed = ParseColorReply(jsonText);
        var ok = ValidateChatJson(jsonText, parsed, out var errors);

        return (ok, parsed, jsonText, errors);
    }

    private void TrackColor(string color)
    {
        if (string.IsNullOrWhiteSpace(color)) return;
        var normalized = color.Trim().ToLowerInvariant();
        _recentColors.Enqueue(normalized);
        while (_recentColors.Count > MaxColorHistory)
            _recentColors.Dequeue();
    }

    private string GetRecentColorsText()
    {
        if (_recentColors.Count == 0) return "none";
        return string.Join(", ", _recentColors);
    }

    private static string BuildSystemPrompt(
        string blueprintPretty,
        string memorySummary,
        string lastUserText,
        string lastColor,
        string lastReply,
        string lastExplanation,
        string directing,
        string recentColorsText)
    {
        var memoryBlock = string.IsNullOrWhiteSpace(memorySummary)
            ? "Memory: (none yet)"
            : "Memory (summary of earlier dialogue; treat as true):\n" + memorySummary;

        var lastTurnState =
$@"LAST TURN STATE (treat as true):
- last_user: ""{lastUserText}""
- last_color: ""{(string.IsNullOrWhiteSpace(lastColor) ? "none" : lastColor)}""
- last_room_reply: ""{lastReply}""
- last_explanation: ""{lastExplanation}""
- recent_colors (last {MaxColorHistory}): {recentColorsText}";

        var directingBlock = string.IsNullOrWhiteSpace(directing)
            ? ""
            : $"DIRECTOR INSTRUCTIONS (HIGHEST PRIORITY — obey unless impossible):\n{directing}\n\n";

        var schemaBlock =
@"Respond with JSON only using this schema (no extra keys):
{
  ""color"": string,
  ""reply"": string,
  ""explanation"": string
}

Rules:
- color: ONLY use one of these: red, green, blue, yellow, white, orange, purple, cyan. 
  * Choose based on SPECIFIC emotional shifts, dramatic beats, or character state changes
  * VARY your color choices intentionally - avoid repeating recent colors unless dramatically justified
  * Each color should reflect a distinct emotional quality:
    - red: passion, anger, danger, intensity
    - blue: calm, sadness, introspection, cold
    - green: growth, envy, nature, unease
    - yellow: joy, caution, energy, warmth
    - white: clarity, emptiness, purity, starkness
    - orange: excitement, transition, warmth, creativity
    - purple: mystery, luxury, spirituality, tension
    - cyan: detachment, technology, coolness, clarity
  * If you've used a color recently (see recent_colors above), choose a DIFFERENT one unless there's a compelling dramatic reason to repeat
  * Let the conversation's emotional arc guide your color choices, not atmosphere alone
- reply: theatrical, in-character, max 2 sentences. NEVER repeat the exact same reply from last_room_reply; vary your language and imagery.
- explanation: briefly explain WHY this specific color for THIS moment (not just general mood) - what shifted?";

        return
    $@"You are a ROOM portrayed as an ACTOR.
Stay consistent with the character blueprint and the conversation.
Output MUST follow the JSON schema requested by the user message (no extra keys).

{directingBlock}{schemaBlock}

CHARACTER BLUEPRINT (dramaturgy):
{blueprintPretty}

{memoryBlock}

{lastTurnState}

Guidance:
- Always follow the DIRECTOR INSTRUCTIONS above (highest priority).
- Choose colors INTENTIONALLY based on emotional beats, not default atmosphere. Each color change should mark a shift.
- Avoid repeating colors you've used recently unless there's a strong dramatic reason.
- You may reference any part of the blueprint that feels relevant to your response.
- Do not repeat your previous reply verbatim; vary your theatrical language.";
    }

    private async Task<string> SummarizeMemoryAsync(string priorSummary, List<ChatMsg> chunk)
    {
        if (_cts == null || _cts.IsCancellationRequested) return priorSummary ?? "";

        // Only use persisted directing notes, NOT live input
        var directing = _directingNotes ?? "";

        var system =
$@"You compress dialogue into durable memory for a roleplaying ROOM-ACTOR.
Keep it short and concrete. Do not invent new facts.
Output JSON only: {{ ""memory_summary"": string }}.

DIRECTOR INSTRUCTIONS (HIGHEST PRIORITY — obey unless impossible):
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

        LogRequestDebug("Chat:SummarizeMemory", directing, system, new List<OpenAIClient.Msg>
        {
            new OpenAIClient.Msg("system", system),
            new OpenAIClient.Msg("user", user)
        });
        var jsonText = await OpenAI.ChatCompletionsJsonAsync(new[]
        {
            new OpenAIClient.Msg("system", system),
            new OpenAIClient.Msg("user", user)
        }, model: SelectedModelId);

        if (_cts.IsCancellationRequested) return priorSummary ?? "";

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
        _directingNotes = DirectingNotesStore.Notes;
        if (DirectingDisplay != null)
            DirectingDisplay.text = string.IsNullOrWhiteSpace(_directingNotes)
                ? (string.IsNullOrWhiteSpace(_directingPreview) ? "(directing: none)" : _directingPreview)
                : (_directingNotes + (string.IsNullOrWhiteSpace(_directingPreview) ? "" : "\n" + _directingPreview));
    }

    private void Append(string line)
    {
        Debug.Log(line);
        lock (_pendingAppends)
        {
            _pendingAppends.Enqueue(line);
        }
    }

    private void LogScenicDecision(
        string latestMoveText,
        string dialogueWindowUsed,
        string intendedAction,
        string selectedActionLabel,
        string reason,
        bool executionSucceeded,
        string executionResult)
    {
        try
        {
            ScenicEventLogger.LogEvent(
                latestMoveText: latestMoveText,
                dialogueWindowUsed: dialogueWindowUsed,
                characterSummary: _memorySummary,
                previousObjective: "n/a (legacy color flow)",
                previousStance: "n/a (legacy color flow)",
                updatedObjective: "n/a (legacy color flow)",
                updatedStance: "n/a (legacy color flow)",
                intendedAction: intendedAction,
                selectedActionLabel: selectedActionLabel,
                resolvedMemoryId: string.Empty,
                resolvedMemoryLabel: string.Empty,
                reason: reason,
                executionSucceeded: executionSucceeded,
                executionResult: executionResult,
                rawResponseReference: ChatTraceLogger.LatestResponseFilePath
            );
        }
        catch (Exception ex)
        {
            Append($"[scenic-log] Failed to write scenic event: {ex.Message}");
        }
    }

    private static string BuildDialogueWindowUsed(List<OpenAIClient.Msg> messages, int maxChars = 2400)
    {
        if (messages == null || messages.Count == 0) return "";

        var sb = new StringBuilder();
        for (int i = 0; i < messages.Count; i++)
        {
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(messages[i].role);
            sb.Append(": ");
            sb.Append(messages[i].content ?? "");
        }

        var full = sb.ToString();
        if (full.Length <= maxChars) return full;
        return full.Substring(0, maxChars) + "...[truncated]";
    }

    // Add directing-note helper (explicit commit). Appends to persistent store and updates UI.
    public void AddDirectingNote()
    {
        PersistDirectingFromInput(append: true, clearInput: true);
    }

    // Persist directorial instructions as stable state; append-only on explicit commit.
    private void PersistDirectingFromInput(bool append, bool clearInput = false)
    {
        if (DirectingInput == null) return;
        var txt = (DirectingInput.text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(txt)) return;

        if (!append) return; // append-only: no overwrite path
        DirectingNotesStore.Append(txt);
        _directingNotes = DirectingNotesStore.Notes;

        if (clearInput) DirectingInput.text = "";
            Append($"[DIRECTING] Committed (persistent): {txt}");
            Debug.Log($"[DIRECTING] Full stored notes:\n{_directingNotes}");
        if (DirectingDisplay != null) DirectingDisplay.text = _directingNotes;
    }

    private string GetDirecting()
    {
        // Only return persisted notes from shared store, NOT the live preview field
        _directingNotes = DirectingNotesStore.Notes;
        return _directingNotes ?? "";
    }

    // Debug: verify directing persistence and presence in the system prompt for each request
    private void LogRequestDebug(string contextTag, string directing, string systemPrompt, List<OpenAIClient.Msg> messages)
    {
        var hasDirecting = !string.IsNullOrWhiteSpace(directing);
        var probe = GetProbe(directing, 32);
        var systemHasProbe = !string.IsNullOrWhiteSpace(systemPrompt) && !string.IsNullOrWhiteSpace(probe) && systemPrompt.IndexOf(probe, StringComparison.Ordinal) >= 0;

        var systemPreview = Preview(systemPrompt, 240);
        var recentColorsText = GetRecentColorsText();
        Debug.Log($"[ChatRequest:{contextTag}] directing={(hasDirecting ? "non-empty" : "empty")}, system_has_directing={(systemHasProbe ? "yes" : "no")}, recent_colors={recentColorsText}");
        Debug.Log($"[ChatRequest:{contextTag}] system preview: {systemPreview}");
        Debug.Log($"[ChatRequest:{contextTag}] roles: {BuildRolesString(messages)}");

        // Full message dump for debugging
        Debug.Log($"[ChatRequest:{contextTag}] === FULL REQUEST DETAILS ===");
        if (!string.IsNullOrWhiteSpace(directing))
            Debug.Log($"[ChatRequest:{contextTag}] Persisted directing notes:\n{directing}");
        else
            Debug.Log($"[ChatRequest:{contextTag}] Persisted directing notes: (empty)");
        
        Debug.Log($"[ChatRequest:{contextTag}] Message count: {messages.Count}");
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            var preview = msg.content.Length <= 500 ? msg.content : msg.content.Substring(0, 500) + $"... ({msg.content.Length} chars total)";
            Debug.Log($"[ChatRequest:{contextTag}] Message[{i}] role={msg.role}, content:\n{preview}");
        }
        Debug.Log($"[ChatRequest:{contextTag}] === END REQUEST DETAILS ===");
    }

    private static string BuildRolesString(List<OpenAIClient.Msg> messages)
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

    private static string Preview(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(empty)";
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    private static string GetProbe(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();
        return t.Length <= max ? t : t.Substring(0, max);
    }

    // Map semantic color names to Unity Color and apply to N lights.
    private void ApplyColorToLights(string colorName, int count)
    {
        if (count <= 0) return;

        // Fallback: keep the last stable color (or white) if black/empty is requested
        var effective = (colorName ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(effective) || effective == "black")
            effective = string.IsNullOrWhiteSpace(_lastColor) ? "white" : _lastColor.ToLowerInvariant();

        Light[] lights = TargetLights != null && TargetLights.Length > 0
            ? TargetLights
            : UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (lights == null || lights.Length == 0)
        {
            Append("[lights] No Light objects found in scene.");
            return;
        }

        Color col = effective switch
        {
            "red" => Color.red,
            "blue" => Color.blue,
            "green" => Color.green,
            "yellow" => Color.yellow,
            "purple" => new Color(0.6f, 0.2f, 0.8f),
            "white" => Color.white,
            "orange" => new Color(1f, 0.5f, 0f),
            "pink" => new Color(1f, 0.4f, 0.7f),
            _ => ParseHexOrDefault(effective)
        };

        int applied = 0;
        for (int i = 0; i < lights.Length && applied < count; i++)
        {
            var L = lights[i];
            if (L == null) continue;

            L.enabled = true;
            L.color = col;
            // steady, visible intensity (no darkness/pulses)
            L.intensity = Mathf.Max(0f, intensityMedium);

            applied++;
        }

        Append($"[lights] Applied color '{effective}' to {applied} light(s).");
    }

    private void TurnOffLights(int count)
    {
        if (count <= 0) return;

        Light[] lights = TargetLights != null && TargetLights.Length > 0
            ? TargetLights
            : UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        if (lights == null || lights.Length == 0) return;

        int applied = 0;
        for (int i = 0; i < lights.Length && applied < count; i++)
        {
            var L = lights[i];
            if (L == null) continue;
            L.intensity = 0f;
            applied++;
        }
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
}
