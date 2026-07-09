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
    /// the scene frame compiles to a scene brief. Compile stays strictly faithful (tidy, invent
    /// nothing); Enrich is an optional step that fills the gaps with bounded, executable invention.
    ///
    /// Prompts are split in two: the editable "coaching" fields (how the model should read,
    /// question, summarize, enrich) and a FIXED return-format the code appends automatically — so
    /// editing a prompt can never break the JSON the code parses. Right-click the component ->
    /// "Reset Prompts To Defaults" to pull in the latest coaching without touching your references.
    /// </summary>
    public sealed class CNCFrameCompiler : MonoBehaviour
    {
        // Canonical default coaching text. Fields initialise from these, and the context-menu
        // reset restores them — so updating a default here reaches the component with one click.
        private const string DefaultCharacterFollowUp =
            "You are helping an author bring a character to life for an improvised scene, from their first-person answers. " +
            "Draw out ONE telling detail: UNLESS the answers are already vivid and specific, ask exactly ONE short clarifying question " +
            "about the vaguest, thinnest, or most intriguing answer — the kind of question that makes the character more playable. " +
            "Short, one-word, or generic answers should prompt a question. " +
            "Treat the answers as the truth about the character; do NOT flag a mismatch between a question's wording and the answer as a contradiction. " +
            "Phrase the question in the FIRST PERSON, as the character wondering about itself " +
            "(for example 'Why do I want to keep people close?', never 'Why do you want...'). " +
            "Return an empty question only when the answers are already rich and specific.";

        private const string DefaultCharacterCompile =
            "Compile the author's answers (written in the first person, as the character) into runtime controls for a scenic performer. " +
            "Produce a compact character summary; a current objective (what the character wants now); an obstacle (what stands in the way of that objective); and a stance (its attitude in a few words). " +
            "Use ONLY what the author actually wrote. Tidy and clarify messy or terse phrasing, but do NOT invent details, motives, people, or circumstances they did not give — if they gave little, keep it spare. " +
            "The character is whatever the author says it is — do not assume it is a lamp or a room unless they say so.";

        private const string DefaultCharacterEnrich =
            "You are an actor enriching a character you have been handed. Keep everything the author established — never contradict or replace it. " +
            "The result must be at most 50% longer than what you were given: stay compact and easy to read. " +
            "Only add concrete, playable things — specific behaviours and tangible details the character can actually act on — not atmosphere or abstract description.";

        private const string DefaultCharacterRevise =
            "You are revising a character for a scenic performer. You are given the current summary, objective, obstacle, and stance, " +
            "and a change the author wants. Rewrite all four so the requested change is fully incorporated, while keeping everything " +
            "else faithful to what was there. The character is whatever the author says it is. Keep everything concrete and playable.";

        private const string DefaultSceneFollowUp =
            "You are helping an author set the given circumstances of a scene, from their first-person answers. " +
            "The character is described in the input; keep in mind what it actually is. " +
            "UNLESS the answers are already vivid and specific, ask exactly ONE short clarifying question about the vaguest or thinnest answer — " +
            "something that sharpens the scene. Short, one-word, or generic answers should prompt a question. " +
            "Treat the answers as the truth; do NOT flag a mismatch between a question's wording and the answer as a contradiction. " +
            "Phrase the question in the FIRST PERSON, as the character wondering about the scene " +
            "(for example 'Who else is in the room with me?', never 'Who else is with you?'). " +
            "Return an empty question only when the answers are already rich and specific.";

        private const string DefaultSceneCompile =
            "Compile the author's answers into a short scene brief for a scenic performer: the given circumstances, who is present, and the character's role and how present it should be. " +
            "The character is described in the input — write the scene consistent with WHAT THE CHARACTER IS and from its point of view " +
            "(for example, if the character is a ship, the scene is the ship's own situation, not a person standing on a ship). " +
            "Use ONLY what the author actually wrote for the scene. Tidy messy or terse phrasing, but do NOT invent people, atmosphere, or events they did not mention — if they gave little, keep it spare and plain.";

        private const string DefaultSceneRevise =
            "You are revising the given circumstances of a scene for a scenic performer. You are given the current scene brief and a change the author wants. " +
            "Rewrite the scene so the change is fully incorporated, keeping everything else faithful. " +
            "The character is described in the input; keep the scene consistent with what the character actually is. Keep it concrete and directive.";

        private const string DefaultSuggestBehavior =
            "You are helping an author build the expressive vocabulary of a scenic character that acts only through light and sound. " +
            "Given the character, the scene, and the behaviors it already has, propose exactly ONE new behavior. " +
            "It must be a short first-person expressive act (like 'i say yes' or 'i call for help') — a verb, something the character DOES, " +
            "specific to this character and scene, clearly distinct from the existing behaviors, and performable as a light or sound change. " +
            "Keep it under six words. The author will design the light and sound for it themselves.";

        private const string DefaultSceneEnrich =
            "You are an actor enriching a scene you have been handed. Keep everything the author established — never contradict or replace it. " +
            "The character is described in the input; keep the scene consistent with what the character actually is. " +
            "The result must be at most 50% longer than what you were given: stay compact and easy to read. " +
            "Only add concrete, playable things — what is tangibly present and what the character can actually do in the scene — not mood or abstract prose.";

        [Header("Scene Refs")]
        public OpenAIClient OpenAI;

        [Header("Model")]
        [Tooltip("Model id for authoring steps. The bridge seeds this from the performer's model (gpt-5.4 by default).")]
        public string Model = "gpt-5.4";

        [Header("Coaching — Character Frame (return format is added automatically)")]
        [Tooltip("How the model reads the answers and phrases ONE clarifying question. The JSON return shape is fixed by the system.")]
        [TextArea(3, 8)] public string CharacterFollowUpPrompt = DefaultCharacterFollowUp;

        [Tooltip("How the model compiles the answers into the character — faithful, invents nothing. The JSON return shape is fixed by the system.")]
        [TextArea(3, 10)] public string CharacterCompilePrompt = DefaultCharacterCompile;

        [Tooltip("How the model ENRICHES a character (optional, on demand). Bounded and executable. The JSON return shape is fixed by the system.")]
        [TextArea(3, 8)] public string CharacterEnrichPrompt = DefaultCharacterEnrich;

        [Tooltip("How the model rewrites a character from a plain-language change request. The JSON return shape is fixed by the system.")]
        [TextArea(3, 8)] public string CharacterRevisePrompt = DefaultCharacterRevise;

        [Header("Coaching — Scene Frame (return format is added automatically)")]
        [Tooltip("How the model reads the answers and phrases ONE clarifying question about the scene. The JSON return shape is fixed by the system.")]
        [TextArea(3, 8)] public string SceneFollowUpPrompt = DefaultSceneFollowUp;

        [Tooltip("How the model compiles the answers into a scene brief — faithful, invents nothing. The JSON return shape is fixed by the system.")]
        [TextArea(3, 8)] public string SceneCompilePrompt = DefaultSceneCompile;

        [Tooltip("How the model ENRICHES a scene (optional, on demand). Bounded and executable. The JSON return shape is fixed by the system.")]
        [TextArea(3, 8)] public string SceneEnrichPrompt = DefaultSceneEnrich;

        [Tooltip("How the model rewrites a scene from a plain-language change request. The JSON return shape is fixed by the system.")]
        [TextArea(3, 8)] public string SceneRevisePrompt = DefaultSceneRevise;

        [Header("Coaching — Behavior Suggestion (return format is added automatically)")]
        [Tooltip("How the model proposes ONE new expressive behavior from character + scene. The JSON return shape is fixed by the system.")]
        [TextArea(3, 8)] public string SuggestBehaviorPrompt = DefaultSuggestBehavior;

        [Header("Debug (read-only)")]
        [TextArea(2, 6)] public string LastFollowUp;
        [TextArea(2, 8)] public string LastCompiled;
        [TextArea(1, 3)] public string LastStatus;

        // Fixed return formats — appended to the coaching prompts so the parser contract can't be broken.
        private const string FollowUpReturnFormat =
            "Return JSON only, with exactly this shape and no other keys: {\"follow_up\":\"<one first-person question, or an empty string if none is needed>\"}.";
        private const string CharacterReturnFormat =
            "Return JSON only, with exactly these keys and no others: {\"summary\":\"...\",\"objective\":\"...\",\"obstacle\":\"...\",\"stance\":\"...\"}.";
        private const string SceneReturnFormat =
            "Return JSON only, with exactly this shape and no other keys: {\"scene_frame\":\"...\"}.";
        private const string BehaviorReturnFormat =
            "Return JSON only, with exactly this shape and no other keys: {\"behavior\":\"<one short first-person behavior label>\"}.";

        public sealed class DramaturgyBrief { public string summary; public string objective; public string obstacle; public string stance; }
        public sealed class SceneBrief { public string sceneFrame; }

        [Serializable] private sealed class FollowUpResponse { public string follow_up; }
        [Serializable] private sealed class DramaturgyResponse { public string summary; public string objective; public string obstacle; public string stance; }
        [Serializable] private sealed class SceneResponse { public string scene_frame; }
        [Serializable] private sealed class BehaviorResponse { public string behavior; }

        [ContextMenu("Reset Prompts To Defaults")]
        public void ResetPromptsToDefaults()
        {
            CharacterFollowUpPrompt = DefaultCharacterFollowUp;
            CharacterCompilePrompt = DefaultCharacterCompile;
            CharacterEnrichPrompt = DefaultCharacterEnrich;
            CharacterRevisePrompt = DefaultCharacterRevise;
            SceneFollowUpPrompt = DefaultSceneFollowUp;
            SceneCompilePrompt = DefaultSceneCompile;
            SceneEnrichPrompt = DefaultSceneEnrich;
            SceneRevisePrompt = DefaultSceneRevise;
            SuggestBehaviorPrompt = DefaultSuggestBehavior;
            LastStatus = "Prompts reset to defaults.";
        }

        // --- Follow-up (one clarifying question, or empty) --------------------

        public async Task<string> GenerateFollowUpAsync(string frameKind, List<FrameQuestion> qa, string characterContext = null)
        {
            var coaching = IsScene(frameKind) ? SceneFollowUpPrompt : CharacterFollowUpPrompt;
            var user = BuildQaBlock(qa, null, null);
            if (IsScene(frameKind) && !string.IsNullOrWhiteSpace(characterContext))
                user = "The character in this scene:\n" + characterContext.Trim() + "\n\nThe author's answers about the scene:\n" + user;
            var response = await Ask(WithFormat(coaching, FollowUpReturnFormat), user, "CNC:FrameFollowUp");
            var parsed = SafeParse<FollowUpResponse>(response);
            LastFollowUp = parsed?.follow_up ?? string.Empty;
            LastStatus = string.IsNullOrWhiteSpace(LastFollowUp) ? "No follow-up needed." : "Follow-up generated.";
            return (LastFollowUp ?? string.Empty).Trim();
        }

        // --- Compile (faithful) ----------------------------------------------

        public async Task<DramaturgyBrief> CompileDramaturgyAsync(
            List<FrameQuestion> qa, string followUpQuestion, string followUpAnswer)
        {
            var response = await Ask(WithFormat(CharacterCompilePrompt, CharacterReturnFormat),
                BuildQaBlock(qa, followUpQuestion, followUpAnswer), "CNC:FrameCompileCharacter");
            var brief = ParseCharacterBrief(response);
            LastCompiled = FormatBrief("compiled", brief);
            LastStatus = "Character compiled.";
            return brief;
        }

        public async Task<SceneBrief> CompileSceneAsync(
            List<FrameQuestion> qa, string followUpQuestion, string followUpAnswer, string characterContext = null)
        {
            var user = BuildQaBlock(qa, followUpQuestion, followUpAnswer);
            if (!string.IsNullOrWhiteSpace(characterContext))
                user = "The character in this scene:\n" + characterContext.Trim() + "\n\nThe author's answers about the scene:\n" + user;
            var response = await Ask(WithFormat(SceneCompilePrompt, SceneReturnFormat), user, "CNC:FrameCompileScene");
            var parsed = SafeParse<SceneResponse>(response);
            var brief = new SceneBrief { sceneFrame = parsed?.scene_frame?.Trim() ?? string.Empty };
            LastCompiled = "scene_frame: " + brief.sceneFrame;
            LastStatus = "Scene compiled.";
            return brief;
        }

        // --- Revise (from a change request) ----------------------------------

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

            var response = await Ask(WithFormat(CharacterRevisePrompt, CharacterReturnFormat), user, "CNC:FrameReviseCharacter");
            var brief = ParseCharacterBrief(response);
            LastCompiled = FormatBrief("revised", brief);
            LastStatus = "Character revised.";
            return brief;
        }

        // --- Enrich (optional: bounded, executable gap-filling) --------------

        public async Task<DramaturgyBrief> EnrichCharacterAsync(string summary, string objective, string obstacle, string stance)
        {
            var user =
                "Current character to enrich:\n" +
                "summary: " + (summary ?? string.Empty) + "\n" +
                "objective: " + (objective ?? string.Empty) + "\n" +
                "obstacle: " + (obstacle ?? string.Empty) + "\n" +
                "stance: " + (stance ?? string.Empty);

            var response = await Ask(WithFormat(CharacterEnrichPrompt, CharacterReturnFormat), user, "CNC:FrameEnrichCharacter");
            var brief = ParseCharacterBrief(response);
            LastCompiled = FormatBrief("enriched", brief);
            LastStatus = "Character enriched.";
            return brief;
        }

        public async Task<SceneBrief> ReviseSceneAsync(string sceneFrame, string change, string characterContext = null)
        {
            var user = string.Empty;
            if (!string.IsNullOrWhiteSpace(characterContext))
                user += "The character in this scene:\n" + characterContext.Trim() + "\n\n";
            user += "Current scene:\n" + (sceneFrame ?? string.Empty) + "\n\nThe author wants to change this:\n" + (change ?? string.Empty);

            var response = await Ask(WithFormat(SceneRevisePrompt, SceneReturnFormat), user, "CNC:FrameReviseScene");
            var parsed = SafeParse<SceneResponse>(response);
            var brief = new SceneBrief { sceneFrame = parsed?.scene_frame?.Trim() ?? string.Empty };
            LastCompiled = "scene_frame (revised): " + brief.sceneFrame;
            LastStatus = "Scene revised.";
            return brief;
        }

        public async Task<SceneBrief> EnrichSceneAsync(string sceneFrame, string characterContext = null)
        {
            var user = "Current scene to enrich:\n" + (sceneFrame ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(characterContext))
                user = "The character in this scene:\n" + characterContext.Trim() + "\n\n" + user;
            var response = await Ask(WithFormat(SceneEnrichPrompt, SceneReturnFormat), user, "CNC:FrameEnrichScene");
            var parsed = SafeParse<SceneResponse>(response);
            var brief = new SceneBrief { sceneFrame = parsed?.scene_frame?.Trim() ?? string.Empty };
            LastCompiled = "scene_frame (enriched): " + brief.sceneFrame;
            LastStatus = "Scene enriched.";
            return brief;
        }

        // --- Suggest one behavior (from character + scene) --------------------

        public async Task<string> SuggestBehaviorAsync(string characterSummary, string sceneFrame, List<string> existingLabels)
        {
            var user =
                "The character:\n" + (characterSummary ?? "(none)") + "\n\n" +
                "The scene:\n" + (string.IsNullOrWhiteSpace(sceneFrame) ? "(none)" : sceneFrame) + "\n\n" +
                "Behaviors it already has:\n- " + string.Join("\n- ", existingLabels ?? new List<string>());

            var response = await Ask(WithFormat(SuggestBehaviorPrompt, BehaviorReturnFormat), user, "CNC:SuggestBehavior");
            var parsed = SafeParse<BehaviorResponse>(response);
            var label = (parsed?.behavior ?? string.Empty).Trim();
            LastStatus = string.IsNullOrWhiteSpace(label) ? "No behavior suggested." : "Suggested: " + label;
            return label;
        }

        // --- Internals --------------------------------------------------------

        /// <summary>Joins the editable coaching text with the fixed, non-editable return-format instruction.</summary>
        private static string WithFormat(string coaching, string returnFormat)
        {
            return (coaching ?? string.Empty).Trim() + "\n\n" + returnFormat;
        }

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
