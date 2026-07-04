using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using AaltoSystemV3;

namespace CNCDemo
{
    /// <summary>
    /// Turns a frame's question/answer list into a compact, playable brief, using the existing
    /// OpenAIClient. The dramaturgical frame compiles to summary + objective + obstacle + stance;
    /// the scene frame compiles to a scene brief. Each frame can first ask ONE follow-up question,
    /// phrased in the character's own first-person voice. The character can also be revised after
    /// the fact from a plain-language change request.
    ///
    /// Every prompt is an editable field, so nothing the model is told is hidden. New file only.
    /// </summary>
    public sealed class CNCFrameCompiler : MonoBehaviour
    {
        [Header("Scene Refs")]
        public OpenAIClient OpenAI;

        [Header("Model")]
        [Tooltip("Model id for authoring steps. Cheaper is fine here (e.g. gpt-4o-mini); the performance model can differ.")]
        public string Model = "gpt-4o-mini";

        [Header("Prompts — Character Frame")]
        [Tooltip("Decides whether to ask one clarifying question about the character. MUST keep the first-person instruction and end by asking for JSON {\"follow_up\":\"...\"}.")]
        [TextArea(3, 8)]
        public string CharacterFollowUpPrompt =
            "You are helping an author define the inner life of a character for an improvised scene. " +
            "You are given their answers to a few questions, written in the first person, as the character. " +
            "If something important is unclear, thin, or contradictory, ask exactly ONE short, specific clarifying question. " +
            "Phrase the question in the FIRST PERSON, as the character speaking to itself " +
            "(for example 'Why do I want to keep people close?', never 'Why do you want...'). " +
            "If the answers are already clear enough to work with, return an empty string. " +
            "Return JSON only: {\"follow_up\":\"<one first-person question, or empty>\"}.";

        [Tooltip("Compiles the character answers into summary + objective + obstacle + stance. MUST end by asking for JSON with those four keys.")]
        [TextArea(3, 12)]
        public string CharacterCompilePrompt =
            "Compile the author's answers (written in the first person, as the character) into runtime controls for a scenic performer. " +
            "Produce four things: a compact character summary; one current objective (what the character wants now); " +
            "one obstacle (what stands in the way of that objective); and one stance (its attitude in a few words). " +
            "The character is whatever the author says it is — do not assume it is a lamp or a room unless they say so. " +
            "Keep everything concrete and playable. Avoid abstraction and generic assistant language. Stay faithful to what the author wrote. " +
            "Return JSON only: {\"summary\":\"...\",\"objective\":\"...\",\"obstacle\":\"...\",\"stance\":\"...\"}.";

        [Tooltip("Rewrites an existing character from a plain-language change request. MUST end by asking for JSON with the same four keys.")]
        [TextArea(3, 10)]
        public string CharacterRevisePrompt =
            "You are revising a character for a scenic performer. You are given the current summary, objective, obstacle, and stance, " +
            "and a change the author wants. Rewrite all four so the requested change is fully incorporated, while keeping everything " +
            "else faithful to what was there. The character is whatever the author says it is. Keep everything concrete and playable. " +
            "Return JSON only: {\"summary\":\"...\",\"objective\":\"...\",\"obstacle\":\"...\",\"stance\":\"...\"}.";

        [Header("Prompts — Scene Frame")]
        [Tooltip("Decides whether to ask one clarifying question about the scene. MUST keep the first-person instruction and end by asking for JSON {\"follow_up\":\"...\"}.")]
        [TextArea(3, 8)]
        public string SceneFollowUpPrompt =
            "You are helping an author define the given circumstances of a scene for an improvised performance. " +
            "You are given their answers to a few questions, written in the first person, as the character in the scene. " +
            "If something important is unclear, thin, or contradictory, ask exactly ONE short, specific clarifying question. " +
            "Phrase the question in the FIRST PERSON, as the character speaking to itself " +
            "(for example 'Who else is in the room with me?', never 'Who else is with you?'). " +
            "If the answers are already clear enough to work with, return an empty string. " +
            "Return JSON only: {\"follow_up\":\"<one first-person question, or empty>\"}.";

        [Tooltip("Compiles the scene answers into a scene brief. MUST end by asking for JSON {\"scene_frame\":\"...\"}.")]
        [TextArea(3, 10)]
        public string SceneCompilePrompt =
            "Compile the author's answers into a short scene brief for a scenic performer. " +
            "State the given circumstances, who is present, and the character's role and how present it should be. " +
            "Keep it concrete and directive; a few sentences. Stay faithful to what the author wrote. " +
            "Return JSON only: {\"scene_frame\":\"...\"}.";

        [Header("Debug (read-only)")]
        [TextArea(2, 6)] public string LastFollowUp;
        [TextArea(2, 8)] public string LastCompiled;
        [TextArea(1, 3)] public string LastStatus;

        public sealed class DramaturgyBrief { public string summary; public string objective; public string obstacle; public string stance; }
        public sealed class SceneBrief { public string sceneFrame; }

        [Serializable] private sealed class FollowUpResponse { public string follow_up; }
        [Serializable] private sealed class DramaturgyResponse { public string summary; public string objective; public string obstacle; public string stance; }
        [Serializable] private sealed class SceneResponse { public string scene_frame; }

        // --- Follow-up (one clarifying question, or empty) --------------------

        /// <summary>Returns one short first-person follow-up question, or empty if the answers are clear enough.</summary>
        public async Task<string> GenerateFollowUpAsync(string frameKind, List<FrameQuestion> qa)
        {
            var system = IsScene(frameKind) ? SceneFollowUpPrompt : CharacterFollowUpPrompt;

            var response = await Ask(system, BuildQaBlock(qa, null, null), "CNC:FrameFollowUp");
            var parsed = SafeParse<FollowUpResponse>(response);
            LastFollowUp = parsed?.follow_up ?? string.Empty;
            LastStatus = string.IsNullOrWhiteSpace(LastFollowUp) ? "No follow-up needed." : "Follow-up generated.";
            return (LastFollowUp ?? string.Empty).Trim();
        }

