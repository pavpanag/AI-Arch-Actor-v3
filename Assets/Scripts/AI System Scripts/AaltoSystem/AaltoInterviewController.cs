using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Serialization;
using UnityEngine;

namespace AaltoSystemV3
{
    public sealed class AaltoInterviewController : MonoBehaviour
    {
        public enum InterviewFlowState
        {
            AwaitingInitialInput,
            AwaitingClarifications,
            GeneratingDraft,
            DraftReadyForReview,
            RevisingDraft,
            RehearsalReady,
            Error
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
        [InspectorName("Objective/Stance Bootstrapper (Agent Produced)")]
        [FormerlySerializedAs("AaltoChat")]
        [FormerlySerializedAs("DirectedPerformer")]
        public AaltoObjectiveStanceBootstrapper ObjectiveStanceBootstrapper;

        [Header("User Input")]
        [TextArea(2, 10)]
        [InspectorName("Back Story (User Input)")]
        public string BackstoryText;
        [TextArea(2, 10)]
        [InspectorName("Motive (User Input)")]
        public string MotiveText;
        [TextArea(2, 10)]
        [InspectorName("Obstacle (User Input)")]
        public string ObstacleText;
        [TextArea(2, 10)]
        [InspectorName("Circumstances (User Input)")]
        public string CircumstancesText;

        [TextArea(2, 10)]
        [InspectorName("Follow Up Questions (Agent)")]
        public string FollowUpQuestionsText;
        [TextArea(2, 10)]
        [InspectorName("Follow Up Answers (User Input)")]
        [FormerlySerializedAs("ClarificationAnswersText")]
        public string FollowUpAnswersText;

        [TextArea(2, 10)]
        [InspectorName("Character Summary (Agent)")]
        public string LastSummary;

        [TextArea(2, 5)]
        [InspectorName("Last Status (Agent Produced)")]
        public string LastStatus;

        [TextArea(3, 12)]
        [InspectorName("Interview Questions (Agent Produced)")]
        public string InterviewQuestionsText;

        [Header("Architect Input")]
        [InspectorName("Interview Prompt Set (Architect Input)")]
        public AaltoInterviewPromptSet PromptSet;

        [Tooltip("If true, rehearsal should begin only after explicit summary approval.")]
        [InspectorName("Require Approval Before Rehearsal (Architect Input)")]
        public bool RequireApprovalBeforeRehearsal = true;
        [Tooltip("Exact number of follow-up clarification questions to request and keep.")]
        [InspectorName("Follow Up Question Count (Architect Input)")]
        [Range(1, 4)] public int MaxFollowUpQuestions = 2;

        [InspectorName("Model (OpenAI Dropdown)")]
        public OpenAIModelPreset ModelSelection = OpenAIModelPreset.Gpt4oMini;

        [Range(20, 500)]
        [InspectorName("Min Summary Chars (Architect Input)")]
        public int MinSummaryChars = 60;
        [Range(40, 800)]
        [InspectorName("Max Summary Chars (Architect Input)")]
        public int MaxSummaryChars = 260;

        [Header("Agent Produced (System/Debug)")]
        [TextArea(4, 10)]
        [InspectorName("Last Clarification System Prompt (Agent Produced)")]
        public string LastClarificationSystemPrompt;
        [TextArea(4, 12)]
        [InspectorName("Last Clarification User Prompt (Agent Produced)")]
        public string LastClarificationUserPrompt;
        [TextArea(4, 12)]
        [InspectorName("Last Clarification Response JSON (Agent Produced)")]
        public string LastClarificationResponseJson;
        [TextArea(4, 10)]
        [InspectorName("Last Summary System Prompt (Agent Produced)")]
        public string LastSummarySystemPrompt;
        [TextArea(4, 12)]
        [InspectorName("Last Summary User Prompt (Agent Produced)")]
        public string LastSummaryUserPrompt;
        [TextArea(4, 12)]
        [InspectorName("Last Summary Response JSON (Agent Produced)")]
        public string LastSummaryResponseJson;
        [TextArea(4, 12)]
        [InspectorName("Prompt Inspection Text (Agent Produced)")]
        public string PromptInspectionText;
        [TextArea(2, 10)]
        [InspectorName("Revision Notes (Optional User Input)")]
        public string RevisionNotesText;
        public InterviewFlowState CurrentFlowState = InterviewFlowState.AwaitingInitialInput;
        [InspectorName("Flow State Display (Agent Produced)")]
        public string FlowStateDisplay = "AwaitingInitialInput";
        public string LatestProfilePath => Path.Combine(Application.persistentDataPath, "latest_alto_character_profile.json");
        public bool HasApprovedSummary { get; private set; }

