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
                question = "Who am I?",
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
                question = "What's in the way of what I want?",
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
        // Each behavior: a label the model can choose, a light condition (colour/brightness/
        // pulse), and an optional sound. Slot i maps to "memory i+1" / "sound i+1", so the
        // label -> trigger seam is unchanged and the Python rig stays a drop-in alternative.
        // "i say yes" and "i say no" are the fixed core; the third is meant to be suggested
        // from the character and scene.

        [Header("Expression Vocabulary (behaviors)")]
        public List<CNCBehaviorSpec> Behaviors = new List<CNCBehaviorSpec>
        {
            new CNCBehaviorSpec { label = "i say yes", colorHex = "#FFC073", brightness = 1f,   pulse = false, pulseSeconds = 2f },
            new CNCBehaviorSpec { label = "i say no",  colorHex = "#4A6DE5", brightness = 0.3f, pulse = false, pulseSeconds = 2f },
            new CNCBehaviorSpec { label = "",          colorHex = "#FFFFFF", brightness = 0.6f, pulse = true,  pulseSeconds = 3f },
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

}
