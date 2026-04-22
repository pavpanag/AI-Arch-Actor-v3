using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using AaltoSystemV3;

[CustomEditor(typeof(AaltoInterviewController))]
public sealed class AaltoInterviewControllerEditor : Editor
{
    SerializedProperty openAIProperty;
    SerializedProperty objectiveStanceBootstrapperProperty;

    SerializedProperty backstoryTextProperty;
    SerializedProperty motiveTextProperty;
    SerializedProperty obstacleTextProperty;
    SerializedProperty circumstancesTextProperty;
    SerializedProperty followUpQuestionsTextProperty;
    SerializedProperty followUpAnswersTextProperty;

    SerializedProperty lastSummaryProperty;
    SerializedProperty lastStatusProperty;
    SerializedProperty flowStateDisplayProperty;

    SerializedProperty promptSetProperty;
    SerializedProperty requireApprovalBeforeRehearsalProperty;
    SerializedProperty maxFollowUpQuestionsProperty;
    SerializedProperty modelSelectionProperty;
    SerializedProperty minSummaryCharsProperty;
    SerializedProperty maxSummaryCharsProperty;

    private const double InputDebounceSeconds = 0.9;

    private static bool _debounceHooked;
    private static readonly Dictionary<string, PendingEdit> PendingEdits = new Dictionary<string, PendingEdit>(StringComparer.OrdinalIgnoreCase);

    private sealed class PendingEdit
    {
        public int instanceId;
        public string fieldKey;
        public string previousValue;
        public string newValue;
        public double lastEditTime;
        public string source;
    }

    void OnEnable()
    {
        openAIProperty = serializedObject.FindProperty("OpenAI");
        objectiveStanceBootstrapperProperty = serializedObject.FindProperty("ObjectiveStanceBootstrapper");

        backstoryTextProperty = serializedObject.FindProperty("BackstoryText");
        motiveTextProperty = serializedObject.FindProperty("MotiveText");
        obstacleTextProperty = serializedObject.FindProperty("ObstacleText");
        circumstancesTextProperty = serializedObject.FindProperty("CircumstancesText");
        followUpQuestionsTextProperty = serializedObject.FindProperty("FollowUpQuestionsText");
        followUpAnswersTextProperty = serializedObject.FindProperty("FollowUpAnswersText");

        lastSummaryProperty = serializedObject.FindProperty("LastSummary");
        lastStatusProperty = serializedObject.FindProperty("LastStatus");
        flowStateDisplayProperty = serializedObject.FindProperty("FlowStateDisplay");

        promptSetProperty = serializedObject.FindProperty("PromptSet");
        requireApprovalBeforeRehearsalProperty = serializedObject.FindProperty("RequireApprovalBeforeRehearsal");
        maxFollowUpQuestionsProperty = serializedObject.FindProperty("MaxFollowUpQuestions");
        modelSelectionProperty = serializedObject.FindProperty("ModelSelection");
        minSummaryCharsProperty = serializedObject.FindProperty("MinSummaryChars");
        maxSummaryCharsProperty = serializedObject.FindProperty("MaxSummaryChars");
    }

    public override void OnInspectorGUI()
    {
        EnsureDebounceUpdateHook();
        serializedObject.Update();

        DrawSceneRefs();
        DrawInterviewWorkflow();
        DrawResultSection();
        DrawConfigSection();

        serializedObject.ApplyModifiedProperties();
    }