        private bool _awaitingClarifications;

        private void Awake()
        {
            SetFlowState(InterviewFlowState.AwaitingInitialInput);
        }

        private string SelectedModelId => ModelSelection switch
        {
            OpenAIModelPreset.Gpt54 => "gpt-5.4",
            OpenAIModelPreset.Gpt54Mini => "gpt-5.4-mini",
            OpenAIModelPreset.Gpt41 => "gpt-4.1",
            OpenAIModelPreset.Gpt41Mini => "gpt-4.1-mini",
            OpenAIModelPreset.Gpt4oMini => "gpt-4o-mini",
            _ => "gpt-4o-mini"
        };

        [Serializable]
        private sealed class SummaryResponse
        {
            public string character_summary;
        }

        [Serializable]
        private sealed class ClarificationQuestionResponse
        {
            public string[] clarification_questions;
        }

        public async void GenerateClarificationQuestionsFromInputs()
        {
            var backstory = BackstoryText ?? string.Empty;
            var motive = MotiveText ?? string.Empty;
            var obstacle = ObstacleText ?? string.Empty;
            var circumstances = CircumstancesText ?? string.Empty;

            EmitInterviewEvent(
                "interview.followup_questions_requested",
                "{\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"," +
                "\"backstory\":\"" + AaltoLaunchSessionLogger.EscapeJson(backstory) + "\"," +
                "\"motive\":\"" + AaltoLaunchSessionLogger.EscapeJson(motive) + "\"," +
                "\"obstacle\":\"" + AaltoLaunchSessionLogger.EscapeJson(obstacle) + "\"," +
                "\"circumstances\":\"" + AaltoLaunchSessionLogger.EscapeJson(circumstances) + "\"}");

            InterviewQuestionsText = BuildInterviewQuestionsDisplay();

            if (string.IsNullOrWhiteSpace(backstory) &&
                string.IsNullOrWhiteSpace(motive) &&
                string.IsNullOrWhiteSpace(obstacle) &&
                string.IsNullOrWhiteSpace(circumstances))
            {
                SetFlowState(InterviewFlowState.AwaitingInitialInput);
                SetStatus("[aalto-interview] Provide background, motivation, obstacle, and/or circumstances first.");
                return;
            }

            try
            {
                SetFlowState(InterviewFlowState.AwaitingClarifications);
                SetStatus("[aalto-interview] Generating clarification questions...");

                var followUpJson = await RequestClarificationQuestionsAsync(backstory, motive, obstacle, circumstances);

                if (!TryParseClarificationQuestions(followUpJson, out var followUps, out var followUpError))
                {
                    SetFlowState(InterviewFlowState.Error);
                    SetStatus("[aalto-interview] " + followUpError);
                    return;
                }

                if (followUps.Count > 0)
                {
                    var previousQuestions = FollowUpQuestionsText ?? string.Empty;
                    FollowUpQuestionsText = string.Join("\n", followUps.ToArray());
                    EmitInterviewEvent(
                        "interview.followup_questions_generated",
                        "{\"questions\":\"" + AaltoLaunchSessionLogger.EscapeJson(FollowUpQuestionsText ?? string.Empty) + "\"," +
                        "\"question_count\":" + followUps.Count + "}");
                    if (!string.Equals(previousQuestions, FollowUpQuestionsText ?? string.Empty, StringComparison.Ordinal))
                    {
                        EmitInterviewEvent(
                            "interview.followup_questions_changed",
                            "{\"previous_questions\":\"" + AaltoLaunchSessionLogger.EscapeJson(previousQuestions) + "\"," +
                            "\"new_questions\":\"" + AaltoLaunchSessionLogger.EscapeJson(FollowUpQuestionsText ?? string.Empty) + "\"}");
                    }
                    _awaitingClarifications = true;
                    HasApprovedSummary = false;
                    SetFlowState(InterviewFlowState.AwaitingClarifications);
                    SetStatus("[aalto-interview] Clarification questions ready. Answer them in Follow Up Answers, then run GenerateCharacterSummaryFromInputs.");
                    return;
                }

                FollowUpQuestionsText = string.Empty;
                _awaitingClarifications = false;
                SetFlowState(InterviewFlowState.AwaitingClarifications);
                SetStatus("[aalto-interview] No clarification questions were produced. Generate character summary directly.");
            }
            catch (Exception ex)
            {
                SetFlowState(InterviewFlowState.Error);
                SetStatus("[aalto-interview] Error: " + ex.Message);
            }
        }

