using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

public sealed class BlueprintInterviewController : MonoBehaviour
{
    [Header("Scene refs")]
    public OpenAIClient OpenAI;
    public TMP_InputField DramaturgyInput;
    public TMP_InputField DirectingInput;
    public TMP_Text ConversationLog;
    // new realtime overview fields
    public TMP_Text BlueprintDisplay;
    public TMP_Text DirectingDisplay;

    [Header("Config")]
    public string Model = "gpt-4o-mini";
    public int MaxClarificationQuestions = 4;

    [Header("UI")]
    [Tooltip("Max number of lines to keep in the on-screen ConversationLog (rolling)")]
    public int MaxDisplayLines = 12;

    private readonly string[] _questions =
    {
        "What should I know about my character as a room? (1–3 short lines)",
        "What is it trying to achieve right now? (objectives: 1–2)",
        "What gets in the way? (obstacles: 1–2)",
        "How does it feel inside right now?",
        "What’s happening around it right now?"
    };

    private enum State
    {
        Idle,
        AskingQ,
        ProcessingSynthesis,
        AskingClarifications,
        ProcessingFinalize,
        AwaitingApproval,
        AwaitingRevisionNotes,
        ProcessingRevision
    }

    private State _state = State.Idle;
    private int _qIndex = 0;

    private List<string> _clarificationQs = new();
    private int _cIndex = 0;

    private readonly Dictionary<string, string> _raw = new();
    private string _transcript = "";
    private string _blueprintJson = ""; // raw JSON object text from model (root)

    private string LatestBlueprintPath =>
        Path.Combine(Application.persistentDataPath, "latest_blueprint.json");

    private readonly Queue<string> _pendingAppends = new Queue<string>();

    // rolling display buffer and accumulated directing notes
    private readonly List<string> _displayLines = new List<string>();
    private string _directingNotes = "";
    private string _directingPreview = "";

    private void Awake()
    {
        // optional convenience; safe if you prefer button hooks instead
        if (DramaturgyInput != null)
            DramaturgyInput.onSubmit.AddListener(_ => SubmitDramaturgy());

        // live preview and Enter-to-add for directing input
        if (DirectingInput != null)
        {
            DirectingInput.onValueChanged.AddListener(s => OnDirectingChanged(s));
            DirectingInput.onSubmit.AddListener(_ => AddDirectingNote());
        }
        
        // auto-find ConversationLog if not set (helps when wiring UI in scene is missed)
        if (ConversationLog == null)
        {
            // prefer a TMP_Text named "ConversationLog", otherwise pick the first TMP_Text in scene
            var byName = Array.Find(UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None),
                t => t.name.Equals("ConversationLog", StringComparison.OrdinalIgnoreCase));
            if (byName != null) ConversationLog = byName;
            else
            {
                var all = UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (all.Length > 0) ConversationLog = all[0];
            }

            if (ConversationLog == null)
                Debug.LogWarning("[BlueprintInterviewController] ConversationLog not assigned and none found in scene.");
            else
                Debug.Log($"[BlueprintInterviewController] Auto-attached ConversationLog: {ConversationLog.name}");
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
        // also show immediate preview in Console for visibility
        Debug.Log($"[DIRECTING PREVIEW] {_directingPreview}");
        if (DirectingDisplay != null)
        {
            DirectingDisplay.text = string.IsNullOrWhiteSpace(_directingNotes) ? _directingPreview : _directingNotes + "\n" + _directingPreview;
        }
    }

    public void StartInterview()
    {
        if (OpenAI == null) { Append("[error] OpenAIClient not assigned."); return; }
        if (DramaturgyInput == null || ConversationLog == null) { Append("[error] TMP refs missing."); return; }

        _raw.Clear();
        _clarificationQs.Clear();
        _qIndex = 0;
        _cIndex = 0;
        _transcript = "";
        _blueprintJson = "";

        Append("\n=== Dramaturgical Interview (Room as Actor) ===\n");
        _state = State.AskingQ;
        AskNextQuestion();
    }

