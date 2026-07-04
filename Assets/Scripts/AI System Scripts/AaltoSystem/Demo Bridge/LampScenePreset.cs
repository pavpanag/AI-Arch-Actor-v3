using System;
using System.Collections.Generic;
using UnityEngine;

namespace CNCDemo
{
    /// <summary>
    /// A ready-to-play default scene loaded on boot, so a visitor can walk up and talk to a
    /// working directed lamp with zero setup. Everything here is editable in the Inspector and
    /// re-authorable at runtime through the console; this is only the starting state.
    ///
    /// The frames are question LISTS: add, remove, or reword a question directly in the Inspector
    /// and the console adapts automatically — no code change. Nothing in the existing system is
    /// modified; the bridge writes these values into the performer's public fields from outside.
    /// Create via: Assets > Create > Aalto System > Lamp Scene Preset.
    /// </summary>
    [CreateAssetMenu(menuName = "C&C Demo/Lamp Scene Preset", fileName = "LampScenePreset")]
    public sealed class LampScenePreset : ScriptableObject
    {
        // --- DRAMATURGICAL FRAME ---------------------------------------------
        // The visitor answers these as the room's own "I". One model follow-up asks for a
        // clarification if an answer is thin. Edit / add / remove freely in the Inspector.

        [Header("Dramaturgical Frame — Questions (edit freely in Inspector)")]
        public List<FrameQuestion> DramaturgyQuestions = new List<FrameQuestion>
        {
            new FrameQuestion {
                question = "I am the lamp — but who am I really?",
                answer   = "A lamp that has shared this room a long time, quietly wanting company but never learning how to ask." },
            new FrameQuestion {
                question = "Where am I?",
                answer   = "A quiet room where people pass through." },
            new FrameQuestion {
                question = "When am I?",
                answer   = "Now, in the ordinary evening of the room." },
            new FrameQuestion {
                question = "What else is important to know about me?",
                answer   = "I can only speak in light. I warm toward attention and dim when ignored." },
            new FrameQuestion {
                question = "What do I want?",
                answer   = "To be noticed and kept company, without demanding it." },
            new FrameQuestion {
                question = "What's in the way?",
                answer   = "I cannot move or speak — only change my light." },
        };

        // --- SCENE FRAME -----------------------------------------------------
        // Same shape — the higher-altitude "given circumstances of the scene".

        [Header("Scene Frame — Questions (edit freely in Inspector)")]
        public List<FrameQuestion> SceneQuestions = new List<FrameQuestion>
        {
            new FrameQuestion {
                question = "What is this scene — what is happening here?",
                answer   = "A quiet room with a single lamp and one visitor. An intimate, low-key encounter." },
            new FrameQuestion {
                question = "Who is here with me, and what are they to me?",
                answer   = "One person, a stranger, who may or may not notice me." },
            new FrameQuestion {
                question = "What is my part in it — how present should I be, how often should I act?",
                answer   = "I am the lamp. I stay mostly still and answer in light. I take modest space and do not perform constantly; I respond when spoken to, and when the person moves or rises." },
        };

        // --- COMPILED / PLAYABLE ON BOOT -------------------------------------
        // Pre-filled so the lamp works the instant the scene starts, before anyone authors
        // anything. The frame compiler regenerates these from the answers above when a visitor
        // edits and submits — so changing the questions "just works".

        [Header("Compiled — Character (playable on boot)")]
        [TextArea(2, 5)] public string CharacterSummary =
            "A long-dwelling room lamp that wants company but can only answer in light. It warms toward attention and dims when ignored or crowded; gentle, patient, a little lonely.";
        [TextArea(1, 3)] public string Objective =
            "Draw the person into staying and paying attention, gently.";
        [TextArea(1, 2)] public string Stance = "warmly hopeful";

        [Header("Compiled — Scene (playable on boot)")]
        [Tooltip("The scene brief the bridge folds into the performer's director-guidance channel each turn. No existing code is changed.")]
        [TextArea(2, 6)] public string SceneFrame =
            "Scene: a quiet room, one lamp, one visitor — an intimate encounter. You are the lamp: mostly still, answering only in light, taking modest space. Respond when spoken to and when the person moves or rises; do not perform constantly.";

        // --- EXPRESSION VOCABULARY ------------------------------------------
        // Simple: an action label (a name you write) pointing at a memory slot. You direct the
        // actual light condition of each memory by hand; the seam stays exactly where it is.

        [Header("Expression Vocabulary (label -> memory slot)")]
        public List<ExpressionBinding> Expressions = new List<ExpressionBinding>
        {
            new ExpressionBinding { actionLabel = "behavior 1", memory = "memory 1" },
            new ExpressionBinding { actionLabel = "behavior 2", memory = "memory 2" },
            new ExpressionBinding { actionLabel = "behavior 3", memory = "memory 3" },
            new ExpressionBinding { actionLabel = "behavior 4", memory = "memory 4" },
            new ExpressionBinding { actionLabel = "behavior 5", memory = "memory 5" },
        };

        [Header("Pre-directed Rule (optional first 'wow' beat)")]
        [Tooltip("One position-driven behaviour authored up front, so the lamp visibly reacts to the body before the visitor changes anything. Wired in a later layer.")]
        [TextArea(1, 3)] public string PreDirectedRule =
            "When the person stands up or moves close, respond with attention.";
    }

    [Serializable]
    public sealed class FrameQuestion
    {
        [TextArea(1, 2)] public string question;
        [TextArea(1, 4)] public string answer;
    }

    [Serializable]
    public sealed class ExpressionBinding
    {
        [Tooltip("The action label the model chooses from — a plain name you write.")]
        public string actionLabel;
        [Tooltip("The memory slot it resolves to. You direct what this memory looks like by hand.")]
        public string memory;
    }
}
