using UnityEditor;
using UnityEngine;
using AaltoSystemV3;

[CustomEditor(typeof(AaltoObjectiveStanceBootstrapper))]
public sealed class AaltoObjectiveStanceBootstrapperEditor : Editor
{
    SerializedProperty interviewControllerProperty;
    SerializedProperty openAIProperty;
    SerializedProperty autoGenerateOnSummaryUpdateProperty;
    SerializedProperty currentCharacterSummaryProperty;
    SerializedProperty generatedObjectiveProperty;
    SerializedProperty generatedStanceProperty;
    SerializedProperty lastStatusProperty;
    SerializedProperty generationStatusProperty;

    void OnEnable()
    {
        interviewControllerProperty = serializedObject.FindProperty("InterviewController");
        openAIProperty = serializedObject.FindProperty("OpenAI");
        autoGenerateOnSummaryUpdateProperty = serializedObject.FindProperty("AutoGenerateOnSummaryUpdate");
        currentCharacterSummaryProperty = serializedObject.FindProperty("CurrentCharacterSummary");
        generatedObjectiveProperty = serializedObject.FindProperty("GeneratedObjective");
        generatedStanceProperty = serializedObject.FindProperty("GeneratedStance");
        lastStatusProperty = serializedObject.FindProperty("LastStatus");
        generationStatusProperty = serializedObject.FindProperty("GenerationStatus");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        var controller = (AaltoObjectiveStanceBootstrapper)target;

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Quick Action", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("One-pass operation: pull latest interview summary and generate objective + stance once.", MessageType.Info);
        if (GUILayout.Button("Pull Summary + Generate Objective/Stance"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.PullSummaryAndGenerateNow();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }

        EditorGUILayout.Space(8f);
        DrawPropertiesExcluding(serializedObject, "m_Script");

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Current Snapshot", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(currentCharacterSummaryProperty);
        EditorGUILayout.PropertyField(generatedObjectiveProperty);
        EditorGUILayout.PropertyField(generatedStanceProperty);
        EditorGUILayout.PropertyField(generationStatusProperty);
        EditorGUILayout.PropertyField(lastStatusProperty);

        serializedObject.ApplyModifiedProperties();
    }
}
