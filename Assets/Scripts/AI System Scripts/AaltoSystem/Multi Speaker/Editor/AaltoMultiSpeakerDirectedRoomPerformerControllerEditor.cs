using UnityEditor;
using UnityEngine;
using AaltoSystemV3;
using System;
using System.Collections.Generic;
using System.IO;

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
    SerializedProperty dialogLogLimitProperty;
    SerializedProperty dialogLogCompactModeProperty;
    SerializedProperty currentTakeNumberProperty;
    SerializedProperty dialogTurnsProperty;
    SerializedProperty archivedDialogTakesProperty;

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
    SerializedProperty selectedActionStateModeProperty;
    SerializedProperty neutralMemoryTriggerProperty;
    SerializedProperty responsePulseSecondsProperty;
    SerializedProperty executionModeStatusProperty;
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
        dialogLogLimitProperty = serializedObject.FindProperty("DialogLogLimit");
        dialogLogCompactModeProperty = serializedObject.FindProperty("DialogLogCompactMode");
        currentTakeNumberProperty = serializedObject.FindProperty("CurrentTakeNumber");
        dialogTurnsProperty = serializedObject.FindProperty("DialogTurns");
        archivedDialogTakesProperty = serializedObject.FindProperty("ArchivedDialogTakes");

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
        selectedActionStateModeProperty = serializedObject.FindProperty("SelectedActionStateMode");
        neutralMemoryTriggerProperty = serializedObject.FindProperty("NeutralMemoryTrigger");
        responsePulseSecondsProperty = serializedObject.FindProperty("ResponsePulseSeconds");
        executionModeStatusProperty = serializedObject.FindProperty("ExecutionModeStatus");
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
        DrawParticipants();
        DrawSimulation();
        DrawEvaluateAndRoomResponse();
        DrawSpeechIngress();
        DrawDialogLog();
        DrawRuntimeContext();
        DrawConfig();
        DrawDebug();
        DrawSceneRefs();

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

        EditorGUILayout.Space(4f);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Start New Take"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.StartNewTake();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }

        if (GUILayout.Button("Export Dialogs (JSON + CSV)"))
        {
            serializedObject.ApplyModifiedProperties();

            var defaultDirectory = ResolveDefaultExportDirectory(controller.DialogExportFolderRelativePath);
            var defaultFileName = "AaltoMultiSpeakerDialogArchive_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
            var savePath = EditorUtility.SaveFilePanel(
                "Export Multi-Speaker Dialog Archive (JSON + CSV)",
                defaultDirectory,
                defaultFileName,
                "json");

            if (!string.IsNullOrWhiteSpace(savePath))
            {
                try
                {
                    var folder = Path.GetDirectoryName(savePath);
                    var baseName = Path.GetFileNameWithoutExtension(savePath);
                    var resolvedFolder = string.IsNullOrWhiteSpace(folder) ? defaultDirectory : folder;
                    var jsonPath = Path.Combine(resolvedFolder, baseName + ".json");
                    var csvPath = Path.Combine(resolvedFolder, baseName + ".csv");

                    var json = controller.BuildDialogArchiveExportJson();
                    var csv = controller.BuildDialogArchiveExportCsv();

                    File.WriteAllText(jsonPath, json);
                    File.WriteAllText(csvPath, csv);

                    EditorUtility.DisplayDialog(
                        "Dialog Export",
                        "Multi-speaker dialog archive exported as both JSON and CSV.\n\n" +
                        "JSON: " + jsonPath + "\n" +
                        "CSV: " + csvPath,
                        "OK");
                }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog("Dialog Export Failed", ex.Message, "OK");
                }
            }

            serializedObject.Update();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(6f);
    }

    private static string ResolveDefaultExportDirectory(string relativeOrAbsolute)
    {
        var raw = (relativeOrAbsolute ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return Application.dataPath;

        var candidate = raw;
        if (!Path.IsPathRooted(candidate))
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrWhiteSpace(projectRoot))
                return Application.dataPath;

            candidate = Path.Combine(projectRoot, raw.Replace('\\', '/').TrimStart('/'));
        }

        var fullPath = Path.GetFullPath(candidate);
        Directory.CreateDirectory(fullPath);
        return fullPath;
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
        EditorGUILayout.PropertyField(lastIncomingSpeakerProperty);
        EditorGUILayout.PropertyField(lastIncomingTextProperty);
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
    }

    private void DrawSimRow(string label, SerializedProperty textProperty, System.Action submitAction)
    {
        var controller = (AaltoMultiSpeakerDirectedRoomPerformerController)target;

        EditorGUILayout.PropertyField(textProperty, new GUIContent(label));
        if (GUILayout.Button("Submit " + label))
        {
            serializedObject.ApplyModifiedProperties();
            submitAction();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }
    }

    private void DrawEvaluateAndRoomResponse()
    {
        var controller = (AaltoMultiSpeakerDirectedRoomPerformerController)target;

        EditorGUILayout.Space(8f);
        if (GUILayout.Button("Evaluate Dialogue And Produce Response Now"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.EvaluateDialogueNowContextMenu();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Room Response", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(lastDecisionReactNowProperty);
        EditorGUILayout.PropertyField(selectedActionTextProperty);
        EditorGUILayout.PropertyField(actionJustificationTextProperty);
    }

    private void DrawDialogLog()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Dialog Log", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(currentTakeNumberProperty);
        EditorGUILayout.PropertyField(dialogLogLimitProperty);
        EditorGUILayout.PropertyField(dialogLogCompactModeProperty);
        EditorGUILayout.PropertyField(archivedDialogTakesProperty, true);

        if (dialogTurnsProperty == null || dialogTurnsProperty.arraySize == 0)
        {
            EditorGUILayout.HelpBox("No decision turns yet.", MessageType.Info);
            return;
        }

        for (int i = 0; i < dialogTurnsProperty.arraySize; i++)
        {
            var turnProperty = dialogTurnsProperty.GetArrayElementAtIndex(i);
            var latestLineProperty = turnProperty.FindPropertyRelative("latestDialogueLine");
            var pendingBatchProperty = turnProperty.FindPropertyRelative("pendingDialogueBatch");
            var decisionContextSummaryProperty = turnProperty.FindPropertyRelative("decisionContextSummary");
            var pendingDialogueLineCountProperty = turnProperty.FindPropertyRelative("pendingDialogueLineCount");
            var promptMemoryScopeUsedProperty = turnProperty.FindPropertyRelative("promptMemoryScopeUsed");
            var promptMemoryLineCountProperty = turnProperty.FindPropertyRelative("promptMemoryLineCount");
            var selectedResponseProperty = turnProperty.FindPropertyRelative("selectedResponse");
            var justificationProperty = turnProperty.FindPropertyRelative("justification");
            var showDetailsProperty = turnProperty.FindPropertyRelative("showDetails");
            var reactNowProperty = turnProperty.FindPropertyRelative("reactNow");

            var latestText = (latestLineProperty.stringValue ?? string.Empty).Trim();
            var pendingBatchText = pendingBatchProperty != null ? (pendingBatchProperty.stringValue ?? string.Empty).Trim() : string.Empty;
            var contextSummaryText = decisionContextSummaryProperty != null ? (decisionContextSummaryProperty.stringValue ?? string.Empty).Trim() : string.Empty;
            var chunkLineCount = pendingDialogueLineCountProperty != null ? pendingDialogueLineCountProperty.intValue : 0;
            var memoryScopeText = promptMemoryScopeUsedProperty != null ? (promptMemoryScopeUsedProperty.stringValue ?? string.Empty).Trim() : string.Empty;
            var memoryLineCount = promptMemoryLineCountProperty != null ? promptMemoryLineCountProperty.intValue : 0;
            var responseText = (selectedResponseProperty.stringValue ?? string.Empty).Trim();
            var reactLabel = reactNowProperty.boolValue ? "react" : "no-react";

            EditorGUILayout.BeginVertical("box");
            if (dialogLogCompactModeProperty.boolValue)
            {
                var chunkLabel = chunkLineCount > 0 ? chunkLineCount + " lines" : "no lines";
                var compactContext = !string.IsNullOrWhiteSpace(contextSummaryText) ? contextSummaryText : latestText;
                var compactLine = $"Turn {i + 1} | Chunk: {chunkLabel} ({compactContext})  |  Response: {responseText}";
                if (string.IsNullOrWhiteSpace(compactContext) && string.IsNullOrWhiteSpace(responseText))
                    compactLine = $"Turn {i + 1} | (empty turn)";

                showDetailsProperty.boolValue = EditorGUILayout.Foldout(showDetailsProperty.boolValue, compactLine, true);
                if (showDetailsProperty.boolValue)
                {
                    EditorGUILayout.Space(2f);
                    EditorGUILayout.LabelField("Evaluated Dialogue Chunk", EditorStyles.miniBoldLabel);
                    DrawAutoHeightSelectableLabel(pendingBatchText, 72f);
                    EditorGUILayout.LabelField("Memory Context", EditorStyles.miniBoldLabel);
                    EditorGUILayout.LabelField($"Scope: {memoryScopeText} | Lines: {Mathf.Max(0, memoryLineCount)}", EditorStyles.wordWrappedMiniLabel);
                    EditorGUILayout.LabelField("Justification", EditorStyles.miniBoldLabel);
                    DrawAutoHeightSelectableLabel((justificationProperty.stringValue ?? string.Empty).Trim(), 60f);
                }
            }
            else
            {
                EditorGUILayout.LabelField($"Turn {i + 1} ({reactLabel})", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Decision Context Summary", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(contextSummaryText, EditorStyles.textArea, GUILayout.MinHeight(34f));
                EditorGUILayout.LabelField("Evaluated Dialogue Chunk", EditorStyles.miniBoldLabel);
                DrawAutoHeightSelectableLabel(pendingBatchText, 72f);
                EditorGUILayout.LabelField("Latest Dialogue In Chunk", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(latestText, EditorStyles.textArea, GUILayout.MinHeight(34f));
                EditorGUILayout.LabelField("Memory Context Used", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel($"Scope: {memoryScopeText} | Lines: {Mathf.Max(0, memoryLineCount)}", EditorStyles.textArea, GUILayout.MinHeight(34f));
                EditorGUILayout.LabelField("Selected Response", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(responseText, EditorStyles.textArea, GUILayout.MinHeight(34f));
                EditorGUILayout.LabelField("Justification", EditorStyles.miniBoldLabel);
                DrawAutoHeightSelectableLabel((justificationProperty.stringValue ?? string.Empty).Trim(), 60f);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2f);
        }
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
        var controller = (AaltoMultiSpeakerDirectedRoomPerformerController)target;

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Prompt + Config", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(generalInstructionsProperty);
        EditorGUILayout.PropertyField(recentDialogueLimitProperty);
        EditorGUILayout.PropertyField(promptMemoryScopeProperty);
        EditorGUILayout.PropertyField(modelProperty);
        EditorGUILayout.PropertyField(applyActionThroughRegistryProperty);
        EditorGUILayout.PropertyField(selectedActionStateModeProperty);
        DrawNeutralMemoryTriggerSelector(controller);
        EditorGUILayout.PropertyField(responsePulseSecondsProperty);
        EditorGUILayout.PropertyField(executionModeStatusProperty);
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

    private static void DrawAutoHeightSelectableLabel(string text, float minHeight)
    {
        var content = new GUIContent(text ?? string.Empty);
        var width = Mathf.Max(120f, EditorGUIUtility.currentViewWidth - 48f);
        var desiredHeight = EditorStyles.textArea.CalcHeight(content, width);
        var finalHeight = Mathf.Max(minHeight, desiredHeight + 6f);
        EditorGUILayout.SelectableLabel(content.text, EditorStyles.textArea, GUILayout.Height(finalHeight));
    }

    private void DrawNeutralMemoryTriggerSelector(AaltoMultiSpeakerDirectedRoomPerformerController controller)
    {
        if (controller == null || controller.ActionMemoryRegistry == null || controller.ActionMemoryRegistry.mappings == null)
        {
            EditorGUILayout.PropertyField(neutralMemoryTriggerProperty);
            return;
        }

        var options = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < controller.ActionMemoryRegistry.mappings.Count; i++)
        {
            var mapping = controller.ActionMemoryRegistry.mappings[i];
            if (mapping == null)
                continue;

            var trigger = (mapping.memoryTrigger ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trigger))
                continue;
            if (!seen.Add(trigger))
                continue;

            options.Add(trigger);
        }

        if (options.Count == 0)
        {
            EditorGUILayout.PropertyField(neutralMemoryTriggerProperty);
            return;
        }

        var current = (neutralMemoryTriggerProperty.stringValue ?? string.Empty).Trim();
        var displayOptions = new List<string>(options);
        var selectedIndex = -1;
        for (int i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i], current, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
                break;
            }
        }

        if (selectedIndex < 0 && !string.IsNullOrWhiteSpace(current))
        {
            displayOptions.Add("(custom) " + current);
            selectedIndex = displayOptions.Count - 1;
        }

        if (selectedIndex < 0)
            selectedIndex = 0;

        var newIndex = EditorGUILayout.Popup("Neutral Memory Trigger", selectedIndex, displayOptions.ToArray());
        if (newIndex >= 0 && newIndex < options.Count)
            neutralMemoryTriggerProperty.stringValue = options[newIndex];

        if (newIndex >= options.Count)
            EditorGUILayout.PropertyField(neutralMemoryTriggerProperty);
    }
}
