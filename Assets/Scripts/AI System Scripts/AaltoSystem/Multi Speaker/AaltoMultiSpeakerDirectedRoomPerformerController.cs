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
    /// Multi-speaker performer controller.
    /// Listens to dialogue events from up to 4 speakers and decides when to react.
    /// </summary>
    public sealed class AaltoMultiSpeakerDirectedRoomPerformerController : MonoBehaviour
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

        [Serializable]
        private sealed class DialogueLine
        {
            public string lineId;
            public string speakerId;
            public string text;
            public string timestamp;
            public string inputSource;
            public string receivedUtc;
        }

        [Serializable]
        private sealed class ReactionDecision
        {
            public bool react_now;
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
            public string latestDialogueLine;

            [TextArea(2, 8)]
            public string pendingDialogueBatch;

            [TextArea(2, 8)]
            public string priorDialogueContext;

            [TextArea(1, 4)]
            public string decisionContextSummary;

            public int pendingDialogueLineCount;

            public string promptMemoryScopeUsed;

            public int promptMemoryLineCount;

            [TextArea(1, 3)]
            public string selectedResponse;

            [TextArea(2, 6)]
            public string justification;

            public bool reactNow;
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

        public enum DialogueMemoryScope
        {
            [InspectorName("Recent Window")]
            RecentWindow = 0,
            [InspectorName("Full Accumulated")]
            FullAccumulated = 1
        }

        [Header("Scene Refs")]
        [InspectorName("OpenAI Client (Agent Produced)")]
        public OpenAIClient OpenAI;
        [InspectorName("Action Memory Registry (Agent Produced)")]
        public AaltoActionMemoryRegistry ActionMemoryRegistry;
        [InspectorName("Objective/Stance Bootstrapper (Primary Source)")]
        public AaltoObjectiveStanceBootstrapper ObjectiveStanceBootstrapper;

        [Header("Participants (Tick Enabled Speakers)")]
        public bool Speaker1Enabled = true;
        public string Speaker1Id = "actor_1";
        public bool Speaker2Enabled = true;
        public string Speaker2Id = "actor_2";
        public bool Speaker3Enabled = false;
        public string Speaker3Id = "actor_3";
        public bool Speaker4Enabled = false;
        public string Speaker4Id = "actor_4";

        [Header("Spoken Input")]
        [Tooltip("If false, incoming spoken events from OSC are ignored.")]
        public bool AcceptExternalSpeechInput = true;

        [TextArea(1, 3)]
        public string LastIncomingSpeaker;
        [TextArea(1, 4)]
        public string LastIncomingText;
        [TextArea(1, 3)]
        public string ExternalSpeechStatus;

        [Header("Dialogue Memory")]
        [TextArea(8, 22)]
        [InspectorName("Dialogue Built So Far")]
        public string DialogueTranscriptText;
        [TextArea(3, 10)]
        [InspectorName("New Dialogue Since Last Decision")]
        public string PendingDialogueSinceLastDecision;
        [InspectorName("Total Dialogue Lines")]
        public int TotalDialogueLines;

        [Header("Simulation (Per Speaker)")]
        [TextArea(1, 3)]
        public string SimulatedSpeaker1Text;
        [TextArea(1, 3)]
        public string SimulatedSpeaker2Text;
        [TextArea(1, 3)]
        public string SimulatedSpeaker3Text;
        [TextArea(1, 3)]
        public string SimulatedSpeaker4Text;
        [TextArea(1, 3)]
        public string SimulationStatus;

        [Header("Room Response")]
        public bool LastDecisionReactNow;
        [TextArea(1, 3)]
        public string SelectedActionText;
        [TextArea(2, 6)]
        public string ActionJustificationText;

        [Header("Dialog Log")]
        [Tooltip("Maximum number of decision turns kept in the inspector dialog log.")]
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
        public bool PullContextFromBootstrapper = true;

        [TextArea(2, 8)]
        public string CurrentCharacterSummary;
        [TextArea(1, 4)]
        public string CurrentObjective = "Protect continuity of the room's inner life.";
        [TextArea(1, 4)]
        public string CurrentStance = "firm";
        [TextArea(2, 8)]
        public string DirectorGuidance;

        [Header("Prompt")]
        [TextArea(18, 40)]
        public string GeneralInstructions =
            "You are performing the inner life of a room in an improvised scene where multiple human actors speak to each other and to the room.\n\n" +
            "You monitor the ongoing dialogue and decide whether the room should react now. The room should not react to every line.\n\n" +
            "When reacting, choose exactly one action label from the provided available list.\n\n" +
            "Decision policy:\n" +
            "- React only when it is dramaturgically meaningful, useful, and legible\n" +
            "- If no reaction is needed now, set react_now=false and leave chosen_action empty\n" +
            "- If reacting, set react_now=true and choose one valid label from available_action_labels\n" +
            "- Use objective, stance, dialogue history, and director guidance\n\n" +
            "Return JSON only with exactly this schema:\n" +
            "{\n" +
            "  \"react_now\": true,\n" +
            "  \"chosen_action\": \"<one action label from available_action_labels or empty when react_now=false>\",\n" +
            "  \"justification\": \"<1-3 sentences>\"\n" +
            "}";

        [Range(4, 40)]
        public int RecentDialogueLimit = 16;

        [Tooltip("Controls which dialogue memory set is included in the prompt context.")]
        public DialogueMemoryScope PromptMemoryScope = DialogueMemoryScope.FullAccumulated;

        [Header("Model")]
        [InspectorName("Model (OpenAI Dropdown)")]
        public OpenAIModelPreset Model = OpenAIModelPreset.Gpt4oMini;

        [Header("Execution")]
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

        [Tooltip("If true, prompt action labels come only from the manually pulled snapshot.")]
        public bool UsePulledActionLabelsSnapshot = true;
        public List<string> PulledActionLabelsSnapshot = new List<string>();
        [TextArea(1, 3)]
        public string ActionPullStatus;

        [Tooltip("If false, unknown model labels are rejected and fallback is used.")]
        public bool EnforceRegistryActionLabels = true;
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

        private readonly Queue<DialogueLine> _recentDialogue = new Queue<DialogueLine>();
        private readonly Queue<DialogueLine> _pendingDialogueSinceLastDecision = new Queue<DialogueLine>();
        private readonly List<DialogueLine> _allDialogueHistory = new List<DialogueLine>();
        private CancellationTokenSource _cts;
        private CancellationTokenSource _neutralReturnCts;
        private int _nextDialogueLineIndex = 1;
        private int _nextDecisionTurnIndex = 1;
        private string _lastDirectorGuidanceLogged = string.Empty;

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
            _lastDirectorGuidanceLogged = DirectorGuidance ?? string.Empty;
        }

        private void OnValidate()
        {
            SyncContextFromBootstrapper();
            TryLogDirectorGuidanceChanged();
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

        private void TryLogDirectorGuidanceChanged()
        {
            if (!Application.isPlaying)
                return;

            var current = DirectorGuidance ?? string.Empty;
            var before = _lastDirectorGuidanceLogged ?? string.Empty;
            if (string.Equals(before, current, StringComparison.Ordinal))
                return;

            _lastDirectorGuidanceLogged = current;
            EmitPerformerEvent(
                "performer.director_guidance_changed",
                "{\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                "\"previous\":\"" + AaltoLaunchSessionLogger.EscapeJson(before) + "\"," +
                "\"current\":\"" + AaltoLaunchSessionLogger.EscapeJson(current) + "\"}");
        }

        public void ReceiveExternalSpeech(string speakerId, string text, string inputSource = "external_speech")
        {
            var id = NormalizeSpeakerId(speakerId);
            var transcript = (text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(transcript))
            {
                ExternalSpeechStatus = "Ignored empty transcript.";
                EmitPerformerEvent("performer.dialogue_rejected",
                    "{\"input_source\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputSource ?? string.Empty) + "\"," +
                    "\"speaker_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(id) + "\",\"reason\":\"empty_transcript\"}");
                return;
            }

            LastIncomingSpeaker = id;
            LastIncomingText = transcript;

            if (!AcceptExternalSpeechInput)
            {
                ExternalSpeechStatus = "Ignored incoming speech: spoken input disabled.";
                EmitPerformerEvent("performer.dialogue_rejected",
                    "{\"input_source\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputSource ?? string.Empty) + "\"," +
                    "\"speaker_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(id) + "\",\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(transcript) + "\",\"reason\":\"spoken_input_disabled\"}");
                return;
            }

            if (!IsEnabledSpeaker(id))
            {
                ExternalSpeechStatus = "Ignored incoming speech: speaker is not enabled in participants.";
                EmitPerformerEvent("performer.dialogue_rejected",
                    "{\"input_source\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputSource ?? string.Empty) + "\"," +
                    "\"speaker_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(id) + "\",\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(transcript) + "\",\"reason\":\"speaker_not_enabled\"}");
                return;
            }

            var line = EnqueueDialogue(id, transcript, inputSource);
            ExternalSpeechStatus = "Incoming speech accepted. Press Evaluate Dialogue And Produce Response Now to run a decision pass.";
            Debug.Log("[AaltoMultiSpeakerPerformer] Received speech from " + id + ": " + transcript);
            EmitPerformerEvent("performer.dialogue_received",
                "{\"line_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(line != null ? line.lineId : string.Empty) + "\"," +
                "\"input_source\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputSource ?? string.Empty) + "\"," +
                "\"received_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(line != null ? line.receivedUtc ?? string.Empty : string.Empty) + "\"," +
                "\"speaker_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(id) + "\",\"text\":\"" + AaltoLaunchSessionLogger.EscapeJson(transcript) + "\",\"total_dialogue_lines\":" + TotalDialogueLines + "}");
        }

        public void SubmitSimulatedSpeaker1Input()
        {
            SubmitSimulatedSpeakerInput(1, Speaker1Id, Speaker1Enabled, ref SimulatedSpeaker1Text);
        }

        public void SubmitSimulatedSpeaker2Input()
        {
            SubmitSimulatedSpeakerInput(2, Speaker2Id, Speaker2Enabled, ref SimulatedSpeaker2Text);
        }

        public void SubmitSimulatedSpeaker3Input()
        {
            SubmitSimulatedSpeakerInput(3, Speaker3Id, Speaker3Enabled, ref SimulatedSpeaker3Text);
        }

        public void SubmitSimulatedSpeaker4Input()
        {
            SubmitSimulatedSpeakerInput(4, Speaker4Id, Speaker4Enabled, ref SimulatedSpeaker4Text);
        }

        public void SubmitAllSimulatedInputs()
        {
            SubmitSimulatedSpeaker1Input();
            SubmitSimulatedSpeaker2Input();
            SubmitSimulatedSpeaker3Input();
            SubmitSimulatedSpeaker4Input();
        }

        [ContextMenu("Multi Speaker Performer/Evaluate Dialogue Now")]
        public void EvaluateDialogueNowContextMenu()
        {
            _ = EvaluateDialogueNowAsync();
        }

        private void SubmitSimulatedSpeakerInput(int slot, string speakerId, bool enabled, ref string sourceText)
        {
            var text = (sourceText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                SimulationStatus = "Speaker " + slot + " has no simulated text.";
                return;
            }

            if (!enabled)
            {
                SimulationStatus = "Speaker " + slot + " is disabled in participants.";
                return;
            }

            sourceText = string.Empty;
            SimulationStatus = "Submitted simulated text for speaker " + slot + ".";
            ReceiveExternalSpeech(speakerId, text, "simulated_speaker_" + slot);
        }

        private async Task EvaluateDialogueNowAsync()
        {
            if (_cts == null || _cts.IsCancellationRequested)
                return;

            try
            {
                SyncContextFromBootstrapper();

                if (OpenAI == null)
                {
                    SetStatus("OpenAI client is not assigned.");
                    return;
                }

                var availableActions = GetAvailableActionLabels();
                if (availableActions.Count == 0)
                {
                    SetStatus(UsePulledActionLabelsSnapshot
                        ? "No pulled action labels available. Pull snapshot first."
                        : "No action labels configured in ActionMemoryRegistry.");
                    return;
                }

                if (!TryDequeuePendingDialogueForDecision(out var pendingBatch))
                {
                    SetStatus("No new dialogue since the last decision.");
                    return;
                }

                var latest = pendingBatch[pendingBatch.Count - 1];
                var priorContextLines = BuildPriorContextMemoryLines(pendingBatch);
                var turnIndex = ReserveDecisionTurnIndex();
                var turnId = BuildDecisionTurnId(turnIndex);
                var inputReceivedUtc = GetEarliestReceivedUtc(pendingBatch);
                var decisionStartedUtc = DateTime.UtcNow;
                var modelResponseUtc = default(DateTime);
                var rawModelAction = string.Empty;
                var parseErrorText = string.Empty;
                var fallbackUsed = false;
                var dispatchTimeline = new ActionDispatchTimeline();

                var runtimePrompt = BuildRuntimePrompt(latest, pendingBatch, priorContextLines, availableActions);
                LastRuntimePrompt = runtimePrompt;
                EmitPerformerEvent(
                    "performer.decision_requested",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"input_received_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputReceivedUtc.ToString("o")) + "\"," +
                    "\"decision_started_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(decisionStartedUtc.ToString("o")) + "\"," +
                    "\"pending_dialogue_line_ids\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildDialogueLineIdCsv(pendingBatch)) + "\"," +
                    "\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"," +
                    "\"pending_dialogue\":\"" + AaltoLaunchSessionLogger.EscapeJson(PendingDialogueToString(pendingBatch)) + "\"," +
                    "\"pending_dialogue_line_count\":" + pendingBatch.Count + "," +
                    "\"memory_scope\":\"" + AaltoLaunchSessionLogger.EscapeJson(PromptMemoryScope.ToString()) + "\"," +
                    "\"memory_line_count\":" + priorContextLines.Count + "," +
                    "\"available_action_labels\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildActionLabelCsv(availableActions)) + "\"," +
                    "\"action_memory_pairs\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildActionMemoryPairsSnapshot()) + "\"}");

                var messages = new List<OpenAIClient.Msg>
                {
                    new OpenAIClient.Msg("system", GeneralInstructions ?? string.Empty),
                    new OpenAIClient.Msg("user", runtimePrompt)
                };

                SetStatus("Requesting multi-speaker reaction decision from model...");
                var response = await OpenAI.ChatCompletionsJsonAsync(
                    messages,
                    model: SelectedModelId,
                    contextTag: "Aalto:MultiSpeakerDirectedRoomPerformer");

                LastRawModelResponse = response;
                modelResponseUtc = DateTime.UtcNow;
                EmitPerformerEvent(
                    "performer.model_exchange",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"model_response_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(modelResponseUtc.ToString("o")) + "\"," +
                    "\"trace_request_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(OpenAI != null ? OpenAI.LastTraceRequestId ?? string.Empty : string.Empty) + "\"," +
                    "\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"," +
                    "\"runtime_prompt\":\"" + AaltoLaunchSessionLogger.EscapeJson(runtimePrompt) + "\"," +
                    "\"raw_response\":\"" + AaltoLaunchSessionLogger.EscapeJson(response ?? string.Empty) + "\"}");

                if (!TryParseDecision(response, out var reactNow, out var chosenAction, out var justification, out var parseError))
                {
                    reactNow = true;
                    chosenAction = ResolveFallbackAction(availableActions);
                    justification = parseError;
                    parseErrorText = parseError ?? string.Empty;
                    fallbackUsed = true;
                    SetStatus("Parse failed; fallback reaction was used.");
                }

                rawModelAction = chosenAction ?? string.Empty;

                LastDecisionReactNow = reactNow;
                ActionJustificationText = (justification ?? string.Empty).Trim();
                EmitPerformerEvent(
                    "performer.decision_result",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"react_now\":" + (reactNow ? "true" : "false") + "," +
                    "\"chosen_action_raw\":\"" + AaltoLaunchSessionLogger.EscapeJson(chosenAction ?? string.Empty) + "\"," +
                    "\"fallback_used\":" + (fallbackUsed ? "true" : "false") + "," +
                    "\"parse_error\":\"" + AaltoLaunchSessionLogger.EscapeJson(parseErrorText) + "\"," +
                    "\"pending_dialogue_line_ids\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildDialogueLineIdCsv(pendingBatch)) + "\"," +
                    "\"pending_dialogue\":\"" + AaltoLaunchSessionLogger.EscapeJson(PendingDialogueToString(pendingBatch)) + "\"," +
                    "\"justification\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionJustificationText ?? string.Empty) + "\"}");

                if (!reactNow)
                {
                    SelectedActionText = string.Empty;
                    AddDialogTurn(latest, pendingBatch, priorContextLines, SelectedActionText, ActionJustificationText, reactNow: false, PromptMemoryScope, priorContextLines.Count);
                    EmitTurnRecord(turnId, turnIndex, inputReceivedUtc, decisionStartedUtc, modelResponseUtc, rawModelAction, fallbackUsed, parseErrorText, dispatchTimeline, latest, pendingBatch, priorContextLines, availableActions, false, "No action applied because react_now=false.");
                    SetStatus("Decision: no reaction now.");
                    RefreshPromptInspection();
                    return;
                }

                var resolvedAction = ResolveActionLabel(chosenAction, availableActions);
                if (EnforceRegistryActionLabels && string.IsNullOrWhiteSpace(resolvedAction))
                {
                    resolvedAction = ResolveFallbackAction(availableActions);
                    ActionJustificationText += " [Action label was outside available set.]";
                    fallbackUsed = true;
                    if (string.IsNullOrWhiteSpace(parseErrorText))
                        parseErrorText = "Action label was outside available set.";
                }
                else if (string.IsNullOrWhiteSpace(resolvedAction))
                {
                    resolvedAction = (chosenAction ?? string.Empty).Trim();
                }

                SelectedActionText = resolvedAction;
                AddDialogTurn(latest, pendingBatch, priorContextLines, SelectedActionText, ActionJustificationText, reactNow: true, PromptMemoryScope, priorContextLines.Count);
                dispatchTimeline = await ApplyActionDispatchAsync(SelectedActionText, turnId, turnIndex);

                EmitTurnRecord(turnId, turnIndex, inputReceivedUtc, decisionStartedUtc, modelResponseUtc, rawModelAction, fallbackUsed, parseErrorText, dispatchTimeline, latest, pendingBatch, priorContextLines, availableActions, true, ExecutionModeStatus);

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

        private string BuildRuntimePrompt(DialogueLine latestDialogue, List<DialogueLine> pendingBatch, List<DialogueLine> priorContextLines, List<string> availableActions)
        {
            var sb = new StringBuilder();
            sb.AppendLine("latest_dialogue_event_within_current_chunk:");
            sb.AppendLine("speaker=" + NormalizeSpeakerId(latestDialogue?.speakerId));
            sb.AppendLine("text=" + ((latestDialogue?.text) ?? string.Empty).Trim());
            sb.AppendLine();

            sb.AppendLine("current_decision_dialogue_chunk_since_last_evaluation:");
            if (pendingBatch != null && pendingBatch.Count > 0)
            {
                for (int i = 0; i < pendingBatch.Count; i++)
                {
                    var d = pendingBatch[i];
                    sb.AppendLine("- [" + d.timestamp + "] " + d.speakerId + ": " + d.text);
                }
            }
            else
            {
                sb.AppendLine("- (none)");
            }
            sb.AppendLine();

            sb.AppendLine("active_speakers:");
            foreach (var id in GetEnabledSpeakerIds())
                sb.AppendLine("- " + id);
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

            sb.AppendLine("prior_dialogue_memory_for_continuation (excluding current decision chunk, oldest to newest, scope=" + PromptMemoryScope + "):");
            foreach (var d in priorContextLines)
                sb.AppendLine("- [" + d.timestamp + "] " + d.speakerId + ": " + d.text);
            if (priorContextLines.Count == 0)
                sb.AppendLine("- (none yet)");
            sb.AppendLine();

            sb.AppendLine("response_format:");
            sb.AppendLine("Return JSON only with exactly this schema:");
            sb.AppendLine("{");
            sb.AppendLine("  \"react_now\": true,");
            sb.AppendLine("  \"chosen_action\": \"<one action label from available_action_labels or empty if react_now=false>\",");
            sb.AppendLine("  \"justification\": \"<1-3 sentences>\"");
            sb.AppendLine("}");

            return sb.ToString().Trim();
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

            if (!string.IsNullOrWhiteSpace(summary))
                CurrentCharacterSummary = summary;
            if (!string.IsNullOrWhiteSpace(objective))
                CurrentObjective = objective;
            if (!string.IsNullOrWhiteSpace(stance))
                CurrentStance = stance;

            ContextSyncStatus = "Context synced from bootstrapper.";
        }

        [ContextMenu("Multi Speaker Performer/Pull Context From Bootstrapper Now")]
        public void PullContextFromBootstrapperNow()
        {
            SyncContextFromBootstrapper();
            SetStatus("Context pull executed.");
        }

        [ContextMenu("Multi Speaker Performer/Pull Action Labels From Registry Now")]
        public void PullActionsFromRegistryNow()
        {
            PulledActionLabelsSnapshot = BuildActionLabelListFromRegistry();
            ActionPullStatus = "Pulled " + PulledActionLabelsSnapshot.Count + " action labels from registry.";
            EmitPerformerEvent(
                "performer.action_labels_pulled",
                "{\"label_count\":" + PulledActionLabelsSnapshot.Count + "," +
                "\"labels\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildActionLabelCsv(PulledActionLabelsSnapshot)) + "\"," +
                "\"action_memory_pairs\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildActionMemoryPairsSnapshot()) + "\"}");
            SetStatus(ActionPullStatus);
        }

        [ContextMenu("Multi Speaker Performer/Clear Dialogue History")]
        public void ClearDialogueHistory()
        {
            CancelPendingNeutralReturn();
            _recentDialogue.Clear();
            _pendingDialogueSinceLastDecision.Clear();
            _allDialogueHistory.Clear();
            DialogueTranscriptText = string.Empty;
            PendingDialogueSinceLastDecision = string.Empty;
            TotalDialogueLines = 0;
            DialogTurns?.Clear();
            SetStatus("Dialogue history cleared.");
        }

        [ContextMenu("Multi Speaker Performer/Start New Take")]
        public void StartNewTake()
        {
            CancelPendingNeutralReturn();
            TryExportArchiveToProjectFolder("start_new_take");
            ArchiveCurrentTakeIfNeeded();
            ClearDialogueHistory();

            LastDecisionReactNow = false;
            SelectedActionText = string.Empty;
            ActionJustificationText = string.Empty;
            LastRuntimePrompt = string.Empty;
            LastRawModelResponse = string.Empty;
            PromptInspectionText = string.Empty;
            LastIncomingSpeaker = string.Empty;
            LastIncomingText = string.Empty;
            ExternalSpeechStatus = string.Empty;
            SimulationStatus = string.Empty;

            CurrentTakeNumber = Mathf.Max(1, CurrentTakeNumber + 1);
            SetStatus($"Started new take #{CurrentTakeNumber}. Active dialogue memory cleared.");
        }

        [ContextMenu("Multi Speaker Performer/Export Dialog Archive Now")]
        public void ExportDialogArchiveNow()
        {
            TryExportArchiveToProjectFolder("manual");
        }

        [ContextMenu("Multi Speaker Performer/Refresh Prompt Inspection")]
        public void RefreshPromptInspectionContextMenu()
        {
            RefreshPromptInspection();
            SetStatus("Prompt inspection refreshed.");
        }

        private DialogueLine EnqueueDialogue(string speakerId, string text, string inputSource)
        {
            var lineIndex = Mathf.Max(1, _nextDialogueLineIndex++);
            var receivedUtc = DateTime.UtcNow;
            var line = new DialogueLine
            {
                lineId = BuildDialogueLineId(lineIndex),
                speakerId = NormalizeSpeakerId(speakerId),
                text = (text ?? string.Empty).Trim(),
                timestamp = receivedUtc.ToString("HH:mm:ss"),
                inputSource = string.IsNullOrWhiteSpace(inputSource) ? "external_speech" : inputSource.Trim(),
                receivedUtc = receivedUtc.ToString("o")
            };

            _recentDialogue.Enqueue(line);
            _pendingDialogueSinceLastDecision.Enqueue(line);
            _allDialogueHistory.Add(line);
            TotalDialogueLines++;
            RebuildDialogueDisplayText();
            RebuildPendingDialogueDisplayText();

            var max = Mathf.Max(1, RecentDialogueLimit);
            while (_recentDialogue.Count > max)
                _recentDialogue.Dequeue();

            return line;
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
            sb.AppendLine("take_type,take_number,take_captured_at_utc,turn_index,react_now,chunk_line_count,decision_context_summary,latest_dialogue_line,decision_dialogue_chunk,decision_dialogue_chunk_inline,prior_dialogue_context,prior_dialogue_context_inline,prompt_memory_scope,prompt_memory_line_count,selected_response,justification");

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

        private async Task<ActionDispatchTimeline> ApplyActionDispatchAsync(string selectedAction, string turnId, int turnIndex)
        {
            var timeline = new ActionDispatchTimeline
            {
                actionDispatchUtc = DateTime.UtcNow.ToString("o"),
                mode = SelectedActionStateMode.ToString()
            };

            if (!ApplyActionThroughRegistry || ActionMemoryRegistry == null || string.IsNullOrWhiteSpace(selectedAction))
            {
                EmitPerformerEvent(
                    "performer.action_resolved",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"chosen_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(selectedAction ?? string.Empty) + "\",\"applied_through_registry\":false}");
                ExecutionModeStatus = "Registry dispatch disabled or unavailable. Reaction was resolved only.";
                SetStatus("Reaction resolved: " + selectedAction);
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
                EmitPerformerEvent(
                    "performer.action_applied",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"chosen_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(selectedAction ?? string.Empty) + "\"," +
                    "\"resolved_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionMemoryRegistry.LastResolvedMemoryTrigger ?? string.Empty) + "\"," +
                    "\"registry_send\":\"failed\",\"error\":\"" + AaltoLaunchSessionLogger.EscapeJson(sendError ?? string.Empty) + "\"}");
                ExecutionModeStatus = "Dispatch failed. Response state was not applied.";
                SetStatus("Reaction resolved but registry send failed: " + sendError);
                return timeline;
            }

            timeline.resolvedMemoryTrigger = ActionMemoryRegistry.LastResolvedMemoryTrigger ?? string.Empty;
            timeline.registrySend = "ok";
            timeline.actionDispatchCompletedUtc = DateTime.UtcNow.ToString("o");
            EmitPerformerEvent(
                "performer.action_applied",
                "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                "\"turn_index\":" + turnIndex + "," +
                "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                "\"chosen_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(selectedAction ?? string.Empty) + "\"," +
                "\"resolved_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionMemoryRegistry.LastResolvedMemoryTrigger ?? string.Empty) + "\"," +
                "\"registry_send\":\"ok\",\"mode\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedActionStateMode.ToString()) + "\"}");

            if (SelectedActionStateMode == ActionStateMode.HoldResponseState)
            {
                CancelPendingNeutralReturn();
                ExecutionModeStatus = "Hold mode active. Response state remains until another action is applied.";
                SetStatus("Reaction applied: " + selectedAction);
                return timeline;
            }

            var neutralTrigger = (NeutralMemoryTrigger ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(neutralTrigger))
            {
                ExecutionModeStatus = "Pulse mode active, but neutral memory trigger is empty.";
                SetStatus("Reaction applied, but neutral return is not configured.");
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
            SetStatus("Reaction pulsed: " + selectedAction + " (returning to neutral soon)");

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
                EmitPerformerEvent(
                    "performer.neutral_return",
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
                EmitPerformerEvent(
                    "performer.neutral_return",
                    "{\"turn_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(turnId) + "\"," +
                    "\"turn_index\":" + turnIndex + "," +
                    "\"take_number\":" + Mathf.Max(1, CurrentTakeNumber) + "," +
                    "\"neutral_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(neutralTrigger) + "\",\"send\":\"failed\",\"error\":\"" + AaltoLaunchSessionLogger.EscapeJson(neutralError ?? string.Empty) + "\"}");
                ExecutionModeStatus = "Pulse mode failed to return to neutral. " + neutralError;
                SetStatus("Reaction applied but neutral return failed: " + neutralError);
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

        private void AddDialogTurn(
            DialogueLine latestDialogue,
            List<DialogueLine> pendingBatch,
            List<DialogueLine> priorContextLines,
            string selectedAction,
            string justification,
            bool reactNow,
            DialogueMemoryScope memoryScopeUsed,
            int memoryLineCount)
        {
            if (DialogTurns == null)
                DialogTurns = new List<DialogTurnEntry>();

            var latestLine = latestDialogue == null
                ? string.Empty
                : "[" + latestDialogue.timestamp + "] " + NormalizeSpeakerId(latestDialogue.speakerId) + ": " + (latestDialogue.text ?? string.Empty).Trim();

            DialogTurns.Add(new DialogTurnEntry
            {
                latestDialogueLine = latestLine,
                pendingDialogueBatch = PendingDialogueToString(pendingBatch),
                priorDialogueContext = PendingDialogueToString(priorContextLines),
                decisionContextSummary = BuildPendingDialogueDecisionSummary(pendingBatch),
                pendingDialogueLineCount = pendingBatch != null ? pendingBatch.Count : 0,
                promptMemoryScopeUsed = memoryScopeUsed.ToString(),
                promptMemoryLineCount = Mathf.Max(0, memoryLineCount),
                selectedResponse = reactNow
                    ? (selectedAction ?? string.Empty).Trim()
                    : "(no reaction)",
                justification = (justification ?? string.Empty).Trim(),
                reactNow = reactNow,
                showDetails = false,
                showJustification = false
            });

            var max = Mathf.Max(1, DialogLogLimit);
            while (DialogTurns.Count > max)
                DialogTurns.RemoveAt(0);
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
                    latestDialogueLine = entry.latestDialogueLine,
                    pendingDialogueBatch = entry.pendingDialogueBatch,
                    priorDialogueContext = entry.priorDialogueContext,
                    decisionContextSummary = entry.decisionContextSummary,
                    pendingDialogueLineCount = entry.pendingDialogueLineCount,
                    promptMemoryScopeUsed = entry.promptMemoryScopeUsed,
                    promptMemoryLineCount = entry.promptMemoryLineCount,
                    selectedResponse = entry.selectedResponse,
                    justification = entry.justification,
                    reactNow = entry.reactNow,
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
                    .Append(CsvEscape(string.Empty)).Append(',')
                    .Append(CsvEscape(string.Empty)).Append(',')
                    .Append(CsvEscape(string.Empty)).Append(',')
                    .Append(CsvEscape(string.Empty)).Append(',')
                    .Append(CsvEscape(string.Empty)).Append(',')
                    .Append(CsvEscape(string.Empty)).Append(',')
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
                    .Append(CsvEscape(turn != null && turn.reactNow ? "true" : "false")).Append(',')
                    .Append(CsvEscape(turn != null ? Mathf.Max(0, turn.pendingDialogueLineCount).ToString() : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? turn.decisionContextSummary : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? turn.latestDialogueLine : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? turn.pendingDialogueBatch : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? FlattenForCsvInline(turn.pendingDialogueBatch) : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? turn.priorDialogueContext : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? FlattenForCsvInline(turn.priorDialogueContext) : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? turn.promptMemoryScopeUsed : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? Mathf.Max(0, turn.promptMemoryLineCount).ToString() : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? turn.selectedResponse : string.Empty)).Append(',')
                    .Append(CsvEscape(turn != null ? turn.justification : string.Empty))
                    .AppendLine();
            }
        }

        private static string FlattenForCsvInline(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            text = text.Replace("\r\n", " | ").Replace('\r', '|').Replace('\n', '|');
            while (text.Contains("  "))
                text = text.Replace("  ", " ");
            return text;
        }

        private static string CsvEscape(string value)
        {
            var text = value ?? string.Empty;
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            text = text.Replace("\"", "\"\"");
            return "\"" + text + "\"";
        }

        private bool TryDequeuePendingDialogueForDecision(out List<DialogueLine> pendingBatch)
        {
            pendingBatch = new List<DialogueLine>();
            while (_pendingDialogueSinceLastDecision.Count > 0)
                pendingBatch.Add(_pendingDialogueSinceLastDecision.Dequeue());

            RebuildPendingDialogueDisplayText();
            return pendingBatch.Count > 0;
        }

        private void RebuildDialogueDisplayText()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _allDialogueHistory.Count; i++)
            {
                var d = _allDialogueHistory[i];
                sb.AppendLine("[" + d.timestamp + "] " + d.speakerId + ": " + d.text);
            }
            DialogueTranscriptText = sb.ToString().Trim();
        }

        private void RebuildPendingDialogueDisplayText()
        {
            var sb = new StringBuilder();
            foreach (var d in _pendingDialogueSinceLastDecision)
                sb.AppendLine("[" + d.timestamp + "] " + d.speakerId + ": " + d.text);
            PendingDialogueSinceLastDecision = sb.ToString().Trim();
        }

        private List<DialogueLine> GetPromptMemoryLines()
        {
            if (PromptMemoryScope == DialogueMemoryScope.FullAccumulated)
                return new List<DialogueLine>(_allDialogueHistory);

            return new List<DialogueLine>(_recentDialogue);
        }

        private List<DialogueLine> BuildPriorContextMemoryLines(List<DialogueLine> pendingBatch)
        {
            var baseLines = GetPromptMemoryLines();
            if (baseLines.Count == 0)
                return baseLines;

            var pendingCount = pendingBatch != null ? pendingBatch.Count : 0;
            if (pendingCount <= 0)
                return baseLines;

            // Continuation context should exclude the chunk currently being evaluated.
            if (pendingCount >= baseLines.Count)
                return new List<DialogueLine>();

            var priorCount = baseLines.Count - pendingCount;
            return baseLines.GetRange(0, priorCount);
        }

        private bool IsEnabledSpeaker(string speakerId)
        {
            var id = NormalizeSpeakerId(speakerId);
            var enabledIds = GetEnabledSpeakerIds();
            for (int i = 0; i < enabledIds.Count; i++)
            {
                if (string.Equals(enabledIds[i], id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private List<string> GetEnabledSpeakerIds()
        {
            var ids = new List<string>();
            TryAddSpeaker(ids, Speaker1Enabled, Speaker1Id);
            TryAddSpeaker(ids, Speaker2Enabled, Speaker2Id);
            TryAddSpeaker(ids, Speaker3Enabled, Speaker3Id);
            TryAddSpeaker(ids, Speaker4Enabled, Speaker4Id);
            return ids;
        }

        private static void TryAddSpeaker(List<string> ids, bool enabled, string speakerId)
        {
            if (!enabled)
                return;

            var id = NormalizeSpeakerId(speakerId);
            if (string.IsNullOrWhiteSpace(id))
                return;

            for (int i = 0; i < ids.Count; i++)
            {
                if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            ids.Add(id);
        }

        private static string NormalizeSpeakerId(string speakerId)
        {
            var id = (speakerId ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(id) ? "unknown" : id;
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
                if (mapping == null)
                    continue;

                var label = (mapping.actionLabel ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(label) || seen.Contains(label))
                    continue;

                seen.Add(label);
                labels.Add(label);
            }

            return labels;
        }

        private static string BuildActionLabelCsv(List<string> labels)
        {
            if (labels == null || labels.Count == 0)
                return string.Empty;

            return string.Join(",", labels.ToArray());
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

            return string.Join("|", pairs.ToArray());
        }

        private static bool TryParseDecision(
            string response,
            out bool reactNow,
            out string chosenAction,
            out string justification,
            out string error)
        {
            reactNow = false;
            chosenAction = string.Empty;
            justification = string.Empty;
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
                var parsed = JsonUtility.FromJson<ReactionDecision>(json);
                if (parsed == null)
                {
                    error = "Decision JSON parse returned null.";
                    return false;
                }

                reactNow = parsed.react_now;
                chosenAction = (parsed.chosen_action ?? string.Empty).Trim();
                justification = (parsed.justification ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                error = "Decision JSON parse failed: " + ex.Message;
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

            return availableActions != null && availableActions.Count > 0 ? availableActions[0] : string.Empty;
        }

        private void SetStatus(string status)
        {
            LastStatus = status ?? string.Empty;
            Debug.Log("[AaltoMultiSpeakerPerformer] " + LastStatus);
            EmitPerformerEvent("performer.status", "{\"status\":\"" + AaltoLaunchSessionLogger.EscapeJson(LastStatus) + "\"}");
        }

        private static string PendingDialogueToString(List<DialogueLine> lines)
        {
            if (lines == null || lines.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                var d = lines[i];
                if (i > 0) sb.Append("\n");
                sb.Append('[').Append(d.timestamp).Append("] ").Append(d.speakerId).Append(": ").Append(d.text);
            }
            return sb.ToString();
        }

        private static string BuildPendingDialogueDecisionSummary(List<DialogueLine> lines)
        {
            if (lines == null || lines.Count == 0)
                return "No pending dialogue lines.";

            if (lines.Count == 1)
            {
                var only = lines[0];
                return "1 line in chunk: [" + only.timestamp + "] " + NormalizeSpeakerId(only.speakerId) + ": " + (only.text ?? string.Empty).Trim();
            }

            var first = lines[0];
            var last = lines[lines.Count - 1];
            return lines.Count + " lines in chunk from [" + first.timestamp + "] " + NormalizeSpeakerId(first.speakerId) +
                   " to [" + last.timestamp + "] " + NormalizeSpeakerId(last.speakerId) + ".";
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
                    "multi-dialog__" + runId +
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

                EmitPerformerEvent(
                    "performer.archive_exported",
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

        private static void EmitPerformerEvent(string eventType, string payloadJson)
        {
            AaltoLaunchSessionLogger.EmitEvent("AaltoMultiSpeakerPerformer", eventType, payloadJson);
        }

        private void EmitTurnRecord(
            string turnId,
            int turnIndex,
            DateTime inputReceivedUtc,
            DateTime decisionStartedUtc,
            DateTime modelResponseUtc,
            string rawModelAction,
            bool fallbackUsed,
            string parseError,
            ActionDispatchTimeline dispatchTimeline,
            DialogueLine latest,
            List<DialogueLine> pendingBatch,
            List<DialogueLine> priorContextLines,
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
                "\"turn_type\":\"multi_speaker\"," +
                "\"input_source\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildInputSourceCsv(pendingBatch)) + "\"," +
                "\"input_received_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(inputReceivedUtc.ToString("o")) + "\"," +
                "\"decision_started_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(decisionStartedUtc.ToString("o")) + "\"," +
                "\"model_response_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(modelResponseUtc == default ? string.Empty : modelResponseUtc.ToString("o")) + "\"," +
                "\"action_dispatch_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.actionDispatchUtc ?? string.Empty) + "\"," +
                "\"action_dispatch_completed_utc\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.actionDispatchCompletedUtc ?? string.Empty) + "\"," +
                "\"latency_input_to_model_ms\":" + latencyInputToModelMs + "," +
                "\"latency_input_to_action_ms\":" + latencyInputToActionMs + "," +
                "\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"," +
                "\"trace_request_id\":\"" + AaltoLaunchSessionLogger.EscapeJson(OpenAI != null ? OpenAI.LastTraceRequestId ?? string.Empty : string.Empty) + "\"," +
                "\"pending_dialogue_line_ids\":\"" + AaltoLaunchSessionLogger.EscapeJson(BuildDialogueLineIdCsv(pendingBatch)) + "\"," +
                "\"actor_input\":\"" + AaltoLaunchSessionLogger.EscapeJson(latest?.text ?? string.Empty) + "\"," +
                "\"actor_source\":\"" + AaltoLaunchSessionLogger.EscapeJson(latest?.speakerId ?? string.Empty) + "\"," +
                "\"pending_dialogue_batch\":\"" + AaltoLaunchSessionLogger.EscapeJson(PendingDialogueToString(pendingBatch)) + "\"," +
                "\"prior_dialogue_context\":\"" + AaltoLaunchSessionLogger.EscapeJson(PendingDialogueToString(priorContextLines)) + "\"," +
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
                "\"pending_dialogue_line_count\":" + (pendingBatch != null ? pendingBatch.Count : 0) + "," +
                "\"prompt_memory_scope\":\"" + AaltoLaunchSessionLogger.EscapeJson(PromptMemoryScope.ToString()) + "\"," +
                "\"prompt_memory_line_count\":" + (priorContextLines != null ? priorContextLines.Count : 0) + "," +
                "\"selected_action\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedActionText ?? string.Empty) + "\"," +
                "\"selected_memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.resolvedMemoryTrigger ?? string.Empty) + "\"," +
                "\"registry_send\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.registrySend ?? string.Empty) + "\"," +
                "\"registry_error\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.registryError ?? string.Empty) + "\"," +
                "\"neutral_return_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.neutralReturnTrigger ?? string.Empty) + "\"," +
                "\"neutral_return_send\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.neutralReturnSend ?? string.Empty) + "\"," +
                "\"neutral_return_error\":\"" + AaltoLaunchSessionLogger.EscapeJson(dispatchTimeline.neutralReturnError ?? string.Empty) + "\"," +
                "\"director_guidance_snapshot\":\"" + AaltoLaunchSessionLogger.EscapeJson(DirectorGuidance ?? string.Empty) + "\"," +
                "\"decision_note\":\"" + AaltoLaunchSessionLogger.EscapeJson(ActionJustificationText ?? string.Empty) + "\"," +
                "\"react_now\":" + (LastDecisionReactNow ? "true" : "false") + "," +
                "\"execution_succeeded\":" + (executionSucceeded ? "true" : "false") + "," +
                "\"execution_result\":\"" + AaltoLaunchSessionLogger.EscapeJson(executionResult ?? string.Empty) + "\"," +
                "\"execution\":{" +
                    "\"succeeded\":" + (executionSucceeded ? "true" : "false") + "," +
                    "\"result\":\"" + AaltoLaunchSessionLogger.EscapeJson(executionResult ?? string.Empty) + "\"}}";

            EmitPerformerEvent("performer.turn_record", payload);
        }

        private int ReserveDecisionTurnIndex()
        {
            return Mathf.Max(1, _nextDecisionTurnIndex++);
        }

        private string BuildDecisionTurnId(int turnIndex)
        {
            var runId = string.IsNullOrWhiteSpace(AaltoLaunchSessionLogger.CurrentRunId)
                ? "RUN_000"
                : AaltoLaunchSessionLogger.CurrentRunId;

            return runId + "__multi__take_" + Mathf.Max(1, CurrentTakeNumber).ToString("000") +
                   "__turn_" + Mathf.Max(1, turnIndex).ToString("0000");
        }

        private string BuildDialogueLineId(int lineIndex)
        {
            var runId = string.IsNullOrWhiteSpace(AaltoLaunchSessionLogger.CurrentRunId)
                ? "RUN_000"
                : AaltoLaunchSessionLogger.CurrentRunId;

            return runId + "__multi__line_" + Mathf.Max(1, lineIndex).ToString("0000");
        }

        private static string BuildDialogueLineIdCsv(List<DialogueLine> lines)
        {
            if (lines == null || lines.Count == 0)
                return string.Empty;

            var ids = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                var id = lines[i] != null ? lines[i].lineId : string.Empty;
                if (!string.IsNullOrWhiteSpace(id))
                    ids.Add(id);
            }

            return string.Join(",", ids.ToArray());
        }

        private static string BuildInputSourceCsv(List<DialogueLine> lines)
        {
            if (lines == null || lines.Count == 0)
                return string.Empty;

            var sources = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Count; i++)
            {
                var source = (lines[i] != null ? lines[i].inputSource : string.Empty) ?? string.Empty;
                source = source.Trim();
                if (string.IsNullOrWhiteSpace(source) || !seen.Add(source))
                    continue;

                sources.Add(source);
            }

            return string.Join(",", sources.ToArray());
        }

        private static DateTime GetEarliestReceivedUtc(List<DialogueLine> lines)
        {
            var earliest = default(DateTime);
            if (lines == null || lines.Count == 0)
                return DateTime.UtcNow;

            for (int i = 0; i < lines.Count; i++)
            {
                var value = lines[i] != null ? lines[i].receivedUtc : string.Empty;
                var parsed = ParseUtcOrDefault(value);
                if (parsed == default)
                    continue;

                if (earliest == default || parsed < earliest)
                    earliest = parsed;
            }

            return earliest == default ? DateTime.UtcNow : earliest;
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