    public void SubmitDramaturgy()
    {
        if (_state == State.Idle) return;
        if (DramaturgyInput == null) return;

        var text = (DramaturgyInput.text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        DramaturgyInput.text = "";
        DramaturgyInput.ActivateInputField();

        _ = HandleDramaturgyAsync(text);
    }

    private async Task HandleDramaturgyAsync(string text)
    {
        try
        {
            switch (_state)
            {
                case State.AskingQ:
                    _raw[$"Q{_qIndex + 1}"] = text;
                    Append($"A{_qIndex + 1}: {text}\n");
                    _qIndex++;
                    if (_qIndex < _questions.Length) AskNextQuestion();
                    else await SynthesizeThenClarifyAsync();
                    break;

                case State.AskingClarifications:
                    _raw[$"CLARIFICATION_{_cIndex + 1}"] = text;
                    Append($"CA{_cIndex + 1}: {text}\n");
                    _cIndex++;
                    if (_cIndex < _clarificationQs.Count) AskNextClarification();
                    else await FinalizeAsync();
                    break;

                case State.AwaitingApproval:
                {
                    var yn = text.Trim().ToLowerInvariant();
                    if (yn == "yes" || yn == "y")
                    {
                        SaveLatestBlueprint(_blueprintJson);
                        Append($"Saved blueprint to: {LatestBlueprintPath}");
                        Append("\nApproved. Done.\n");
                        _state = State.Idle;
                        return;
                    }

                    if (yn == "no" || yn == "n")
                    {
                        Append("\nTell the room-actor what feels wrong or what to shift (raw notes).");
                        _state = State.AwaitingRevisionNotes;
                        return;
                    }

                    Append("Type 'yes' to approve, or 'no' to revise.");
                    break;
                }

                case State.AwaitingRevisionNotes:
                    await ReviseAsync(text);
                    break;

                default:
                    Append("[busy] Wait for the model call to finish.");
                    break;
            }
        }
        catch (Exception e)
        {
            Append($"[error] {e.Message}");
            _state = State.Idle;
        }
    }

    private void AskNextQuestion()
    {
        Append($"Q{_qIndex + 1}: {_questions[_qIndex]}");
        Append("(submit answer in DramaturgyInput)\n");
    }

    private void AskNextClarification()
    {
        Append($"C{_cIndex + 1}: {_clarificationQs[_cIndex]}");
        Append("(submit answer in DramaturgyInput)\n");
    }

    private async Task SynthesizeThenClarifyAsync()
    {
        _state = State.ProcessingSynthesis;

        _transcript = BuildTranscript(_questions, _raw, clarifications: null);

        var directing = GetDirecting();
        var blueprintRootJson = await SynthesizeBlueprintAsync(_raw, _transcript, directing);
        _blueprintJson = PrettyJson(blueprintRootJson);

        PrintBlueprint(_blueprintJson);

        _clarificationQs = await RequestClarificationsOnceAsync(_raw, _blueprintJson, _transcript, directing);
        if (_clarificationQs.Count > 0)
        {
            Append("\nClarification round (one-off). Answer briefly.\n");
            _state = State.AskingClarifications;
            _cIndex = 0;
            AskNextClarification();
            return;
        }

        await FinalizeAsync(); // no clarifications needed → finalize anyway (best-effort)
    }

    private async Task FinalizeAsync()
    {
        _state = State.ProcessingFinalize;

        _transcript = BuildTranscript(_questions, _raw, _clarificationQs.Count > 0 ? _clarificationQs : null);

        var directing = GetDirecting();
        var finalJson = await FinalizeBlueprintWithClarificationsAsync(_raw, _blueprintJson, _transcript, directing);
        _blueprintJson = PrettyJson(finalJson);

        PrintBlueprint(_blueprintJson);
        Append("Does this represent your room? (yes / no)");
        _state = State.AwaitingApproval;
    }

    private async Task ReviseAsync(string feedback)
    {
        _state = State.ProcessingRevision;

        Append($"DIRECTOR_FEEDBACK: {feedback}\n");
        _transcript = string.IsNullOrWhiteSpace(_transcript)
            ? BuildTranscript(_questions, _raw, _clarificationQs.Count > 0 ? _clarificationQs : null)
            : _transcript + $"\n\nDIRECTOR_FEEDBACK:\n{feedback}";

        var directing = GetDirecting();
        var revised = await ReviseBlueprintAsync(_raw, _blueprintJson, feedback, _transcript, directing);
        _blueprintJson = PrettyJson(revised);

        PrintBlueprint(_blueprintJson);
        Append("Does this represent your room? (yes / no)");
        _state = State.AwaitingApproval;
    }

    // ---------- Model calls (ported prompts) ----------

    private async Task<string> SynthesizeBlueprintAsync(Dictionary<string, string> raw, string transcript, string directing)
    {
        var system = @"
You are a dramaturg shaping a ROOM as an ACTOR.
Output JSON only.

Holistic inference rules:
- Consider the entire dialogue (full Q/A transcript) to build the most coherent character blueprint; reconcile relationships between parts.
- Allow careful interpretive judgment to place items where they make the most playable sense; do NOT invent new facts. If something is inferred from objectives/obstacles/conditions, either omit it or mark it clearly as ""implied"".
- Core_character must come from the identity answer (Q1). If Q1 contains any usable identity phrase, extract a short noun-phrase for core_character rather than outputting unknown. Never replace it with an obstacle named entity.
- Backstory: if backstory is empty or minimal but objectives/obstacles strongly imply prior events or identity facts, you may add ONE cautious backstory item marked as ""implied"" (e.g., ""implied: experienced racism""). Do not invent details beyond what the transcript clearly suggests.
- Place external blockers that oppose the objective into obstacles (even if originally listed as circumstances); if the transcript includes any external facts, ensure at least one of them remains in given_circumstances (even if others become obstacles).
- Keep conditions strictly internal (feelings, drives, inner tensions); move lights/noise/crowds into circumstances or obstacles (as blockers), not conditions.
- Each entry MUST be a complete sentence or clear phrase with subject-verb structure where possible. Keep close to original wording but complete the thought.
- Keep each entry concise (max ~12 words).
- All fields except core_character MUST be arrays of strings (even if 1 item).

Rules:
- Distill raw answers into concise, playable material.
- You may use general world knowledge ONLY if highly confident.
  If unsure, do NOT guess: write ""unknown"".
- Do not invent specific facts.
- Blueprint must feel like directing material, not software labels.
- If the user provided relevant content, do not output an empty array; produce at least one cautious item or ""unknown"".

Schema:
{
  ""blueprint"": {
    ""core_character"": string,
    ""backstory"": [string],
    ""objectives"": [string],
    ""obstacles"": [string],
    ""conditions"": [string],
    ""given_circumstances"": [string]
  }
}
".Trim();

        var user =
            "DIRECTOR INSTRUCTIONS (always obey if compatible):\n" +
            (string.IsNullOrWhiteSpace(directing) ? "(none)" : directing) +
            "\n\nFULL Q/A TRANSCRIPT:\n" +
            transcript +
            "\n\nRAW INPUT (for reference):\n" +
            SimpleSerializeDictionary(raw) +
            "\n\nDistill into the blueprint JSON only.";

        return await CallJsonObjectAsync(system, user);
    }

    private async Task<List<string>> RequestClarificationsOnceAsync(Dictionary<string, string> raw, string blueprintJson, string transcript, string directing)
    {
        var system = @"
You are a dramaturg who asks ONE round of clarifying questions to make a ROOM-ACTOR character blueprint playable.
Output JSON only.

Goal:
- Keep what is already clear.
- Ask only what is necessary to ground vague/private references or make behaviour playable.
- Base clarifying questions on the full Q/A transcript, not single answers.
- If needed: ask to disambiguate role identity (""is X metaphor or literal?""), add one concrete external circumstance if missing, and one concrete internal state if missing.

Constraints:
- Ask at most N questions.
- Each question must be specific and answerable in 1–2 lines.
- Do NOT ask for web links.
- Do NOT ask questions about playing style (expression/tone) yet.
- If no clarifications are needed, return an empty list.

Return schema:
{
  ""clarification_questions"": [string]
}
".Trim();

        var ub = new StringBuilder();
        ub.Append("N = ").Append(MaxClarificationQuestions).Append("\n\nDIRECTOR INSTRUCTIONS (always obey if compatible):\n");
        ub.Append(string.IsNullOrWhiteSpace(directing) ? "(none)" : directing);
        ub.Append("\n\nFULL Q/A TRANSCRIPT:\n").Append(transcript);
        ub.Append("\n\nRAW INPUT:\n").Append(SimpleSerializeDictionary(raw));
        ub.Append("\n\nCURRENT BLUEPRINT (best effort):\n").Append(blueprintJson);
        ub.Append("\n\nProduce clarification_questions only.");
        var user = ub.ToString().Trim();

        var jsonText = await CallJsonObjectAsync(system, user);

        var arr = ExtractStringArrayFromJson(jsonText, "clarification_questions");
        if (arr == null) return new List<string>();

        var list = new List<string>();
        foreach (var s in arr)
        {
            var t = (s ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(t)) list.Add(t);
        }

        return list.Take(MaxClarificationQuestions).ToList();
    }

    private async Task<string> FinalizeBlueprintWithClarificationsAsync(Dictionary<string, string> raw, string priorBlueprintJson, string transcript, string directing)
    {
        var system = @"
You are a dramaturg finalizing a ROOM-ACTOR blueprint.
You already asked ONE clarification round; now do the best you can with what you have.
Output JSON only.

Holistic inference rules:
- Consider all raw + clarification inputs together (full Q/A transcript) to build the most coherent, playable blueprint.
- Allow careful interpretive judgment to place items where they make the most playable sense; do NOT invent new facts. If something is inferred from objectives/obstacles/conditions, either omit it or mark it clearly as ""implied"".
- Core_character must come from the identity answer (Q1). If Q1 contains any usable identity phrase, extract a short noun-phrase for core_character rather than outputting unknown. Never replace it with an obstacle named entity.
- Backstory: if backstory is empty or minimal but objectives/obstacles strongly imply prior events or identity facts, you may add ONE cautious backstory item marked as ""implied"". Do not invent details beyond what the transcript clearly suggests.
- Place external blockers that oppose the objective into obstacles; keep at least one external fact in given_circumstances.
- Keep conditions strictly internal; move lights/noise/crowds into circumstances/obstacles, not conditions.
- Each entry MUST be a complete sentence or clear phrase; max ~12 words.
- All fields except core_character MUST be arrays of strings (even if 1 item).
- If something remains unknown, write ""unknown"" (as an item).

Schema:
{
  ""blueprint"": {
    ""core_character"": string,
    ""backstory"": [string],
    ""objectives"": [string],
    ""obstacles"": [string],
    ""conditions"": [string],
    ""given_circumstances"": [string]
  }
}
".Trim();

        var user = (
            "DIRECTOR INSTRUCTIONS (always obey if compatible):\n" +
            (string.IsNullOrWhiteSpace(directing) ? "(none)" : directing) +
            "\n\nFULL Q/A TRANSCRIPT (including clarifications):\n" + transcript +
            "\n\nRAW INPUT (including clarifications):\n" + SimpleSerializeDictionary(raw) +
            "\n\nPRIOR BLUEPRINT:\n" + priorBlueprintJson +
            "\n\nFinalize the blueprint now. Output JSON only."
        ).Trim();

        return await CallJsonObjectAsync(system, user);
    }

    private async Task<string> ReviseBlueprintAsync(Dictionary<string, string> raw, string blueprintJson, string feedback, string transcript, string directing)
    {
        var system = @"
You revise a ROOM-ACTOR blueprint using directing notes.
Output JSON only.

Classification Rules (repeat):
- Given circumstances = external facts (noise, crowds, environment).
- Obstacles = specific blockers of objectives.
- Avoid duplicate identical entries across fields.
- Each entry MUST be a complete sentence or clear phrase; max ~12 words.
- All fields except core_character MUST be arrays of strings.

Rules:
- Interpret feedback as an actor would.
- Adjust only what is necessary.
- Do not invent facts.

Schema:
{
  ""blueprint"": {
    ""core_character"": string,
    ""backstory"": [string],
    ""objectives"": [string],
    ""obstacles"": [string],
    ""conditions"": [string],
    ""given_circumstances"": [string]
  }
}
".Trim();

        var user = (
            "DIRECTOR INSTRUCTIONS (always obey if compatible):\n" +
            (string.IsNullOrWhiteSpace(directing) ? "(none)" : directing) +
            "\n\nFULL Q/A TRANSCRIPT (including clarifications):\n" + transcript +
            "\n\nRAW INPUT:\n" + SimpleSerializeDictionary(raw) +
            "\n\nCURRENT BLUEPRINT:\n" + blueprintJson +
            "\n\nDIRECTOR FEEDBACK:\n" + feedback +
            "\n\nUpdate the blueprint accordingly. Output JSON only."
        ).Trim();

        return await CallJsonObjectAsync(system, user);
    }

    private async Task<string> CallJsonObjectAsync(string system, string user)
    {
        var messages = new List<OpenAIClient.Msg>
        {
            new OpenAIClient.Msg("system", system),
            new OpenAIClient.Msg("user", user),
        };

        var content = await OpenAI.ChatCompletionsJsonAsync(messages, model: Model);
        // basic validation: ensure returned text looks like a JSON object
        if (!LooksLikeJsonObject(content))
            throw new Exception("Model did not return a JSON object.");
        return content;
    }

    // ---------- helpers ----------

    // Add a public method to capture directing instructions from DirectingInput into persistent directing notes.
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

    private void PrintBlueprint(string blueprintPrettyJson)
    {
        Append("\n=== CHARACTER BLUEPRINT (DISTILLED) ===");
        Append(blueprintPrettyJson);
        Append("======================================\n");
        // update immediate blueprint display
        _blueprintJson = blueprintPrettyJson;
        if (BlueprintDisplay != null) BlueprintDisplay.text = _blueprintJson;
    }

    private void SaveLatestBlueprint(string blueprintJsonText)
    {
        var pretty = PrettyJson(blueprintJsonText);
        File.WriteAllText(LatestBlueprintPath, pretty);
    }

    private static string BuildTranscript(string[] questions, Dictionary<string, string> raw, List<string> clarifications)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < questions.Length; i++)
        {
            sb.AppendLine($"Q{i + 1}: {questions[i]}");
            var key = $"Q{i + 1}";
            raw.TryGetValue(key, out var a);
            sb.AppendLine($"A{i + 1}: {a ?? ""}");
        }

