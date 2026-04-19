using UnityEditor;
using UnityEngine;
using AaltoSystemV3;

[CustomEditor(typeof(AaltoMultiSpeakerDirectedRoomPerformerController))]
public sealed class AaltoMultiSpeakerDirectedRoomPerformerControllerEditor : Editor
{
    SerializedProperty openAIProperty;
    SerializedProperty actionMemoryRegistryProperty;
    SerializedProperty objectiveStanceBootstrapperProperty;

    SerializedProperty speaker1EnabledProperty;
    SerializedProperty speaker1IdProperty;
    SerializedProperty speaker2EnabledProperty;
    SerializedProperty speaker2IdProperty;
    SerializedProperty speaker3EnabledProperty;
    SerializedProperty speaker3IdProperty;
    SerializedProperty speaker4EnabledProperty;
    SerializedProperty speaker4IdProperty;

    SerializedProperty acceptExternalSpeechInputProperty;
    SerializedProperty autoEvaluateOnExternalSpeechProperty;
    SerializedProperty lastIncomingSpeakerProperty;
    SerializedProperty lastIncomingTextProperty;
    SerializedProperty externalSpeechStatusProperty;
    SerializedProperty dialogueTranscriptTextProperty;
    SerializedProperty pendingDialogueSinceLastDecisionProperty;
    SerializedProperty totalDialogueLinesProperty;

    SerializedProperty simulatedSpeaker1TextProperty;
    SerializedProperty simulatedSpeaker2TextProperty;
    SerializedProperty simulatedSpeaker3TextProperty;
    SerializedProperty simulatedSpeaker4TextProperty;
    SerializedProperty simulationStatusProperty;

    SerializedProperty lastDecisionReactNowProperty;
    SerializedProperty selectedActionTextProperty;
    SerializedProperty actionJustificationTextProperty;

    SerializedProperty pullContextFromBootstrapperProperty;
    SerializedProperty currentCharacterSummaryProperty;
    SerializedProperty currentObjectiveProperty;
    SerializedProperty currentStanceProperty;
    SerializedProperty directorGuidanceProperty;

    SerializedProperty generalInstructionsProperty;
    SerializedProperty recentDialogueLimitProperty;
    SerializedProperty promptMemoryScopeProperty;
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

