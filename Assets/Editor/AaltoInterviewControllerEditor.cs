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

        EditorGUILayout.PropertyField(backstoryTextProperty);
        EditorGUILayout.PropertyField(motiveTextProperty);
        EditorGUILayout.PropertyField(obstacleTextProperty);
        EditorGUILayout.PropertyField(circumstancesTextProperty);

        if (GUILayout.Button("Generate Follow Up Questions"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.GenerateFollowUpQuestions();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }

        EditorGUILayout.PropertyField(followUpQuestionsTextProperty);
        EditorGUILayout.PropertyField(followUpAnswersTextProperty);

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
}
