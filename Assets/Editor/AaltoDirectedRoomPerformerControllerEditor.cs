using UnityEditor;
using UnityEngine;
using AaltoSystemV3;
using System;
using System.IO;
using System.Collections.Generic;

[CustomEditor(typeof(AaltoDirectedRoomPerformerController))]
public sealed class AaltoDirectedRoomPerformerControllerEditor : Editor
{
    SerializedProperty openAIProperty;
    SerializedProperty actionMemoryRegistryProperty;
    SerializedProperty objectiveStanceBootstrapperProperty;

    SerializedProperty inspectorActorInputProperty;
    SerializedProperty acceptExternalSpeechInputProperty;
    SerializedProperty autoSubmitExternalSpeechProperty;
    SerializedProperty lastExternalSpeechTextProperty;
    SerializedProperty directorGuidanceInputProperty;
    SerializedProperty directorGuidanceInputsProperty;

    SerializedProperty selectedActionTextProperty;
    SerializedProperty actionJustificationTextProperty;

    SerializedProperty dialogLogLimitProperty;
    SerializedProperty dialogLogCompactModeProperty;
    SerializedProperty dialogTurnsProperty;

    SerializedProperty pullContextFromBootstrapperProperty;
    SerializedProperty currentCharacterSummaryProperty;
    SerializedProperty currentObjectiveProperty;
    SerializedProperty currentStanceProperty;
    SerializedProperty directorGuidanceProperty;

    SerializedProperty generalInstructionsProperty;
    SerializedProperty recentHistoryLimitProperty;
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