    void DrawSceneRefs()
    {
        EditorGUILayout.LabelField("Scene Refs", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(openAIProperty);
        EditorGUILayout.PropertyField(objectiveStanceBootstrapperProperty);
    }

    void DrawInterviewWorkflow()
    {
        var controller = (AaltoInterviewController)target;

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Interview Workflow", EditorStyles.boldLabel);

        DrawUserInputTextArea(controller, backstoryTextProperty, "backstory", "Back Story (User Input)");
        DrawUserInputTextArea(controller, motiveTextProperty, "motive", "Motive (User Input)");
        DrawUserInputTextArea(controller, obstacleTextProperty, "obstacle", "Obstacle (User Input)");
        DrawUserInputTextArea(controller, circumstancesTextProperty, "circumstances", "Circumstances (User Input)");

        if (GUILayout.Button("Generate Follow Up Questions"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.GenerateFollowUpQuestions();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }

        EditorGUILayout.PropertyField(followUpQuestionsTextProperty);
        DrawUserInputTextArea(controller, followUpAnswersTextProperty, "followup_answers", "Follow Up Answers (User Input)");

        if (GUILayout.Button("Submit Follow Up Answers And Create Summary"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.SubmitFollowUpAnswersAndCreateSummary();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }
    }

    void DrawResultSection()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(lastSummaryProperty);
        EditorGUILayout.PropertyField(flowStateDisplayProperty);
        EditorGUILayout.PropertyField(lastStatusProperty);
    }

    void DrawConfigSection()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Architect Input", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(promptSetProperty);
        EditorGUILayout.PropertyField(requireApprovalBeforeRehearsalProperty);
        EditorGUILayout.PropertyField(maxFollowUpQuestionsProperty);
        EditorGUILayout.PropertyField(modelSelectionProperty);
        EditorGUILayout.PropertyField(minSummaryCharsProperty);
        EditorGUILayout.PropertyField(maxSummaryCharsProperty);
    }

    private static void EnsureDebounceUpdateHook()
    {
        if (_debounceHooked)
            return;

        _debounceHooked = true;
        EditorApplication.update += FlushDebouncedEdits;
    }

    private static void FlushDebouncedEdits()
    {
        if (PendingEdits.Count == 0)
            return;

        if (!Application.isPlaying)
        {
            PendingEdits.Clear();
            return;
        }

        var now = EditorApplication.timeSinceStartup;
        var ready = new List<string>();
        foreach (var kv in PendingEdits)
        {
            if (now - kv.Value.lastEditTime >= InputDebounceSeconds)
                ready.Add(kv.Key);
        }

        for (int i = 0; i < ready.Count; i++)
        {
            var key = ready[i];
            if (!PendingEdits.TryGetValue(key, out var edit))
                continue;

            PendingEdits.Remove(key);

            var payload =
                "{\"field\":\"" + AaltoLaunchSessionLogger.EscapeJson(edit.fieldKey ?? string.Empty) + "\"," +
                "\"previous\":\"" + AaltoLaunchSessionLogger.EscapeJson(edit.previousValue ?? string.Empty) + "\"," +
                "\"current\":\"" + AaltoLaunchSessionLogger.EscapeJson(edit.newValue ?? string.Empty) + "\"}";

            AaltoLaunchSessionLogger.EmitEvent(edit.source ?? "AaltoInterviewController", "interview.user_input_changed", payload);
        }
    }

    private void DrawUserInputTextArea(AaltoInterviewController controller, SerializedProperty prop, string fieldKey, string label)
    {
        if (controller == null || prop == null)
            return;

        EditorGUILayout.LabelField(label, EditorStyles.boldLabel);

        var previous = prop.stringValue ?? string.Empty;
        EditorGUI.BeginChangeCheck();
        var next = EditorGUILayout.TextArea(previous, GUILayout.MinHeight(44f));
        if (EditorGUI.EndChangeCheck())
        {
            prop.stringValue = next ?? string.Empty;
            QueueDebouncedEdit(controller, fieldKey, previous, prop.stringValue);
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Submit " + fieldKey, GUILayout.Width(160f)))
        {
            serializedObject.ApplyModifiedProperties();
            EmitImmediateSubmit(controller, fieldKey, previous, prop.stringValue ?? string.Empty);
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }
        if (GUILayout.Button("Clear", GUILayout.Width(70f)))
        {
            serializedObject.ApplyModifiedProperties();
            var clearedPrev = prop.stringValue ?? string.Empty;
            prop.stringValue = string.Empty;
            QueueDebouncedEdit(controller, fieldKey, clearedPrev, prop.stringValue);
            EmitImmediateSubmit(controller, fieldKey, clearedPrev, prop.stringValue);
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(6f);
    }

    private static void QueueDebouncedEdit(AaltoInterviewController controller, string fieldKey, string previous, string current)
    {
        if (!Application.isPlaying)
            return;

        var id = controller != null ? controller.GetInstanceID() : 0;
        var key = id.ToString() + "::" + (fieldKey ?? string.Empty);
        var now = EditorApplication.timeSinceStartup;

        if (!PendingEdits.TryGetValue(key, out var pending))
        {
            pending = new PendingEdit
            {
                instanceId = id,
                fieldKey = fieldKey ?? string.Empty,
                previousValue = previous ?? string.Empty,
                newValue = current ?? string.Empty,
                lastEditTime = now,
                source = "AaltoInterviewController"
            };
            PendingEdits[key] = pending;
            return;
        }

        // Keep the earliest previous value until we flush, so we preserve the whole edit burst.
        pending.newValue = current ?? string.Empty;
        pending.lastEditTime = now;
    }

    private static void EmitImmediateSubmit(AaltoInterviewController controller, string fieldKey, string previous, string current)
    {
        if (!Application.isPlaying)
            return;

        var payload =
            "{\"field\":\"" + AaltoLaunchSessionLogger.EscapeJson(fieldKey ?? string.Empty) + "\"," +
            "\"previous\":\"" + AaltoLaunchSessionLogger.EscapeJson(previous ?? string.Empty) + "\"," +
            "\"current\":\"" + AaltoLaunchSessionLogger.EscapeJson(current ?? string.Empty) + "\"}";

        AaltoLaunchSessionLogger.EmitEvent("AaltoInterviewController", "interview.user_input_submitted", payload);
    }
}
