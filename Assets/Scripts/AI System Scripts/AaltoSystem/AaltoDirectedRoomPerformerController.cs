using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace AaltoSystemV3
{
    /// <summary>
    /// A standalone room-performance controller that uses a fixed dramaturgical instruction set
    /// plus runtime context (summary/objective/stance/history/director guidance/actions)
    /// to select exactly one action label from the action registry.
    /// </summary>
    public sealed class AaltoDirectedRoomPerformerController : MonoBehaviour
    {
        public enum ActionStateMode
        {
            [InspectorName("Hold Response State")]
            HoldResponseState = 0,
            [InspectorName("Pulse Then Return To Neutral")]
            PulseThenReturnToNeutral = 1
        }

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
        [InspectorName("OpenAI Client (Agent Produced)")]
        public OpenAIClient OpenAI;
        [InspectorName("Action Memory Registry (Agent Produced)")]
        public AaltoActionMemoryRegistry ActionMemoryRegistry;
        [InspectorName("Objective/Stance Bootstrapper (Primary Source)")]
        public AaltoObjectiveStanceBootstrapper ObjectiveStanceBootstrapper;

        [Header("Simulated Speech")]
        [TextArea(1, 4)]
        [InspectorName("Simulated Speech Input")]
        public string InspectorActorInput;

        [Header("Spoken Input")]
        [Tooltip("If true, ReceiveExternalSpeech accepts spoken transcripts from OSC/Vosk bridge.")]
        public bool AcceptExternalSpeechInput = true;

        [Tooltip("If true, accepted spoken transcripts are submitted immediately as turns.")]
        public bool AutoSubmitExternalSpeech = true;

        [TextArea(1, 3)]
        [InspectorName("Latest Spoken Transcript")]
        public string LastExternalSpeechText;

        [TextArea(2, 6)]
        [InspectorName("Director Guidance Input")]
        public string DirectorGuidanceInput;

        [InspectorName("Director Guidance Inputs")]
        public List<string> DirectorGuidanceInputs = new List<string>();

        [TextArea(1, 3)]
        [InspectorName("Selected Action (Agent Produced)")]
        public string SelectedActionText;

        [TextArea(2, 6)]
        [InspectorName("Action Justification (Agent Produced)")]
        public string ActionJustificationText;

        [Header("Dialog Log")]
        [Tooltip("Maximum number of dialog turns kept in the inspector log.")]
        [Range(10, 500)]
        public int DialogLogLimit = 120;

        [Tooltip("If true, dialog log renders as one-line entries with foldout details.")]
        public bool DialogLogCompactMode = true;

        [InspectorName("Current Take Number")]
        public int CurrentTakeNumber = 1;

        [InspectorName("Dialog Turns (Agent Produced)")]
        public List<DialogTurnEntry> DialogTurns = new List<DialogTurnEntry>();

        [InspectorName("Archived Takes (Agent Produced)")]
        public List<DialogTakeArchiveEntry> ArchivedDialogTakes = new List<DialogTakeArchiveEntry>();

        [Header("Runtime Context")]
        [Tooltip("If true, the performer pulls summary/objective/stance from ObjectiveStanceBootstrapper before each turn.")]
        public bool PullContextFromBootstrapper = true;

        [TextArea(2, 8)]
        [InspectorName("Current Character Summary (Agent Produced)")]
        public string CurrentCharacterSummary;

        [TextArea(1, 4)]
        [InspectorName("Current Objective (Agent Produced)")]
        public string CurrentObjective = "Protect continuity of the room's inner life.";

        [TextArea(1, 4)]
        [InspectorName("Current Stance (Agent Produced)")]
        public string CurrentStance = "firm";

        [TextArea(2, 8)]
        [InspectorName("Director Guidance (Architect Input)")]
        public string DirectorGuidance;

        [Header("Prompt")]
        [TextArea(18, 40)]
        [InspectorName("General Instructions (System Prompt)")]
        public string GeneralInstructions =
            "You are performing the inner life of a room in an improvised scene with another actor, who speaks to the room. You listen to what the actor says and respond by selecting the most appropriate action from a predefined list of actions.\n\n" +
            "Do not behave like a chatbot or a neutral assistant. Approach the role as a method actor would: interpret each moment from within the room's inner life, shaped by backstory, objectives, obstacles, circumstances, stance, recent interaction history, and director guidance. All of these are provided in this prompt.\n\n" +
            "The room cannot speak directly. It can only express itself through a fixed list of available action labels. These labels correspond to physical or scenographic behaviours, such as changes in light, atmosphere, or spatial expression. The actor will perceive your response only through the environment.\n\n" +
            "Your task is to choose the single action that is most dramaturgically appropriate for the current moment.\n\n" +
            "When choosing an action:\n" +
            "- take into account the actor's latest spoken phrase, since your action responds directly to it\n" +
            "- take into account the character summary, current objective, current stance, recent interaction history, and director guidance\n" +
            "- do not respond only to the latest line; consider previous interactions so that the scene unfolds with coherence and dramaturgically appropriate development\n" +
            "- do not act randomly\n" +
            "- do not invent new actions; only choose from the provided list\n" +
            "- aim for behavioural coherence, interpretability, and dramatic usefulness\n\n" +
            "Treat contradictions as part of the role, not as errors. If different parts of the context pull in different directions, handle this as an actor would: choose the action that best expresses or productively navigates the tension of the moment.\n\n" +
            "Use the recent interaction history and past justifications to maintain continuity. If a behaviour, tone, symbolic association, or use of an action has already been established, remain consistent unless there is a strong reason to shift.\n\n" +
            "Return JSON only with exactly this schema:\n" +
            "{\n" +
            "  \"chosen_action\": \"<one action label from the available list>\",\n" +
            "  \"justification\": \"<1-3 sentences explaining why this action was chosen>\"\n" +
            "}";

        [Range(2, 12)]
        [Tooltip("How many recent interactions are included in the runtime prompt.")]
        public int RecentHistoryLimit = 5;

        [Header("Model")]
        [InspectorName("Model (OpenAI Dropdown)")]
        public OpenAIModelPreset Model = OpenAIModelPreset.Gpt4oMini;

        [Header("Execution")]
        [Tooltip("If true, selected action is sent through ActionMemoryRegistry.TrySendMemoryTriggerForActionLabel.")]
        public bool ApplyActionThroughRegistry = true;

        [Tooltip("Choose whether the selected response remains active, or briefly pulses then returns to a neutral memory trigger.")]
        [InspectorName("Action State Mode")]
        public ActionStateMode SelectedActionStateMode = ActionStateMode.HoldResponseState;

        [Tooltip("Neutral memory trigger used when mode is Pulse Then Return To Neutral (example: 'memory 1').")]
        [InspectorName("Neutral Memory Trigger")]
        public string NeutralMemoryTrigger = "memory 1";

        [Tooltip("How long the selected response should remain before returning to the neutral memory trigger.")]
        [Range(0.1f, 5f)]
        [InspectorName("Response Pulse Seconds")]
        public float ResponsePulseSeconds = 1f;

        [TextArea(1, 3)]
        [InspectorName("Execution Mode Status")]
        public string ExecutionModeStatus;

        [Tooltip("If true, prompt action labels come only from the manually pulled snapshot (no automatic registry fallback).")]
        public bool UsePulledActionLabelsSnapshot = true;

        [InspectorName("Pulled Action Labels Snapshot")]
        public List<string> PulledActionLabelsSnapshot = new List<string>();

        [TextArea(1, 3)]
        [InspectorName("Action Pull Status")]
        public string ActionPullStatus;

        [Tooltip("If false, unknown model action labels are rejected and fallback is used.")]
        public bool EnforceRegistryActionLabels = true;

        [Tooltip("Fallback label used when parsing/validation fails.")]
        public string FallbackActionLabel = "no";

        [Header("Debug")]
        [TextArea(4, 14)]
        public string LastRuntimePrompt;
        [TextArea(4, 14)]
        public string LastRawModelResponse;
        [TextArea(2, 6)]
        public string LastStatus;
        [TextArea(4, 16)]
        public string PromptInspectionText;
        [TextArea(2, 6)]
        public string ContextSyncStatus;

        private readonly Queue<string> _recentInteractions = new Queue<string>();
        private CancellationTokenSource _cts;
        private CancellationTokenSource _neutralReturnCts;

        [Serializable]
        private sealed class DecisionResponse
        {
            public string chosen_action;
            public string justification;
        }

        [Serializable]
        public sealed class DialogTurnEntry
        {
            [TextArea(1, 3)]
            public string actorLine;

            [TextArea(1, 3)]
            public string selectedResponse;

            [TextArea(2, 6)]
            public string justification;

            public bool showDetails;

            public bool showJustification;
        }

        [Serializable]
        public sealed class DialogTakeArchiveEntry
        {
            public int takeNumber;
            public string capturedAtUtc;
            public int turnCount;
            public List<DialogTurnEntry> turns = new List<DialogTurnEntry>();
        }

        [Serializable]
        private sealed class DialogArchiveExportPayload
        {
            public string exportedAtUtc;
            public int currentTakeNumber;
            public int archivedTakeCount;
            public List<DialogTakeArchiveEntry> archivedTakes = new List<DialogTakeArchiveEntry>();
            public DialogTakeArchiveEntry currentTake;
        }

        private string SelectedModelId => Model switch
        {
            OpenAIModelPreset.Gpt54 => "gpt-5.4",
            OpenAIModelPreset.Gpt54Mini => "gpt-5.4-mini",
            OpenAIModelPreset.Gpt41 => "gpt-4.1",
            OpenAIModelPreset.Gpt41Mini => "gpt-4.1-mini",
            OpenAIModelPreset.Gpt4oMini => "gpt-4o-mini",
            _ => "gpt-4o-mini"
        };

        private void Awake()
        {
            _cts = new CancellationTokenSource();
            SyncContextFromBootstrapper();
        }

        private void OnValidate()
        {
            SyncContextFromBootstrapper();
        }

        private void OnDestroy()
        {
            CancelPendingNeutralReturn();

            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        public void SubmitTurnFromInspectorInput()
        {
            var text = (InspectorActorInput ?? string.Empty).Trim();
            text = text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            InspectorActorInput = string.Empty;
            EmitPerformerEvent("performer.turn_submitted",
                "{\"source\":\"inspector\",\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(text) + "\"}");

            _ = HandleTurnAsync(text);
        }

        public void SubmitTurn(string latestActorInput)
        {
            var text = (latestActorInput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            EmitPerformerEvent("performer.turn_submitted",
                "{\"source\":\"api\",\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(text) + "\"}");
            _ = HandleTurnAsync(text);
        }

        public void ReceiveExternalSpeech(string text)
        {
            var transcript = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return;
            }

            LastExternalSpeechText = transcript;
            Debug.Log("[AaltoDirectedRoomPerformer] Received external speech: " + transcript);
            EmitPerformerEvent("performer.external_speech_received",
                "{\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(transcript) + "\"}");

            if (!AcceptExternalSpeechInput)
            {
                EmitPerformerEvent("performer.external_speech_ignored",
                    "{\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(transcript) + "\",\"reason\":\"spoken_input_disabled\"}");
                return;
            }

            if (AutoSubmitExternalSpeech)
            {
                SubmitTurn(transcript);
            }
            else
            {
                InspectorActorInput = transcript;
            }
        }

        public void SubmitDirectorGuidanceInput()
        {
            var guidance = (DirectorGuidanceInput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(guidance))
            {
                return;
            }

            DirectorGuidanceInputs.Add(guidance);
            DirectorGuidanceInput = string.Empty;
            RebuildDirectorGuidanceFromInputs();
        }

        public void RemoveDirectorGuidanceAt(int index)
        {
            if (index < 0 || index >= DirectorGuidanceInputs.Count)
            {
                return;
            }

            DirectorGuidanceInputs.RemoveAt(index);
            RebuildDirectorGuidanceFromInputs();
        }

        public void ClearDirectorGuidanceInputs()
        {
            DirectorGuidanceInputs.Clear();
            RebuildDirectorGuidanceFromInputs();
        }

        private async Task HandleTurnAsync(string latestActorInput)
        {
            if (_cts == null || _cts.IsCancellationRequested)
                return;

            try
            {
                SyncContextFromBootstrapper();

                SetStatus("Preparing runtime prompt...");
                var availableActions = GetAvailableActionLabels();
                if (availableActions.Count == 0)
                {
                    SetStatus(UsePulledActionLabelsSnapshot
                        ? "No pulled action labels available. Use 'Pull Action Labels From Registry Now' first."
                        : "No action labels configured in ActionMemoryRegistry.");
                    return;
                }

                var runtimePrompt = BuildRuntimePrompt(latestActorInput, availableActions);
                LastRuntimePrompt = runtimePrompt;
                EmitPerformerEvent("performer.request_prepared",
                    "{\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"," +
                    "\"latest_actor_input\":\"" + AaltoLaunchSessionLogger.EscapeJson(latestActorInput) + "\"," +
                    "\"available_action_labels\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildActionLabelCsv(availableActions)) + "\"," +
                    "\"action_memory_pairs\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildActionMemoryPairsSnapshot()) + "\"," +
                    "\"runtime_prompt\":\"" + AaltoLaunchSessionLogger.EscapeJson(runtimePrompt) + "\"}");

                var messages = new List<OpenAIClient.Msg>
                {
                    new OpenAIClient.Msg("system", GeneralInstructions ?? string.Empty),
                    new OpenAIClient.Msg("user", runtimePrompt)
                };

                SetStatus("Requesting action decision from model...");
                var response = await OpenAI.ChatCompletionsJsonAsync(
                    messages,
                    model: SelectedModelId,
                    contextTag: "Aalto:DirectedRoomPerformer");

                LastRawModelResponse = response;
                EmitPerformerEvent("performer.model_response",
                    "{\"raw_response\":\"" + AaltoLaunchSessionLogger.EscapeJson(response ?? string.Empty) + "\"}");

                if (!TryParseDecision(response, out var action, out var justification, out var parseError))
                {
                    SetStatus("Parse failed: " + parseError + " | Applying fallback action.");
                    action = ResolveFallbackAction(availableActions);
                    justification = parseError;
                }

                var resolvedAction = ResolveActionLabel(action, availableActions);
                if (EnforceRegistryActionLabels && string.IsNullOrWhiteSpace(resolvedAction))
                {
                    resolvedAction = ResolveFallbackAction(availableActions);
                    justification = (justification ?? string.Empty) + " [Action label was outside available set.]";
                }
                else if (string.IsNullOrWhiteSpace(resolvedAction))
                {
                    resolvedAction = action?.Trim();
                }

                SelectedActionText = resolvedAction;
                ActionJustificationText = (justification ?? string.Empty).Trim();
                EmitPerformerEvent("performer.decision_resolved",
                    "{\"chosen_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedActionText ?? string.Empty) + "\"," +
                    "\"justification\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionJustificationText ?? string.Empty) + "\"}");

                var historyLine =
                    "actor: " + latestActorInput.Trim() +
                    " | action: " + SelectedActionText +
                    " | why: " + ActionJustificationText;
                EnqueueHistory(historyLine);
                AddDialogTurn(latestActorInput, SelectedActionText, ActionJustificationText);

                await ApplyActionDispatchAsync(SelectedActionText);

                EmitTurnRecord(
                    latestActorInput,
                    availableActions,
                    string.IsNullOrWhiteSpace(ExecutionModeStatus) || ExecutionModeStatus.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0,
                    ExecutionModeStatus);

                RefreshPromptInspection();
            }
            catch (Exception ex)
            {
                EmitPerformerEvent("performer.error",
                    "{\"message\":\"" + AaltoLaunchSessionLogger.EscapeJson(ex.Message) + "\"}");
                SetStatus("Error: " + ex.Message);
                RefreshPromptInspection();
            }
        }

        private void RefreshPromptInspection()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== SYSTEM INSTRUCTIONS ===");
            sb.AppendLine(GeneralInstructions ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("=== RUNTIME PROMPT ===");
            sb.AppendLine(LastRuntimePrompt ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("=== MODEL RESPONSE ===");
            sb.AppendLine(LastRawModelResponse ?? string.Empty);
            PromptInspectionText = sb.ToString().Trim();
        }

        private string BuildRuntimePrompt(string latestActorInput, List<string> availableActions)
        {
            var sb = new StringBuilder();
            sb.AppendLine("latest_actor_input:");
            sb.AppendLine((latestActorInput ?? string.Empty).Trim());
            sb.AppendLine();

            sb.AppendLine("character_summary:");
            sb.AppendLine((CurrentCharacterSummary ?? string.Empty).Trim());
            sb.AppendLine();

            sb.AppendLine("current_objective:");
            sb.AppendLine((CurrentObjective ?? string.Empty).Trim());
            sb.AppendLine();

            sb.AppendLine("current_stance:");
            sb.AppendLine((CurrentStance ?? string.Empty).Trim());
            sb.AppendLine();

            sb.AppendLine("director_guidance:");
            sb.AppendLine((DirectorGuidance ?? string.Empty).Trim());
            sb.AppendLine();

            sb.AppendLine("available_action_labels:");
            for (int i = 0; i < availableActions.Count; i++)
                sb.AppendLine("- " + availableActions[i]);
            sb.AppendLine();

            sb.AppendLine("recent_interaction_history (most recent last):");
            foreach (var line in _recentInteractions)
                sb.AppendLine("- " + line);
            if (_recentInteractions.Count == 0)
                sb.AppendLine("- (none yet)");

            sb.AppendLine();
            sb.AppendLine("response_format:");
            sb.AppendLine("Return JSON only with exactly this schema:");
            sb.AppendLine("{");
            sb.AppendLine("  \"chosen_action\": \"<one action label from available_action_labels>\",");
            sb.AppendLine("  \"justification\": \"<1-3 sentences>\"");
            sb.AppendLine("}");

            return sb.ToString().Trim();
        }

        private void SyncContextFromBootstrapper()
        {
            if (!PullContextFromBootstrapper || ObjectiveStanceBootstrapper == null)
            {
                ContextSyncStatus = !PullContextFromBootstrapper
                    ? "Context pull disabled."
                    : "Objective/Stance bootstrapper not assigned.";
                return;
            }

            var summary = (ObjectiveStanceBootstrapper.CurrentCharacterSummary ?? string.Empty).Trim();
            var objective = (ObjectiveStanceBootstrapper.GeneratedObjective ?? string.Empty).Trim();
            var stance = (ObjectiveStanceBootstrapper.GeneratedStance ?? string.Empty).Trim();

            // Pull each field independently so summary is available immediately,
            // even if objective/stance generation is still pending.
            if (!string.IsNullOrWhiteSpace(summary))
                CurrentCharacterSummary = summary;
            if (!string.IsNullOrWhiteSpace(objective))
                CurrentObjective = objective;
            if (!string.IsNullOrWhiteSpace(stance))
                CurrentStance = stance;

            var hasSummary = !string.IsNullOrWhiteSpace(CurrentCharacterSummary);
            var hasObjective = !string.IsNullOrWhiteSpace(CurrentObjective);
            var hasStance = !string.IsNullOrWhiteSpace(CurrentStance);

            ContextSyncStatus = hasSummary && hasObjective && hasStance
                ? "Context synced from bootstrapper."
                : "Context partially synced. Waiting for missing fields from bootstrapper.";
        }

        [ContextMenu("Directed Performer/Pull Context From Bootstrapper Now")]
        public void PullContextFromBootstrapperNow()
        {
            var beforeSummary = CurrentCharacterSummary;
            var beforeObjective = CurrentObjective;
            var beforeStance = CurrentStance;

            SyncContextFromBootstrapper();

            var changed =
                !string.Equals(beforeSummary, CurrentCharacterSummary, StringComparison.Ordinal) ||
                !string.Equals(beforeObjective, CurrentObjective, StringComparison.Ordinal) ||
                !string.Equals(beforeStance, CurrentStance, StringComparison.Ordinal);

            if (ObjectiveStanceBootstrapper == null)
            {
                SetStatus("Context pull skipped: ObjectiveStanceBootstrapper is not assigned.");
                return;
            }

            if (!PullContextFromBootstrapper)
            {
                SetStatus("Context pull skipped: PullContextFromBootstrapper is disabled.");
                return;
            }

            SetStatus(changed
                ? "Context pulled from bootstrapper."
                : "Context pull completed; no new values were available yet.");
        }

        [ContextMenu("Directed Performer/Pull Action Labels From Registry Now")]
        public void PullActionsFromRegistryNow()
        {
            var pulled = BuildActionLabelListFromRegistry();
            PulledActionLabelsSnapshot = pulled;
            EmitPerformerEvent("performer.action_labels_pulled",
                "{\"count\":" + pulled.Count + "," +
                "\"available_action_labels\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildActionLabelCsv(pulled)) + "\"," +
                "\"action_memory_pairs\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildActionMemoryPairsSnapshot()) + "\"}");

            if (pulled.Count == 0)
            {
                ActionPullStatus = "No action labels found in ActionMemoryRegistry.";
                SetStatus(ActionPullStatus);
                return;
            }

            ActionPullStatus = $"Pulled {pulled.Count} action labels from registry.";
            SetStatus(ActionPullStatus);
        }

        [ContextMenu("Directed Performer/Submit Inspector Input")]
        public void SubmitInspectorInputContextMenu()
        {
            SubmitTurnFromInspectorInput();
        }

        private List<string> GetAvailableActionLabels()
        {
            if (UsePulledActionLabelsSnapshot)
                return PulledActionLabelsSnapshot != null
                    ? new List<string>(PulledActionLabelsSnapshot)
                    : new List<string>();

            return BuildActionLabelListFromRegistry();
        }

        private List<string> BuildActionLabelListFromRegistry()
        {
            var labels = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (ActionMemoryRegistry == null || ActionMemoryRegistry.mappings == null)
                return labels;

            for (int i = 0; i < ActionMemoryRegistry.mappings.Count; i++)
            {
                var mapping = ActionMemoryRegistry.mappings[i];
                if (mapping == null) continue;
                var label = (mapping.actionLabel ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(label)) continue;
                if (seen.Contains(label)) continue;
                seen.Add(label);
                labels.Add(label);
            }

            return labels;
        }

        private static string BuildActionLabelCsv(List<string> labels)
        {
            if (labels == null || labels.Count == 0)
                return string.Empty;

            return string.Join(",", labels);
        }

        private string BuildActionMemoryPairsSnapshot()
        {
            if (ActionMemoryRegistry == null || ActionMemoryRegistry.mappings == null || ActionMemoryRegistry.mappings.Count == 0)
                return string.Empty;

            var pairs = new List<string>();
            for (int i = 0; i < ActionMemoryRegistry.mappings.Count; i++)
            {
                var mapping = ActionMemoryRegistry.mappings[i];
                if (mapping == null)
                    continue;

                var label = (mapping.actionLabel ?? string.Empty).Trim();
                var memory = (mapping.memoryTrigger ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(memory))
                    continue;

                pairs.Add(label + "=>" + memory);
            }

            return string.Join("|", pairs);
        }

        private void EnqueueHistory(string line)
        {
            _recentInteractions.Enqueue((line ?? string.Empty).Trim());
            var max = Mathf.Max(1, RecentHistoryLimit);
            while (_recentInteractions.Count > max)
                _recentInteractions.Dequeue();
        }

        private void AddDialogTurn(string actorLine, string selectedResponse, string justification)
        {
            if (DialogTurns == null)
                DialogTurns = new List<DialogTurnEntry>();

            DialogTurns.Add(new DialogTurnEntry
            {
                actorLine = (actorLine ?? string.Empty).Trim(),
                selectedResponse = (selectedResponse ?? string.Empty).Trim(),
                justification = (justification ?? string.Empty).Trim(),
                showDetails = false,
                showJustification = false
            });

            var max = Mathf.Max(1, DialogLogLimit);
            while (DialogTurns.Count > max)
                DialogTurns.RemoveAt(0);
        }

        private async Task ApplyActionDispatchAsync(string selectedAction)
        {
            if (!ApplyActionThroughRegistry || ActionMemoryRegistry == null || string.IsNullOrWhiteSpace(selectedAction))
            {
                EmitPerformerEvent("performer.action_dispatch",
                    "{\"chosen_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(selectedAction ?? string.Empty) + "\",\"applied_through_registry\":false}");
                ExecutionModeStatus = "Registry dispatch disabled or unavailable. Action was resolved only.";
                SetStatus("Action resolved: " + selectedAction);
                return;
            }

            if (!ActionMemoryRegistry.TrySendMemoryTriggerForActionLabel(selectedAction, out var sendError))
            {
                EmitPerformerEvent("performer.action_dispatch",
                    "{\"chosen_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(selectedAction) + "\"," +
                    "\"resolved_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionMemoryRegistry.LastResolvedMemoryTrigger ?? string.Empty) + "\"," +
                    "\"registry_send\":\"failed\",\"error\":\"" + AaltoLaunchSessionLogger.EscapeJson(sendError ?? string.Empty) + "\"}");
                ExecutionModeStatus = "Dispatch failed. Response state was not applied.";
                SetStatus("Action resolved but registry send failed: " + sendError);
                return;
            }

            EmitPerformerEvent("performer.action_dispatch",
                "{\"chosen_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(selectedAction) + "\"," +
                "\"resolved_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionMemoryRegistry.LastResolvedMemoryTrigger ?? string.Empty) + "\"," +
                "\"registry_send\":\"ok\",\"mode\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedActionStateMode.ToString()) + "\"}");

            if (SelectedActionStateMode == ActionStateMode.HoldResponseState)
            {
                CancelPendingNeutralReturn();
                ExecutionModeStatus = "Hold mode active. Response state remains until another action is applied.";
                SetStatus("Action applied: " + selectedAction);
                return;
            }

            var neutralTrigger = (NeutralMemoryTrigger ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(neutralTrigger))
            {
                ExecutionModeStatus = "Pulse mode active, but neutral memory trigger is empty.";
                SetStatus("Action applied, but neutral return is not configured.");
                return;
            }

            CancelPendingNeutralReturn();
            _neutralReturnCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var returnToken = _neutralReturnCts.Token;

            var pulseSeconds = Mathf.Max(0.1f, ResponsePulseSeconds);
            ExecutionModeStatus =
                "Pulse mode active. Applied '" + selectedAction + "' then returning to neutral trigger '" + neutralTrigger +
                "' after " + pulseSeconds.ToString("0.00") + "s.";
            SetStatus("Action pulsed: " + selectedAction + " (returning to neutral soon)");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pulseSeconds), returnToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (_cts == null || _cts.IsCancellationRequested || returnToken.IsCancellationRequested)
                return;

            if (ActionMemoryRegistry.TrySendMemoryTrigger(neutralTrigger, out var neutralError))
            {
                EmitPerformerEvent("performer.neutral_return",
                    "{\"neutral_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(neutralTrigger) + "\",\"send\":\"ok\"}");
                ExecutionModeStatus = "Pulse mode completed. Returned to neutral trigger '" + neutralTrigger + "'.";
                SetStatus("Returned to neutral: " + neutralTrigger);
            }
            else
            {
                EmitPerformerEvent("performer.neutral_return",
                    "{\"neutral_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(neutralTrigger) + "\",\"send\":\"failed\",\"error\":\"" + AaltoLaunchSessionLogger.EscapeJson(neutralError ?? string.Empty) + "\"}");
                ExecutionModeStatus = "Pulse mode failed to return to neutral. " + neutralError;
                SetStatus("Action applied but neutral return failed: " + neutralError);
            }
        }

        private void CancelPendingNeutralReturn()
        {
            if (_neutralReturnCts == null)
                return;

            if (!_neutralReturnCts.IsCancellationRequested)
                _neutralReturnCts.Cancel();

            _neutralReturnCts.Dispose();
            _neutralReturnCts = null;
        }

        [ContextMenu("Directed Performer/Start New Take")]
        public void StartNewTake()
        {
            CancelPendingNeutralReturn();
            ArchiveCurrentTakeIfNeeded();

            _recentInteractions.Clear();
            DialogTurns?.Clear();

            InspectorActorInput = string.Empty;
            LastExternalSpeechText = string.Empty;
            SelectedActionText = string.Empty;
            ActionJustificationText = string.Empty;
            LastRuntimePrompt = string.Empty;
            LastRawModelResponse = string.Empty;
            PromptInspectionText = string.Empty;

            CurrentTakeNumber = Mathf.Max(1, CurrentTakeNumber + 1);
            SetStatus($"Started new take #{CurrentTakeNumber}. Active dialog memory cleared.");
        }

        public string BuildDialogArchiveExportJson()
        {
            var payload = new DialogArchiveExportPayload
            {
                exportedAtUtc = DateTime.UtcNow.ToString("o"),
                currentTakeNumber = Mathf.Max(1, CurrentTakeNumber),
                archivedTakeCount = ArchivedDialogTakes != null ? ArchivedDialogTakes.Count : 0,
                archivedTakes = CloneTakeArchiveList(ArchivedDialogTakes),
                currentTake = new DialogTakeArchiveEntry
                {
                    takeNumber = Mathf.Max(1, CurrentTakeNumber),
                    capturedAtUtc = DateTime.UtcNow.ToString("o"),
                    turnCount = DialogTurns != null ? DialogTurns.Count : 0,
                    turns = CloneDialogTurns(DialogTurns)
                }
            };

            return JsonUtility.ToJson(payload, true);
        }

        public string BuildDialogArchiveExportCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("take_type,take_number,take_captured_at_utc,turn_index,actor_line,selected_response,justification");

            if (ArchivedDialogTakes != null)
            {
                for (int i = 0; i < ArchivedDialogTakes.Count; i++)
                {
                    var take = ArchivedDialogTakes[i];
                    if (take == null)
                        continue;

                    AppendTakeRows(sb, "archived", take.takeNumber, take.capturedAtUtc, take.turns);
                }
            }

            AppendTakeRows(
                sb,
                "current",
                Mathf.Max(1, CurrentTakeNumber),
                DateTime.UtcNow.ToString("o"),
                DialogTurns);

            return sb.ToString();
        }

        private void ArchiveCurrentTakeIfNeeded()
        {
            if (DialogTurns == null || DialogTurns.Count == 0)
                return;

            if (ArchivedDialogTakes == null)
                ArchivedDialogTakes = new List<DialogTakeArchiveEntry>();

            var takeEntry = new DialogTakeArchiveEntry
            {
                takeNumber = Mathf.Max(1, CurrentTakeNumber),
                capturedAtUtc = DateTime.UtcNow.ToString("o"),
                turnCount = DialogTurns.Count,
                turns = CloneDialogTurns(DialogTurns)
            };

            ArchivedDialogTakes.Add(takeEntry);
        }

        private static List<DialogTurnEntry> CloneDialogTurns(List<DialogTurnEntry> source)
        {
            var clone = new List<DialogTurnEntry>();
            if (source == null)
                return clone;

            for (int i = 0; i < source.Count; i++)
            {
                var entry = source[i];
                if (entry == null)
                    continue;

                clone.Add(new DialogTurnEntry
                {
                    actorLine = entry.actorLine,
                    selectedResponse = entry.selectedResponse,
                    justification = entry.justification,
                    showDetails = entry.showDetails,
                    showJustification = entry.showJustification
                });
            }

            return clone;
        }

        private static List<DialogTakeArchiveEntry> CloneTakeArchiveList(List<DialogTakeArchiveEntry> source)
        {
            var clone = new List<DialogTakeArchiveEntry>();
            if (source == null)
                return clone;

            for (int i = 0; i < source.Count; i++)
            {
                var take = source[i];
                if (take == null)
                    continue;

                clone.Add(new DialogTakeArchiveEntry
                {
                    takeNumber = take.takeNumber,
                    capturedAtUtc = take.capturedAtUtc,
                    turnCount = take.turnCount,
                    turns = CloneDialogTurns(take.turns)
                });
            }

            return clone;
        }

        private static void AppendTakeRows(
            StringBuilder sb,
            string takeType,
            int takeNumber,
            string takeCapturedAtUtc,
            List<DialogTurnEntry> turns)
        {
            if (sb == null)
                return;

            if (turns == null || turns.Count == 0)
            {
                sb.Append(CsvEscape(takeType)).Append(',')
                    .Append(CsvEscape(takeNumber.ToString())).Append(',')
                    .Append(CsvEscape(takeCapturedAtUtc)).Append(',')
                    .Append(CsvEscape(string.Empty)).Append(',')
                    .Append(CsvEscape(string.Empty)).Append(',')
                    .Append(CsvEscape(string.Empty)).Append(',')
                    .Append(CsvEscape(string.Empty))
                    .AppendLine();
                return;
            }

            for (int i = 0; i < turns.Count; i++)
            {
                var turn = turns[i];

                sb.Append(CsvEscape(takeType)).Append(',')
                    .Append(CsvEscape(takeNumber.ToString())).Append(',')
                    .Append(CsvEscape(takeCapturedAtUtc)).Append(',')
                    .Append(CsvEscape((i + 1).ToString())).Append(',')
                    .Append(CsvEscape(turn != null ? turn.actorLine : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? turn.selectedResponse : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? turn.justification : string.Empty))
                    .AppendLine();
            }
        }

        private static string CsvEscape(string value)
        {
            var text = value ?? string.Empty;
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            text = text.Replace("\"", "\"\"");
            return "\"" + text + "\"";
        }

        private void RebuildDirectorGuidanceFromInputs()
        {
            if (DirectorGuidanceInputs == null || DirectorGuidanceInputs.Count == 0)
            {
                DirectorGuidance = string.Empty;
                return;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < DirectorGuidanceInputs.Count; i++)
            {
                var entry = (DirectorGuidanceInputs[i] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(entry))
                    continue;

                if (sb.Length > 0)
                    sb.AppendLine();

                sb.Append("- ").Append(entry);
            }

            DirectorGuidance = sb.ToString().Trim();
        }

        private static bool TryParseDecision(
            string response,
            out string chosenAction,
            out string justification,
            out string error)
        {
            chosenAction = null;
            justification = null;
            error = null;

            var text = (response ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "Model returned empty response.";
                return false;
            }

            var json = ExtractJsonObject(text);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Model output did not contain a valid JSON object.";
                return false;
            }

            try
            {
                var parsed = JsonUtility.FromJson<DecisionResponse>(json);
                if (parsed == null)
                {
                    error = "Decision JSON parse returned null.";
                    return false;
                }

                chosenAction = (parsed.chosen_action ?? string.Empty).Trim();
                justification = (parsed.justification ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                error = "Decision JSON parse failed: " + ex.Message;
                return false;
            }

            if (string.IsNullOrWhiteSpace(chosenAction))
            {
                error = "chosen_action is empty.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(justification))
                justification = "(no justification provided)";

            return true;
        }

        private static string ExtractJsonObject(string raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0)
                    text = text.Substring(firstNewline + 1).Trim();

                if (text.EndsWith("```", StringComparison.Ordinal))
                    text = text.Substring(0, text.Length - 3).Trim();
            }

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return string.Empty;

            return text.Substring(start, end - start + 1).Trim();
        }

        private string ResolveActionLabel(string candidate, List<string> availableActions)
        {
            var raw = (candidate ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw) || availableActions == null || availableActions.Count == 0)
                return null;

            for (int i = 0; i < availableActions.Count; i++)
            {
                if (string.Equals(availableActions[i], raw, StringComparison.OrdinalIgnoreCase))
                    return availableActions[i];
            }

            return null;
        }

        private string ResolveFallbackAction(List<string> availableActions)
        {
            var fallback = ResolveActionLabel(FallbackActionLabel, availableActions);
            if (!string.IsNullOrWhiteSpace(fallback))
                return fallback;

            return availableActions != null && availableActions.Count > 0 ? availableActions[0] : "";
        }


        private void SetStatus(string status)
        {
            LastStatus = status ?? string.Empty;
            Debug.Log("[AaltoDirectedRoomPerformer] " + LastStatus);
            EmitPerformerEvent("performer.status", "{\"status\":\"" + AaltoLaunchSessionLogger.EscapeJson(LastStatus) + "\"}");
        }

        [ContextMenu("Directed Performer/Clear Runtime History")]
        public void ClearRuntimeHistory()
        {
            _recentInteractions.Clear();
            DialogTurns?.Clear();
            LastStatus = "Runtime history cleared.";
        }

        [ContextMenu("Directed Performer/Refresh Prompt Inspection")]
        public void RefreshPromptInspectionContextMenu()
        {
            RefreshPromptInspection();
            LastStatus = "Prompt inspection refreshed.";
        }

        private static void EmitPerformerEvent(string eventType, string payloadJson)
        {
            AaltoLaunchSessionLogger.EmitEvent("AaltoDirectedRoomPerformer", eventType, payloadJson);
        }

        private void EmitTurnRecord(string latestActorInput, List<string> availableActions, bool executionSucceeded, string executionResult)
        {
            var payload =
                "{"
                + "\"run_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(AaltoLaunchSessionLogger.CurrentRunId) + "\"," +
                "\"turn_type\":\"single_speaker\"," +
                "\"actor_input\":\"" + AaltoLaunchSessionLogger.EscapeJson(latestActorInput ?? string.Empty) + "\"," +
                "\"interpreted_intent\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedActionText ?? string.Empty) + "\"," +
                "\"character_state\":{" +
                    "\"summary\":\"" + AaltoLaunchSessionLogger.EscapeJson(CurrentCharacterSummary ?? string.Empty) + "\"," +
                    "\"objective\":\"" + AaltoLaunchSessionLogger.EscapeJson(CurrentObjective ?? string.Empty) + "\"," +
                    "\"stance\":\"" + AaltoLaunchSessionLogger.EscapeJson(CurrentStance ?? string.Empty) + "\"}," +
                "\"available_actions\":" + BuildJsonStringArray(availableActions) + "," +
                "\"selected_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedActionText ?? string.Empty) + "\"," +
                "\"decision_note\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionJustificationText ?? string.Empty) + "\"," +
                "\"execution\":{" +
                    "\"succeeded\":" + (executionSucceeded ? "true" : "false") + "," +
                    "\"result\":\"" + AaltoLaunchSessionLogger.EscapeJson(executionResult ?? string.Empty) + "\"}}";

            EmitPerformerEvent("performer.turn_record", payload);
        }

        private static string BuildJsonStringArray(List<string> values)
        {
            if (values == null || values.Count == 0)
                return "[]";

            var sb = new StringBuilder();
            sb.Append("[");
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0)
                    sb.Append(",");

                sb.Append("\"").Append(AaltoLaunchSessionLogger.EscapeJson(values[i] ?? string.Empty)).Append("\"");
            }
            sb.Append("]");
            return sb.ToString();
        }
    }
}
