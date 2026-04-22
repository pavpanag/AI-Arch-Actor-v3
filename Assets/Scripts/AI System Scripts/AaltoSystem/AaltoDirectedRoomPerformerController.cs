using System;
using System.Collections.Generic;
using System.IO;
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

        [Header("Archive Export")]
        [Tooltip("Relative folder (from project root) where dialogue archives are written.")]
        public string DialogExportFolderRelativePath = "Recordings/AaltoExports/dialog-archives";

        [Tooltip("If true, exports JSON and CSV before clearing state on Start New Take.")]
        public bool AutoExportOnStartNewTake = true;

        [TextArea(1, 3)]
        [InspectorName("Last Export Status")]
        public string LastExportStatus;

        [TextArea(1, 3)]
        [InspectorName("Last Export Directory")]
        public string LastExportDirectory;

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
        private int _nextTurnIndex = 1;

        [Serializable]
        private sealed class DecisionResponse
        {
            public string chosen_action;
            public string justification;
        }

        private sealed class ActionDispatchTimeline
        {
            public string resolvedMemoryTrigger = string.Empty;
            public string registrySend = string.Empty;
            public string registryError = string.Empty;
            public string mode = string.Empty;
            public string neutralReturnTrigger = string.Empty;
            public string neutralReturnSend = string.Empty;
            public string neutralReturnError = string.Empty;
            public string actionDispatchUtc = string.Empty;
            public string actionDispatchCompletedUtc = string.Empty;
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
            SubmitTurnInternal(text, "inspector");
        }

        public void SubmitTurn(string latestActorInput)
        {
            var text = (latestActorInput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            SubmitTurnInternal(text, "api");
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
            var turnIndex = ReserveTurnIndex();
            var turnId = BuildTurnId(turnIndex);
            var inputReceivedUtc = DateTime.UtcNow;
            EmitPerformerEvent("performer.external_speech_received",
                "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                "\"turn_index\":" + turnIndex + "," +
                "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                "\"input_received_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputReceivedUtc.ToString("o")) + "\"," +
                "\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(transcript) + "\"}");

            if (!AcceptExternalSpeechInput)
            {
                EmitPerformerEvent("performer.external_speech_ignored",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(transcript) + "\",\"reason\":\"spoken_input_disabled\"}");
                return;
            }

            if (AutoSubmitExternalSpeech)
            {
                SubmitTurnInternal(transcript, "external_speech", turnIndex, turnId, inputReceivedUtc);
            }
            else
            {
                InspectorActorInput = transcript;
            }
        }

        private void SubmitTurnInternal(string text, string source)
        {
            var turnIndex = ReserveTurnIndex();
            var turnId = BuildTurnId(turnIndex);
            SubmitTurnInternal(text, source, turnIndex, turnId, DateTime.UtcNow);
        }

        private void SubmitTurnInternal(string text, string source, int turnIndex, string turnId, DateTime inputReceivedUtc)
        {
            EmitPerformerEvent("performer.turn_submitted",
                "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                "\"turn_index\":" + turnIndex + "," +
                "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                "\"source\":\"" + AaltoLaunchSessionLogger.EscapeJson(source ?? string.Empty) + "\"," +
                "\"input_received_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputReceivedUtc.ToString("o")) + "\"," +
                "\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(text ?? string.Empty) + "\"}");

            _ = HandleTurnAsync(text, source, turnIndex, turnId, inputReceivedUtc);
        }

        public void SubmitDirectorGuidanceInput()
        {
            var guidance = (DirectorGuidanceInput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(guidance))
            {
                return;
            }

            var before = DirectorGuidance ?? string.Empty;
            DirectorGuidanceInputs.Add(guidance);
            DirectorGuidanceInput = string.Empty;
            RebuildDirectorGuidanceFromInputs();

            EmitPerformerEvent(
                "performer.director_guidance_added",
                "{\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                "\"added\":\"" + AaltoLaunchSessionLogger.EscapeJson(guidance) + "\"," +
                "\"previous\":\"" + AaltoLaunchSessionLogger.EscapeJson(before) + "\"," +
                "\"current\":\"" + AaltoLaunchSessionLogger.EscapeJson(DirectorGuidance ?? string.Empty) + "\"," +
                "\"count\":" + (DirectorGuidanceInputs != null ? DirectorGuidanceInputs.Count : 0) + "}");
        }

        public void RemoveDirectorGuidanceAt(int index)
        {
            if (index < 0 || index >= DirectorGuidanceInputs.Count)
            {
                return;
            }

            var before = DirectorGuidance ?? string.Empty;
            var removed = (DirectorGuidanceInputs[index] ?? string.Empty).Trim();
            DirectorGuidanceInputs.RemoveAt(index);
            RebuildDirectorGuidanceFromInputs();

            EmitPerformerEvent(
                "performer.director_guidance_removed",
                "{\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                "\"removed\":\"" + AaltoLaunchSessionLogger.EscapeJson(removed) + "\"," +
                "\"previous\":\"" + AaltoLaunchSessionLogger.EscapeJson(before) + "\"," +
                "\"current\":\"" + AaltoLaunchSessionLogger.EscapeJson(DirectorGuidance ?? string.Empty) + "\"," +
                "\"count\":" + (DirectorGuidanceInputs != null ? DirectorGuidanceInputs.Count : 0) + "}");
        }

        public void ClearDirectorGuidanceInputs()
        {
            var before = DirectorGuidance ?? string.Empty;
            DirectorGuidanceInputs.Clear();
            RebuildDirectorGuidanceFromInputs();

            EmitPerformerEvent(
                "performer.director_guidance_cleared",
                "{\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                "\"previous\":\"" + AaltoLaunchSessionLogger.EscapeJson(before) + "\"," +
                "\"current\":\"" + AaltoLaunchSessionLogger.EscapeJson(DirectorGuidance ?? string.Empty) + "\"," +
                "\"count\":0}");
        }

        private async Task HandleTurnAsync(string latestActorInput, string inputSource, int turnIndex, string turnId, DateTime inputReceivedUtc)
        {
            if (_cts == null || _cts.IsCancellationRequested)
                return;

            var decisionStartedUtc = DateTime.UtcNow;
            var modelResponseUtc = default(DateTime);
            var rawModelAction = string.Empty;
            var parseErrorText = string.Empty;
            var fallbackUsed = false;
            var dispatchTimeline = new ActionDispatchTimeline();

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
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"input_source\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputSource ?? string.Empty) + "\"," +
                    "\"input_received_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputReceivedUtc.ToString("o")) + "\"," +
                    "\"decision_started_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(decisionStartedUtc.ToString("o")) + "\"," +
                    "\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"," +
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
                modelResponseUtc = DateTime.UtcNow;
                EmitPerformerEvent("performer.model_response",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"model_response_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(modelResponseUtc.ToString("o")) + "\"," +
                    "\"trace_request_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(OpenAI != null ? OpenAI.LastTraceRequestId ?? string.Empty : string.Empty) + "\"," +
                    "\"raw_response\":\"" + AaltoLaunchSessionLogger.EscapeJson(response ?? string.Empty) + "\"}");

                if (!TryParseDecision(response, out var action, out var justification, out var parseError))
                {
                    SetStatus("Parse failed: " + parseError + " | Applying fallback action.");
                    parseErrorText = parseError ?? string.Empty;
                    fallbackUsed = true;
                    action = ResolveFallbackAction(availableActions);
                    justification = parseError;
                }

                rawModelAction = action ?? string.Empty;

                var resolvedAction = ResolveActionLabel(action, availableActions);
                if (EnforceRegistryActionLabels && string.IsNullOrWhiteSpace(resolvedAction))
                {
                    resolvedAction = ResolveFallbackAction(availableActions);
                    justification = (justification ?? string.Empty) + " [Action label was outside available set.]";
                    fallbackUsed = true;
                    if (string.IsNullOrWhiteSpace(parseErrorText))
                        parseErrorText = "Action label was outside available set.";
                }
                else if (string.IsNullOrWhiteSpace(resolvedAction))
                {
                    resolvedAction = action?.Trim();
                }

                SelectedActionText = resolvedAction;
                ActionJustificationText = (justification ?? string.Empty).Trim();
                EmitPerformerEvent("performer.decision_resolved",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"raw_model_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(rawModelAction) + "\"," +
                    "\"fallback_used\":" + (fallbackUsed ? "true" : "false") + "," +
                    "\"parse_error\":\"" + AaltoLaunchSessionLogger.EscapeJson(parseErrorText) + "\"," +
                    "\"chosen_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedActionText ?? string.Empty) + "\"," +
                    "\"justification\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionJustificationText ?? string.Empty) + "\"}");

                var historyLine =
                    "actor: " + latestActorInput.Trim() +
                    " | action: " + SelectedActionText +
                    " | why: " + ActionJustificationText;
                EnqueueHistory(historyLine);
                AddDialogTurn(latestActorInput, SelectedActionText, ActionJustificationText);

                dispatchTimeline = await ApplyActionDispatchAsync(SelectedActionText, turnId, turnIndex);

                EmitTurnRecord(
                    turnId,
                    turnIndex,
                    inputSource,
                    inputReceivedUtc,
                    decisionStartedUtc,
                    modelResponseUtc,
                    rawModelAction,
                    fallbackUsed,
                    parseErrorText,
                    dispatchTimeline,
                    latestActorInput,
                    availableActions,
                    string.IsNullOrWhiteSpace(ExecutionModeStatus) || ExecutionModeStatus.IndexOf("failed", StringComparison.OrdinalIgnoreCase) < 0,
                    ExecutionModeStatus);

                RefreshPromptInspection();
            }
            catch (Exception ex)
            {
                EmitPerformerEvent("performer.error",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"message\":\"" + AaltoLaunchSessionLogger.EscapeJson(ex.Message) + "\"}");
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

        private async Task<ActionDispatchTimeline> ApplyActionDispatchAsync(string selectedAction, string turnId, int turnIndex)
        {
            var timeline = new ActionDispatchTimeline
            {
                actionDispatchUtc = DateTime.UtcNow.ToString("o"),
                mode = SelectedActionStateMode.ToString()
            };

            if (!ApplyActionThroughRegistry || ActionMemoryRegistry == null || string.IsNullOrWhiteSpace(selectedAction))
            {
                EmitPerformerEvent("performer.action_dispatch",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"chosen_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(selectedAction ?? string.Empty) + "\",\"applied_through_registry\":false}");
                ExecutionModeStatus = "Registry dispatch disabled or unavailable. Action was resolved only.";
                SetStatus("Action resolved: " + selectedAction);
                timeline.registrySend = "not_applied";
                timeline.actionDispatchCompletedUtc = DateTime.UtcNow.ToString("o");
                return timeline;
            }

            if (!ActionMemoryRegistry.TrySendMemoryTriggerForActionLabel(selectedAction, out var sendError))
            {
                timeline.resolvedMemoryTrigger = ActionMemoryRegistry.LastResolvedMemoryTrigger ?? string.Empty;
                timeline.registrySend = "failed";
                timeline.registryError = sendError ?? string.Empty;
                timeline.actionDispatchCompletedUtc = DateTime.UtcNow.ToString("o");
                EmitPerformerEvent("performer.action_dispatch",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"chosen_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(selectedAction) + "\"," +
                    "\"resolved_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionMemoryRegistry.LastResolvedMemoryTrigger ?? string.Empty) + "\"," +
                    "\"registry_send\":\"failed\",\"error\":\"" + AaltoLaunchSessionLogger.EscapeJson(sendError ?? string.Empty) + "\"}");
                ExecutionModeStatus = "Dispatch failed. Response state was not applied.";
                SetStatus("Action resolved but registry send failed: " + sendError);
                return timeline;
            }

            timeline.resolvedMemoryTrigger = ActionMemoryRegistry.LastResolvedMemoryTrigger ?? string.Empty;
            timeline.registrySend = "ok";
            timeline.actionDispatchCompletedUtc = DateTime.UtcNow.ToString("o");
            EmitPerformerEvent("performer.action_dispatch",
                "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                "\"turn_index\":" + turnIndex + "," +
                "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                "\"chosen_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(selectedAction) + "\"," +
                "\"resolved_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionMemoryRegistry.LastResolvedMemoryTrigger ?? string.Empty) + "\"," +
                "\"registry_send\":\"ok\",\"mode\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedActionStateMode.ToString()) + "\"}");

            if (SelectedActionStateMode == ActionStateMode.HoldResponseState)
            {
                CancelPendingNeutralReturn();
                ExecutionModeStatus = "Hold mode active. Response state remains until another action is applied.";
                SetStatus("Action applied: " + selectedAction);
                return timeline;
            }

            var neutralTrigger = (NeutralMemoryTrigger ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(neutralTrigger))
            {
                ExecutionModeStatus = "Pulse mode active, but neutral memory trigger is empty.";
                SetStatus("Action applied, but neutral return is not configured.");
                timeline.neutralReturnSend = "not_configured";
                return timeline;
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
                timeline.neutralReturnSend = "cancelled";
                return timeline;
            }

            if (_cts == null || _cts.IsCancellationRequested || returnToken.IsCancellationRequested)
            {
                timeline.neutralReturnSend = "cancelled";
                return timeline;
            }

            timeline.neutralReturnTrigger = neutralTrigger;
            if (ActionMemoryRegistry.TrySendMemoryTrigger(neutralTrigger, out var neutralError))
            {
                timeline.neutralReturnSend = "ok";
                EmitPerformerEvent("performer.neutral_return",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"neutral_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(neutralTrigger) + "\",\"send\":\"ok\"}");
                ExecutionModeStatus = "Pulse mode completed. Returned to neutral trigger '" + neutralTrigger + "'.";
                SetStatus("Returned to neutral: " + neutralTrigger);
            }
            else
            {
                timeline.neutralReturnSend = "failed";
                timeline.neutralReturnError = neutralError ?? string.Empty;
                EmitPerformerEvent("performer.neutral_return",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"neutral_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(neutralTrigger) + "\",\"send\":\"failed\",\"error\":\"" + AaltoLaunchSessionLogger.EscapeJson(neutralError ?? string.Empty) + "\"}");
                ExecutionModeStatus = "Pulse mode failed to return to neutral. " + neutralError;
                SetStatus("Action applied but neutral return failed: " + neutralError);
            }

            return timeline;
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
            TryExportArchiveToProjectFolder("start_new_take");
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

        [ContextMenu("Directed Performer/Export Dialog Archive Now")]
        public void ExportDialogArchiveNow()
        {
            TryExportArchiveToProjectFolder("manual");
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

        private void TryExportArchiveToProjectFolder(string reason)
        {
            if (!AutoExportOnStartNewTake && string.Equals(reason, "start_new_take", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var exportDirectory = ResolveDialogExportDirectory();
                Directory.CreateDirectory(exportDirectory);

                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
                var takeNumber = Mathf.Max(1, CurrentTakeNumber);
                var runId = string.IsNullOrWhiteSpace(AaltoLaunchSessionLogger.CurrentRunId)
                    ? "RUN_000"
                    : AaltoLaunchSessionLogger.CurrentRunId;
                var baseName =
                    "single-dialog__" + runId +
                    "__take_" + takeNumber.ToString("000") +
                    "__" + timestamp;

                var jsonPath = Path.Combine(exportDirectory, baseName + ".json");
                var csvPath = Path.Combine(exportDirectory, baseName + ".csv");

                File.WriteAllText(jsonPath, BuildDialogArchiveExportJson(), Encoding.UTF8);
                File.WriteAllText(csvPath, BuildDialogArchiveExportCsv(), Encoding.UTF8);

                LastExportDirectory = exportDirectory;
                LastExportStatus =
                    "Exported dialogue archive to " + exportDirectory +
                    " (json=" + Path.GetFileName(jsonPath) + ", csv=" + Path.GetFileName(csvPath) + ")";

                EmitPerformerEvent("performer.archive_exported",
                    "{\"reason\":\"" + AaltoLaunchSessionLogger.EscapeJson(reason ?? string.Empty) + "\"," +
                    "\"directory\":\"" + AaltoLaunchSessionLogger.EscapeJson(exportDirectory) + "\"," +
                    "\"json_file\":\"" + AaltoLaunchSessionLogger.EscapeJson(Path.GetFileName(jsonPath)) + "\"," +
                    "\"csv_file\":\"" + AaltoLaunchSessionLogger.EscapeJson(Path.GetFileName(csvPath)) + "\"}");
            }
            catch (Exception ex)
            {
                LastExportStatus = "Archive export failed: " + ex.Message;
                SetStatus(LastExportStatus);
            }
        }

        private string ResolveDialogExportDirectory()
        {
            var relative = (DialogExportFolderRelativePath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(relative))
                return AaltoLaunchSessionLogger.ResolveDialogArchivesDirectory();

            if (Path.IsPathRooted(relative))
                return Path.GetFullPath(relative);

            relative = relative.Replace('\\', '/').TrimStart('/');

#if UNITY_EDITOR
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrWhiteSpace(projectRoot))
                return AaltoLaunchSessionLogger.ResolveDialogArchivesDirectory();

            return Path.GetFullPath(Path.Combine(projectRoot, relative));
#else
            return Path.GetFullPath(Path.Combine(Application.persistentDataPath, relative));
#endif
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

        private void EmitTurnRecord(
            string turnId,
            int turnIndex,
            string inputSource,
            DateTime inputReceivedUtc,
            DateTime decisionStartedUtc,
            DateTime modelResponseUtc,
            string rawModelAction,
            bool fallbackUsed,
            string parseError,
            ActionDispatchTimeline dispatchTimeline,
            string latestActorInput,
            List<string> availableActions,
            bool executionSucceeded,
            string executionResult)
        {
            dispatchTimeline = dispatchTimeline ?? new ActionDispatchTimeline();
            var actionDispatchUtc = ParseUtcOrDefault(dispatchTimeline.actionDispatchUtc);
            var latencyInputToModelMs = modelResponseUtc == default ? -1 : Mathf.RoundToInt((float)(modelResponseUtc - inputReceivedUtc).TotalMilliseconds);
            var latencyInputToActionMs = actionDispatchUtc == default ? -1 : Mathf.RoundToInt((float)(actionDispatchUtc - inputReceivedUtc).TotalMilliseconds);
            var payload =
                "{"
                + "\"run_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(AaltoLaunchSessionLogger.CurrentRunId) + "\"," +
                "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                "\"turn_index\":" + turnIndex + "," +
                "\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId ?? string.Empty) + "\"," +
                "\"turn_type\":\"single_speaker\"," +
                "\"input_source\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputSource ?? string.Empty) + "\"," +
                "\"input_received_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputReceivedUtc.ToString("o")) + "\"," +
                "\"decision_started_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(decisionStartedUtc.ToString("o")) + "\"," +
                "\"model_response_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(modelResponseUtc == default ? string.Empty : modelResponseUtc.ToString("o")) + "\"," +
                "\"action_dispatch_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.actionDispatchUtc ?? string.Empty) + "\"," +
                "\"action_dispatch_completed_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.actionDispatchCompletedUtc ?? string.Empty) + "\"," +
                "\"latency_input_to_model_ms\":" + latencyInputToModelMs + "," +
                "\"latency_input_to_action_ms\":" + latencyInputToActionMs + "," +
                "\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"," +
                "\"trace_request_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(OpenAI != null ? OpenAI.LastTraceRequestId ?? string.Empty : string.Empty) + "\"," +
                "\"actor_input\":\"" + AaltoLaunchSessionLogger.EscapeJson(latestActorInput ?? string.Empty) + "\"," +
                "\"interpreted_intent\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedActionText ?? string.Empty) + "\"," +
                "\"raw_model_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(rawModelAction ?? string.Empty) + "\"," +
                "\"fallback_used\":" + (fallbackUsed ? "true" : "false") + "," +
                "\"parse_error\":\"" + AaltoLaunchSessionLogger.EscapeJson(parseError ?? string.Empty) + "\"," +
                "\"character_state\":{" +
                    "\"summary\":\"" + AaltoLaunchSessionLogger.EscapeJson(CurrentCharacterSummary ?? string.Empty) + "\"," +
                    "\"objective\":\"" + AaltoLaunchSessionLogger.EscapeJson(CurrentObjective ?? string.Empty) + "\"," +
                    "\"stance\":\"" + AaltoLaunchSessionLogger.EscapeJson(CurrentStance ?? string.Empty) + "\"}," +
                "\"available_actions\":" + BuildJsonStringArray(availableActions) + "," +
                "\"available_action_count\":" + (availableActions != null ? availableActions.Count : 0) + "," +
                "\"selected_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedActionText ?? string.Empty) + "\"," +
                "\"selected_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.resolvedMemoryTrigger ?? string.Empty) + "\"," +
                "\"registry_send\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.registrySend ?? string.Empty) + "\"," +
                "\"registry_error\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.registryError ?? string.Empty) + "\"," +
                "\"neutral_return_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.neutralReturnTrigger ?? string.Empty) + "\"," +
                "\"neutral_return_send\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.neutralReturnSend ?? string.Empty) + "\"," +
                "\"neutral_return_error\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.neutralReturnError ?? string.Empty) + "\"," +
                "\"director_guidance_snapshot\":\"" + AaltoLaunchSessionLogger.EscapeJson(DirectorGuidance ?? string.Empty) + "\"," +
                "\"decision_note\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionJustificationText ?? string.Empty) + "\"," +
                "\"execution_succeeded\":" + (executionSucceeded ? "true" : "false") + "," +
                "\"execution_result\":\"" + AaltoLaunchSessionLogger.EscapeJson(executionResult ?? string.Empty) + "\"," +
                "\"execution\":{" +
                    "\"succeeded\":" + (executionSucceeded ? "true" : "false") + "," +
                    "\"result\":\"" + AaltoLaunchSessionLogger.EscapeJson(executionResult ?? string.Empty) + "\"}}";

            EmitPerformerEvent("performer.turn_record", payload);
        }

        private int ReserveTurnIndex()
        {
            return Mathf.Max(1, _nextTurnIndex++);
        }

        private string BuildTurnId(int turnIndex)
        {
            var runId = string.IsNullOrWhiteSpace(AaltoLaunchSessionLogger.CurrentRunId)
                ? "RUN_000"
                : AaltoLaunchSessionLogger.CurrentRunId;

            return runId + "__single__take_" + Mathf.Max(1, CurrentTakeNumber).ToString("000") +
                   "__turn_" + Mathf.Max(1, turnIndex).ToString("0000");
        }

        private static DateTime ParseUtcOrDefault(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return default;

            return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed.ToUniversalTime()
                : default;
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