        public async void GenerateCharacterSummaryFromInputs()
        {
            var backstory = BackstoryText ?? string.Empty;
            var motive = MotiveText ?? string.Empty;
            var obstacle = ObstacleText ?? string.Empty;
            var circumstances = CircumstancesText ?? string.Empty;
            var followUpAnswers = FollowUpAnswersText ?? string.Empty;

            EmitInterviewEvent(
                "interview.summary_requested",
                "{\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"," +
                "\"backstory\":\"" + AaltoLaunchSessionLogger.EscapeJson(backstory) + "\"," +
                "\"motive\":\"" + AaltoLaunchSessionLogger.EscapeJson(motive) + "\"," +
                "\"obstacle\":\"" + AaltoLaunchSessionLogger.EscapeJson(obstacle) + "\"," +
                "\"circumstances\":\"" + AaltoLaunchSessionLogger.EscapeJson(circumstances) + "\"," +
                "\"followup_answers\":\"" + AaltoLaunchSessionLogger.EscapeJson(followUpAnswers) + "\"}");

            InterviewQuestionsText = BuildInterviewQuestionsDisplay();

            if (string.IsNullOrWhiteSpace(backstory) &&
                string.IsNullOrWhiteSpace(motive) &&
                string.IsNullOrWhiteSpace(obstacle) &&
                string.IsNullOrWhiteSpace(circumstances))
            {
                SetFlowState(InterviewFlowState.AwaitingInitialInput);
                SetStatus("[aalto-interview] Provide background, motivation, obstacle, and/or circumstances first.");
                return;
            }

            try
            {
                var clarification = _awaitingClarifications ? (FollowUpAnswersText ?? string.Empty) : string.Empty;
                if (_awaitingClarifications && string.IsNullOrWhiteSpace(clarification))
                {
                    SetFlowState(InterviewFlowState.AwaitingClarifications);
                    SetStatus("[aalto-interview] Clarification answers are required before summary generation.");
                    return;
                }

                SetFlowState(InterviewFlowState.GeneratingDraft);
                SetStatus("[aalto-interview] Generating draft character summary...");
                var json = await RequestSummaryAsync(backstory, motive, obstacle, circumstances, clarification);

                if (!TryParseSummary(json, out var summary, out var error))
                {
                    SetFlowState(InterviewFlowState.Error);
                    SetStatus("[aalto-interview] " + error);
                    return;
                }

                var previousSummary = LastSummary ?? string.Empty;
                LastSummary = summary.Trim();
                EmitInterviewEvent(
                    "interview.summary_generated",
                    "{\"summary\":\"" + AaltoLaunchSessionLogger.EscapeJson(LastSummary ?? string.Empty) + "\"}");
                if (!string.Equals(previousSummary, LastSummary ?? string.Empty, StringComparison.Ordinal))
                {
                    EmitInterviewEvent(
                        "interview.summary_changed",
                        "{\"previous_summary\":\"" + AaltoLaunchSessionLogger.EscapeJson(previousSummary) + "\"," +
                        "\"new_summary\":\"" + AaltoLaunchSessionLogger.EscapeJson(LastSummary ?? string.Empty) + "\"}");
                }
                HasApprovedSummary = false;
                SetRuntimeApprovalState(false);
                _awaitingClarifications = false;
                RefreshPromptInspectionText();
                SetFlowState(InterviewFlowState.DraftReadyForReview);
                SetStatus("[aalto-interview] Draft summary ready. ApproveCurrentSummary() to start rehearsal, or ReviseSummaryFromNotes() to change it.");
            }
            catch (Exception ex)
            {
                SetFlowState(InterviewFlowState.Error);
                SetStatus("[aalto-interview] Error: " + ex.Message);
            }
        }

        public void GenerateFollowUpQuestions()
        {
            GenerateClarificationQuestionsFromInputs();
        }