    private void OnEnable()
    {
        openAIProperty = serializedObject.FindProperty("OpenAI");
        actionMemoryRegistryProperty = serializedObject.FindProperty("ActionMemoryRegistry");
        objectiveStanceBootstrapperProperty = serializedObject.FindProperty("ObjectiveStanceBootstrapper");

        speaker1EnabledProperty = serializedObject.FindProperty("Speaker1Enabled");
        speaker1IdProperty = serializedObject.FindProperty("Speaker1Id");
        speaker2EnabledProperty = serializedObject.FindProperty("Speaker2Enabled");
        speaker2IdProperty = serializedObject.FindProperty("Speaker2Id");
        speaker3EnabledProperty = serializedObject.FindProperty("Speaker3Enabled");
        speaker3IdProperty = serializedObject.FindProperty("Speaker3Id");
        speaker4EnabledProperty = serializedObject.FindProperty("Speaker4Enabled");
        speaker4IdProperty = serializedObject.FindProperty("Speaker4Id");

        acceptExternalSpeechInputProperty = serializedObject.FindProperty("AcceptExternalSpeechInput");
        autoEvaluateOnExternalSpeechProperty = serializedObject.FindProperty("AutoEvaluateOnExternalSpeech");
        lastIncomingSpeakerProperty = serializedObject.FindProperty("LastIncomingSpeaker");
        lastIncomingTextProperty = serializedObject.FindProperty("LastIncomingText");
        externalSpeechStatusProperty = serializedObject.FindProperty("ExternalSpeechStatus");
        dialogueTranscriptTextProperty = serializedObject.FindProperty("DialogueTranscriptText");
        pendingDialogueSinceLastDecisionProperty = serializedObject.FindProperty("PendingDialogueSinceLastDecision");
        totalDialogueLinesProperty = serializedObject.FindProperty("TotalDialogueLines");

        simulatedSpeaker1TextProperty = serializedObject.FindProperty("SimulatedSpeaker1Text");
        simulatedSpeaker2TextProperty = serializedObject.FindProperty("SimulatedSpeaker2Text");
        simulatedSpeaker3TextProperty = serializedObject.FindProperty("SimulatedSpeaker3Text");
        simulatedSpeaker4TextProperty = serializedObject.FindProperty("SimulatedSpeaker4Text");
        simulationStatusProperty = serializedObject.FindProperty("SimulationStatus");

        lastDecisionReactNowProperty = serializedObject.FindProperty("LastDecisionReactNow");
        selectedActionTextProperty = serializedObject.FindProperty("SelectedActionText");
        actionJustificationTextProperty = serializedObject.FindProperty("ActionJustificationText");

        pullContextFromBootstrapperProperty = serializedObject.FindProperty("PullContextFromBootstrapper");
        currentCharacterSummaryProperty = serializedObject.FindProperty("CurrentCharacterSummary");
        currentObjectiveProperty = serializedObject.FindProperty("CurrentObjective");
        currentStanceProperty = serializedObject.FindProperty("CurrentStance");
        directorGuidanceProperty = serializedObject.FindProperty("DirectorGuidance");

        generalInstructionsProperty = serializedObject.FindProperty("GeneralInstructions");
        recentDialogueLimitProperty = serializedObject.FindProperty("RecentDialogueLimit");
        promptMemoryScopeProperty = serializedObject.FindProperty("PromptMemoryScope");
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

        DrawQuickActions();
        DrawSceneRefs();
        DrawParticipants();
        DrawSpeechIngress();
        DrawSimulation();
        DrawRoomResponse();
        DrawRuntimeContext();
        DrawConfig();
        DrawDebug();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawQuickActions()
    {
        var controller = (AaltoMultiSpeakerDirectedRoomPerformerController)target;

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

        if (GUILayout.Button("Evaluate Dialogue Now"))
        {
            controller.EvaluateDialogueNowContextMenu();
            EditorUtility.SetDirty(controller);
        }

        EditorGUILayout.Space(6f);
    }

    private void DrawSceneRefs()
    {
        EditorGUILayout.LabelField("Scene Refs", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(openAIProperty);
        EditorGUILayout.PropertyField(actionMemoryRegistryProperty);
        EditorGUILayout.PropertyField(objectiveStanceBootstrapperProperty);
    }

    private void DrawParticipants()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Participants (Enable Who Is In Scene)", EditorStyles.boldLabel);
        DrawSpeakerRow(speaker1EnabledProperty, speaker1IdProperty, "Speaker 1");
        DrawSpeakerRow(speaker2EnabledProperty, speaker2IdProperty, "Speaker 2");
        DrawSpeakerRow(speaker3EnabledProperty, speaker3IdProperty, "Speaker 3");
        DrawSpeakerRow(speaker4EnabledProperty, speaker4IdProperty, "Speaker 4");
    }

    private static void DrawSpeakerRow(SerializedProperty enabledProp, SerializedProperty idProp, string label)
    {
        EditorGUILayout.BeginHorizontal();
        enabledProp.boolValue = EditorGUILayout.ToggleLeft(label, enabledProp.boolValue, GUILayout.Width(120f));
        EditorGUILayout.PropertyField(idProp, GUIContent.none);
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSpeechIngress()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Spoken Input", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(acceptExternalSpeechInputProperty);
        EditorGUILayout.PropertyField(autoEvaluateOnExternalSpeechProperty);
        EditorGUILayout.PropertyField(lastIncomingSpeakerProperty);
        EditorGUILayout.PropertyField(lastIncomingTextProperty);
        EditorGUILayout.PropertyField(externalSpeechStatusProperty);
        EditorGUILayout.PropertyField(totalDialogueLinesProperty);
        EditorGUILayout.PropertyField(pendingDialogueSinceLastDecisionProperty);
        EditorGUILayout.PropertyField(dialogueTranscriptTextProperty);
    }

    private void DrawSimulation()
    {
        var controller = (AaltoMultiSpeakerDirectedRoomPerformerController)target;

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Simulation", EditorStyles.boldLabel);

        DrawSimRow("Speaker 1", simulatedSpeaker1TextProperty, controller.SubmitSimulatedSpeaker1Input);
        DrawSimRow("Speaker 2", simulatedSpeaker2TextProperty, controller.SubmitSimulatedSpeaker2Input);
        DrawSimRow("Speaker 3", simulatedSpeaker3TextProperty, controller.SubmitSimulatedSpeaker3Input);
        DrawSimRow("Speaker 4", simulatedSpeaker4TextProperty, controller.SubmitSimulatedSpeaker4Input);

        if (GUILayout.Button("Submit All Simulated Inputs"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.SubmitAllSimulatedInputs();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }

        EditorGUILayout.PropertyField(simulationStatusProperty);
    }

    private void DrawSimRow(string label, SerializedProperty textProperty, System.Action submitAction)
    {
        var controller = (AaltoMultiSpeakerDirectedRoomPerformerController)target;

        EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
        EditorGUILayout.PropertyField(textProperty, GUIContent.none);
        if (GUILayout.Button("Submit " + label))
        {
            serializedObject.ApplyModifiedProperties();
            submitAction();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }
    }

    private void DrawRoomResponse()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Room Response", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(lastDecisionReactNowProperty);
        EditorGUILayout.PropertyField(selectedActionTextProperty);
        EditorGUILayout.PropertyField(actionJustificationTextProperty);
    }

    private void DrawRuntimeContext()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Runtime Context", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(pullContextFromBootstrapperProperty);
        EditorGUILayout.PropertyField(currentCharacterSummaryProperty);
        EditorGUILayout.PropertyField(currentObjectiveProperty);
        EditorGUILayout.PropertyField(currentStanceProperty);
        EditorGUILayout.PropertyField(directorGuidanceProperty);
    }

