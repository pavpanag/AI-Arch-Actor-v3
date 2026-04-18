using UnityEditor;
using UnityEngine;

namespace AaltoSystemV3
{
    [CustomEditor(typeof(AaltoChatController))]
    public sealed class AaltoChatControllerEditor : Editor
    {
        SerializedProperty openAIProperty;
        SerializedProperty actionMemoryRegistryProperty;
        SerializedProperty oscSenderProperty;
        SerializedProperty userInputProperty;
        SerializedProperty conversationLogProperty;
        SerializedProperty decisionLogProperty;

        SerializedProperty characterSummaryProperty;
        SerializedProperty requireApprovedSummaryProperty;
        SerializedProperty characterSummaryApprovedProperty;
        SerializedProperty currentObjectiveProperty;
        SerializedProperty currentStanceProperty;

        SerializedProperty modelProperty;
        SerializedProperty dryRunOnlyProperty;
        SerializedProperty availableActionLabelsProperty;
        SerializedProperty enforceAvailableActionLabelsProperty;
        SerializedProperty defaultFallbackActionLabelProperty;
        SerializedProperty allowedStancesProperty;
        SerializedProperty defaultFallbackStanceProperty;
        SerializedProperty dialogueWindowTurnsProperty;
        SerializedProperty maxConversationCharsProperty;
        SerializedProperty rejectUnknownJsonKeysProperty;
        SerializedProperty warnOnUnknownJsonKeysProperty;

        SerializedProperty promptInspectionTextProperty;
        SerializedProperty updatePromptInspectionTextProperty;

        SerializedProperty inspectorDialogueInputProperty;
        SerializedProperty inspectorDialogueStatusProperty;

        SerializedProperty directingInstructionTextProperty;
        SerializedProperty directingInstructionContextProperty;
        SerializedProperty directingInstructionObjectiveOverrideProperty;
        SerializedProperty directingInstructionStanceOverrideProperty;
        SerializedProperty directingMemoryPreviewProperty;

        SerializedProperty mockScenarioLinesProperty;

        void OnEnable()
        {
            openAIProperty = serializedObject.FindProperty("OpenAI");
            actionMemoryRegistryProperty = serializedObject.FindProperty("ActionMemoryRegistry");
            oscSenderProperty = serializedObject.FindProperty("OscSender");
            userInputProperty = serializedObject.FindProperty("UserInput");
            conversationLogProperty = serializedObject.FindProperty("ConversationLog");
            decisionLogProperty = serializedObject.FindProperty("DecisionLog");

            characterSummaryProperty = serializedObject.FindProperty("CharacterSummary");
            requireApprovedSummaryProperty = serializedObject.FindProperty("RequireApprovedSummary");
            characterSummaryApprovedProperty = serializedObject.FindProperty("CharacterSummaryApproved");
            currentObjectiveProperty = serializedObject.FindProperty("CurrentObjective");
            currentStanceProperty = serializedObject.FindProperty("CurrentStance");

            modelProperty = serializedObject.FindProperty("Model");
            dryRunOnlyProperty = serializedObject.FindProperty("DryRunOnly");
            availableActionLabelsProperty = serializedObject.FindProperty("AvailableActionLabels");
            enforceAvailableActionLabelsProperty = serializedObject.FindProperty("EnforceAvailableActionLabels");
            defaultFallbackActionLabelProperty = serializedObject.FindProperty("DefaultFallbackActionLabel");
            allowedStancesProperty = serializedObject.FindProperty("AllowedStances");
            defaultFallbackStanceProperty = serializedObject.FindProperty("DefaultFallbackStance");
            dialogueWindowTurnsProperty = serializedObject.FindProperty("DialogueWindowTurns");
            maxConversationCharsProperty = serializedObject.FindProperty("MaxConversationChars");
            rejectUnknownJsonKeysProperty = serializedObject.FindProperty("RejectUnknownJsonKeys");
            warnOnUnknownJsonKeysProperty = serializedObject.FindProperty("WarnOnUnknownJsonKeys");

            promptInspectionTextProperty = serializedObject.FindProperty("PromptInspectionText");
            updatePromptInspectionTextProperty = serializedObject.FindProperty("UpdatePromptInspectionText");

            inspectorDialogueInputProperty = serializedObject.FindProperty("InspectorDialogueInput");
            inspectorDialogueStatusProperty = serializedObject.FindProperty("InspectorDialogueStatus");

            directingInstructionTextProperty = serializedObject.FindProperty("DirectingInstructionText");
            directingInstructionContextProperty = serializedObject.FindProperty("DirectingInstructionContext");
            directingInstructionObjectiveOverrideProperty = serializedObject.FindProperty("DirectingInstructionObjectiveOverride");
            directingInstructionStanceOverrideProperty = serializedObject.FindProperty("DirectingInstructionStanceOverride");
            directingMemoryPreviewProperty = serializedObject.FindProperty("DirectingMemoryPreview");

            mockScenarioLinesProperty = serializedObject.FindProperty("MockScenarioLines");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawSceneRefsSection();
            DrawRuntimeStateSection();
            DrawTurnInputSection();
            DrawDecisionTraceSection((AaltoChatController)target);
            DrawPromptAndMemorySection((AaltoChatController)target);
            DrawConfigurationSection();
            DrawTestingSection((AaltoChatController)target);

            serializedObject.ApplyModifiedProperties();
        }