        public void SubmitFollowUpAnswersAndCreateSummary()
        {
            GenerateCharacterSummaryFromInputs();
        }

        public void LoadLatestProfile()
        {
            try
            {
                if (!File.Exists(LatestProfilePath))
                {
                    SetFlowState(InterviewFlowState.AwaitingInitialInput);
                    SetStatus("[aalto-interview] No saved profile found.");
                    return;
                }

                var json = File.ReadAllText(LatestProfilePath);
                var profile = JsonUtility.FromJson<AaltoCharacterProfile>(json);
                if (profile == null || string.IsNullOrWhiteSpace(profile.character_summary))
                {
                    SetFlowState(InterviewFlowState.Error);
                    SetStatus("[aalto-interview] Saved profile is invalid.");
                    return;
                }

                BackstoryText = profile.backstory ?? string.Empty;
                MotiveText = profile.motive ?? string.Empty;
                ObstacleText = profile.obstacle ?? string.Empty;
                CircumstancesText = profile.circumstances ?? string.Empty;
                LastSummary = profile.character_summary;
                InterviewQuestionsText = BuildInterviewQuestionsDisplay();

                HasApprovedSummary = true;
                _awaitingClarifications = false;
                FollowUpQuestionsText = string.Empty;
                FollowUpAnswersText = string.Empty;

                ApplySummaryToRuntime(profile.character_summary);
                SetRuntimeApprovalState(true);
                SetFlowState(InterviewFlowState.RehearsalReady);
                SetStatus("[aalto-interview] Loaded latest profile and applied to Aalto runtime.");
                EmitInterviewEvent(
                    "interview.profile_loaded",
                    "{\"profile_path\":\"" + AaltoLaunchSessionLogger.EscapeJson(LatestProfilePath) + "\"," +
                    "\"summary\":\"" + AaltoLaunchSessionLogger.EscapeJson(LastSummary ?? string.Empty) + "\"}");
            }
            catch (Exception ex)
            {
                SetFlowState(InterviewFlowState.Error);
                SetStatus("[aalto-interview] Load failed: " + ex.Message);
            }
        }

        public void ApproveCurrentSummary()
        {
            if (string.IsNullOrWhiteSpace(LastSummary))
            {
                SetFlowState(InterviewFlowState.AwaitingInitialInput);
                SetStatus("[aalto-interview] No summary to approve yet.");
                return;
            }

            try
            {
                var profile = new AaltoCharacterProfile
                {
                    timestampUtc = DateTime.UtcNow.ToString("o"),
                    backstory = (BackstoryText ?? string.Empty).Trim(),
                    motive = (MotiveText ?? string.Empty).Trim(),
                    obstacle = (ObstacleText ?? string.Empty).Trim(),
                    circumstances = (CircumstancesText ?? string.Empty).Trim(),
                    character_summary = LastSummary.Trim()
                };

                SaveProfile(profile);
                ApplySummaryToRuntime(profile.character_summary);

                HasApprovedSummary = true;
                _awaitingClarifications = false;
                FollowUpQuestionsText = string.Empty;
                FollowUpAnswersText = string.Empty;
                SetRuntimeApprovalState(true);

                SetFlowState(InterviewFlowState.RehearsalReady);
                SetStatus("[aalto-interview] Summary approved. Rehearsal can begin.");
                EmitInterviewEvent(
                    "interview.summary_approved",
                    "{\"profile_path\":\"" + AaltoLaunchSessionLogger.EscapeJson(LatestProfilePath) + "\"," +
                    "\"summary\":\"" + AaltoLaunchSessionLogger.EscapeJson(LastSummary ?? string.Empty) + "\"}");
            }
            catch (Exception ex)
            {
                SetFlowState(InterviewFlowState.Error);
                SetStatus("[aalto-interview] Approval failed: " + ex.Message);
            }
        }