        // --- Compile ----------------------------------------------------------

        public async Task<DramaturgyBrief> CompileDramaturgyAsync(
            List<FrameQuestion> qa, string followUpQuestion, string followUpAnswer)
        {
            var response = await Ask(CharacterCompilePrompt, BuildQaBlock(qa, followUpQuestion, followUpAnswer), "CNC:FrameCompileCharacter");
            var brief = ParseCharacterBrief(response);
            LastCompiled = FormatBrief("compiled", brief);
            LastStatus = "Character compiled.";
            return brief;
        }

        public async Task<DramaturgyBrief> ReviseCharacterAsync(
            string summary, string objective, string obstacle, string stance, string change)
        {
            var user =
                "Current character:\n" +
                "summary: " + (summary ?? string.Empty) + "\n" +
                "objective: " + (objective ?? string.Empty) + "\n" +
                "obstacle: " + (obstacle ?? string.Empty) + "\n" +
                "stance: " + (stance ?? string.Empty) + "\n\n" +
                "The author wants to change this:\n" + (change ?? string.Empty);

            var response = await Ask(CharacterRevisePrompt, user, "CNC:FrameReviseCharacter");
            var brief = ParseCharacterBrief(response);
            LastCompiled = FormatBrief("revised", brief);
            LastStatus = "Character revised.";
            return brief;
        }

        public async Task<SceneBrief> CompileSceneAsync(
            List<FrameQuestion> qa, string followUpQuestion, string followUpAnswer)
        {
            var response = await Ask(SceneCompilePrompt, BuildQaBlock(qa, followUpQuestion, followUpAnswer), "CNC:FrameCompileScene");
            var parsed = SafeParse<SceneResponse>(response);
            var brief = new SceneBrief { sceneFrame = parsed?.scene_frame?.Trim() ?? string.Empty };
            LastCompiled = "scene_frame: " + brief.sceneFrame;
            LastStatus = "Scene compiled.";
            return brief;
        }

        // --- Internals --------------------------------------------------------

        private DramaturgyBrief ParseCharacterBrief(string response)
        {
            var parsed = SafeParse<DramaturgyResponse>(response);
            return new DramaturgyBrief
            {
                summary = parsed?.summary?.Trim() ?? string.Empty,
                objective = parsed?.objective?.Trim() ?? string.Empty,
                obstacle = parsed?.obstacle?.Trim() ?? string.Empty,
                stance = parsed?.stance?.Trim() ?? string.Empty
            };
        }

        private static string FormatBrief(string tag, DramaturgyBrief b)
        {
            return tag + "\nsummary: " + b.summary + "\nobjective: " + b.objective +
                   "\nobstacle: " + b.obstacle + "\nstance: " + b.stance;
        }

        private static bool IsScene(string frameKind) =>
            !string.IsNullOrEmpty(frameKind) && frameKind.IndexOf("scene", StringComparison.OrdinalIgnoreCase) >= 0;

        private async Task<string> Ask(string system, string user, string contextTag)
        {
            if (OpenAI == null)
            {
                LastStatus = "OpenAI client not assigned.";
                return string.Empty;
            }

            var messages = new List<OpenAIClient.Msg>
            {
                new OpenAIClient.Msg("system", system ?? string.Empty),
                new OpenAIClient.Msg("user", user ?? string.Empty)
            };

            try
            {
                return await OpenAI.ChatCompletionsJsonAsync(messages, model: Model, contextTag: contextTag);
            }
            catch (Exception ex)
            {
                LastStatus = "Model call failed: " + ex.Message;
                Debug.LogError("[CNCFrameCompiler] " + LastStatus);
                return string.Empty;
            }
        }

        private static string BuildQaBlock(List<FrameQuestion> qa, string followUpQuestion, string followUpAnswer)
        {
            var sb = new StringBuilder();
            if (qa != null)
            {
                for (int i = 0; i < qa.Count; i++)
                {
                    var item = qa[i];
                    if (item == null) continue;
                    var q = (item.question ?? string.Empty).Trim();
                    var a = (item.answer ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(q) && string.IsNullOrWhiteSpace(a)) continue;
                    sb.Append("Q: ").Append(q).Append('\n');
                    sb.Append("A: ").Append(string.IsNullOrWhiteSpace(a) ? "(blank)" : a).Append("\n\n");
                }
            }

            if (!string.IsNullOrWhiteSpace(followUpQuestion))
            {
                sb.Append("Follow-up Q: ").Append(followUpQuestion.Trim()).Append('\n');
                sb.Append("Follow-up A: ").Append(string.IsNullOrWhiteSpace(followUpAnswer) ? "(blank)" : followUpAnswer.Trim()).Append('\n');
            }

            return sb.ToString().Trim();
        }

        private T SafeParse<T>(string response) where T : class
        {
            var json = ExtractJsonObject(response);
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonUtility.FromJson<T>(json); }
            catch (Exception ex) { LastStatus = "Parse failed: " + ex.Message; return null; }
        }

        /// <summary>Strips code fences and slices the first {...} object. Local copy; no shared code touched.</summary>
        private static string ExtractJsonObject(string raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var nl = text.IndexOf('\n');
                if (nl >= 0) text = text.Substring(nl + 1).Trim();
                if (text.EndsWith("```", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 3).Trim();
            }

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return string.Empty;
            return text.Substring(start, end - start + 1).Trim();
        }
    }
}
