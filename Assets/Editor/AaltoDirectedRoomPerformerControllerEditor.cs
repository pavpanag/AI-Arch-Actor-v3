using UnityEditor;
using UnityEngine;
using AaltoSystemV3;

[CustomEditor(typeof(AaltoDirectedRoomPerformerController))]
public sealed class AaltoDirectedRoomPerformerControllerEditor : Editor
{
    SerializedProperty openAIProperty;
    SerializedProperty actionMemoryRegistryProperty;
    SerializedProperty objectiveStanceBootstrapperProperty;

    SerializedProperty inspectorActorInputProperty;
    SerializedProperty inspectorInputStatusProperty;
    SerializedProperty acceptExternalSpeechInputProperty;
    SerializedProperty autoSubmitExternalSpeechProperty;
    SerializedProperty lastExternalSpeechTextProperty;
    SerializedProperty externalSpeechStatusProperty;
    SerializedProperty directorGuidanceInputProperty;
    SerializedProperty directorGuidanceInputStatusProperty;
    SerializedProperty directorGuidanceInputsProperty;

    SerializedProperty selectedActionTextProperty;
    SerializedProperty actionJustificationTextProperty;

    SerializedProperty pullContextFromBootstrapperProperty;
    SerializedProperty currentCharacterSummaryProperty;
    SerializedProperty currentObjectiveProperty;
    SerializedProperty currentStanceProperty;
    SerializedProperty directorGuidanceProperty;

    SerializedProperty generalInstructionsProperty;
    SerializedProperty recentHistoryLimitProperty;
    SerializedProperty modelProperty;
    SerializedProperty applyActionThroughRegistryProperty;
    SerializedProperty usePulledActionLabelsSnapshotProperty;
    SerializedProperty pulledActionLabelsSnapshotProperty;
    SerializedProperty actionPullStatusProperty;
    SerializedProperty enforceRegistryActionLabelsProperty;
    SerializedProperty fallbackActionLabelProperty;

    SerializedProperty lastRuntimePromptProperty;
    SerializedProperty lastRawModelResponseProperty;
    SerializedProperty lastStatusProperty;
    SerializedProperty promptInspectionTextProperty;
    SerializedProperty contextSyncStatusProperty;

    void OnEnable()
    {
        openAIProperty = serializedObject.FindProperty("OpenAI");
        actionMemoryRegistryProperty = serializedObject.FindProperty("ActionMemoryRegistry");
        objectiveStanceBootstrapperProperty = serializedObject.FindProperty("ObjectiveStanceBootstrapper");

        inspectorActorInputProperty = serializedObject.FindProperty("InspectorActorInput");
        inspectorInputStatusProperty = serializedObject.FindProperty("InspectorInputStatus");
        acceptExternalSpeechInputProperty = serializedObject.FindProperty("AcceptExternalSpeechInput");
        autoSubmitExternalSpeechProperty = serializedObject.FindProperty("AutoSubmitExternalSpeech");
        lastExternalSpeechTextProperty = serializedObject.FindProperty("LastExternalSpeechText");
        externalSpeechStatusProperty = serializedObject.FindProperty("ExternalSpeechStatus");
        directorGuidanceInputProperty = serializedObject.FindProperty("DirectorGuidanceInput");
        directorGuidanceInputStatusProperty = serializedObject.FindProperty("DirectorGuidanceInputStatus");
        directorGuidanceInputsProperty = serializedObject.FindProperty("DirectorGuidanceInputs");

        selectedActionTextProperty = serializedObject.FindProperty("SelectedActionText");
        actionJustificationTextProperty = serializedObject.FindProperty("ActionJustificationText");

        pullContextFromBootstrapperProperty = serializedObject.FindProperty("PullContextFromBootstrapper");
        currentCharacterSummaryProperty = serializedObject.FindProperty("CurrentCharacterSummary");
        currentObjectiveProperty = serializedObject.FindProperty("CurrentObjective");
        currentStanceProperty = serializedObject.FindProperty("CurrentStance");
        directorGuidanceProperty = serializedObject.FindProperty("DirectorGuidance");

        generalInstructionsProperty = serializedObject.FindProperty("GeneralInstructions");
        recentHistoryLimitProperty = serializedObject.FindProperty("RecentHistoryLimit");
        modelProperty = serializedObject.FindProperty("Model");
        applyActionThroughRegistryProperty = serializedObject.FindProperty("ApplyActionThroughRegistry");
        usePulledActionLabelsSnapshotProperty = serializedObject.FindProperty("UsePulledActionLabelsSnapshot");
        pulledActionLabelsSnapshotProperty = serializedObject.FindProperty("PulledActionLabelsSnapshot");
        actionPullStatusProperty = serializedObject.FindProperty("ActionPullStatus");
        enforceRegistryActionLabelsProperty = serializedObject.FindProperty("EnforceRegistryActionLabels");
        fallbackActionLabelProperty = serializedObject.FindProperty("FallbackActionLabel");

        lastRuntimePromptProperty = serializedObject.FindProperty("LastRuntimePrompt");
        lastRawModelResponseProperty = serializedObject.FindProperty("LastRawModelResponse");
        lastStatusProperty = serializedObject.FindProperty("LastStatus");
        promptInspectionTextProperty = serializedObject.FindProperty("PromptInspectionText");
        contextSyncStatusProperty = serializedObject.FindProperty("ContextSyncStatus");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawQuickActionsSection();
        DrawSceneRefsSection();
        DrawLiveInputsSection();
        DrawRuntimeContextSection();
        DrawConfigSection();
        DrawDebugSection();

        serializedObject.ApplyModifiedProperties();
    }

