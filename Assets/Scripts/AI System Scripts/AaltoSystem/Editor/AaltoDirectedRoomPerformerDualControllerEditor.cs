using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using AaltoSystemV3;

[CustomEditor(typeof(AaltoDirectedRoomPerformerDualController))]
public sealed class AaltoDirectedRoomPerformerDualControllerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawSimulatedSpeechSection();
        EditorGUILayout.Space(6f);
        DrawQuickActions();
        EditorGUILayout.Space(6f);
        DrawPropertiesExcluding(serializedObject, "m_Script", "InspectorActorInput");

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawSimulatedSpeechSection()
    {
        var controller = (AaltoDirectedRoomPerformerDualController)target;
        var simulatedSpeechProperty = serializedObject.FindProperty("InspectorActorInput");

        EditorGUILayout.LabelField("Simulated Speech", EditorStyles.boldLabel);
        if (simulatedSpeechProperty != null)
            EditorGUILayout.PropertyField(simulatedSpeechProperty, true);

        if (GUILayout.Button("Submit Simulated Speech"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.SubmitTurnFromInspectorInput();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }
    }

    private void DrawQuickActions()
    {
        var controller = (AaltoDirectedRoomPerformerDualController)target;

        EditorGUILayout.LabelField("Quick Actions", EditorStyles.boldLabel);
        if (GUILayout.Button("Pull Context From Bootstrapper Now"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.PullContextFromBootstrapperNow();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }

        if (GUILayout.Button("Pull Action Labels From Registry Now"))
        {
            serializedObject.ApplyModifiedProperties();
            controller.PullActionsFromRegistryNow();
            EditorUtility.SetDirty(controller);
            serializedObject.Update();
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
            var defaultFileName = "AaltoDualDialogArchive_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json";
            var savePath = EditorUtility.SaveFilePanel(
                "Export Dual Performer Dialog Archive (JSON + CSV)",
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

                    File.WriteAllText(jsonPath, controller.BuildDialogArchiveExportJson());
                    File.WriteAllText(csvPath, controller.BuildDialogArchiveExportCsv());

                    EditorUtility.DisplayDialog(
                        "Dialog Export",
                        "Dual-performer dialog archive exported as both JSON and CSV.\n\n" +
                        "JSON: " + jsonPath + "\n" +
                        "CSV: " + csvPath,
                        "OK");
                }
                catch (Exception ex)
                {
                    EditorUtility.DisplayDialog("Dialog Export Failed", ex.Message, "OK");
                }
            }

            EditorUtility.SetDirty(controller);
            serializedObject.Update();
        }
        EditorGUILayout.EndHorizontal();
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
}
