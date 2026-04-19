using System;
using System.Collections.Generic;
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
            public string speakerId;
            public string text;
            public string timestamp;
        }

        [Serializable]
        private sealed class ReactionDecision
        {
            public bool react_now;
            public string chosen_action;
            public string justification;
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

        [Tooltip("If true, a decision pass runs automatically on each accepted speech event.")]
        public bool AutoEvaluateOnExternalSpeech = true;

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
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
        }

        public void ReceiveExternalSpeech(string speakerId, string text)
        {
            var id = NormalizeSpeakerId(speakerId);
            var transcript = (text ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(transcript))
            {
                ExternalSpeechStatus = "Ignored empty transcript.";
                return;
            }

            LastIncomingSpeaker = id;
            LastIncomingText = transcript;

            if (!AcceptExternalSpeechInput)
            {
                ExternalSpeechStatus = "Ignored incoming speech: spoken input disabled.";
                return;
            }

            if (!IsEnabledSpeaker(id))
            {
                ExternalSpeechStatus = "Ignored incoming speech: speaker is not enabled in participants.";
                return;
            }

            EnqueueDialogue(id, transcript);
            ExternalSpeechStatus = "Incoming speech accepted.";
            Debug.Log("[AaltoMultiSpeakerPerformer] Received speech from " + id + ": " + transcript);

            if (AutoEvaluateOnExternalSpeech)
                _ = EvaluateDialogueNowAsync();
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
            ReceiveExternalSpeech(speakerId, text);
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

                var runtimePrompt = BuildRuntimePrompt(latest, pendingBatch, availableActions);
                LastRuntimePrompt = runtimePrompt;

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

                if (!TryParseDecision(response, out var reactNow, out var chosenAction, out var justification, out var parseError))
                {
                    reactNow = true;
                    chosenAction = ResolveFallbackAction(availableActions);
                    justification = parseError;
                    SetStatus("Parse failed; fallback reaction was used.");
                }

                LastDecisionReactNow = reactNow;
                ActionJustificationText = (justification ?? string.Empty).Trim();

                if (!reactNow)
                {
                    SelectedActionText = string.Empty;
                    SetStatus("Decision: no reaction now.");
                    RefreshPromptInspection();
                    return;
                }

                var resolvedAction = ResolveActionLabel(chosenAction, availableActions);
                if (EnforceRegistryActionLabels && string.IsNullOrWhiteSpace(resolvedAction))
                {
                    resolvedAction = ResolveFallbackAction(availableActions);
                    ActionJustificationText += " [Action label was outside available set.]";
                }
                else if (string.IsNullOrWhiteSpace(resolvedAction))
                {
                    resolvedAction = (chosenAction ?? string.Empty).Trim();
                }

                SelectedActionText = resolvedAction;

                if (ApplyActionThroughRegistry && ActionMemoryRegistry != null && !string.IsNullOrWhiteSpace(SelectedActionText))
                {
                    if (ActionMemoryRegistry.TrySendMemoryTriggerForActionLabel(SelectedActionText, out var sendError))
                        SetStatus("Reaction applied: " + SelectedActionText);
                    else
                        SetStatus("Reaction resolved but registry send failed: " + sendError);
                }
                else
                {
                    SetStatus("Reaction resolved: " + SelectedActionText);
                }

                RefreshPromptInspection();
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message);
                RefreshPromptInspection();
            }
        }

        private string BuildRuntimePrompt(DialogueLine latestDialogue, List<DialogueLine> pendingBatch, List<string> availableActions)
        {
            var sb = new StringBuilder();
            sb.AppendLine("latest_dialogue_event:");
            sb.AppendLine("speaker=" + NormalizeSpeakerId(latestDialogue?.speakerId));
            sb.AppendLine("text=" + ((latestDialogue?.text) ?? string.Empty).Trim());
            sb.AppendLine();

            sb.AppendLine("new_dialogue_since_last_decision:");
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

            var memoryLines = GetPromptMemoryLines();
            sb.AppendLine("dialogue_memory_for_context (oldest to newest, scope=" + PromptMemoryScope + "):");
            foreach (var d in memoryLines)
                sb.AppendLine("- [" + d.timestamp + "] " + d.speakerId + ": " + d.text);
            if (memoryLines.Count == 0)
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
            SetStatus(ActionPullStatus);
        }

        [ContextMenu("Multi Speaker Performer/Clear Dialogue History")]
        public void ClearDialogueHistory()
        {
            _recentDialogue.Clear();
            _pendingDialogueSinceLastDecision.Clear();
            _allDialogueHistory.Clear();
            DialogueTranscriptText = string.Empty;
            PendingDialogueSinceLastDecision = string.Empty;
            TotalDialogueLines = 0;
            SetStatus("Dialogue history cleared.");
        }

        [ContextMenu("Multi Speaker Performer/Refresh Prompt Inspection")]
        public void RefreshPromptInspectionContextMenu()
        {
            RefreshPromptInspection();
            SetStatus("Prompt inspection refreshed.");
        }

        private void EnqueueDialogue(string speakerId, string text)
        {
            var line = new DialogueLine
            {
                speakerId = NormalizeSpeakerId(speakerId),
                text = (text ?? string.Empty).Trim(),
                timestamp = DateTime.UtcNow.ToString("HH:mm:ss")
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
        }
    }
}