        public async void ReviseSummaryFromNotes()
        {
            if (string.IsNullOrWhiteSpace(LastSummary))
            {
                SetFlowState(InterviewFlowState.AwaitingInitialInput);
                SetStatus("[aalto-interview] No draft summary to revise yet.");
                return;
            }

            var notes = (RevisionNotesText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(notes))
            {
                SetFlowState(InterviewFlowState.DraftReadyForReview);
                SetStatus("[aalto-interview] Add revision notes first.");
                return;
            }

            try
            {
                SetFlowState(InterviewFlowState.RevisingDraft);
                SetStatus("[aalto-interview] Revising draft summary...");
                EmitInterviewEvent(
                    "interview.summary_revision_requested",
                    "{\"revision_notes\":\"" + AaltoLaunchSessionLogger.EscapeJson(notes) + "\"," +
                    "\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"}");
                var revisedJson = await RequestRevisionAsync(LastSummary, notes);

                if (!TryParseSummary(revisedJson, out var revised, out var error))
                {
                    SetFlowState(InterviewFlowState.Error);
                    SetStatus("[aalto-interview] " + error);
                    return;
                }

                var previousSummary = LastSummary ?? string.Empty;
                LastSummary = revised;
                EmitInterviewEvent(
                    "interview.summary_revised",
                    "{\"summary\":\"" + AaltoLaunchSessionLogger.EscapeJson(LastSummary ?? string.Empty) + "\"}");
                if (!string.Equals(previousSummary, LastSummary ?? string.Empty, StringComparison.Ordinal))
                {
                    EmitInterviewEvent(
                        "interview.summary_changed",
                        "{\"previous_summary\":\"" + AaltoLaunchSessionLogger.EscapeJson(previousSummary) + "\"," +
                        "\"new_summary\":\"" + AaltoLaunchSessionLogger.EscapeJson(LastSummary ?? string.Empty) + "\"}");
                }
                HasApprovedSummary = false;
                SetRuntimeApprovalState(false);
                SetFlowState(InterviewFlowState.DraftReadyForReview);
                SetStatus("[aalto-interview] Revision ready. ApproveCurrentSummary() when satisfied.");
            }
            catch (Exception ex)
            {
                SetFlowState(InterviewFlowState.Error);
                SetStatus("[aalto-interview] Revision failed: " + ex.Message);
            }
        }

        [ContextMenu("Aalto Interview/Generate Follow Up Questions")]
        public void ContextGenerateClarifications() => GenerateFollowUpQuestions();

        [ContextMenu("Aalto Interview/Generate Character Summary")]
        public void ContextGenerateSummary() => SubmitFollowUpAnswersAndCreateSummary();

        [ContextMenu("Aalto Interview/Load Latest Profile")]
        public void ContextLoadLatest() => LoadLatestProfile();

        [ContextMenu("Aalto Interview/Approve Current Summary")]
        public void ContextApproveSummary() => ApproveCurrentSummary();

        [ContextMenu("Aalto Interview/Revise Summary From Notes")]
        public void ContextReviseSummary() => ReviseSummaryFromNotes();

        [ContextMenu("Aalto Interview/Reveal Latest Profile")]
        public void RevealLatestProfile()
        {
            Debug.Log("[aalto-interview] Latest profile path: " + LatestProfilePath);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(LatestProfilePath);
#endif
        }

        private async Task<string> RequestSummaryAsync(string backstory, string motive, string obstacle, string circumstances, string clarificationAnswers)
        {
            if (OpenAI == null)
                throw new Exception("OpenAIClient is not assigned on AaltoInterviewController.");

            var system = BuildSystemPrompt();
            var user = BuildUserPrompt(backstory, motive, obstacle, circumstances, clarificationAnswers);
            LastSummarySystemPrompt = system;
            LastSummaryUserPrompt = user;

            var messages = new System.Collections.Generic.List<OpenAIClient.Msg>
            {
                new OpenAIClient.Msg("system", system),
                new OpenAIClient.Msg("user", user)
            };

            var response = await OpenAI.ChatCompletionsJsonAsync(messages, model: SelectedModelId, contextTag: "Aalto:SummarizeCharacter");
            LastSummaryResponseJson = response;
            EmitInterviewEvent(
                "interview.model_exchange.summary",
                "{\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"," +
                "\"system_prompt\":\"" + AaltoLaunchSessionLogger.EscapeJson(system) + "\"," +
                "\"user_prompt\":\"" + AaltoLaunchSessionLogger.EscapeJson(user) + "\"," +
                "\"raw_response\":\"" + AaltoLaunchSessionLogger.EscapeJson(response ?? string.Empty) + "\"}");
            RefreshPromptInspectionText();
            return response;
        }

