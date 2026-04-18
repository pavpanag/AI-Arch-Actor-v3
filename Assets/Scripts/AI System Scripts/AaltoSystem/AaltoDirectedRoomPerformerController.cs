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

        [Header("User Input")]
        [TextArea(1, 4)]
        [InspectorName("Actor Speaking (Simulated Input)")]
        public string InspectorActorInput;
        [TextArea(1, 3)]
        [InspectorName("Inspector Input Status")]
        public string InspectorInputStatus;

        [Header("Spoken Input")]
        [Tooltip("If true, ReceiveExternalSpeech accepts spoken transcripts from OSC/Vosk bridge.")]
        public bool AcceptExternalSpeechInput = true;

        [Tooltip("If true, accepted spoken transcripts are submitted immediately as turns.")]
        public bool AutoSubmitExternalSpeech = true;

        [TextArea(1, 3)]
        [InspectorName("Latest Spoken Transcript")]
        public string LastExternalSpeechText;

        [TextArea(1, 3)]
        [InspectorName("Spoken Input Status")]
        public string ExternalSpeechStatus;

        [TextArea(2, 6)]
        [InspectorName("Director Guidance Input")]
        public string DirectorGuidanceInput;

        [TextArea(1, 3)]
        [InspectorName("Director Guidance Input Status")]
        public string DirectorGuidanceInputStatus;

        [InspectorName("Director Guidance Inputs")]
        public List<string> DirectorGuidanceInputs = new List<string>();

        [TextArea(1, 3)]
        [InspectorName("Selected Action (Agent Produced)")]
        public string SelectedActionText;

        [TextArea(2, 6)]
        [InspectorName("Action Justification (Agent Produced)")]
        public string ActionJustificationText;

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

        [Serializable]
        private sealed class DecisionResponse
        {
            public string chosen_action;
            public string justification;
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
                InspectorInputStatus = "No inspector actor input provided.";
                return;
            }

            InspectorActorInput = string.Empty;
            InspectorInputStatus = "Inspector input submitted.";

            _ = HandleTurnAsync(text);
        }

        public void SubmitTurn(string latestActorInput)
        {
            var text = (latestActorInput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            _ = HandleTurnAsync(text);
        }

        public void ReceiveExternalSpeech(string text)
        {
            var transcript = (text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                ExternalSpeechStatus = "Ignored empty spoken transcript.";
                return;
            }

            LastExternalSpeechText = transcript;
            Debug.Log("[AaltoDirectedRoomPerformer] Received external speech: " + transcript);

            if (!AcceptExternalSpeechInput)
            {
                ExternalSpeechStatus = "Ignored spoken transcript: spoken input is disabled.";
                return;
            }

            if (AutoSubmitExternalSpeech)
            {
                ExternalSpeechStatus = "Spoken transcript submitted.";
                SubmitTurn(transcript);
            }
            else
            {
                InspectorActorInput = transcript;
                ExternalSpeechStatus = "Spoken transcript received. Ready in simulated input field.";
                InspectorInputStatus = "Simulated input populated from spoken transcript.";
            }
        }

        public void SubmitDirectorGuidanceInput()
        {
            var guidance = (DirectorGuidanceInput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(guidance))
            {
                DirectorGuidanceInputStatus = "No director guidance input provided.";
                return;
            }

            DirectorGuidanceInputs.Add(guidance);
            DirectorGuidanceInput = string.Empty;
            RebuildDirectorGuidanceFromInputs();
            DirectorGuidanceInputStatus = "Director guidance added.";
        }

        public void RemoveDirectorGuidanceAt(int index)
        {
            if (index < 0 || index >= DirectorGuidanceInputs.Count)
            {
                DirectorGuidanceInputStatus = "Invalid director guidance index.";
                return;
            }

            DirectorGuidanceInputs.RemoveAt(index);
            RebuildDirectorGuidanceFromInputs();
            DirectorGuidanceInputStatus = "Director guidance removed.";
        }

        public void ClearDirectorGuidanceInputs()
        {
            DirectorGuidanceInputs.Clear();
            RebuildDirectorGuidanceFromInputs();
            DirectorGuidanceInputStatus = "All director guidance inputs cleared.";
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

                var historyLine =
                    "actor: " + latestActorInput.Trim() +
                    " | action: " + SelectedActionText +
                    " | why: " + ActionJustificationText;
                EnqueueHistory(historyLine);

                if (ApplyActionThroughRegistry && ActionMemoryRegistry != null && !string.IsNullOrWhiteSpace(SelectedActionText))
                {
                    if (ActionMemoryRegistry.TrySendMemoryTriggerForActionLabel(SelectedActionText, out var sendError))
                        SetStatus("Action applied: " + SelectedActionText);
                    else
                        SetStatus("Action resolved but registry send failed: " + sendError);
                }
                else
                {
                    SetStatus("Action resolved: " + SelectedActionText);
                }

                RefreshPromptInspection();
            }
            catch (Exception ex)
            {
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

        private void EnqueueHistory(string line)
        {
            _recentInteractions.Enqueue((line ?? string.Empty).Trim());
            var max = Mathf.Max(1, RecentHistoryLimit);
            while (_recentInteractions.Count > max)
                _recentInteractions.Dequeue();
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
        }

        [ContextMenu("Directed Performer/Clear Runtime History")]
        public void ClearRuntimeHistory()
        {
            _recentInteractions.Clear();
            LastStatus = "Runtime history cleared.";
        }

        [ContextMenu("Directed Performer/Refresh Prompt Inspection")]
        public void RefreshPromptInspectionContextMenu()
        {
            RefreshPromptInspection();
            LastStatus = "Prompt inspection refreshed.";
        }
    }
}
