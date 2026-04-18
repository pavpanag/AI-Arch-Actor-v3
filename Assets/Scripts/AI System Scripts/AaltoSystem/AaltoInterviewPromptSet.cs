using System.Text;
using UnityEngine;

namespace AaltoSystemV3
{
    [CreateAssetMenu(menuName = "Aalto System/Interview Prompt Set", fileName = "AaltoInterviewPromptSet")]
    public sealed class AaltoInterviewPromptSet : ScriptableObject
    {
        [Header("Clarification Prompt")]
        [TextArea(3, 8)]
        [InspectorName("Clarification Role Intro (Architect Input)")]
        public string ClarificationRoleIntro =
            "You generate concise clarification questions for a character interview.";

        [TextArea(5, 10)]
        [InspectorName("Clarification Schema Block (Architect Input)")]
        public string ClarificationSchemaBlock =
            "Return JSON only with exactly this schema:\n" +
            "{\n" +
            "  \"clarification_questions\": [\"...\", \"...\"]\n" +
            "}";

        [TextArea(4, 10)]
        [InspectorName("Clarification Rules Block (Architect Input)")]
        public string ClarificationRulesBlock =
            "Rules:\n" +
            "- Return exactly {clarification_count} questions.\n" +
            "- Questions must target ambiguity in background, motivation, obstacle, and circumstances.\n" +
            "- Keep each question specific and answerable in 1-2 sentences.\n" +
            "- No extra keys.";

        [TextArea(5, 12)]
        [InspectorName("Clarification User Template (User Input)")]
        public string ClarificationUserTemplate =
            "Draft exactly {clarification_count} clarification questions for this interview input:\n\n" +
            "background:\n{backstory}\n\n" +
            "motivation:\n{motive}\n\n" +
            "obstacle:\n{obstacle}\n\n" +
            "circumstances:\n{circumstances}";

        [Header("Summary Prompt")]
        [TextArea(3, 8)]
        [InspectorName("Summary Role Intro (Architect Input)")]
        public string SummaryRoleIntro =
            "You are creating a compact runtime character summary for a scenic rehearsal agent.";

        [TextArea(5, 10)]
        [InspectorName("Summary Schema Block (Architect Input)")]
        public string SummarySchemaBlock =
            "Return JSON only with exactly this schema:\n" +
            "{\n" +
            "  \"character_summary\": \"...\"\n" +
            "}";

        [TextArea(4, 10)]
        [InspectorName("Summary Rules Block (Architect Input)")]
        public string SummaryRulesBlock =
            "Rules:\n" +
            "- Use background, motivation, obstacle, circumstances, and clarification answers (when provided) as source material.\n" +
            "- Keep summary concrete and playable.\n" +
            "- Keep length between {min_summary_chars} and {max_summary_chars} characters.\n" +
            "- Avoid abstraction and generic assistant language.";

        [TextArea(5, 12)]
        [InspectorName("Summary User Template (User Input)")]
        public string SummaryUserTemplate =
            "Create one runtime-ready character summary from this material:\n\n" +
            "background:\n{backstory}\n\n" +
            "motivation:\n{motive}\n\n" +
            "obstacle:\n{obstacle}\n\n" +
            "circumstances:\n{circumstances}\n\n" +
            "clarification_answers:\n{clarification_answers}";

        [Header("Revision Prompt")]
        [TextArea(3, 8)]
        [InspectorName("Revision Role Intro (Architect Input)")]
        public string RevisionRoleIntro =
            "You revise an approved-character draft for scenic rehearsal.";

        [TextArea(5, 10)]
        [InspectorName("Revision Schema Block (Architect Input)")]
        public string RevisionSchemaBlock =
            "Return JSON only with exactly this schema:\n" +
            "{\n" +
            "  \"character_summary\": \"...\"\n" +
            "}";

        [TextArea(4, 10)]
        [InspectorName("Revision Rules Block (Architect Input)")]
        public string RevisionRulesBlock =
            "Rules:\n" +
            "- Preserve the same core character identity unless notes explicitly change it.\n" +
            "- Apply revision notes precisely and keep the result playable.\n" +
            "- Keep length between {min_summary_chars} and {max_summary_chars} characters.\n" +
            "- No extra keys.";

        [TextArea(4, 10)]
        [InspectorName("Revision User Template (User Input)")]
        public string RevisionUserTemplate =
            "Revise this character summary:\n{current_summary}\n\nUsing these revision notes:\n{revision_notes}";

        public string BuildClarificationSystemPrompt(int clarificationCount)
        {
            return ComposeBlocks(
                ReplaceCommonTokens(ClarificationRoleIntro, clarificationCount),
                ReplaceCommonTokens(ClarificationSchemaBlock, clarificationCount),
                ReplaceCommonTokens(ClarificationRulesBlock, clarificationCount));
        }

        public string BuildClarificationUserPrompt(string backstory, string motive, string obstacle, string circumstances, int clarificationCount)
        {
            return ReplaceInterviewTokens(
                ReplaceCommonTokens(ClarificationUserTemplate, clarificationCount),
                backstory, motive, obstacle, circumstances, null, null, null);
        }