        private async Task<string> RequestClarificationQuestionsAsync(string backstory, string motive, string obstacle, string circumstances)
        {
            if (OpenAI == null)
                throw new Exception("OpenAIClient is not assigned on AaltoInterviewController.");

            var clarificationCount = Mathf.Max(1, MaxFollowUpQuestions);

            var system =
                PromptSet != null
                    ? PromptSet.BuildResolvedClarificationSystemPrompt(clarificationCount)
                    : DefaultClarificationSystemPrompt(clarificationCount);

            var userSb = PromptSet != null
                ? new StringBuilder(PromptSet.BuildResolvedClarificationUserPrompt(backstory, motive, obstacle, circumstances, clarificationCount))
                : BuildDefaultClarificationUserPrompt(backstory, motive, obstacle, circumstances, clarificationCount);

            LastClarificationSystemPrompt = system;
            LastClarificationUserPrompt = userSb.ToString();

            var messages = new List<OpenAIClient.Msg>
            {
                new OpenAIClient.Msg("system", system),
                new OpenAIClient.Msg("user", LastClarificationUserPrompt)
            };

            var response = await OpenAI.ChatCompletionsJsonAsync(messages, model: SelectedModelId, contextTag: "Aalto:ClarificationQuestions");
            LastClarificationResponseJson = response;
            EmitInterviewEvent(
                "interview.model_exchange.followup",
                "{\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"," +
                "\"system_prompt\":\"" + AaltoLaunchSessionLogger.EscapeJson(system) + "\"," +
                "\"user_prompt\":\"" + AaltoLaunchSessionLogger.EscapeJson(LastClarificationUserPrompt ?? string.Empty) + "\"," +
                "\"raw_response\":\"" + AaltoLaunchSessionLogger.EscapeJson(response ?? string.Empty) + "\"}");
            RefreshPromptInspectionText();
            return response;
        }

        private async Task<string> RequestRevisionAsync(string currentSummary, string revisionNotes)
        {
            if (OpenAI == null)
                throw new Exception("OpenAIClient is not assigned on AaltoInterviewController.");

            var system = BuildRevisionSystemPrompt();
            var user = BuildRevisionUserPrompt(currentSummary, revisionNotes);
            LastSummarySystemPrompt = system;
            LastSummaryUserPrompt = user;

            var messages = new System.Collections.Generic.List<OpenAIClient.Msg>
            {
                new OpenAIClient.Msg("system", system),
                new OpenAIClient.Msg("user", user)
            };

            var response = await OpenAI.ChatCompletionsJsonAsync(messages, model: SelectedModelId, contextTag: "Aalto:ReviseCharacterSummary");
            LastSummaryResponseJson = response;
            EmitInterviewEvent(
                "interview.model_exchange.revision",
                "{\"model\":\"" + AaltoLaunchSessionLogger.EscapeJson(SelectedModelId) + "\"," +
                "\"system_prompt\":\"" + AaltoLaunchSessionLogger.EscapeJson(system) + "\"," +
                "\"user_prompt\":\"" + AaltoLaunchSessionLogger.EscapeJson(user) + "\"," +
                "\"raw_response\":\"" + AaltoLaunchSessionLogger.EscapeJson(response ?? string.Empty) + "\"}");
            RefreshPromptInspectionText();
            return response;
        }

        private string BuildSystemPrompt()
        {
            return PromptSet != null
                ? PromptSet.BuildResolvedSummarySystemPrompt(MinSummaryChars, MaxSummaryChars)
                : DefaultSummarySystemPrompt();
        }

        private string BuildUserPrompt(string backstory, string motive, string obstacle, string circumstances, string clarificationAnswers)
        {
            return PromptSet != null
                ? PromptSet.BuildResolvedSummaryUserPrompt(backstory, motive, obstacle, circumstances, clarificationAnswers)
                : BuildDefaultSummaryUserPrompt(backstory, motive, obstacle, circumstances, clarificationAnswers);
        }

        private string BuildRevisionSystemPrompt()
        {
            return PromptSet != null
                ? PromptSet.BuildResolvedRevisionSystemPrompt(MinSummaryChars, MaxSummaryChars)
                : DefaultRevisionSystemPrompt();
        }

