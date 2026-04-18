using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace AaltoSystemV3
{
    /// <summary>
    /// Single producer of performer context: summary, objective, stance.
    /// - Summary is sourced from interview (or set manually through SetSummaryFromInterview).
    /// - Objective/stance are generated from summary.
    /// - Performer pulls values from this component.
    /// </summary>
    public sealed class AaltoObjectiveStanceBootstrapper : MonoBehaviour
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
        [InspectorName("Interview Controller (Summary Source)")]
        public AaltoInterviewController InterviewController;

        [Header("Flow")]
        [Tooltip("If true, calling SetSummaryFromInterview will auto-generate objective and stance.")]
        public bool AutoGenerateOnSummaryUpdate = true;

        [Header("Model")]
        [InspectorName("Model (OpenAI Dropdown)")]
        public OpenAIModelPreset Model = OpenAIModelPreset.Gpt4oMini;

        [Header("Architect Input")]
        [TextArea(3, 12)]
        [InspectorName("Objective/Stance System Prompt")]
        public string SystemPrompt =
            "You are creating initial actor-logic controls for a room character. " +
            "Given the character summary, derive one concise current objective and one concise current stance. " +
            "The objective should describe what the room is trying to do now. " +
            "The stance should describe the room's behavioral attitude in one to three words. " +
            "Return JSON only with exactly this schema: {\"objective\":\"...\",\"stance\":\"...\"}.";

        [Header("Agent Produced")]
        [TextArea(2, 8)]
        [InspectorName("Current Character Summary")]
        public string CurrentCharacterSummary;

        [TextArea(2, 6)]
        [InspectorName("Generated Objective")]
        public string GeneratedObjective;

        [TextArea(1, 3)]
        [InspectorName("Generated Stance")]
        public string GeneratedStance;

        [TextArea(2, 6)]
        [InspectorName("Last Status")]
        public string LastStatus;

        [TextArea(4, 16)]
        [InspectorName("Prompt Inspection Text")]
        public string PromptInspectionText;

        [TextArea(1, 3)]
        [InspectorName("Generation Status")]
        public string GenerationStatus;

        private bool _isGenerating;
        private string _lastGeneratedSummary;

        private string SelectedModelId => Model switch
        {
            OpenAIModelPreset.Gpt54 => "gpt-5.4",
            OpenAIModelPreset.Gpt54Mini => "gpt-5.4-mini",
            OpenAIModelPreset.Gpt41 => "gpt-4.1",
            OpenAIModelPreset.Gpt41Mini => "gpt-4.1-mini",
            OpenAIModelPreset.Gpt4oMini => "gpt-4o-mini",
            _ => "gpt-4o-mini"
        };

        [Serializable]
        private sealed class ObjectiveStanceResponse
        {
            public string objective;
            public string stance;
        }

        [ContextMenu("Objective/Stance Bootstrapper/Generate From Interview Summary")]
        public async void GenerateFromInterviewSummary()
        {
            await PullSummaryAndGenerateAsync();
        }

        [ContextMenu("Objective/Stance Bootstrapper/Pull Summary + Generate (One Pass)")]
        public async void PullSummaryAndGenerateNow()
        {
            await PullSummaryAndGenerateAsync();
        }

        public async Task<bool> PullSummaryAndGenerateAsync()
        {
            if (InterviewController == null)
            {
                SetStatus("AaltoInterviewController is not assigned.");
                return false;
            }

            var summary = (InterviewController.LastSummary ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                SetStatus("Interview summary is empty. Generate/approve summary first.");
                return false;
            }

            CurrentCharacterSummary = summary;
            return await GenerateFromCurrentSummaryAsync();
        }

        [ContextMenu("Objective/Stance Bootstrapper/Generate From Current Summary")]
        public async void GenerateFromCurrentSummary()
        {
            await GenerateFromCurrentSummaryAsync();
        }

        public void SetSummaryFromInterview(string summary, bool autoGenerate)
        {
            var normalized = (summary ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            CurrentCharacterSummary = normalized;

            if (autoGenerate && AutoGenerateOnSummaryUpdate)
                _ = GenerateFromCurrentSummaryAsync();
        }

        public bool TryGetContext(out string summary, out string objective, out string stance)
        {
            summary = (CurrentCharacterSummary ?? string.Empty).Trim();
            objective = (GeneratedObjective ?? string.Empty).Trim();
            stance = (GeneratedStance ?? string.Empty).Trim();

            return !string.IsNullOrWhiteSpace(summary)
                && !string.IsNullOrWhiteSpace(objective)
                && !string.IsNullOrWhiteSpace(stance);
        }

        public async Task<bool> GenerateFromCurrentSummaryAsync()
        {
            try
            {
                if (_isGenerating)
                {
                    SetStatus("Generation already running. Skipping duplicate request.");
                    return false;
                }

                if (OpenAI == null)
                {
                    SetStatus("OpenAIClient is not assigned.");
                    return false;
                }

                var summary = (CurrentCharacterSummary ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(summary))
                {
                    SetStatus("Current character summary is empty.");
                    return false;
                }

                var alreadyGenerated =
                    string.Equals(_lastGeneratedSummary, summary, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(GeneratedObjective) &&
                    !string.IsNullOrWhiteSpace(GeneratedStance);

                if (alreadyGenerated)
                {
                    SetStatus("Summary unchanged; objective and stance already generated.");
                    return true;
                }

                _isGenerating = true;
                GenerationStatus = "Generating...";

                var userPrompt =
                    "character_summary:\n" + summary + "\n\n" +
                    "Return JSON only: {\"objective\":\"...\",\"stance\":\"...\"}.";

                var messages = new List<OpenAIClient.Msg>
                {
                    new OpenAIClient.Msg("system", SystemPrompt ?? string.Empty),
                    new OpenAIClient.Msg("user", userPrompt)
                };

                SetStatus("Generating initial objective and stance...");
                var raw = await OpenAI.ChatCompletionsJsonAsync(
                    messages,
                    model: SelectedModelId,
                    contextTag: "Aalto:BootstrapObjectiveStance");

                if (!TryParse(raw, out var objective, out var stance, out var error))
                {
                    SetStatus("Parse failed: " + error);
                    PromptInspectionText = BuildInspection(SystemPrompt, userPrompt, raw);
                    return false;
                }

                GeneratedObjective = objective;
                GeneratedStance = stance;
                _lastGeneratedSummary = summary;

                PromptInspectionText = BuildInspection(SystemPrompt, userPrompt, raw);
                SetStatus("Generated objective + stance from summary.");
                GenerationStatus = "Ready";
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message);
                GenerationStatus = "Error";
                return false;
            }
            finally
            {
                _isGenerating = false;
            }
        }

        private static bool TryParse(string raw, out string objective, out string stance, out string error)
        {
            objective = null;
            stance = null;
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "Empty model response.";
                return false;
            }

            var json = ExtractJsonObject(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "Could not locate JSON object in response.";
                return false;
            }

            try
            {
                var parsed = JsonUtility.FromJson<ObjectiveStanceResponse>(json);
                if (parsed == null)
                {
                    error = "JSON parse returned null.";
                    return false;
                }

                objective = (parsed.objective ?? string.Empty).Trim();
                stance = (parsed.stance ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(objective) || string.IsNullOrWhiteSpace(stance))
                {
                    error = "Missing required fields: objective and/or stance.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "Invalid JSON: " + ex.Message;
                return false;
            }
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

        private static string BuildInspection(string systemPrompt, string userPrompt, string rawResponse)
        {
            return
                "=== SYSTEM PROMPT ===\n" + (systemPrompt ?? string.Empty).Trim() + "\n\n" +
                "=== USER PROMPT ===\n" + (userPrompt ?? string.Empty).Trim() + "\n\n" +
                "=== RAW RESPONSE ===\n" + (rawResponse ?? string.Empty).Trim();
        }

        private void SetStatus(string status)
        {
            LastStatus = status ?? string.Empty;
            Debug.Log("[AaltoObjectiveStanceBootstrapper] " + LastStatus);
        }
    }
}