    void DrawQuickActionsSection()
    {
        var controller = (AaltoDirectedRoomPerformerController)target;

        EditorGUILayout.LabelField("Quick Actions", EditorStyles.boldLabel);
        if (GUILayout.Button("Pull Context From Bootstrapper Now"))
        {
            controller.PullContextFromBootstrapperNow();
            EditorUtility.SetDirty(controller);
        }

        if (GUILayout.Button("Pull Action Labels From Registry Now"))
        {
            controller.PullActionsFromRegistryNow();
            EditorUtility.SetDirty(controller);
        }

        EditorGUILayout.Space(6f);
    }

    void DrawSceneRefsSection()
    {
        EditorGUILayout.LabelField("Scene Refs", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(openAIProperty);
        EditorGUILayout.PropertyField(actionMemoryRegistryProperty);
        EditorGUILayout.PropertyField(objectiveStanceBootstrapperProperty);
    }

    void DrawLiveInputsSection()
    {
        var controller = (AaltoDirectedRoomPerformerController)target;

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Live Inputs", EditorStyles.boldLabel);

        EditorGUILayout.PropertyField(inspectorActorInputProperty);

        if (GUILayout.Button("Submit Simulated Actor Input"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.SubmitTurnFromInspectorInput();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }

        EditorGUILayout.PropertyField(inspectorInputStatusProperty);

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Spoken Input", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(acceptExternalSpeechInputProperty);
        EditorGUILayout.PropertyField(autoSubmitExternalSpeechProperty);
        EditorGUILayout.PropertyField(lastExternalSpeechTextProperty);
        EditorGUILayout.PropertyField(externalSpeechStatusProperty);

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Room Response", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(selectedActionTextProperty);
        EditorGUILayout.PropertyField(actionJustificationTextProperty);

        EditorGUILayout.Space(6f);
        EditorGUILayout.PropertyField(directorGuidanceInputProperty);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Submit Director Guidance"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.SubmitDirectorGuidanceInput();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }

        if (GUILayout.Button("Clear All Guidances"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.ClearDirectorGuidanceInputs();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.PropertyField(directorGuidanceInputStatusProperty);

        EditorGUILayout.LabelField("Director Guidance Inputs", EditorStyles.boldLabel);
        for (int i = 0; i < directorGuidanceInputsProperty.arraySize; i++)
        {
            var entry = directorGuidanceInputsProperty.GetArrayElementAtIndex(i);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(entry, GUIContent.none);
            if (GUILayout.Button("Remove", GUILayout.Width(72f)))
            {
                serializedObject.ApplyModifiedProperties();
                controller.RemoveDirectorGuidanceAt(i);
                EditorUtility.SetDirty(controller);
                serializedObject.Update();
                break;
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    void DrawRuntimeContextSection()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Runtime Context", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(pullContextFromBootstrapperProperty);
        EditorGUILayout.PropertyField(currentCharacterSummaryProperty);
        EditorGUILayout.PropertyField(currentObjectiveProperty);
        EditorGUILayout.PropertyField(currentStanceProperty);
        EditorGUILayout.PropertyField(directorGuidanceProperty);
    }

    void DrawConfigSection()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Prompt + Config", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(generalInstructionsProperty);
        EditorGUILayout.PropertyField(recentHistoryLimitProperty);
        EditorGUILayout.PropertyField(modelProperty);
        EditorGUILayout.PropertyField(applyActionThroughRegistryProperty);
        EditorGUILayout.PropertyField(usePulledActionLabelsSnapshotProperty);
        EditorGUILayout.PropertyField(pulledActionLabelsSnapshotProperty);
        EditorGUILayout.PropertyField(enforceRegistryActionLabelsProperty);
        EditorGUILayout.PropertyField(fallbackActionLabelProperty);
    }

    void DrawDebugSection()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(lastStatusProperty);
        EditorGUILayout.PropertyField(actionPullStatusProperty);
        EditorGUILayout.PropertyField(contextSyncStatusProperty);
        EditorGUILayout.PropertyField(lastRuntimePromptProperty);
        EditorGUILayout.PropertyField(lastRawModelResponseProperty);
        EditorGUILayout.PropertyField(promptInspectionTextProperty);

        var controller = (AaltoDirectedRoomPerformerController)target;
        EditorGUILayout.Space(4f);
        if (GUILayout.Button("Clear Runtime History"))
        {
            controller.ClearRuntimeHistory();
            EditorUtility.SetDirty(controller);
        }

        if (GUILayout.Button("Refresh Prompt Inspection"))
        {
            controller.RefreshPromptInspectionContextMenu();
            EditorUtility.SetDirty(controller);
        }
    }
}