        private static string BuildRevisionUserPrompt(string currentSummary, string revisionNotes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Revise this character summary:");
            sb.AppendLine(currentSummary ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("Using these revision notes:");
            sb.AppendLine(revisionNotes ?? string.Empty);
            return sb.ToString();
        }

        private bool TryParseSummary(string json, out string summary, out string error)
        {
            summary = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Empty model response.";
                return false;
            }

            try
            {
                var normalized = TraceUtils.NormalizeEmbeddedJson(json.Trim());
                var parsed = JsonUtility.FromJson<SummaryResponse>(normalized);
                if (parsed == null || string.IsNullOrWhiteSpace(parsed.character_summary))
                {
                    error = "Response missing required field: character_summary.";
                    return false;
                }

                var clean = parsed.character_summary.Trim();
                if (clean.Length < MinSummaryChars)
                {
                    error = $"Summary too short ({clean.Length} chars). Minimum is {MinSummaryChars}.";
                    return false;
                }
                if (clean.Length > MaxSummaryChars)
                {
                    error = $"Summary too long ({clean.Length} chars). Maximum is {MaxSummaryChars}.";
                    return false;
                }

                summary = clean;
                return true;
            }
            catch (Exception ex)
            {
                error = "Invalid JSON response: " + ex.Message;
                return false;
            }
        }

        private bool TryParseClarificationQuestions(string json, out List<string> questions, out string error)
        {
            questions = new List<string>();
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Empty clarification response.";
                return false;
            }

            try
            {
                var normalized = TraceUtils.NormalizeEmbeddedJson(json.Trim());
                var parsed = JsonUtility.FromJson<ClarificationQuestionResponse>(normalized);
                if (parsed?.clarification_questions == null || parsed.clarification_questions.Length == 0)
                {
                    error = "Clarification response missing required field: clarification_questions.";
                    return false;
                }

                for (int i = 0; i < parsed.clarification_questions.Length; i++)
                {
                    var q = (parsed.clarification_questions[i] ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(q))
                        questions.Add(q);
                }

                var max = Mathf.Max(1, MaxFollowUpQuestions);
                if (questions.Count > max)
                    questions.RemoveRange(max, questions.Count - max);

                if (questions.Count == 0)
                {
                    error = "Clarification response had no usable questions.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "Invalid clarification JSON response: " + ex.Message;
                return false;
            }
        }