        if (clarifications != null && clarifications.Count > 0)
        {
            for (int i = 0; i < clarifications.Count; i++)
            {
                sb.AppendLine($"C{i + 1}: {clarifications[i]}");
                var ckey = $"CLARIFICATION_{i + 1}";
                raw.TryGetValue(ckey, out var ca);
                sb.AppendLine($"CA{i + 1}: {ca ?? ""}");
            }
        }

        return sb.ToString().Trim();
    }

    private static bool LooksLikeJsonObject(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.TrimStart();
        return t.StartsWith("{") && t.Contains("}");
    }

    // Extract top-level array of strings named propertyName, e.g. "clarification_questions"
    private static List<string> ExtractStringArrayFromJson(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName)) return null;
        var key = $"\"{propertyName}\"";
        var pos = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (pos < 0) return null;
        var after = json.Substring(pos + key.Length);
        var bracket = after.IndexOf('[');
        if (bracket < 0) return null;
        var start = pos + key.Length + bracket;
        var end = json.IndexOf(']', start);
        if (end < 0) return null;
        var content = json.Substring(start + 1, end - start - 1);
        var items = new List<string>();
        var i = 0;
        while (i < content.Length)
        {
            // find next quote
            while (i < content.Length && content[i] != '\"') i++;
            if (i >= content.Length) break;
            i++; // skip opening quote
            var sb = new StringBuilder();
            bool esc = false;
            for (; i < content.Length; i++)
            {
                var ch = content[i];
                if (esc) { sb.Append(ch); esc = false; continue; }
                if (ch == '\\') { esc = true; continue; }
                if (ch == '\"') { i++; break; }
                sb.Append(ch);
            }
            items.Add(sb.ToString());
            // skip until comma or end
            while (i < content.Length && content[i] != ',') i++;
            if (i < content.Length && content[i] == ',') i++;
        }
        return items;
    }

    // Add this helper (was referenced but missing)
    private static string SimpleSerializeDictionary(Dictionary<string, string> d)
    {
        if (d == null) return "{}";
        var sb = new StringBuilder();
        sb.Append("{");
        var first = true;
        foreach (var kv in d)
        {
            if (!first) sb.Append(", ");
            sb.Append($"\"{EscapeJson(kv.Key)}\":\"{EscapeJson(kv.Value)}\"");
            first = false;
        }
        sb.Append("}");
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
                    if (ch < 32) sb.AppendFormat("\\u{0:X4}", (int)ch);
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string PrettyJson(string json)
    {
        // keep original if we can't reliably pretty-print
        return string.IsNullOrWhiteSpace(json) ? "{}" : json;
    }

    // Update: flush pending appends into a rolling buffer and render the entire buffer to the TMP text
    private void Update()
    {
        if (_pendingAppends.Count == 0 && string.IsNullOrWhiteSpace(_directingPreview)) return;
        lock (_pendingAppends)
        {
            while (_pendingAppends.Count > 0)
            {
                var line = _pendingAppends.Dequeue();
                // maintain rolling buffer
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

        // update the blueprint and directing overview panels
        if (BlueprintDisplay != null)
            BlueprintDisplay.text = string.IsNullOrWhiteSpace(_blueprintJson) ? "(blueprint: none yet)" : _blueprintJson;
        if (DirectingDisplay != null)
            DirectingDisplay.text = string.IsNullOrWhiteSpace(_directingNotes) && string.IsNullOrWhiteSpace(_directingPreview)
                ? "(directing: none)" : (_directingNotes + (string.IsNullOrWhiteSpace(_directingPreview) ? "" : (_directingNotes.Length > 0 ? "\n" : "") + _directingPreview));
    }

    private void Append(string line)
    {
        // always write to Console so you can see dialogue even if UI isn't visible/assigned
        Debug.Log(line);
        lock (_pendingAppends)
        {
            _pendingAppends.Enqueue(line);
        }
    }
}
