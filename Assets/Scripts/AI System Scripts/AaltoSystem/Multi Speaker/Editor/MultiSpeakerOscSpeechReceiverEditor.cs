using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(MultiSpeakerOscSpeechReceiver))]
public sealed class MultiSpeakerOscSpeechReceiverEditor : Editor
{
    SerializedProperty multiSpeakerPerformerProperty;
    SerializedProperty oscProperty;
    SerializedProperty expectedAddressProperty;
    SerializedProperty acceptAnyChannelProperty;
    SerializedProperty allowedChannelsProperty;

    SerializedProperty channel1SpeakerIdProperty;
    SerializedProperty channel2SpeakerIdProperty;
    SerializedProperty channel3SpeakerIdProperty;
    SerializedProperty channel4SpeakerIdProperty;

    SerializedProperty lastStatusProperty;
    SerializedProperty lastReceivedSpeakerProperty;
    SerializedProperty lastReceivedTextProperty;
    SerializedProperty totalReceivedProperty;
    SerializedProperty totalDispatchedProperty;
    SerializedProperty droppedEmptyTranscriptProperty;
    SerializedProperty droppedByChannelFilterProperty;
    SerializedProperty droppedNoPerformerProperty;
    SerializedProperty speaker1CountProperty;
    SerializedProperty speaker2CountProperty;
    SerializedProperty speaker3CountProperty;
    SerializedProperty speaker4CountProperty;
    SerializedProperty unknownSpeakerCountProperty;

    private void OnEnable()
    {
        multiSpeakerPerformerProperty = serializedObject.FindProperty("MultiSpeakerPerformer");
        oscProperty = serializedObject.FindProperty("osc");
        expectedAddressProperty = serializedObject.FindProperty("ExpectedAddress");
        acceptAnyChannelProperty = serializedObject.FindProperty("AcceptAnyChannel");
        allowedChannelsProperty = serializedObject.FindProperty("AllowedChannels");

        channel1SpeakerIdProperty = serializedObject.FindProperty("Channel1SpeakerId");
        channel2SpeakerIdProperty = serializedObject.FindProperty("Channel2SpeakerId");
        channel3SpeakerIdProperty = serializedObject.FindProperty("Channel3SpeakerId");
        channel4SpeakerIdProperty = serializedObject.FindProperty("Channel4SpeakerId");

        lastStatusProperty = serializedObject.FindProperty("LastStatus");
        lastReceivedSpeakerProperty = serializedObject.FindProperty("LastReceivedSpeaker");
        lastReceivedTextProperty = serializedObject.FindProperty("LastReceivedText");
        totalReceivedProperty = serializedObject.FindProperty("TotalReceived");
        totalDispatchedProperty = serializedObject.FindProperty("TotalDispatched");
        droppedEmptyTranscriptProperty = serializedObject.FindProperty("DroppedEmptyTranscript");
        droppedByChannelFilterProperty = serializedObject.FindProperty("DroppedByChannelFilter");
        droppedNoPerformerProperty = serializedObject.FindProperty("DroppedNoPerformer");
        speaker1CountProperty = serializedObject.FindProperty("Speaker1Count");
        speaker2CountProperty = serializedObject.FindProperty("Speaker2Count");
        speaker3CountProperty = serializedObject.FindProperty("Speaker3Count");
        speaker4CountProperty = serializedObject.FindProperty("Speaker4Count");
        unknownSpeakerCountProperty = serializedObject.FindProperty("UnknownSpeakerCount");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawQuickActions();
        DrawWiring();
        DrawOscConfig();
        DrawMapping();
        DrawIngressMonitor();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawQuickActions()
    {
        var receiver = (MultiSpeakerOscSpeechReceiver)target;

        EditorGUILayout.LabelField("Quick Actions", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Start Receiver"))
        {
            receiver.StartReceiver();
            EditorUtility.SetDirty(receiver);
        }

        if (GUILayout.Button("Stop Receiver"))
        {
            receiver.StopReceiver();
            EditorUtility.SetDirty(receiver);
        }
        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button("Clear Debug Counters"))
        {
            receiver.ClearDebugCounters();
            EditorUtility.SetDirty(receiver);
        }

        EditorGUILayout.Space(6f);
    }

    private void DrawWiring()
    {
        EditorGUILayout.LabelField("Wiring", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(multiSpeakerPerformerProperty);
        EditorGUILayout.PropertyField(oscProperty);
    }

    private void DrawOscConfig()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("OSC Config", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(expectedAddressProperty);
        EditorGUILayout.PropertyField(acceptAnyChannelProperty);
        EditorGUILayout.PropertyField(allowedChannelsProperty);
    }

    private void DrawMapping()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Channel -> Speaker Mapping", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(channel1SpeakerIdProperty);
        EditorGUILayout.PropertyField(channel2SpeakerIdProperty);
        EditorGUILayout.PropertyField(channel3SpeakerIdProperty);
        EditorGUILayout.PropertyField(channel4SpeakerIdProperty);
    }

    private void DrawIngressMonitor()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Ingress Monitor", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(lastStatusProperty);
        EditorGUILayout.PropertyField(lastReceivedSpeakerProperty);
        EditorGUILayout.PropertyField(lastReceivedTextProperty);

        EditorGUILayout.LabelField("Totals", EditorStyles.miniBoldLabel);
        EditorGUILayout.PropertyField(totalReceivedProperty);
        EditorGUILayout.PropertyField(totalDispatchedProperty);
        EditorGUILayout.PropertyField(droppedEmptyTranscriptProperty);
        EditorGUILayout.PropertyField(droppedByChannelFilterProperty);
        EditorGUILayout.PropertyField(droppedNoPerformerProperty);

        EditorGUILayout.LabelField("Per Speaker", EditorStyles.miniBoldLabel);
        EditorGUILayout.PropertyField(speaker1CountProperty);
        EditorGUILayout.PropertyField(speaker2CountProperty);
        EditorGUILayout.PropertyField(speaker3CountProperty);
        EditorGUILayout.PropertyField(speaker4CountProperty);
        EditorGUILayout.PropertyField(unknownSpeakerCountProperty);
    }
}
