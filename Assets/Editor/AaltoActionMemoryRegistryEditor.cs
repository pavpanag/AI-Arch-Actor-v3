using UnityEditor;
using UnityEngine;

namespace AaltoSystemV3
{
    [CustomEditor(typeof(AaltoActionMemoryRegistry))]
    public sealed class AaltoActionMemoryRegistryEditor : Editor
    {
        SerializedProperty mappingsProperty;
        SerializedProperty oscSenderProperty;
        SerializedProperty lastResolvedMemoryTriggerProperty;
        SerializedProperty lastResolvedActionLabelProperty;
        SerializedProperty lastSendStatusProperty;

        void OnEnable()
        {
            mappingsProperty = serializedObject.FindProperty("mappings");
            oscSenderProperty = serializedObject.FindProperty("OscSender");
            lastResolvedMemoryTriggerProperty = serializedObject.FindProperty("LastResolvedMemoryTrigger");
            lastResolvedActionLabelProperty = serializedObject.FindProperty("LastResolvedActionLabel");
            lastSendStatusProperty = serializedObject.FindProperty("LastSendStatus");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawMappingsSection();

            EditorGUILayout.Space(10f);
            DrawAgentProducedSection();

            serializedObject.ApplyModifiedProperties();
        }

        void DrawAgentProducedSection()
        {
            EditorGUILayout.LabelField("Agent Produced", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(oscSenderProperty);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Last Resolved Action Label");
            EditorGUILayout.SelectableLabel(lastResolvedActionLabelProperty.stringValue ?? string.Empty, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.LabelField("Last Resolved Memory Trigger");
            EditorGUILayout.SelectableLabel(lastResolvedMemoryTriggerProperty.stringValue ?? string.Empty, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.LabelField("Last Send Status");
            EditorGUILayout.SelectableLabel(lastSendStatusProperty.stringValue ?? string.Empty, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndVertical();
        }

        void DrawMappingsSection()
        {
            EditorGUILayout.LabelField("Architect Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Memory slots are the primary structure: memory 1 is yes, memory 2 is no, and the remaining seeded slots keep their default labels. New slots fill the first available memory number.", MessageType.Info);

            for (int i = 0; i < mappingsProperty.arraySize; i++)
            {
                SerializedProperty entryProperty = mappingsProperty.GetArrayElementAtIndex(i);
                SerializedProperty actionLabelProperty = entryProperty.FindPropertyRelative("actionLabel");
                SerializedProperty memoryTriggerProperty = entryProperty.FindPropertyRelative("memoryTrigger");
                string memoryLabel = GetMemoryLabel(memoryTriggerProperty.stringValue, i);

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(memoryLabel, GUILayout.Width(70f));
                EditorGUILayout.PropertyField(actionLabelProperty, GUIContent.none);

                if (GUILayout.Button("Submit", GUILayout.Width(70f)))
                {
                    serializedObject.ApplyModifiedProperties();
                    EmitMappingSubmitted(i, actionLabelProperty.stringValue, memoryTriggerProperty.stringValue);
                    return;
                }

                if (GUILayout.Button("Remove", GUILayout.Width(70f)))
                {
                    EmitMappingRemoved(i, actionLabelProperty.stringValue, memoryTriggerProperty.stringValue);
                    mappingsProperty.DeleteArrayElementAtIndex(i);
                    serializedObject.ApplyModifiedProperties();
                    return;
                }

                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.BeginHorizontal();
            GUI.enabled = mappingsProperty.arraySize < 20;
            if (GUILayout.Button("Add Memory Slot"))
            {
                AddMemorySlot();
            }
            GUI.enabled = true;

            if (GUILayout.Button("Reset Defaults"))
            {
                ResetDefaults();
            }

            if (GUILayout.Button("Clear All"))
            {
                mappingsProperty.ClearArray();
            }
            EditorGUILayout.EndHorizontal();
        }

        string GetMemoryLabel(string memoryTrigger, int index)
        {
            if (!string.IsNullOrWhiteSpace(memoryTrigger))
                return memoryTrigger.Trim();

            return $"memory {index + 1}";
        }

        void AddMemorySlot()
        {
            int memoryNumber = FindNextAvailableMemoryNumber();
            if (memoryNumber < 0)
                return;

            int nextIndex = mappingsProperty.arraySize;
            mappingsProperty.arraySize++;

            SerializedProperty entryProperty = mappingsProperty.GetArrayElementAtIndex(nextIndex);
            entryProperty.FindPropertyRelative("actionLabel").stringValue = string.Empty;
            entryProperty.FindPropertyRelative("memoryTrigger").stringValue = $"memory {memoryNumber}";
            serializedObject.ApplyModifiedProperties();
            EmitMappingSlotAdded(nextIndex, $"memory {memoryNumber}");
        }

        int FindNextAvailableMemoryNumber()
        {
            for (int memoryNumber = 1; memoryNumber <= 20; memoryNumber++)
            {
                if (!IsMemoryNumberUsed(memoryNumber))
                    return memoryNumber;
            }

            return -1;
        }

        bool IsMemoryNumberUsed(int memoryNumber)
        {
            string expected = $"memory {memoryNumber}";

            for (int i = 0; i < mappingsProperty.arraySize; i++)
            {
                SerializedProperty entryProperty = mappingsProperty.GetArrayElementAtIndex(i);
                SerializedProperty memoryTriggerProperty = entryProperty.FindPropertyRelative("memoryTrigger");
                if (string.Equals(memoryTriggerProperty.stringValue?.Trim(), expected, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        void ResetDefaults()
        {
            mappingsProperty.arraySize = 8;

            SetEntry(0, "yes", "memory 1");
            SetEntry(1, "no", "memory 2");
            SetEntry(2, "calm down", "memory 3");
            SetEntry(3, "this is good", "memory 4");
            SetEntry(4, "be quiet", "memory 5");
            SetEntry(5, "you belong here", "memory 6");
            SetEntry(6, "no exit", "memory 7");
            SetEntry(7, "no place", "memory 8");
        }

        void SetEntry(int index, string actionLabel, string memoryTrigger)
        {
            SerializedProperty entryProperty = mappingsProperty.GetArrayElementAtIndex(index);
            entryProperty.FindPropertyRelative("actionLabel").stringValue = actionLabel;
            entryProperty.FindPropertyRelative("memoryTrigger").stringValue = memoryTrigger;
        }

        static void EmitMappingSlotAdded(int index, string memoryTrigger)
        {
            if (!Application.isPlaying)
                return;

            string payload =
                "{\"index\":" + index + "," +
                "\"memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(memoryTrigger ?? string.Empty) + "\"}";

            AaltoLaunchSessionLogger.EmitEvent("AaltoActionMemoryRegistry", "action_memory.mapping_slot_added", payload);
        }

        static void EmitMappingSubmitted(int index, string actionLabel, string memoryTrigger)
        {
            if (!Application.isPlaying)
                return;

            string payload =
                "{\"index\":" + index + "," +
                "\"action_label\":\"" + AaltoLaunchSessionLogger.EscapeJson(actionLabel ?? string.Empty) + "\"," +
                "\"memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(memoryTrigger ?? string.Empty) + "\"}";

            AaltoLaunchSessionLogger.EmitEvent("AaltoActionMemoryRegistry", "action_memory.mapping_submitted", payload);
        }

        static void EmitMappingRemoved(int index, string actionLabel, string memoryTrigger)
        {
            if (!Application.isPlaying)
                return;

            string payload =
                "{\"index\":" + index + "," +
                "\"action_label\":\"" + AaltoLaunchSessionLogger.EscapeJson(actionLabel ?? string.Empty) + "\"," +
                "\"memory_trigger\":\"" + AaltoLaunchSessionLogger.EscapeJson(memoryTrigger ?? string.Empty) + "\"}";

            AaltoLaunchSessionLogger.EmitEvent("AaltoActionMemoryRegistry", "action_memory.mapping_removed", payload);
        }
    }
}