    void OnEnable()
    {
        openAIProperty = serializedObject.FindProperty("OpenAI");
        actionMemoryRegistryProperty = serializedObject.FindProperty("ActionMemoryRegistry");
        objectiveStanceBootstrapperProperty = serializedObject.FindProperty("ObjectiveStanceBootstrapper");

        inspectorActorInputProperty = serializedObject.FindProperty("InspectorActorInput");
        acceptExternalSpeechInputProperty = serializedObject.FindProperty("AcceptExternalSpeechInput");
        autoSubmitExternalSpeechProperty = serializedObject.FindProperty("AutoSubmitExternalSpeech");
        lastExternalSpeechTextProperty = serializedObject.FindProperty("LastExternalSpeechText");
        directorGuidanceInputProperty = serializedObject.FindProperty("DirectorGuidanceInput");
        directorGuidanceInputsProperty = serializedObject.FindProperty("DirectorGuidanceInputs");

        selectedActionTextProperty = serializedObject.FindProperty("SelectedActionText");
        actionJustificationTextProperty = serializedObject.FindProperty("ActionJustificationText");

        dialogLogLimitProperty = serializedObject.FindProperty("DialogLogLimit");
        dialogLogCompactModeProperty = serializedObject.FindProperty("DialogLogCompactMode");
        dialogTurnsProperty = serializedObject.FindProperty("DialogTurns");

        pullContextFromBootstrapperProperty = serializedObject.FindProperty("PullContextFromBootstrapper");
        currentCharacterSummaryProperty = serializedObject.FindProperty("CurrentCharacterSummary");
        currentObjectiveProperty = serializedObject.FindProperty("CurrentObjective");
        currentStanceProperty = serializedObject.FindProperty("CurrentStance");
        directorGuidanceProperty = serializedObject.FindProperty("DirectorGuidance");

        generalInstructionsProperty = serializedObject.FindProperty("GeneralInstructions");
        recentHistoryLimitProperty = serializedObject.FindProperty("RecentHistoryLimit");
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

        DrawQuickActionsSection();
        DrawLiveInputsSection();
        DrawDialogLogSection();
        DrawRuntimeContextSection();
        DrawConfigSection();
        DrawDebugSection();
        DrawSceneRefsSection();

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

            var defaultFileName = "AaltoDialogArchive_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
            var savePath = EditorUtility.SaveFilePanel(
                "Export Dialog Archive (JSON + CSV)",
                Application.dataPath,
                defaultFileName,
                "json");

            if (!string.IsNullOrWhiteSpace(savePath))
            {
                try
                {
                    var folder = Path.GetDirectoryName(savePath);
                    var baseName = Path.GetFileNameWithoutExtension(savePath);
                    var jsonPath = Path.Combine(folder ?? Application.dataPath, baseName + ".json");
                    var csvPath = Path.Combine(folder ?? Application.dataPath, baseName + ".csv");

                    var json = controller.BuildDialogArchiveExportJson();
                    var csv = controller.BuildDialogArchiveExportCsv();

                    File.WriteAllText(jsonPath, json);
                    File.WriteAllText(csvPath, csv);

                    EditorUtility.DisplayDialog(
                        "Dialog Export",
                        "Dialog archive exported as both JSON and CSV.\n\n" +
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
        EditorGUILayout.LabelField("Simulated Speech", EditorStyles.boldLabel);

        EditorGUILayout.PropertyField(inspectorActorInputProperty);

        if (GUILayout.Button("Submit Simulated Speech"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.SubmitTurnFromInspectorInput();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Room Response", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(selectedActionTextProperty);
        EditorGUILayout.PropertyField(actionJustificationTextProperty);

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Spoken Input", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(acceptExternalSpeechInputProperty);
        EditorGUILayout.PropertyField(autoSubmitExternalSpeechProperty);
        EditorGUILayout.PropertyField(lastExternalSpeechTextProperty);

        EditorGUILayout.Space(4f);
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

    void DrawDialogLogSection()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Dialog Log", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(dialogLogLimitProperty);
        EditorGUILayout.PropertyField(dialogLogCompactModeProperty);

        if (dialogTurnsProperty == null || dialogTurnsProperty.arraySize == 0)
        {
            EditorGUILayout.HelpBox("No dialog turns yet.", MessageType.Info);
            return;
        }

        for (int i = 0; i < dialogTurnsProperty.arraySize; i++)
        {
            var turnProperty = dialogTurnsProperty.GetArrayElementAtIndex(i);
            var actorLineProperty = turnProperty.FindPropertyRelative("actorLine");
            var selectedResponseProperty = turnProperty.FindPropertyRelative("selectedResponse");
            var justificationProperty = turnProperty.FindPropertyRelative("justification");
            var showDetailsProperty = turnProperty.FindPropertyRelative("showDetails");
            var showJustificationProperty = turnProperty.FindPropertyRelative("showJustification");

            var actorText = (actorLineProperty.stringValue ?? string.Empty).Trim();
            var responseText = (selectedResponseProperty.stringValue ?? string.Empty).Trim();
            var header = $"Turn {i + 1}: {(string.IsNullOrWhiteSpace(actorText) ? "(empty actor line)" : actorText)}";

            EditorGUILayout.BeginVertical("box");
            if (dialogLogCompactModeProperty.boolValue)
            {
                var compactLine = $"Turn {i + 1} | Actor: {actorText}  |  Response: {responseText}";
                if (string.IsNullOrWhiteSpace(actorText) && string.IsNullOrWhiteSpace(responseText))
                    compactLine = $"Turn {i + 1} | (empty dialog turn)";

                showDetailsProperty.boolValue = EditorGUILayout.Foldout(
                    showDetailsProperty.boolValue,
                    compactLine,
                    true);

                if (showDetailsProperty.boolValue)
                {
                    EditorGUILayout.Space(2f);
                    EditorGUILayout.LabelField("Justification", EditorStyles.miniBoldLabel);
                    DrawAutoHeightSelectableLabel((justificationProperty.stringValue ?? string.Empty).Trim(), 60f);
                }
            }
            else
            {
                EditorGUILayout.LabelField(header, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Actor", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(actorText, EditorStyles.textArea, GUILayout.MinHeight(34f));
                EditorGUILayout.LabelField("Selected Response", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(responseText, EditorStyles.textArea, GUILayout.MinHeight(34f));

                var toggleLabel = showJustificationProperty.boolValue ? "Hide Justification" : "Show Justification";
                if (GUILayout.Button(toggleLabel))
                {
                    showJustificationProperty.boolValue = !showJustificationProperty.boolValue;
                }

                if (showJustificationProperty.boolValue)
                {
                    EditorGUILayout.LabelField("Justification", EditorStyles.miniBoldLabel);
                    DrawAutoHeightSelectableLabel((justificationProperty.stringValue ?? string.Empty).Trim(), 60f);
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2f);
        }
    }

    void DrawConfigSection()
    {
        var controller = (AaltoDirectedRoomPerformerController)target;

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Prompt + Config", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(generalInstructionsProperty);
        EditorGUILayout.PropertyField(recentHistoryLimitProperty);
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

    void DrawNeutralMemoryTriggerSelector(AaltoDirectedRoomPerformerController controller)
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
        {
            neutralMemoryTriggerProperty.stringValue = options[newIndex];
        }

        if (newIndex >= options.Count)
        {
            EditorGUILayout.PropertyField(neutralMemoryTriggerProperty);
        }
    }

    static void DrawAutoHeightSelectableLabel(string text, float minHeight)
    {
        var content = new GUIContent(text ?? string.Empty);
        var width = Mathf.Max(120f, EditorGUIUtility.currentViewWidth - 48f);
        var desiredHeight = EditorStyles.textArea.CalcHeight(content, width);
        var finalHeight = Mathf.Max(minHeight, desiredHeight + 6f);
        EditorGUILayout.SelectableLabel(content.text, EditorStyles.textArea, GUILayout.Height(finalHeight));
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