        public string BuildSummarySystemPrompt(int minSummaryChars, int maxSummaryChars)
        {
            return ComposeBlocks(
                ReplaceSummaryTokens(SummaryRoleIntro, minSummaryChars, maxSummaryChars),
                ReplaceSummaryTokens(SummarySchemaBlock, minSummaryChars, maxSummaryChars),
                ReplaceSummaryTokens(SummaryRulesBlock, minSummaryChars, maxSummaryChars));
        }

        public string BuildSummaryUserPrompt(string backstory, string motive, string obstacle, string circumstances, string clarificationAnswers)
        {
            return ReplaceInterviewTokens(
                SummaryUserTemplate,
                backstory, motive, obstacle, circumstances,
                clarificationAnswers,
                null,
                null);
        }

        public string BuildRevisionSystemPrompt(int minSummaryChars, int maxSummaryChars)
        {
            return ComposeBlocks(
                ReplaceSummaryTokens(RevisionRoleIntro, minSummaryChars, maxSummaryChars),
                ReplaceSummaryTokens(RevisionSchemaBlock, minSummaryChars, maxSummaryChars),
                ReplaceSummaryTokens(RevisionRulesBlock, minSummaryChars, maxSummaryChars));
        }

        public string BuildRevisionUserPrompt(string currentSummary, string revisionNotes)
        {
            return ReplaceInterviewTokens(
                RevisionUserTemplate,
                null, null, null, null,
                null,
                currentSummary,
                revisionNotes);
        }

        public string BuildResolvedClarificationSystemPrompt(int clarificationCount)
        {
            return BuildClarificationSystemPrompt(clarificationCount);
        }

        public string BuildResolvedClarificationUserPrompt(string backstory, string motive, string obstacle, string circumstances, int clarificationCount)
        {
            return BuildClarificationUserPrompt(backstory, motive, obstacle, circumstances, clarificationCount);
        }

        public string BuildResolvedSummarySystemPrompt(int minSummaryChars, int maxSummaryChars)
        {
            return BuildSummarySystemPrompt(minSummaryChars, maxSummaryChars);
        }

        public string BuildResolvedSummaryUserPrompt(string backstory, string motive, string obstacle, string circumstances, string clarificationAnswers)
        {
            return BuildSummaryUserPrompt(backstory, motive, obstacle, circumstances, clarificationAnswers);
        }

        public string BuildResolvedRevisionSystemPrompt(int minSummaryChars, int maxSummaryChars)
        {
            return BuildRevisionSystemPrompt(minSummaryChars, maxSummaryChars);
        }

        public string BuildResolvedRevisionUserPrompt(string currentSummary, string revisionNotes)
        {
            return BuildRevisionUserPrompt(currentSummary, revisionNotes);
        }

        private static string ReplaceCommonTokens(string input, int clarificationCount)
        {
            return (input ?? string.Empty)
                .Replace("{clarification_count}", Mathf.Max(1, clarificationCount).ToString());
        }

        private static string ReplaceSummaryTokens(string input, int minSummaryChars, int maxSummaryChars)
        {
            return (input ?? string.Empty)
                .Replace("{min_summary_chars}", Mathf.Max(1, minSummaryChars).ToString())
                .Replace("{max_summary_chars}", Mathf.Max(1, maxSummaryChars).ToString());
        }

        private static string ComposeBlocks(params string[] blocks)
        {
            var sb = new StringBuilder();
            if (blocks == null) return string.Empty;

            for (int i = 0; i < blocks.Length; i++)
            {
                var block = (blocks[i] ?? string.Empty).Trim();
                if (block.Length == 0) continue;
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(block);
            }

            return sb.ToString().Trim();
        }

        private static string ReplaceInterviewTokens(
            string input,
            string backstory,
            string motive,
            string obstacle,
            string circumstances,
            string clarificationAnswers,
            string currentSummary,
            string revisionNotes)
        {
            var sb = new StringBuilder(input ?? string.Empty);
            sb.Replace("{backstory}", string.IsNullOrWhiteSpace(backstory) ? "(none provided)" : backstory.Trim());
            sb.Replace("{motive}", string.IsNullOrWhiteSpace(motive) ? "(none provided)" : motive.Trim());
            sb.Replace("{obstacle}", string.IsNullOrWhiteSpace(obstacle) ? "(none provided)" : obstacle.Trim());
            sb.Replace("{circumstances}", string.IsNullOrWhiteSpace(circumstances) ? "(none provided)" : circumstances.Trim());
            sb.Replace("{clarification_answers}", string.IsNullOrWhiteSpace(clarificationAnswers) ? "(none provided)" : clarificationAnswers.Trim());
            sb.Replace("{current_summary}", string.IsNullOrWhiteSpace(currentSummary) ? "(none provided)" : currentSummary.Trim());
            sb.Replace("{revision_notes}", string.IsNullOrWhiteSpace(revisionNotes) ? "(none provided)" : revisionNotes.Trim());
            return sb.ToString();
        }
    }
}
