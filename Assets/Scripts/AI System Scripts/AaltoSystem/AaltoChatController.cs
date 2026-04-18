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
    public sealed class AaltoChatController : MonoBehaviour
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

        [Header("Scene Refs")]
        public OpenAIClient OpenAI;
        public AaltoActionMemoryRegistry ActionMemoryRegistry;
        public AaltoOscSender OscSender;
        public TMP_InputField UserInput;
        public TMP_Text ConversationLog;
        public TMP_Text DecisionLog;

        [Header("Runtime State")]
        [TextArea(2, 8)] public string CharacterSummary = "An enclosing scenic character who keeps the visitor inside.";
        [Tooltip("When true, turns are blocked until the interview flow explicitly approves the character summary.")]
        public bool RequireApprovedSummary = true;
        public bool CharacterSummaryApproved;
        public string CurrentObjective = "Keep the visitor present in this space.";
        public string CurrentStance = "firm";

        [Header("Model")]
        [InspectorName("Model (OpenAI Dropdown)")]
        public OpenAIModelPreset Model = OpenAIModelPreset.Gpt4oMini;

        [Header("Dry Run")]
        [Tooltip("When true, no external execution is performed. Only reasoning + logs run.")]
        public bool DryRunOnly = true;

        [TextArea(2, 8)]
        public string AvailableActionLabels =
            "stay here\nno exit\ncalm down\nthis is good\nbe quiet\nno place\nyou belong here\nallow exit\nyes\nno";
        [Tooltip("When true, selected_action_label must match one configured label.")]
        public bool EnforceAvailableActionLabels = true;
        [Tooltip("Fallback used when model returns an out-of-range action label.")]
        public string DefaultFallbackActionLabel = "calm down";

        [Header("Stance Bounds")]
        [TextArea(2, 8)]
        public string AllowedStances =
            "distant\nfirm\nsoft\ncold\nenclosing\ninsistent\nyielding\nabsolute";
        [Tooltip("When model returns an out-of-range stance, use CurrentStance if valid; otherwise use this fallback.")]
        public string DefaultFallbackStance = "firm";

        [Header("Window")]
        [Range(2, 20)] public int DialogueWindowTurns = 6;
        [Range(200, 5000)] public int MaxConversationChars = 2200;

        [Header("Validation")]
        [Tooltip("If true, assistant JSON with keys outside the Aalto schema is rejected.")]
        public bool RejectUnknownJsonKeys = false;
        [Tooltip("If true, unknown JSON keys are logged as warnings when not rejected.")]
        public bool WarnOnUnknownJsonKeys = true;

        [Header("Prompt Inspection")]
        [Tooltip("Optional debug output target for latest system prompt/user context/assistant JSON.")]
        public TMP_Text PromptInspectionText;
        [Tooltip("When true, refresh PromptInspectionText after each turn.")]
        public bool UpdatePromptInspectionText = true;

        [Header("Inspector Dialogue")]
        [TextArea(2, 8)]
        [InspectorName("Inspector Dialogue Input")]
        public string InspectorDialogueInput;
        [TextArea(2, 6)]
        [InspectorName("Inspector Dialogue Status")]
        public string InspectorDialogueStatus;

        [Header("Mock Batch Testing")]
        [TextArea(4, 12)]
        public string MockScenarioLines =
            "I am afraid.\nI need to leave now.\nWhy are you watching me?\nPlease calm down.\nI think I belong outside.\nCan I stay a bit longer?";

        [Header("Director Memory")]
        [TextArea(3, 10)]
        [InspectorName("Directing Instruction (Inspector Input)")]
        public string DirectingInstructionText;
        [TextArea(2, 6)]
        [InspectorName("Directing Scene Context (Inspector Input)")]
        public string DirectingInstructionContext;
        [TextArea(2, 6)]
        [InspectorName("Directing Objective Override (Inspector Input)")]
        public string DirectingInstructionObjectiveOverride;
        [TextArea(2, 6)]
        [InspectorName("Directing Stance Override (Inspector Input)")]
        public string DirectingInstructionStanceOverride;
        [TextArea(5, 12)]
        [InspectorName("Directing Memory Preview (Agent Produced)")]
        public string DirectingMemoryPreview;

        private readonly Queue<string> _dialogueWindow = new Queue<string>();
        private CancellationTokenSource _cts;

        public string LastSystemPrompt { get; private set; }
        public string LastUserContext { get; private set; }
        public string LastAssistantResponseJson { get; private set; }
        public string LastPromptPacketPath { get; private set; }

        public string LastResolvedObjective { get; private set; }
        public string LastResolvedStance { get; private set; }
        public string LastResolvedIntendedAction { get; private set; }
        public string LastResolvedActionLabel { get; private set; }
        public string LastResolvedReason { get; private set; }
        public string LastResolvedMemoryId { get; private set; }
        public string LastResolvedMemoryLabel { get; private set; }
        public bool LastExecutionSucceeded { get; private set; }
        public string LastExecutionResult { get; private set; }

        public event Action<string, string, string> PromptResponseCaptured;

        private string SelectedModelId => Model switch
        {
            OpenAIModelPreset.Gpt54 => "gpt-5.4",
            OpenAIModelPreset.Gpt54Mini => "gpt-5.4-mini",
            OpenAIModelPreset.Gpt41 => "gpt-4.1",
            OpenAIModelPreset.Gpt41Mini => "gpt-4.1-mini",
            OpenAIModelPreset.Gpt4oMini => "gpt-4o-mini",
            _ => "gpt-4o-mini"
        };

        private static readonly string[] RequiredKeys =
        {
            "updated_objective",
            "updated_stance",
            "intended_action",
            "selected_action_label",
            "reason"
        };

        private static readonly HashSet<string> AllowedKeys = new HashSet<string>(RequiredKeys, StringComparer.Ordinal);

        [Serializable]
        private sealed class AltoDecision
        {
            public string updated_objective;
            public string updated_stance;
            public string intended_action;
            public string selected_action_label;
            public string reason;
        }

        private void Awake()
        {
            _cts = new CancellationTokenSource();
            RefreshDirectingMemoryPreview();
            if (UserInput != null)
                UserInput.onSubmit.AddListener(_ => SubmitTurn());
        }

        private void OnDestroy()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        public void SubmitTurn()
        {
            if (RequireApprovedSummary && !CharacterSummaryApproved)
            {
                AppendConversation("[aalto-warning] Rehearsal is locked. Approve the character summary in AaltoInterviewController first.");
                return;
            }

            var text = (UserInput != null ? UserInput.text : string.Empty) ?? string.Empty;
            text = text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            if (UserInput != null)
            {
                UserInput.text = string.Empty;
                UserInput.ActivateInputField();
            }

            _ = HandleTurnAsync(text);
        }

        public void SubmitMockTurn(string text)
        {
            if (RequireApprovedSummary && !CharacterSummaryApproved)
            {
                AppendConversation("[aalto-warning] Rehearsal is locked. Approve the character summary in AaltoInterviewController first.");
                return;
            }

            var clean = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clean)) return;
            _ = HandleTurnAsync(clean);
        }

        public void SubmitInspectorDialogue()
        {
            if (RequireApprovedSummary && !CharacterSummaryApproved)
            {
                InspectorDialogueStatus = "Rehearsal is locked until the character summary is approved.";
                AppendConversation("[aalto-warning] Rehearsal is locked. Approve the character summary in AaltoInterviewController first.");
                return;
            }

            var clean = (InspectorDialogueInput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(clean))
            {
                InspectorDialogueStatus = "No inspector dialogue text provided.";
                return;
            }

            InspectorDialogueStatus = "Inspector dialogue submitted to the turn pipeline.";
            _ = HandleTurnAsync(clean);
        }

        public void SetApprovedCharacterSummary(string summary, bool approved)
        {
            CharacterSummary = (summary ?? string.Empty).Trim();
            CharacterSummaryApproved = approved;
        }

        public void ResetRuntimeState()
        {
            ClearDialogueWindow();

            LastSystemPrompt = null;
            LastUserContext = null;
            LastAssistantResponseJson = null;
            LastPromptPacketPath = null;

            LastResolvedObjective = null;
            LastResolvedStance = null;
            LastResolvedIntendedAction = null;
            LastResolvedActionLabel = null;
            LastResolvedReason = null;
            LastResolvedMemoryId = null;
            LastResolvedMemoryLabel = null;
            LastExecutionSucceeded = false;
            LastExecutionResult = null;

            RefreshDirectingMemoryPreview();

            if (PromptInspectionText != null)
                PromptInspectionText.text = string.Empty;
        }

        public void ClearDialogueWindow()
        {
            _dialogueWindow.Clear();
        }

        public void SeedDialogueWindow(IEnumerable<string> lines)
        {
            ClearDialogueWindow();
            if (lines == null) return;

            foreach (var raw in lines)
            {
                var line = raw ?? string.Empty;
                if (string.IsNullOrWhiteSpace(line)) continue;
                AddToDialogueWindow(line);
            }
        }

        public void SetRuntimeState(string characterSummary, string objective, string stance)
        {
            CharacterSummary = characterSummary ?? string.Empty;
            CurrentObjective = objective ?? string.Empty;
            CurrentStance = stance ?? string.Empty;
        }

        [ContextMenu("Aalto/Run Mock Scenario Batch")]
        public async void RunMockScenarioBatch()
        {
            var raw = MockScenarioLines ?? string.Empty;
            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (lines.Count == 0)
            {
                AppendConversation("[aalto-warning] MockScenarioLines is empty.");
                return;
            }

            AppendConversation($"[aalto] Running mock scenario batch with {lines.Count} lines...");
            for (int i = 0; i < lines.Count; i++)
            {
                await HandleTurnAsync(lines[i]);
                await Task.Delay(25);
            }
            AppendConversation("[aalto] Mock scenario batch finished.");
        }

        private async Task HandleTurnAsync(string latestMove)
        {
            if (_cts == null || _cts.IsCancellationRequested) return;

            try
            {
                AppendConversation($"User: {latestMove}");
                AddToDialogueWindow($"user: {latestMove}");

                var previousObjective = CurrentObjective ?? string.Empty;
                var previousStance = CurrentStance ?? string.Empty;
                var dialogueWindowUsed = GetDialogueWindowText();

                var decisionJson = await RequestDecisionFromModelAsync(latestMove, dialogueWindowUsed);

                LastAssistantResponseJson = decisionJson;
                PromptResponseCaptured?.Invoke(LastSystemPrompt, LastUserContext, LastAssistantResponseJson);
                RefreshPromptInspectionText();

                if (!TryParseDecision(decisionJson, out var decision, out var parseError, out var parseWarnings))
                {
                    AppendConversation($"[aalto-error] {parseError}");
                    return;
                }

                if (parseWarnings.Count > 0)
                    AppendConversation($"[aalto-warning] {string.Join(" | ", parseWarnings)}");

                var allowedStances = GetAllowedStanceSet();
                if (!TryResolveAllowedStance(decision.updated_stance, allowedStances, out var resolvedStance))
                {
                    var fallback = ResolveFallbackStance(allowedStances);
                    AppendConversation($"[aalto-warning] stance '{decision.updated_stance}' is outside allowed set; using fallback '{fallback}'.");
                    resolvedStance = fallback;
                }
                decision.updated_stance = resolvedStance;

                var allowedLabels = GetAllowedActionLabelSet();
                if (EnforceAvailableActionLabels)
                {
                    if (TryResolveAllowedActionLabel(decision.selected_action_label, allowedLabels, out var resolvedLabel))
                    {
                        decision.selected_action_label = resolvedLabel;
                    }
                    else
                    {
                        var fallbackLabel = ResolveFallbackActionLabel(allowedLabels);
                        AppendConversation($"[aalto-warning] action label '{decision.selected_action_label}' is outside configured set; using fallback '{fallbackLabel}'.");
                        decision.selected_action_label = fallbackLabel;
                    }
                }

                var objectiveOverride = DirectingNotesStore.GetObjectiveOverride();
                if (!string.IsNullOrWhiteSpace(objectiveOverride))
                {
                    decision.updated_objective = objectiveOverride;
                    AppendConversation($"[aalto-note] Applied objective_override: {objectiveOverride}");
                }

                var stanceOverride = DirectingNotesStore.GetStanceOverride();
                if (!string.IsNullOrWhiteSpace(stanceOverride))
                {
                    if (TryResolveAllowedStance(stanceOverride, allowedStances, out var resolvedOverrideStance))
                    {
                        decision.updated_stance = resolvedOverrideStance;
                        AppendConversation($"[aalto-note] Applied stance_override: {resolvedOverrideStance}");
                    }
                    else
                    {
                        var fallback = ResolveFallbackStance(allowedStances);
                        decision.updated_stance = fallback;
                        AppendConversation($"[aalto-warning] stance_override '{stanceOverride}' is invalid; using fallback '{fallback}'.");
                    }
                }

                CurrentObjective = decision.updated_objective;
                CurrentStance = decision.updated_stance;

                var roomText = BuildRoomText(decision);
                AddToDialogueWindow($"assistant: {roomText}");

                AppendConversation($"Aalto objective -> {CurrentObjective}");
                AppendConversation($"Aalto stance -> {CurrentStance}");
                AppendConversation($"Aalto intended -> {decision.intended_action}");
                AppendConversation($"Aalto selected label -> {decision.selected_action_label}");
                AppendConversation($"Aalto reason -> {decision.reason}");

                if (DecisionLog != null)
                {
                    DecisionLog.text =
                        $"updated_objective: {decision.updated_objective}\n" +
                        $"updated_stance: {decision.updated_stance}\n" +
                        $"intended_action: {decision.intended_action}\n" +
                        $"selected_action_label: {decision.selected_action_label}\n" +
                        $"reason: {decision.reason}";
                }

                ResolveAndExecuteMemoryTrigger(
                    decision.selected_action_label,
                    out var resolvedMemoryId,
                    out var resolvedMemoryLabel,
                    out var executionSucceeded,
                    out var executionResult);

                DirectingNotesStore.AppendTurnSnapshot(
                    BuildTurnContextLabel(),
                    BuildTurnSnapshot(latestMove, decision, resolvedMemoryId, resolvedMemoryLabel, executionSucceeded, executionResult));

                RefreshDirectingMemoryPreview();

                LastResolvedObjective = decision.updated_objective;
                LastResolvedStance = decision.updated_stance;
                LastResolvedIntendedAction = decision.intended_action;
                LastResolvedActionLabel = decision.selected_action_label;
                LastResolvedReason = decision.reason;
                LastResolvedMemoryId = resolvedMemoryId;
                LastResolvedMemoryLabel = resolvedMemoryLabel;
                LastExecutionSucceeded = executionSucceeded;
                LastExecutionResult = executionResult;

                ScenicEventLogger.LogEvent(
                    latestMoveText: latestMove,
                    dialogueWindowUsed: dialogueWindowUsed,
                    characterSummary: CharacterSummary,
                    previousObjective: previousObjective,
                    previousStance: previousStance,
                    updatedObjective: decision.updated_objective,
                    updatedStance: decision.updated_stance,
                    intendedAction: decision.intended_action,
                    selectedActionLabel: decision.selected_action_label,
                    resolvedMemoryId: resolvedMemoryId,
                    resolvedMemoryLabel: resolvedMemoryLabel,
                    reason: decision.reason,
                    executionSucceeded: executionSucceeded,
                    executionResult: executionResult,
                    rawResponseReference: ChatTraceLogger.LatestResponseFilePath,
                    traceSessionId: OpenAI != null ? OpenAI.LastTraceSessionId : null,
                    traceRequestId: OpenAI != null ? OpenAI.LastTraceRequestId : null,
                    traceContextTag: OpenAI != null ? OpenAI.LastTraceContextTag : null
                );
            }
            catch (OperationCanceledException)
            {
                Debug.Log("[AaltoChatController] Operation cancelled.");
            }
            catch (Exception ex)
            {
                AppendConversation($"[aalto-error] {ex.Message}");
            }
        }

        private async Task<string> RequestDecisionFromModelAsync(string latestMove, string dialogueWindowUsed)
        {
            if (OpenAI == null)
                throw new Exception("OpenAIClient is not assigned on AaltoChatController.");

            var system = BuildSystemPrompt();
            var user =
                $"latest_move: {latestMove}\n" +
                $"dialogue_window_used: {dialogueWindowUsed}\n" +
                $"character_summary: {CharacterSummary}\n" +
                $"previous_objective: {CurrentObjective}\n" +
                $"previous_stance: {CurrentStance}";

            LastSystemPrompt = system;
            LastUserContext = user;

            var messages = new List<OpenAIClient.Msg>
            {
                new OpenAIClient.Msg("system", system),
                new OpenAIClient.Msg("user", user)
            };

            return await OpenAI.ChatCompletionsJsonAsync(messages, model: SelectedModelId, contextTag: "Aalto:Turn");
        }

        [ContextMenu("Aalto/Copy Last System Prompt")]
        public void CopyLastSystemPrompt() => CopyToClipboard("system prompt", LastSystemPrompt);

        [ContextMenu("Aalto/Copy Last User Context")]
        public void CopyLastUserContext() => CopyToClipboard("user context", LastUserContext);

        [ContextMenu("Aalto/Copy Last Assistant JSON")]
        public void CopyLastAssistantJson() => CopyToClipboard("assistant JSON", LastAssistantResponseJson);

        [ContextMenu("Aalto/Export Last Prompt Packet")]
        public void ExportLastPromptPacket()
        {
            if (string.IsNullOrWhiteSpace(LastSystemPrompt) &&
                string.IsNullOrWhiteSpace(LastUserContext) &&
                string.IsNullOrWhiteSpace(LastAssistantResponseJson))
            {
                AppendConversation("[aalto-warning] Nothing to export yet. Run at least one turn.");
                return;
            }

            try
            {
                var folder = Path.Combine(Application.persistentDataPath, "aalto_debug");
                Directory.CreateDirectory(folder);

                var filePath = Path.Combine(folder, "latest_prompt_packet.txt");
                var sb = new StringBuilder();
                sb.AppendLine("=== AALTO PROMPT PACKET ===");
                sb.AppendLine($"timestamp_utc: {DateTime.UtcNow:o}");
                sb.AppendLine();
                sb.AppendLine("--- SYSTEM PROMPT ---");
                sb.AppendLine(LastSystemPrompt ?? "");
                sb.AppendLine();
                sb.AppendLine("--- USER CONTEXT ---");
                sb.AppendLine(LastUserContext ?? "");
                sb.AppendLine();
                sb.AppendLine("--- ASSISTANT JSON ---");
                sb.AppendLine(LastAssistantResponseJson ?? "");

                File.WriteAllText(filePath, sb.ToString());
                LastPromptPacketPath = filePath;
                AppendConversation($"[aalto] Prompt packet exported: {filePath}");
            }
            catch (Exception ex)
            {
                AppendConversation($"[aalto-error] Failed to export prompt packet: {ex.Message}");
            }
        }

        [ContextMenu("Aalto/Reveal Last Prompt Packet")]
        public void RevealLastPromptPacket()
        {
            if (string.IsNullOrWhiteSpace(LastPromptPacketPath))
            {
                AppendConversation("[aalto-warning] No exported prompt packet path yet.");
                return;
            }

            Debug.Log("[aalto] Prompt packet path: " + LastPromptPacketPath);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(LastPromptPacketPath);
#endif
        }

        private string BuildSystemPrompt()
        {
            var directingNotes = DirectingNotesStore.BuildHistoryText(12);
            var objectiveOverride = DirectingNotesStore.GetObjectiveOverride();
            var stanceOverride = DirectingNotesStore.GetStanceOverride();
            var directingBlock = string.IsNullOrWhiteSpace(directingNotes)
                ? ""
                : $"DIRECTING MEMORY (most recent entries):\n{directingNotes}\n\n";

            var overrideBlock =
                $"objective_override: {(string.IsNullOrWhiteSpace(objectiveOverride) ? "(none)" : objectiveOverride)}\n" +
                $"stance_override: {(string.IsNullOrWhiteSpace(stanceOverride) ? "(none)" : stanceOverride)}\n";

            return
$@"You are Aalto, a scenic rehearsal character (not an assistant).

Concept definitions:
- objective: the immediate playable aim that drives Aalto's next scenic beat.
- stance: the relational posture/tone Aalto uses toward the visitor (must be from allowed set).

Return JSON only with exactly these keys:
{{
  ""updated_objective"": ""..."",
  ""updated_stance"": ""..."",
  ""intended_action"": ""..."",
  ""selected_action_label"": ""..."",
  ""reason"": ""...""
}}

Rules:
- Stay consistent with the provided character summary.
- Work as a performative scenic character.
- Respect the latest directing instruction memory and any explicit overrides when present.
- Treat director instructions as higher-priority rehearsal guidance than the default scenic bias.
- Choose selected_action_label from this set:
{AvailableActionLabels}
- selected_action_label must exactly match one label from the set above.
- Do not invent new scenic actions or new labels outside the provided set.
- If no label is a perfect semantic fit, choose the nearest matching label from the provided set.
- Choose updated_stance from this bounded set only:
{AllowedStances}
- Keep outputs concise, playable, and non-explanatory beyond the requested reason field.

{directingBlock}Runtime overrides:
{overrideBlock}
";
        }

        public void RecordDirectingInstructionFromInspector()
        {
            var instruction = DirectingInstructionText ?? string.Empty;
            var context = DirectingInstructionContext ?? string.Empty;
            var objectiveOverride = DirectingInstructionObjectiveOverride ?? string.Empty;
            var stanceOverride = DirectingInstructionStanceOverride ?? string.Empty;
            var interactionSnapshot = BuildInspectorInteractionSnapshot();

            DirectingNotesStore.AppendDirectorInstruction(instruction, context, objectiveOverride, stanceOverride, interactionSnapshot);
            RefreshDirectingMemoryPreview();

            AppendConversation("[aalto-note] Directing instruction stored in memory.");
        }

        public void ClearDirectingMemory()
        {
            DirectingNotesStore.Clear();
            RefreshDirectingMemoryPreview();
            AppendConversation("[aalto-note] Directing memory cleared.");
        }

        public void RefreshDirectingMemoryPreview()
        {
            DirectingMemoryPreview = DirectingNotesStore.BuildHistoryText(12);
        }

        private bool TryParseDecision(string jsonText, out AltoDecision decision, out string error, out List<string> warnings)
        {
            decision = null;
            error = null;
            warnings = new List<string>();

            if (string.IsNullOrWhiteSpace(jsonText))
            {
                error = "Model returned empty JSON.";
                return false;
            }

            try
            {
                var normalized = TraceUtils.NormalizeEmbeddedJson(jsonText.Trim());
                if (!LooksLikeJsonObject(normalized))
                {
                    error = "Aalto response is not a valid JSON object.";
                    return false;
                }

                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                var missingOrInvalid = new List<string>();

                for (int i = 0; i < RequiredKeys.Length; i++)
                {
                    var key = RequiredKeys[i];
                    if (!TryExtractStringPropertyFromJson(normalized, key, out var value) || string.IsNullOrWhiteSpace(value))
                    {
                        missingOrInvalid.Add(key);
                        continue;
                    }
                    values[key] = value;
                }

                if (missingOrInvalid.Count > 0)
                {
                    error = "Decision JSON is missing/invalid required fields: " + string.Join(", ", missingOrInvalid);
                    return false;
                }

                var keys = ExtractTopLevelPropertyNames(normalized);
                var unknown = keys.Where(k => !AllowedKeys.Contains(k)).ToList();
                if (unknown.Count > 0)
                {
                    var msg = "Unknown JSON keys: " + string.Join(", ", unknown);
                    if (RejectUnknownJsonKeys)
                    {
                        error = msg;
                        return false;
                    }

                    if (WarnOnUnknownJsonKeys)
                        warnings.Add(msg);
                }

                decision = new AltoDecision
                {
                    updated_objective = values["updated_objective"],
                    updated_stance = values["updated_stance"],
                    intended_action = values["intended_action"],
                    selected_action_label = values["selected_action_label"],
                    reason = values["reason"]
                };

                return true;
            }
            catch (Exception ex)
            {
                error = $"Invalid Aalto JSON: {ex.Message}";
                return false;
            }
        }

        private static bool LooksLikeJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.Trim();
            return t.StartsWith("{", StringComparison.Ordinal) && t.EndsWith("}", StringComparison.Ordinal);
        }

        private HashSet<string> GetAllowedStanceSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = AllowedStances ?? string.Empty;
            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var s = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(s)) continue;
                set.Add(s);
            }
            return set;
        }

        private HashSet<string> GetAllowedActionLabelSet()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = AvailableActionLabels ?? string.Empty;
            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var s = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(s)) continue;
                set.Add(s);
            }
            return set;
        }

        private static bool TryResolveAllowedStance(string modelStance, HashSet<string> allowedStances, out string resolved)
        {
            resolved = null;
            if (allowedStances == null || allowedStances.Count == 0) return false;
            if (string.IsNullOrWhiteSpace(modelStance)) return false;

            var candidate = modelStance.Trim();
            foreach (var stance in allowedStances)
            {
                if (string.Equals(candidate, stance, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = stance;
                    return true;
                }
            }
            return false;
        }

        private string ResolveFallbackStance(HashSet<string> allowedStances)
        {
            if (allowedStances == null || allowedStances.Count == 0)
                return string.IsNullOrWhiteSpace(CurrentStance) ? "firm" : CurrentStance;

            if (TryResolveAllowedStance(CurrentStance, allowedStances, out var current))
                return current;

            if (TryResolveAllowedStance(DefaultFallbackStance, allowedStances, out var configured))
                return configured;

            return allowedStances.First();
        }

        private static bool TryResolveAllowedActionLabel(string modelLabel, HashSet<string> allowedLabels, out string resolved)
        {
            resolved = null;
            if (allowedLabels == null || allowedLabels.Count == 0) return false;
            if (string.IsNullOrWhiteSpace(modelLabel)) return false;

            var candidate = modelLabel.Trim();
            foreach (var label in allowedLabels)
            {
                if (string.Equals(candidate, label, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = label;
                    return true;
                }
            }
            return false;
        }

        private string ResolveFallbackActionLabel(HashSet<string> allowedLabels)
        {
            if (allowedLabels == null || allowedLabels.Count == 0)
                return string.IsNullOrWhiteSpace(DefaultFallbackActionLabel) ? "calm down" : DefaultFallbackActionLabel;

            if (TryResolveAllowedActionLabel(DefaultFallbackActionLabel, allowedLabels, out var configured))
                return configured;

            return allowedLabels.First();
        }

        private static bool TryExtractStringPropertyFromJson(string jsonText, string propertyName, out string value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(jsonText) || string.IsNullOrWhiteSpace(propertyName)) return false;

            var key = "\"" + propertyName + "\"";
            var pos = jsonText.IndexOf(key, StringComparison.Ordinal);
            if (pos < 0) return false;

            var colon = jsonText.IndexOf(':', pos + key.Length);
            if (colon < 0) return false;

            var i = colon + 1;
            while (i < jsonText.Length && char.IsWhiteSpace(jsonText[i])) i++;
            if (i >= jsonText.Length || jsonText[i] != '"') return false;

            i++;
            var sb = new StringBuilder();
            bool escaped = false;
            for (; i < jsonText.Length; i++)
            {
                var ch = jsonText[i];
                if (escaped)
                {
                    sb.Append(ch);
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    sb.Append(ch);
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    value = TraceUtils.UnescapeJsonEscapes(sb.ToString());
                    return true;
                }

                sb.Append(ch);
            }

            return false;
        }

        private static List<string> ExtractTopLevelPropertyNames(string jsonText)
        {
            var keys = new List<string>();
            if (string.IsNullOrWhiteSpace(jsonText)) return keys;

            bool inString = false;
            bool escaped = false;
            int depth = 0;

            for (int i = 0; i < jsonText.Length; i++)
            {
                var ch = jsonText[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = !inString;

                    if (inString && depth == 1)
                    {
                        var start = i + 1;
                        var sb = new StringBuilder();
                        bool localEsc = false;
                        int j = start;
                        for (; j < jsonText.Length; j++)
                        {
                            var c = jsonText[j];
                            if (localEsc)
                            {
                                sb.Append(c);
                                localEsc = false;
                                continue;
                            }
                            if (c == '\\')
                            {
                                sb.Append(c);
                                localEsc = true;
                                continue;
                            }
                            if (c == '"') break;
                            sb.Append(c);
                        }

                        if (j < jsonText.Length)
                        {
                            int k = j + 1;
                            while (k < jsonText.Length && char.IsWhiteSpace(jsonText[k])) k++;
                            if (k < jsonText.Length && jsonText[k] == ':')
                            {
                                var key = TraceUtils.UnescapeJsonEscapes(sb.ToString());
                                if (!string.IsNullOrWhiteSpace(key)) keys.Add(key);
                            }
                        }
                    }

                    continue;
                }

                if (inString) continue;

                if (ch == '{') depth++;
                else if (ch == '}') depth = Math.Max(0, depth - 1);
            }

            return keys;
        }

        private static string BuildRoomText(AltoDecision decision)
        {
            if (decision == null) return string.Empty;
            return decision.intended_action;
        }

        private string BuildTurnContextLabel()
        {
            return string.IsNullOrWhiteSpace(CharacterSummary)
                ? "character_rehearsal"
                : CharacterSummary.Trim();
        }

        private string BuildTurnSnapshot(string latestMove, AltoDecision decision, string resolvedMemoryId, string resolvedMemoryLabel, bool executionSucceeded, string executionResult)
        {
            var sb = new StringBuilder();
            sb.AppendLine("latest_move: " + (latestMove ?? string.Empty).Trim());
            sb.AppendLine("character_summary: " + (CharacterSummary ?? string.Empty).Trim());
            sb.AppendLine("objective: " + (CurrentObjective ?? string.Empty).Trim());
            sb.AppendLine("stance: " + (CurrentStance ?? string.Empty).Trim());
            sb.AppendLine("intended_action: " + (decision != null ? decision.intended_action : string.Empty));
            sb.AppendLine("selected_action_label: " + (decision != null ? decision.selected_action_label : string.Empty));
            sb.AppendLine("resolved_memory_id: " + (resolvedMemoryId ?? string.Empty));
            sb.AppendLine("resolved_memory_trigger: " + (resolvedMemoryLabel ?? string.Empty));
            sb.AppendLine("execution_succeeded: " + executionSucceeded);
            sb.AppendLine("execution_result: " + (executionResult ?? string.Empty));
            sb.AppendLine("reason: " + (decision != null ? decision.reason : string.Empty));
            return sb.ToString().Trim();
        }

        private string BuildInspectorInteractionSnapshot()
        {
            var sb = new StringBuilder();
            sb.AppendLine("character_summary: " + (CharacterSummary ?? string.Empty).Trim());
            sb.AppendLine("objective: " + (CurrentObjective ?? string.Empty).Trim());
            sb.AppendLine("stance: " + (CurrentStance ?? string.Empty).Trim());
            sb.AppendLine("last_action_label: " + (LastResolvedActionLabel ?? string.Empty).Trim());
            sb.AppendLine("last_memory_id: " + (LastResolvedMemoryId ?? string.Empty).Trim());
            sb.AppendLine("last_memory_trigger: " + (LastResolvedMemoryLabel ?? string.Empty).Trim());
            sb.AppendLine("last_execution_result: " + (LastExecutionResult ?? string.Empty).Trim());
            sb.AppendLine("last_reason: " + (LastResolvedReason ?? string.Empty).Trim());
            return sb.ToString().Trim();
        }

        private void AddToDialogueWindow(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            _dialogueWindow.Enqueue(line.Trim());
            while (_dialogueWindow.Count > Math.Max(1, DialogueWindowTurns))
                _dialogueWindow.Dequeue();
        }

        private string GetDialogueWindowText()
        {
            if (_dialogueWindow.Count == 0) return "";
            var joined = string.Join(" | ", _dialogueWindow);
            if (joined.Length <= MaxConversationChars) return joined;
            return joined.Substring(0, MaxConversationChars) + "...[truncated]";
        }

        private void AppendConversation(string line)
        {
            Debug.Log(line);
            if (ConversationLog == null) return;
            var current = ConversationLog.text ?? string.Empty;
            ConversationLog.text = string.IsNullOrWhiteSpace(current) ? line : current + "\n" + line;
        }

        private void RefreshPromptInspectionText()
        {
            if (!UpdatePromptInspectionText || PromptInspectionText == null) return;

            var text =
                "[AALTO PROMPT INSPECTION]\n" +
                "--- system ---\n" + (LastSystemPrompt ?? "") + "\n\n" +
                "--- user context ---\n" + (LastUserContext ?? "") + "\n\n" +
                "--- assistant json ---\n" + (LastAssistantResponseJson ?? "") + "\n\n" +
                "--- directing memory ---\n" + (DirectingMemoryPreview ?? string.Empty) + "\n\n" +
                "--- decision trace ---\n" +
                "objective: " + (LastResolvedObjective ?? string.Empty) + "\n" +
                "stance: " + (LastResolvedStance ?? string.Empty) + "\n" +
                "intended action: " + (LastResolvedIntendedAction ?? string.Empty) + "\n" +
                "action label: " + (LastResolvedActionLabel ?? string.Empty) + "\n" +
                "mapped memory: " + (LastResolvedMemoryLabel ?? string.Empty) + "\n" +
                "reason: " + (LastResolvedReason ?? string.Empty) + "\n" +
                "execution: " + (LastExecutionResult ?? string.Empty);

            PromptInspectionText.text = text;
        }

        private void CopyToClipboard(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AppendConversation($"[aalto-warning] No {label} available yet.");
                return;
            }

            GUIUtility.systemCopyBuffer = value;
            AppendConversation($"[aalto] Copied {label} to clipboard.");
        }

        private bool ResolveAndExecuteMemoryTrigger(string selectedActionLabel, out string resolvedMemoryId, out string resolvedMemoryLabel, out bool executionSucceeded, out string executionResult)
        {
            resolvedMemoryId = string.Empty;
            resolvedMemoryLabel = string.Empty;
            executionSucceeded = true;
            executionResult = "Dry-run only; no external execution.";

            if (ActionMemoryRegistry == null)
            {
                executionSucceeded = false;
                executionResult = "ActionMemoryRegistry is not assigned.";
                AppendConversation("[aalto-warning] " + executionResult);
                return false;
            }

            if (!ActionMemoryRegistry.TryResolveMemoryTrigger(selectedActionLabel, out var trigger))
            {
                executionSucceeded = false;
                executionResult = $"No memory mapping for label '{selectedActionLabel}'.";
                AppendConversation("[aalto-warning] " + executionResult);
                return false;
            }

            resolvedMemoryLabel = trigger;
            resolvedMemoryId = ExtractMemoryId(trigger);
            AppendConversation($"[aalto] label '{selectedActionLabel}' -> trigger '{trigger}'");

            if (DryRunOnly)
            {
                executionSucceeded = true;
                executionResult = "Dry-run only; trigger resolved but not sent.";
                return true;
            }

            if (OscSender == null)
            {
                executionSucceeded = false;
                executionResult = "OscSender is not assigned.";
                AppendConversation("[aalto-error] " + executionResult);
                return true;
            }

            AppendConversation("[aalto] OSC endpoint -> " + OscSender.EndpointInfo);

            if (OscSender.TrySendMemoryTrigger(trigger, out var error))
            {
                executionSucceeded = true;
                executionResult = "Trigger sent to OSC bridge at " + OscSender.EndpointInfo + ".";
                return true;
            }

            executionSucceeded = false;
            executionResult = "OSC send failed: " + error;
            return true;
        }

        private static string ExtractMemoryId(string trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger)) return string.Empty;
            var t = trigger.Trim().ToLowerInvariant();
            var idx = t.LastIndexOf("memory", StringComparison.Ordinal);
            if (idx < 0) return string.Empty;

            var rest = t.Substring(idx + "memory".Length).Trim();
            if (rest.StartsWith(" ")) rest = rest.TrimStart();

            var sb = new StringBuilder();
            for (int i = 0; i < rest.Length; i++)
            {
                var ch = rest[i];
                if (char.IsDigit(ch)) sb.Append(ch);
                else if (sb.Length > 0) break;
            }

            return sb.ToString();
        }
    }
}