        private void SaveProfile(AaltoCharacterProfile profile)
        {
            var folder = Path.GetDirectoryName(LatestProfilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            var json = JsonUtility.ToJson(profile, true);
            File.WriteAllText(LatestProfilePath, json);
            Debug.Log("[aalto-interview] Saved profile to: " + LatestProfilePath);
        }

        private void ApplySummaryToRuntime(string summary)
        {
            if (ObjectiveStanceBootstrapper == null || string.IsNullOrWhiteSpace(summary)) return;
            ObjectiveStanceBootstrapper.SetSummaryFromInterview(summary.Trim(), true);
            Debug.Log("[aalto-interview] Applied character summary to AaltoObjectiveStanceBootstrapper.");
        }

        private void SetRuntimeApprovalState(bool approved)
        {
            // Approval semantics are intentionally not enforced in the runtime handoff.
            // Kept as a no-op to preserve interview flow method calls.
        }

        private void SetFlowState(InterviewFlowState state)
        {
            CurrentFlowState = state;
            FlowStateDisplay = state.ToString();
        }

        private string BuildInterviewQuestionsDisplay()
        {
            var sb = new StringBuilder();
            sb.AppendLine("1) What is the background of the character?");
            sb.AppendLine("2) What is the main motivation?");
            sb.AppendLine("3) What is the main obstacle they face?");
            sb.AppendLine("4) What circumstances are they currently experiencing?");
            return sb.ToString().TrimEnd();
        }

        private void SetStatus(string msg)
        {
            Debug.Log(msg);
            LastStatus = msg;
            EmitInterviewEvent("interview.status", "{\"status\":\"" + AaltoLaunchSessionLogger.EscapeJson(msg ?? string.Empty) + "\"}");
            RefreshPromptInspectionText();
        }

        private static void EmitInterviewEvent(string eventType, string payloadJson)
        {
            AaltoLaunchSessionLogger.EmitEvent("AaltoInterviewController", eventType, payloadJson);
        }

        private static string DefaultClarificationSystemPrompt(int clarificationCount)
        {
            return
                "You generate concise clarification questions for a character interview.\n" +
                "Return JSON only with exactly this schema:\n" +
                "{\n" +
                "  \"clarification_questions\": [\"...\", \"...\"]\n" +
                "}\n" +
                "Rules:\n" +
                $"- Return exactly {Mathf.Max(1, clarificationCount)} questions.\n" +
                "- Questions must target ambiguity in background, motivation, obstacle, and circumstances.\n" +
                "- Keep each question specific and answerable in 1-2 sentences.\n" +
                "- No extra keys.";
        }

        private static StringBuilder BuildDefaultClarificationUserPrompt(string backstory, string motive, string obstacle, string circumstances, int clarificationCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Draft exactly {Mathf.Max(1, clarificationCount)} clarification questions for this interview input:");
            sb.AppendLine("background:");
            sb.AppendLine(string.IsNullOrWhiteSpace(backstory) ? "(none provided)" : backstory.Trim());
            sb.AppendLine();
            sb.AppendLine("motivation:");
            sb.AppendLine(string.IsNullOrWhiteSpace(motive) ? "(none provided)" : motive.Trim());
            sb.AppendLine();
            sb.AppendLine("obstacle:");
            sb.AppendLine(string.IsNullOrWhiteSpace(obstacle) ? "(none provided)" : obstacle.Trim());
            sb.AppendLine();
            sb.AppendLine("circumstances:");
            sb.AppendLine(string.IsNullOrWhiteSpace(circumstances) ? "(none provided)" : circumstances.Trim());
            return sb;
        }

        private string DefaultSummarySystemPrompt()
        {
            return
                $"You are creating a compact runtime character summary for a scenic rehearsal agent.\n" +
                "Return JSON only with exactly this schema:\n" +
                "{\n" +
                "  \"character_summary\": \"...\"\n" +
                "}\n" +
                "Rules:\n" +
                "- Use background, motivation, obstacle, circumstances, and clarification answers (when provided) as source material.\n" +
                "- Keep summary concrete and playable.\n" +
                $"- Keep length between {MinSummaryChars} and {MaxSummaryChars} characters.\n" +
                "- Avoid abstraction and generic assistant language.";
        }

        private string BuildDefaultSummaryUserPrompt(string backstory, string motive, string obstacle, string circumstances, string clarificationAnswers)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Create one runtime-ready character summary from this material:");
            sb.AppendLine("background:");
            sb.AppendLine(string.IsNullOrWhiteSpace(backstory) ? "(none provided)" : backstory.Trim());
            sb.AppendLine();
            sb.AppendLine("motivation:");
            sb.AppendLine(string.IsNullOrWhiteSpace(motive) ? "(none provided)" : motive.Trim());
            sb.AppendLine();
            sb.AppendLine("obstacle:");
            sb.AppendLine(string.IsNullOrWhiteSpace(obstacle) ? "(none provided)" : obstacle.Trim());
            sb.AppendLine();
            sb.AppendLine("circumstances:");
            sb.AppendLine(string.IsNullOrWhiteSpace(circumstances) ? "(none provided)" : circumstances.Trim());

            if (!string.IsNullOrWhiteSpace(clarificationAnswers))
            {
                sb.AppendLine();
                sb.AppendLine("clarification_answers:");
                sb.AppendLine(clarificationAnswers.Trim());
            }

            return sb.ToString();
        }

        private string DefaultRevisionSystemPrompt()
        {
            return
                $"You revise an approved-character draft for scenic rehearsal.\n" +
                "Return JSON only with exactly this schema:\n" +
                "{\n" +
                "  \"character_summary\": \"...\"\n" +
                "}\n" +
                "Rules:\n" +
                "- Preserve the same core character identity unless notes explicitly change it.\n" +
                "- Apply revision notes precisely and keep the result playable.\n" +
                $"- Keep length between {MinSummaryChars} and {MaxSummaryChars} characters.\n" +
                "- No extra keys.";
        }

        private void RefreshPromptInspectionText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[INTERVIEW QUESTION DEBUG]");
            sb.AppendLine($"flow: {FlowStateDisplay}");
            sb.AppendLine($"status: {LastStatus ?? string.Empty}");
            sb.AppendLine();
            sb.AppendLine("--- generated follow-up questions ---");
            sb.AppendLine(FollowUpQuestionsText ?? string.Empty);
            PromptInspectionText = sb.ToString();
        }
    }
}