        void DrawSceneRefsSection()
        {
            EditorGUILayout.LabelField("Scene Refs", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(openAIProperty);
            EditorGUILayout.PropertyField(actionMemoryRegistryProperty);
            EditorGUILayout.PropertyField(oscSenderProperty);
            EditorGUILayout.PropertyField(userInputProperty);
            EditorGUILayout.PropertyField(conversationLogProperty);
            EditorGUILayout.PropertyField(decisionLogProperty);
        }

        void DrawRuntimeStateSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Runtime State", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(characterSummaryProperty);
            EditorGUILayout.PropertyField(requireApprovedSummaryProperty);
            EditorGUILayout.PropertyField(characterSummaryApprovedProperty);
            EditorGUILayout.PropertyField(currentObjectiveProperty);
            EditorGUILayout.PropertyField(currentStanceProperty);
        }

        void DrawTurnInputSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Inspector Dialogue", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Type a rehearsal line here when you want to drive the chat from the Inspector instead of the scene input field.", MessageType.Info);
            EditorGUILayout.PropertyField(inspectorDialogueInputProperty);
            EditorGUILayout.PropertyField(inspectorDialogueStatusProperty);

            if (GUILayout.Button("Submit Inspector Dialogue"))
            {
                var controller = (AaltoChatController)target;
                controller.SubmitInspectorDialogue();
                EditorUtility.SetDirty(controller);
            }
        }

        void DrawDecisionTraceSection(AaltoChatController controller)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Decision Trace", EditorStyles.boldLabel);
            DrawReadOnlyTextArea("Current Stance", controller != null ? controller.CurrentStance : string.Empty);
            DrawReadOnlyTextArea("Intended Action", controller != null ? controller.LastResolvedIntendedAction : string.Empty);
            DrawReadOnlyTextArea("Action Label", controller != null ? controller.LastResolvedActionLabel : string.Empty);
            DrawReadOnlyTextArea("Mapped Memory", controller != null ? controller.LastResolvedMemoryLabel : string.Empty);
            DrawReadOnlyTextArea("Reason", controller != null ? controller.LastResolvedReason : string.Empty);
            DrawReadOnlyTextArea("Execution Result", controller != null ? controller.LastExecutionResult : string.Empty);
        }

        void DrawPromptAndMemorySection(AaltoChatController controller)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Prompt and Memory", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(promptInspectionTextProperty);
            EditorGUILayout.PropertyField(updatePromptInspectionTextProperty);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Directing Memory Tools", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Store a director instruction with scene context and current interaction snapshot so it can be reviewed later from the Inspector.", MessageType.Info);
            EditorGUILayout.PropertyField(directingInstructionTextProperty);
            EditorGUILayout.PropertyField(directingInstructionContextProperty);
            EditorGUILayout.PropertyField(directingInstructionObjectiveOverrideProperty);
            EditorGUILayout.PropertyField(directingInstructionStanceOverrideProperty);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Record Directing Instruction"))
            {
                controller.RecordDirectingInstructionFromInspector();
                EditorUtility.SetDirty(controller);
            }

            if (GUILayout.Button("Refresh Directing Memory Preview"))
            {
                controller.RefreshDirectingMemoryPreview();
                EditorUtility.SetDirty(controller);
            }
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Clear Directing Memory"))
            {
                controller.ClearDirectingMemory();
                EditorUtility.SetDirty(controller);
            }

            EditorGUILayout.LabelField("Directing Memory Preview", EditorStyles.boldLabel);
            DrawReadOnlyTextArea(string.Empty, controller != null ? controller.DirectingMemoryPreview : string.Empty, 180f);
        }

        void DrawConfigurationSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(modelProperty);
            EditorGUILayout.PropertyField(dryRunOnlyProperty);
            EditorGUILayout.PropertyField(availableActionLabelsProperty);
            EditorGUILayout.PropertyField(enforceAvailableActionLabelsProperty);
            EditorGUILayout.PropertyField(defaultFallbackActionLabelProperty);
            EditorGUILayout.PropertyField(allowedStancesProperty);
            EditorGUILayout.PropertyField(defaultFallbackStanceProperty);
            EditorGUILayout.PropertyField(dialogueWindowTurnsProperty);
            EditorGUILayout.PropertyField(maxConversationCharsProperty);
            EditorGUILayout.PropertyField(rejectUnknownJsonKeysProperty);
            EditorGUILayout.PropertyField(warnOnUnknownJsonKeysProperty);
        }

        void DrawTestingSection(AaltoChatController controller)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Batch Testing", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(mockScenarioLinesProperty);

            if (GUILayout.Button("Run Mock Scenario Batch"))
            {
                controller.RunMockScenarioBatch();
                EditorUtility.SetDirty(controller);
            }
        }

        static void DrawReadOnlyTextArea(string label, string value, float minHeight = 60f)
        {
            if (!string.IsNullOrWhiteSpace(label))
                EditorGUILayout.LabelField(label);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextArea(value ?? string.Empty, GUILayout.MinHeight(minHeight));
            }
        }
    }
}