    private void DrawConfig()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Prompt + Config", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(generalInstructionsProperty);
        EditorGUILayout.PropertyField(recentDialogueLimitProperty);
        EditorGUILayout.PropertyField(promptMemoryScopeProperty);
        EditorGUILayout.PropertyField(modelProperty);
        EditorGUILayout.PropertyField(applyActionThroughRegistryProperty);
        EditorGUILayout.PropertyField(usePulledActionLabelsSnapshotProperty);
        EditorGUILayout.PropertyField(pulledActionLabelsSnapshotProperty);
        EditorGUILayout.PropertyField(enforceRegistryActionLabelsProperty);
        EditorGUILayout.PropertyField(fallbackActionLabelProperty);
    }

    private void DrawDebug()
    {
        var controller = (AaltoMultiSpeakerDirectedRoomPerformerController)target;

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(lastStatusProperty);
        EditorGUILayout.PropertyField(actionPullStatusProperty);
        EditorGUILayout.PropertyField(contextSyncStatusProperty);
        EditorGUILayout.PropertyField(lastRuntimePromptProperty);
        EditorGUILayout.PropertyField(lastRawModelResponseProperty);
        EditorGUILayout.PropertyField(promptInspectionTextProperty);

        if (GUILayout.Button("Clear Dialogue History"))
        {
            controller.ClearDialogueHistory();
            EditorUtility.SetDirty(controller);
        }

        if (GUILayout.Button("Refresh Prompt Inspection"))
        {
            controller.RefreshPromptInspectionContextMenu();
            EditorUtility.SetDirty(controller);
        }
    }
}
