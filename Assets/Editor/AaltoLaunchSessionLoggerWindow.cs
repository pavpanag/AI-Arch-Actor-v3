using System;
using AaltoSystemV3;
using UnityEditor;
using UnityEngine;

public sealed class AaltoLaunchSessionLoggerWindow : EditorWindow
{
    [Serializable]
    private sealed class NoteEntry
    {
        public string TimeStamp;
        public string Kind;
        public string Label;
        public string Body;
    }

    private Vector2 _notesHistoryScroll;
    private string _noteText = string.Empty;
    private int _microphoneDeviceIndex;
    private string[] _microphoneDevices = Array.Empty<string>();
    private readonly System.Collections.Generic.List<NoteEntry> _notesHistory = new System.Collections.Generic.List<NoteEntry>();

    [MenuItem("Log Event/Launch Logging")]
    public static void OpenWindow()
    {
        var window = GetWindow<AaltoLaunchSessionLoggerWindow>("Launch Logging");
        window.minSize = new Vector2(520f, 240f);
        window.Show();
    }

    private void OnEnable()
    {
        EditorApplication.update += Repaint;
    }

    private void OnDisable()
    {
        EditorApplication.update -= Repaint;
    }

    private void OnGUI()
    {
        HandleKeyboardShortcuts();

        EditorGUILayout.LabelField("Launch Logging", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Type a note, then add it to the session log. Use audio note buttons to record WAV notes.", MessageType.Info);
        EditorGUILayout.HelpBox("Shortcut: Ctrl/Cmd+Enter = Add Note", MessageType.None);
        if (!Application.isPlaying)
            EditorGUILayout.HelpBox("Play mode is required to write notes into session datasets.", MessageType.Warning);

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Current run:", EditorStyles.miniBoldLabel);
        EditorGUILayout.LabelField(string.IsNullOrWhiteSpace(AaltoLaunchSessionLogger.CurrentRunTag) ? "(not running yet)" : AaltoLaunchSessionLogger.CurrentRunTag);

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Take Notes", EditorStyles.boldLabel);
        _noteText = EditorGUILayout.TextArea(_noteText, GUILayout.MinHeight(72f));

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            if (GUILayout.Button("Add Note", GUILayout.Height(30f)))
            {
                AddWrittenNote();
            }
        }

        using (new EditorGUI.DisabledScope(!Application.isPlaying || AaltoLaunchSessionLogger.IsAudioNoteRecording))
        {
            if (GUILayout.Button("Record Audio Note", GUILayout.Height(30f)))
            {
                StartAudioNoteRecording();
            }
        }

        using (new EditorGUI.DisabledScope(!AaltoLaunchSessionLogger.IsAudioNoteRecording))
        {
            if (GUILayout.Button("Stop Audio Note", GUILayout.Height(30f)))
            {
                StopAudioNoteRecording();
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Audio Note Status", AaltoLaunchSessionLogger.AudioNoteStatus);

        EditorGUILayout.Space(8f);
        DrawNotesHistorySection();
    }

    private void AddWrittenNote()
    {
        if (!Application.isPlaying)
            return;

        var body = (_noteText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(body))
            return;

        AaltoLaunchSessionLogger.EmitAnnotation("note", body);
        AppendHistory("written_note", "note", body);
        _noteText = string.Empty;
    }

    private void StartAudioNoteRecording()
    {
        RefreshMicrophoneDevices();
        var deviceName = _microphoneDevices.Length > 0
            ? _microphoneDevices[Mathf.Clamp(_microphoneDeviceIndex, 0, _microphoneDevices.Length - 1)]
            : string.Empty;

        var body = (_noteText ?? string.Empty).Trim();
        AaltoLaunchSessionLogger.StartAudioNoteRecording("audio_note", body, deviceName);
        AppendHistory("audio_note_start", "audio_note", body);
    }

    private void StopAudioNoteRecording()
    {
        AaltoLaunchSessionLogger.StopAudioNoteRecording();
        AppendHistory("audio_note_stop", "audio_note", AaltoLaunchSessionLogger.AudioNoteStatus);
        _noteText = string.Empty;
    }

    private void DrawNotesHistorySection()
    {
        EditorGUILayout.LabelField("Notes Taken", EditorStyles.boldLabel);

        if (_notesHistory.Count == 0)
        {
            EditorGUILayout.HelpBox("No notes submitted yet in this window.", MessageType.None);
            return;
        }

        using (new EditorGUI.DisabledScope(true))
        {
            _notesHistoryScroll = EditorGUILayout.BeginScrollView(_notesHistoryScroll, GUILayout.Height(130f));
            for (int i = _notesHistory.Count - 1; i >= 0; i--)
            {
                var n = _notesHistory[i];
                var body = string.IsNullOrWhiteSpace(n.Body) ? "" : (" | " + n.Body);
                EditorGUILayout.TextField($"{n.TimeStamp} | {n.Kind} | {n.Label}{body}");
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void AppendHistory(string kind, string label, string body)
    {
        _notesHistory.Add(new NoteEntry
        {
            TimeStamp = DateTime.Now.ToString("HH:mm:ss"),
            Kind = string.IsNullOrWhiteSpace(kind) ? "note" : kind,
            Label = string.IsNullOrWhiteSpace(label) ? "(no label)" : label,
            Body = body ?? string.Empty,
        });
    }

    private void RefreshMicrophoneDevices()
    {
        var devices = Microphone.devices;
        if (devices == null)
        {
            _microphoneDevices = Array.Empty<string>();
            _microphoneDeviceIndex = 0;
            return;
        }

        if (_microphoneDevices.Length == devices.Length)
        {
            var same = true;
            for (int i = 0; i < devices.Length; i++)
            {
                if (!string.Equals(_microphoneDevices[i], devices[i], StringComparison.Ordinal))
                {
                    same = false;
                    break;
                }
            }

            if (same)
                return;
        }

        _microphoneDevices = devices;
        if (_microphoneDevices.Length == 0)
            _microphoneDeviceIndex = 0;
        else
            _microphoneDeviceIndex = Mathf.Clamp(_microphoneDeviceIndex, 0, _microphoneDevices.Length - 1);
    }

    private void HandleKeyboardShortcuts()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown)
            return;

        var cmdOrCtrl = e.command || e.control;
        if (!cmdOrCtrl)
            return;

        switch (e.keyCode)
        {
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                AddWrittenNote();
                e.Use();
                Repaint();
                break;
        }
    }
}
