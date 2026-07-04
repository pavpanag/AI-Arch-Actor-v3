using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AaltoSystemV3
{
    /// <summary>
    /// Central rehearsal logger.
    /// Writes a structured event stream and a raw Unity log stream per launch session.
    /// </summary>
    public sealed class AaltoLaunchSessionLogger : MonoBehaviour
    {
        private const string RunCounterPrefsKey = "AaltoLaunchSessionLogger.RunCounter";
        // Write logs OUTSIDE Assets/ so Unity does not try to import live-written files
        // (that caused an endless reimport loop that pinned the main thread).
        public const string ExportRootRelativePath = "Recordings/AaltoExports";
        public const string FullEventLogsSubfolder = "full-event-logs";
        public const string DialogArchivesSubfolder = "dialog-archives";

        [Serializable]
        private sealed class EventEnvelope
        {
            public long event_id;
            public string session_id;
            public string ts_utc;
            public string ts_local;
            public string source;
            public string event_type;
            public string payload_json;
        }

        [Serializable]
        private sealed class RawLogEnvelope
        {
            public long raw_id;
            public string session_id;
            public string ts_utc;
            public string ts_local;
            public string log_type;
            public string message;
            public string stack_trace;
        }

        [Serializable]
        private sealed class SessionManifest
        {
            public string session_id;
            public string start_utc;
            public string end_utc;
            public string start_local;
            public string end_local;
            public string session_folder;
            public long structured_event_count;
            public long raw_log_count;
            public long audio_note_count;
            public long ux_timeline_row_count;
            public string audio_notes_folder;
            public string ux_timeline_csv;
            public string ux_timeline_jsonl;
            public bool closed_cleanly;
        }

        [Serializable]
        private sealed class UxTimelineRow
        {
            public long row_id;
            public string session_id;
            public string run_id;
            public string ts_utc;
            public string ts_local;
            public string source;
            public string event_type;
            public string system_mode;
            public string take_number;
            public string turn_index;
            public string turn_id;
            public string input_source;
            public string actor_source;
            public string actor_input;
            public string pending_dialogue_batch;
            public string prior_dialogue_context;
            public string react_now;
            public string selected_action;
            public string selected_memory_trigger;
            public string decision_note;
            public string execution_succeeded;
            public string execution_result;
            public string registry_send;
            public string neutral_return_send;
            public string model;
            public string raw_model_action;
            public string fallback_used;
            public string parse_error;
            public string prompt_memory_scope;
            public string prompt_memory_line_count;
            public string pending_dialogue_line_count;
            public string available_action_count;
            public string director_guidance;
            public string input_received_utc;
            public string decision_started_utc;
            public string model_response_utc;
            public string action_dispatch_utc;
            public string action_dispatch_completed_utc;
            public string latency_input_to_model_ms;
            public string latency_input_to_action_ms;
            public string trace_request_id;
            public long source_event_id;

            // High-signal config snapshot fields (kept narrow on purpose).
            public string action_labels_all;
            public string action_labels_added;
            public string action_labels_removed;
            public string action_memory_pairs;
        }

        private static readonly object Gate = new object();
        private static AaltoLaunchSessionLogger _instance;

        public static int CurrentRunNumber { get; private set; }
        public static string CurrentRunTag => "RUN " + CurrentRunNumber.ToString("000");
        public static string CurrentRunId => "RUN_" + CurrentRunNumber.ToString("000");

        private string _sessionId;
        private DateTime _startUtc;
        private DateTime _startLocal;
        private DateTime _endUtc;
        private DateTime _endLocal;

        private string _rootFolder;
        private string _sessionFolderOpen;
        private string _sessionFolderFinal;
        private string _eventsPath;
        private string _rawLogsPath;
        private string _eventsCsvPath;
        private string _rawCsvPath;
        private string _sqlitePath;
        private string _manifestPath;
        private string _summaryPath;
        private string _transcriptsPath;
        private string _uxTimelineCsvPath;
        private string _uxTimelineJsonlPath;
        private string _audioNotesFolder;

        [Header("Run Overlay")]
        public bool ShowRunOverlay = true;
        public bool PlayRunStartBeep = true;

        private StreamWriter _eventsWriter;
        private StreamWriter _rawWriter;
        private StreamWriter _eventsCsvWriter;
        private StreamWriter _rawCsvWriter;

        private object _sqliteConnection;
        private bool _sqliteReady;
        private string _sqliteProviderName;

        private long _eventSeq;
        private long _rawSeq;
        private long _audioNoteSeq;
        private bool _isClosed;

        private bool _audioNoteRecording;
        private AudioClip _audioNoteClip;
        private string _audioNoteLabel;
        private string _audioNoteBody;
        private string _audioNoteDeviceName;
        private int _audioNoteSampleRate;
        private DateTime _audioNoteStartUtc;
        private DateTime _audioNoteStartLocal;
        private long _audioNoteActiveId;
        private string _audioNoteStatus = "Idle.";

        private readonly Dictionary<string, long> _eventTypeCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _sourceCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly List<EventEnvelope> _structuredEvents = new List<EventEnvelope>();
        private long _uxTimelineRowCount;

        private static string BuildSessionFolderNameOpen(int runNumber, DateTime localStart)
        {
            var run = "RUN_" + Mathf.Max(0, runNumber).ToString("000");
            var stamp = localStart.ToString("yyyy-MM-dd_HH-mm-ss");
            return run + "__full-events__" + stamp + "__to__OPEN";
        }

        private static string BuildSessionFolderNameFinal(string openFolderName, DateTime localEnd)
        {
            var endStamp = localEnd.ToString("yyyy-MM-dd_HH-mm-ss");
            if (!string.IsNullOrWhiteSpace(openFolderName) && openFolderName.EndsWith("__to__OPEN", StringComparison.OrdinalIgnoreCase))
                return openFolderName.Substring(0, openFolderName.Length - "OPEN".Length) + endStamp;

            return openFolderName + "__to__" + endStamp;
        }

        private static string SessionFilePrefix(int runNumber, string sessionId)
        {
            var safeRun = "RUN_" + Mathf.Max(0, runNumber).ToString("000");
            var sid = (sessionId ?? string.Empty).Trim();
            if (sid.Length > 8)
                sid = sid.Substring(0, 8);
            if (string.IsNullOrWhiteSpace(sid))
                sid = "session";

            return safeRun + "__sid_" + sid;
        }

        public static string CurrentSessionFolder
        {
            get
            {
                lock (Gate)
                {
                    return _instance != null ? _instance._sessionFolderOpen : string.Empty;
                }
            }
        }

        public static string ResolveExportRootDirectory()
        {
#if UNITY_EDITOR
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (!string.IsNullOrWhiteSpace(projectRoot))
                return Path.GetFullPath(Path.Combine(projectRoot, ExportRootRelativePath));
#endif
            return Path.GetFullPath(Path.Combine(Application.persistentDataPath, "AaltoExports"));
        }

        public static string ResolveFullEventLogsDirectory()
        {
            return Path.Combine(ResolveExportRootDirectory(), FullEventLogsSubfolder);
        }

        public static string ResolveDialogArchivesDirectory()
        {
            return Path.Combine(ResolveExportRootDirectory(), DialogArchivesSubfolder);
        }

        public static bool IsAudioNoteRecording
        {
            get
            {
                lock (Gate)
                {
                    return _instance != null && _instance._audioNoteRecording;
                }
            }
        }

        public static string AudioNoteStatus
        {
            get
            {
                lock (Gate)
                {
                    return _instance != null ? _instance._audioNoteStatus : "Idle.";
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureInstance()
        {
            lock (Gate)
            {
                if (_instance != null)
                    return;

                var existing = FindFirstObjectByType<AaltoLaunchSessionLogger>();
                if (existing != null)
                {
                    _instance = existing;
                    return;
                }

                var go = new GameObject("AaltoLaunchSessionLogger");
                _instance = go.AddComponent<AaltoLaunchSessionLogger>();
                DontDestroyOnLoad(go);
            }
        }

        private void Awake()
        {
            lock (Gate)
            {
                if (_instance != null && _instance != this)
                {
                    Destroy(gameObject);
                    return;
                }

                _instance = this;
                DontDestroyOnLoad(gameObject);
                InitializeSessionIfNeeded();
            }
        }

        private void OnEnable()
        {
            Application.logMessageReceivedThreaded += OnUnityLogReceived;
        }

        private void OnDisable()
        {
            Application.logMessageReceivedThreaded -= OnUnityLogReceived;
        }

        private void OnApplicationQuit()
        {
            lock (Gate)
            {
                CloseSessionInternal(true);
            }
        }

        private void OnDestroy()
        {
            lock (Gate)
            {
                CloseSessionInternal(true);
            }
        }

        public static void EmitEvent(string source, string eventType, string payloadJson)
        {
            if (!Application.isPlaying)
                return;

            lock (Gate)
            {
                EnsureInstance();
                if (_instance == null)
                    return;

                _instance.InitializeSessionIfNeeded();
                _instance.EmitEventInternal(source, eventType, payloadJson);
            }
        }

        public static void EmitTextEvent(string source, string eventType, string text)
        {
            var payload = "{\"text\":\"" + EscapeJson(text ?? string.Empty) + "\"}";
            EmitEvent(source, eventType, payload);
        }

        public static void EmitAnnotation(string label, string body)
        {
            var payload = "{\"label\":\"" + EscapeJson(label ?? string.Empty) + "\",\"body\":\"" + EscapeJson(body ?? string.Empty) + "\"}";
            EmitEvent("annotation", "annotation.note", payload);
        }

        public static void EmitMarker(string label)
        {
            var payload = "{\"label\":\"" + EscapeJson(label ?? string.Empty) + "\"}";
            EmitEvent("annotation", "annotation.marker", payload);
        }

        public static void StartAudioNoteRecording(string label, string body, string deviceName)
        {
            if (!Application.isPlaying)
                return;

            lock (Gate)
            {
                EnsureInstance();
                if (_instance == null)
                    return;

                _instance.InitializeSessionIfNeeded();
                _instance.StartAudioNoteRecordingInternal(label, body, deviceName);
            }
        }

        public static void StopAudioNoteRecording()
        {
            if (!Application.isPlaying)
                return;

            lock (Gate)
            {
                if (_instance == null)
                    return;

                _instance.StopAudioNoteRecordingInternal("manual_stop");
            }
        }

        private void InitializeSessionIfNeeded()
        {
            if (_eventsWriter != null && _rawWriter != null)
                return;

            _startUtc = DateTime.UtcNow;
            _startLocal = DateTime.Now;
            _sessionId = Guid.NewGuid().ToString("N");

            CurrentRunNumber = PlayerPrefs.GetInt(RunCounterPrefsKey, 0) + 1;
            PlayerPrefs.SetInt(RunCounterPrefsKey, CurrentRunNumber);
            PlayerPrefs.Save();

            _rootFolder = ResolveFullEventLogsDirectory();
            Directory.CreateDirectory(_rootFolder);

            _sessionFolderOpen = Path.Combine(_rootFolder, BuildSessionFolderNameOpen(CurrentRunNumber, _startLocal));
            Directory.CreateDirectory(_sessionFolderOpen);
            _audioNotesFolder = Path.Combine(_sessionFolderOpen, "audio-notes");
            Directory.CreateDirectory(_audioNotesFolder);

            var filePrefix = SessionFilePrefix(CurrentRunNumber, _sessionId);

            _eventsPath = Path.Combine(_sessionFolderOpen, filePrefix + "__events.jsonl");
            _rawLogsPath = Path.Combine(_sessionFolderOpen, filePrefix + "__unity-raw.jsonl");
            _eventsCsvPath = Path.Combine(_sessionFolderOpen, filePrefix + "__events.csv");
            _rawCsvPath = Path.Combine(_sessionFolderOpen, filePrefix + "__unity-raw.csv");
            _sqlitePath = Path.Combine(_sessionFolderOpen, filePrefix + "__session.db");
            _manifestPath = Path.Combine(_sessionFolderOpen, filePrefix + "__session-manifest.json");
            _summaryPath = Path.Combine(_sessionFolderOpen, filePrefix + "__session-summary.md");
            _transcriptsPath = Path.Combine(_sessionFolderOpen, filePrefix + "__audio-note-transcripts.jsonl");
            _uxTimelineCsvPath = Path.Combine(_sessionFolderOpen, filePrefix + "__ux-timeline.csv");
            _uxTimelineJsonlPath = Path.Combine(_sessionFolderOpen, filePrefix + "__ux-timeline.jsonl");

            _eventsWriter = new StreamWriter(new FileStream(_eventsPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            _rawWriter = new StreamWriter(new FileStream(_rawLogsPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            _eventsCsvWriter = new StreamWriter(new FileStream(_eventsCsvPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            _rawCsvWriter = new StreamWriter(new FileStream(_rawCsvPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            if (new FileInfo(_eventsCsvPath).Length == 0)
                _eventsCsvWriter.WriteLine("event_id,session_id,ts_utc,ts_local,source,event_type,payload_json");

            if (new FileInfo(_rawCsvPath).Length == 0)
                _rawCsvWriter.WriteLine("raw_id,session_id,ts_utc,ts_local,log_type,message,stack_trace");

            TryInitializeSqlite();

            EmitEventInternal("launch", "session_started",
                "{\"run_number\":" + CurrentRunNumber + ",\"run_id\":\"" + EscapeJson(CurrentRunId) + "\",\"run_tag\":\"" + EscapeJson(CurrentRunTag) + "\"," +
                "\"session_folder\":\"" + EscapeJson(_sessionFolderOpen) + "\",\"persistent_data_path\":\"" + EscapeJson(Application.persistentDataPath) + "\"}");

            if (PlayRunStartBeep)
                PlayStartBeep();

            WriteManifest(false);
        }

        private void EmitEventInternal(string source, string eventType, string payloadJson)
        {
            if (_isClosed || _eventsWriter == null)
                return;

            _eventSeq++;
            var e = new EventEnvelope
            {
                event_id = _eventSeq,
                session_id = _sessionId,
                ts_utc = DateTime.UtcNow.ToString("o"),
                ts_local = DateTime.Now.ToString("o"),
                source = source ?? string.Empty,
                event_type = eventType ?? string.Empty,
                payload_json = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson
            };

            _structuredEvents.Add(e);
            _eventsWriter.WriteLine(ToJsonLine(e));
            _eventsCsvWriter?.WriteLine(ToCsvRow(
                e.event_id.ToString(),
                e.session_id,
                e.ts_utc,
                e.ts_local,
                e.source,
                e.event_type,
                e.payload_json));

            TryInsertSqliteEvent(e);

            if (!_eventTypeCounts.ContainsKey(e.event_type))
                _eventTypeCounts[e.event_type] = 0;
            _eventTypeCounts[e.event_type]++;

            if (!_sourceCounts.ContainsKey(e.source))
                _sourceCounts[e.source] = 0;
            _sourceCounts[e.source]++;

            WriteManifest(false);
        }

        private void OnUnityLogReceived(string condition, string stackTrace, LogType type)
        {
            lock (Gate)
            {
                if (_instance == null)
                    return;

                _instance.InitializeSessionIfNeeded();
                _instance.EmitRawLogInternal(condition, stackTrace, type);
            }
        }

        private void EmitRawLogInternal(string condition, string stackTrace, LogType type)
        {
            if (_isClosed || _rawWriter == null)
                return;

            _rawSeq++;
            var r = new RawLogEnvelope
            {
                raw_id = _rawSeq,
                session_id = _sessionId,
                ts_utc = DateTime.UtcNow.ToString("o"),
                ts_local = DateTime.Now.ToString("o"),
                log_type = type.ToString(),
                message = condition ?? string.Empty,
                stack_trace = stackTrace ?? string.Empty
            };

            _rawWriter.WriteLine(ToJsonLine(r));
            _rawCsvWriter?.WriteLine(ToCsvRow(
                r.raw_id.ToString(),
                r.session_id,
                r.ts_utc,
                r.ts_local,
                r.log_type,
                r.message,
                r.stack_trace));

            TryInsertSqliteRawLog(r);
            WriteManifest(false);
        }

        private void StartAudioNoteRecordingInternal(string label, string body, string deviceName)
        {
            if (!Application.isPlaying)
            {
                _audioNoteStatus = "Audio note recording requires Play mode.";
                return;
            }

            if (_audioNoteRecording)
            {
                _audioNoteStatus = "An audio note is already recording.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_audioNotesFolder))
            {
                _audioNoteStatus = "Audio note folder is not ready yet.";
                return;
            }

            var selectedDevice = ResolveMicrophoneDeviceName(deviceName);
            var sampleRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 44100;
            const int captureSeconds = 600;

            try
            {
                _audioNoteClip = Microphone.Start(selectedDevice, false, captureSeconds, sampleRate);
                if (_audioNoteClip == null)
                {
                    _audioNoteStatus = "Audio note recording could not start.";
                    return;
                }

                _audioNoteRecording = true;
                _audioNoteLabel = string.IsNullOrWhiteSpace(label) ? "audio_note" : label.Trim();
                _audioNoteBody = body ?? string.Empty;
                _audioNoteDeviceName = selectedDevice;
                _audioNoteSampleRate = sampleRate;
                _audioNoteStartUtc = DateTime.UtcNow;
                _audioNoteStartLocal = DateTime.Now;
                _audioNoteActiveId = ++_audioNoteSeq;
                _audioNoteStatus = "Recording audio note...";

                EmitEventInternal("audio_note", "audio_note_started",
                    "{\"audio_note_id\":" + _audioNoteActiveId +
                    ",\"label\":\"" + EscapeJson(_audioNoteLabel) + "\"" +
                    ",\"body\":\"" + EscapeJson(_audioNoteBody) + "\"" +
                    ",\"device_name\":\"" + EscapeJson(_audioNoteDeviceName) + "\"" +
                    ",\"sample_rate\":" + _audioNoteSampleRate +
                    ",\"session_folder\":\"" + EscapeJson(_sessionFolderOpen) + "\"" +
                    ",\"audio_notes_folder\":\"" + EscapeJson(_audioNotesFolder) + "\"" +
                    "}");
            }
            catch (Exception ex)
            {
                _audioNoteRecording = false;
                _audioNoteClip = null;
                _audioNoteStatus = "Audio note recording failed to start: " + ex.Message;
                TryEndMicrophone(selectedDevice);
            }
        }

        private void StopAudioNoteRecordingInternal(string reason)
        {
            if (!_audioNoteRecording)
            {
                if (!string.IsNullOrWhiteSpace(reason) && _audioNoteStatus == "Recording audio note...")
                    _audioNoteStatus = "No audio note is recording.";
                return;
            }

            var clip = _audioNoteClip;
            var deviceName = _audioNoteDeviceName;
            var label = _audioNoteLabel;
            var body = _audioNoteBody;
            var sampleRate = _audioNoteSampleRate > 0 ? _audioNoteSampleRate : 44100;
            var startedUtc = _audioNoteStartUtc;
            var startedLocal = _audioNoteStartLocal;
            var noteId = _audioNoteActiveId;

            _audioNoteRecording = false;
            _audioNoteClip = null;
            _audioNoteDeviceName = string.Empty;
            _audioNoteLabel = string.Empty;
            _audioNoteBody = string.Empty;
            _audioNoteSampleRate = 0;
            _audioNoteActiveId = 0;

            int recordedFrameCount = 0;
            try
            {
                recordedFrameCount = Mathf.Max(0, Microphone.GetPosition(deviceName));
            }
            catch
            {
                recordedFrameCount = 0;
            }

            TryEndMicrophone(deviceName);

            if (clip == null)
            {
                _audioNoteStatus = "Audio note stopped, but no clip was captured.";
                EmitAudioNoteFailed(noteId, label, body, deviceName, startedUtc, startedLocal, reason, _audioNoteStatus);
                return;
            }

            var channels = Mathf.Max(1, clip.channels);
            var totalSamples = Mathf.Max(0, clip.samples * channels);
            var sourceData = new float[totalSamples];
            if (!clip.GetData(sourceData, 0))
            {
                _audioNoteStatus = "Audio note stopped, but clip data could not be read.";
                EmitAudioNoteFailed(noteId, label, body, deviceName, startedUtc, startedLocal, reason, _audioNoteStatus);
                return;
            }

            var framesToKeep = recordedFrameCount > 0 ? Mathf.Min(recordedFrameCount, clip.samples) : clip.samples;
            var sampleCountToKeep = Mathf.Clamp(framesToKeep * channels, 0, sourceData.Length);
            if (sampleCountToKeep <= 0)
                sampleCountToKeep = sourceData.Length;

            var trimmedData = new float[sampleCountToKeep];
            Array.Copy(sourceData, trimmedData, sampleCountToKeep);

            var startStamp = startedLocal == default ? DateTime.Now : startedLocal;
            var fileName = BuildAudioNoteFileName(startStamp, noteId, label);
            var wavPath = Path.Combine(_audioNotesFolder, fileName);

            try
            {
                WriteWavFile(wavPath, trimmedData, channels, sampleRate);
                if (_audioNoteSeq < noteId)
                    _audioNoteSeq = noteId;
                _audioNoteStatus = "Audio note saved: " + fileName;

                EmitEventInternal("audio_note", "audio_note_saved",
                    "{\"audio_note_id\":" + noteId +
                    ",\"label\":\"" + EscapeJson(label) + "\"" +
                    ",\"body\":\"" + EscapeJson(body) + "\"" +
                    ",\"device_name\":\"" + EscapeJson(deviceName) + "\"" +
                    ",\"started_utc\":\"" + EscapeJson(startedUtc.ToString("o")) + "\"" +
                    ",\"started_local\":\"" + EscapeJson(startedLocal.ToString("o")) + "\"" +
                    ",\"ended_utc\":\"" + EscapeJson(DateTime.UtcNow.ToString("o")) + "\"" +
                    ",\"sample_rate\":" + sampleRate +
                    ",\"channels\":" + channels +
                    ",\"recorded_frames\":" + recordedFrameCount +
                    ",\"file_name\":\"" + EscapeJson(fileName) + "\"" +
                    ",\"file_path\":\"" + EscapeJson(wavPath) + "\"" +
                    ",\"audio_notes_folder\":\"" + EscapeJson(_audioNotesFolder) + "\"" +
                    ",\"transcript_status\":\"pending\"" +
                    ",\"transcript_engine\":\"vosk\"" +
                    ",\"reason\":\"" + EscapeJson(reason ?? string.Empty) + "\"" +
                    "}");

                AppendPendingTranscriptRecord(noteId, label, body, fileName, wavPath, startedUtc, startedLocal, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _audioNoteStatus = "Failed to save audio note WAV: " + ex.Message;
                EmitAudioNoteFailed(noteId, label, body, deviceName, startedUtc, startedLocal, reason, _audioNoteStatus);
            }
        }

        private void EmitAudioNoteFailed(long noteId, string label, string body, string deviceName, DateTime startedUtc, DateTime startedLocal, string reason, string message)
        {
            EmitEventInternal("audio_note", "audio_note_failed",
                "{\"audio_note_id\":" + noteId +
                ",\"label\":\"" + EscapeJson(label ?? string.Empty) + "\"" +
                ",\"body\":\"" + EscapeJson(body ?? string.Empty) + "\"" +
                ",\"device_name\":\"" + EscapeJson(deviceName ?? string.Empty) + "\"" +
                ",\"started_utc\":\"" + EscapeJson(startedUtc == default ? string.Empty : startedUtc.ToString("o")) + "\"" +
                ",\"started_local\":\"" + EscapeJson(startedLocal == default ? string.Empty : startedLocal.ToString("o")) + "\"" +
                ",\"reason\":\"" + EscapeJson(reason ?? string.Empty) + "\"" +
                ",\"message\":\"" + EscapeJson(message ?? string.Empty) + "\"" +
                "}");
        }

        private string ResolveMicrophoneDeviceName(string preferredDeviceName)
        {
            var devices = Microphone.devices;
            if (devices == null || devices.Length == 0)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(preferredDeviceName))
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    if (string.Equals(devices[i], preferredDeviceName, StringComparison.OrdinalIgnoreCase))
                        return devices[i];
                }
            }

            return devices[0];
        }

        private static void TryEndMicrophone(string deviceName)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(deviceName))
                    Microphone.End(deviceName);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }

        private static string BuildAudioNoteFileName(DateTime localStart, long noteId, string label)
        {
            var stamp = localStart.ToString("yyyy-MM-dd_HH-mm-ss");
            var safeLabel = MakeSafeFileName(string.IsNullOrWhiteSpace(label) ? "audio_note" : label.Trim());
            return "audio_note_" + stamp + "__" + noteId.ToString("000") + "__" + safeLabel + ".wav";
        }

        private static string BuildAudioTranscriptFileName(string wavFileName)
        {
            var baseName = Path.GetFileNameWithoutExtension(wavFileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(baseName))
                return "audio_note.txt";

            var m = Regex.Match(baseName, "^audio_note_(\\d{4}-\\d{2}-\\d{2}_\\d{2}-\\d{2}-\\d{2})__(\\d+)(?:__.*)?$", RegexOptions.CultureInvariant);
            if (m.Success)
            {
                var stamp = m.Groups[1].Value;
                var idRaw = m.Groups[2].Value;
                int id;
                if (int.TryParse(idRaw, out id))
                    return "audio_note_" + stamp + "__" + id.ToString("000") + "__audio_note.txt";
            }

            return baseName + ".txt";
        }

        private static string MakeSafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "audio_note";

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (Array.IndexOf(invalid, c) >= 0)
                    sb.Append('_');
                else if (char.IsWhiteSpace(c))
                    sb.Append('_');
                else
                    sb.Append(char.ToLowerInvariant(c));
            }

            return sb.Length == 0 ? "audio_note" : sb.ToString();
        }

        private void AppendPendingTranscriptRecord(long noteId, string label, string body, string wavFileName, string wavPath, DateTime startedUtc, DateTime startedLocal, DateTime endedUtc)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_transcriptsPath))
                    return;

                var transcriptDir = Path.Combine(_audioNotesFolder ?? string.Empty, "transcripts");
                var transcriptFileName = BuildAudioTranscriptFileName(wavFileName);
                var transcriptPath = Path.Combine(transcriptDir, transcriptFileName);

                var line =
                    "{" +
                    "\"ts_utc\":\"" + EscapeJson(DateTime.UtcNow.ToString("o")) + "\"," +
                    "\"session_id\":\"" + EscapeJson(_sessionId ?? string.Empty) + "\"," +
                    "\"run_number\":" + CurrentRunNumber + "," +
                    "\"run_id\":\"" + EscapeJson(CurrentRunId) + "\"," +
                    "\"audio_note_id\":" + noteId + "," +
                    "\"audio_file_path\":\"" + EscapeJson(wavPath ?? string.Empty) + "\"," +
                    "\"transcript_file_path\":\"" + EscapeJson(transcriptPath) + "\"," +
                    "\"transcript_text\":\"\"," +
                    "\"transcript_status\":\"pending\"," +
                    "\"transcript_engine\":\"vosk\"," +
                    "\"transcribed_utc\":\"\"," +
                    "\"transcript_confidence\":null," +
                    "\"corrected_transcript_text\":null," +
                    "\"correction_status\":\"none\"," +
                    "\"corrected_utc\":null," +
                    "\"note_label\":\"" + EscapeJson(label ?? string.Empty) + "\"," +
                    "\"note_body\":\"" + EscapeJson(body ?? string.Empty) + "\"," +
                    "\"wav_file\":\"" + EscapeJson(wavFileName ?? string.Empty) + "\"," +
                    "\"started_utc\":\"" + EscapeJson(startedUtc == default ? string.Empty : startedUtc.ToString("o")) + "\"," +
                    "\"started_local\":\"" + EscapeJson(startedLocal == default ? string.Empty : startedLocal.ToString("o")) + "\"," +
                    "\"ended_utc\":\"" + EscapeJson(endedUtc == default ? string.Empty : endedUtc.ToString("o")) + "\"" +
                    "}";

                using (var writer = new StreamWriter(new FileStream(_transcriptsPath, FileMode.Append, FileAccess.Write, FileShare.Read)))
                {
                    writer.NewLine = "\n";
                    writer.WriteLine(line);
                }
            }
            catch
            {
                // Transcript sidecar prep is best-effort and must not break logging.
            }
        }

        private static void WriteWavFile(string path, float[] samples, int channels, int sampleRate)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("WAV path cannot be empty.", nameof(path));

            samples = samples ?? Array.Empty<float>();
            channels = Mathf.Max(1, channels);
            sampleRate = Mathf.Max(8000, sampleRate);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using (var writer = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read)))
            {
                var byteCount = samples.Length * sizeof(short);
                var blockAlign = (short)(channels * sizeof(short));
                var byteRate = sampleRate * blockAlign;

                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + byteCount);
                writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write(blockAlign);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(byteCount);

                for (int i = 0; i < samples.Length; i++)
                {
                    var clamped = Mathf.Clamp(samples[i], -1f, 1f);
                    writer.Write((short)(clamped * short.MaxValue));
                }
            }
        }

        private void CloseSessionInternal(bool clean)
        {
            if (_isClosed)
                return;

            _isClosed = true;
            _endUtc = DateTime.UtcNow;
            _endLocal = DateTime.Now;

            try
            {
                if (_eventsWriter != null)
                {
                    StopAudioNoteRecordingInternal("shutdown");
                    EmitEventInternal("launch", "session_ending",
                        "{\"reason\":\"application_shutdown\",\"clean\":" + (clean ? "true" : "false") + "}");
                }
            }
            catch
            {
                // Best-effort logging during shutdown.
            }

            try { _eventsWriter?.Dispose(); } catch { }
            try { _rawWriter?.Dispose(); } catch { }
            try { _eventsCsvWriter?.Dispose(); } catch { }
            try { _rawCsvWriter?.Dispose(); } catch { }
            _eventsWriter = null;
            _rawWriter = null;
            _eventsCsvWriter = null;
            _rawCsvWriter = null;

            WriteUxTimelineExport();

            try
            {
                if (_sqliteConnection != null)
                {
                    InvokeIfExists(_sqliteConnection, "Close");
                    InvokeIfExists(_sqliteConnection, "Dispose");
                }
            }
            catch { }
            _sqliteConnection = null;
            _sqliteReady = false;

            WriteManifest(true);
            WriteSessionSummaryMarkdown();

            try
            {
                var openName = Path.GetFileName(_sessionFolderOpen);
                var finalName = BuildSessionFolderNameFinal(openName, _endLocal);
                var parentDir = Path.GetDirectoryName(_sessionFolderOpen);
                _sessionFolderFinal = string.IsNullOrWhiteSpace(parentDir) ? finalName : Path.Combine(parentDir, finalName);
                if (!string.Equals(_sessionFolderOpen, _sessionFolderFinal, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(_sessionFolderOpen) &&
                    !Directory.Exists(_sessionFolderFinal))
                {
                    Directory.Move(_sessionFolderOpen, _sessionFolderFinal);
                }
            }
            catch
            {
                // Keep open folder name if rename fails.
            }
        }

        private void WriteManifest(bool closedCleanly)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_manifestPath))
                    return;

                var manifest = new SessionManifest
                {
                    session_id = _sessionId,
                    start_utc = _startUtc.ToString("o"),
                    end_utc = _endUtc == default ? string.Empty : _endUtc.ToString("o"),
                    start_local = _startLocal.ToString("o"),
                    end_local = _endLocal == default ? string.Empty : _endLocal.ToString("o"),
                    session_folder = _sessionFolderOpen,
                    structured_event_count = _eventSeq,
                    raw_log_count = _rawSeq,
                    audio_note_count = _audioNoteSeq,
                    ux_timeline_row_count = _uxTimelineRowCount,
                    audio_notes_folder = _audioNotesFolder,
                    ux_timeline_csv = _uxTimelineCsvPath,
                    ux_timeline_jsonl = _uxTimelineJsonlPath,
                    closed_cleanly = closedCleanly
                };

                File.WriteAllText(_manifestPath, JsonUtility.ToJson(manifest, true));
            }
            catch
            {
                // Do not fail runtime for telemetry issues.
            }
        }

        private void WriteSessionSummaryMarkdown()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_summaryPath))
                    return;

                var sb = new StringBuilder();
                sb.AppendLine("# Aalto Launch Session Summary");
                sb.AppendLine();
                sb.AppendLine("## Session");
                sb.AppendLine("- Session ID: " + (_sessionId ?? string.Empty));
                sb.AppendLine("- Run: " + CurrentRunTag);
                sb.AppendLine("- Start (local): " + _startLocal.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("- End (local): " + (_endLocal == default ? "(open)" : _endLocal.ToString("yyyy-MM-dd HH:mm:ss")));
                sb.AppendLine("- Start (UTC): " + _startUtc.ToString("o"));
                sb.AppendLine("- End (UTC): " + (_endUtc == default ? "(open)" : _endUtc.ToString("o")));
                sb.AppendLine("- Structured events: " + _eventSeq);
                sb.AppendLine("- Raw Unity logs: " + _rawSeq);
                sb.AppendLine();

                sb.AppendLine("## Files");
                sb.AppendLine("- " + Path.GetFileName(_eventsPath));
                sb.AppendLine("- " + Path.GetFileName(_eventsCsvPath));
                sb.AppendLine("- " + Path.GetFileName(_rawLogsPath));
                sb.AppendLine("- " + Path.GetFileName(_rawCsvPath));
                sb.AppendLine("- " + Path.GetFileName(_uxTimelineCsvPath) + " (curated UX timeline)");
                sb.AppendLine("- " + Path.GetFileName(_uxTimelineJsonlPath) + " (curated UX timeline)");
                sb.AppendLine("- audio-notes/ (WAV audio notes)");
                sb.AppendLine("- " + Path.GetFileName(_transcriptsPath) + " (from Vosk sidecar)");
                sb.AppendLine("- audio-note-transcripts.csv (from Vosk sidecar)");
                sb.AppendLine("- audio-notes/transcripts/*.txt (from Vosk sidecar)");
                sb.AppendLine("- " + Path.GetFileName(_sqlitePath) + " (SQLite, when provider available)");
                sb.AppendLine("- " + Path.GetFileName(_manifestPath));
                sb.AppendLine("- " + Path.GetFileName(_summaryPath));
                sb.AppendLine();

                sb.AppendLine("## Storage Formats");
                sb.AppendLine("- JSONL: Full fidelity append logs");
                sb.AppendLine("- CSV: Spreadsheet-friendly flat export");
                sb.AppendLine("- SQLite: Queryable database (provider: " + (string.IsNullOrWhiteSpace(_sqliteProviderName) ? "unavailable" : _sqliteProviderName) + ")");
                sb.AppendLine("- UX Timeline: one curated row per performer decision or researcher note");
                sb.AppendLine();

                sb.AppendLine("## UX Timeline");
                sb.AppendLine("- Rows: " + _uxTimelineRowCount);
                sb.AppendLine("- Built from structured events at run shutdown.");
                sb.AppendLine("- Full prompts, raw model responses, and Unity logs remain in the appendix files rather than the main UX table.");
                sb.AppendLine();

                sb.AppendLine("## Events By Source");
                if (_sourceCounts.Count == 0)
                {
                    sb.AppendLine("- (none)");
                }
                else
                {
                    foreach (var kv in SortedByCountDescending(_sourceCounts))
                        sb.AppendLine("- " + kv.Key + ": " + kv.Value);
                }
                sb.AppendLine();

                sb.AppendLine("## Events By Type");
                if (_eventTypeCounts.Count == 0)
                {
                    sb.AppendLine("- (none)");
                }
                else
                {
                    foreach (var kv in SortedByCountDescending(_eventTypeCounts))
                        sb.AppendLine("- " + kv.Key + ": " + kv.Value);
                }
                sb.AppendLine();

                sb.AppendLine("## Notes");
                sb.AppendLine("- Structured events include full payload content per event in events.jsonl.");
                sb.AppendLine("- Raw Unity logs are captured in unity-raw.jsonl for low-level debugging.");
                sb.AppendLine("- Annotation events can be added from the Aalto Launch Session Logger editor window.");
                sb.AppendLine("- Audio notes are written as WAV files under audio-notes/ and logged as structured events.");

                File.WriteAllText(_summaryPath, sb.ToString());
            }
            catch
            {
                // Best-effort summary generation.
            }
        }

        private static List<KeyValuePair<string, long>> SortedByCountDescending(Dictionary<string, long> map)
        {
            var items = new List<KeyValuePair<string, long>>(map);
            items.Sort((a, b) =>
            {
                var cmp = b.Value.CompareTo(a.Value);
                return cmp != 0 ? cmp : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
            });
            return items;
        }

        private void WriteUxTimelineExport()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_uxTimelineCsvPath) || string.IsNullOrWhiteSpace(_uxTimelineJsonlPath))
                    return;

                var directory = Path.GetDirectoryName(_uxTimelineCsvPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var rows = BuildUxTimelineRows();
                _uxTimelineRowCount = rows.Count;

                var csv = new StringBuilder();
                csv.AppendLine("row_id,session_id,run_id,ts_utc,ts_local,source,event_type,system_mode,take_number,turn_index,turn_id,input_source,actor_source,actor_input,pending_dialogue_batch,prior_dialogue_context,react_now,selected_action,selected_memory_trigger,decision_note,execution_succeeded,execution_result,registry_send,neutral_return_send,model,raw_model_action,fallback_used,parse_error,prompt_memory_scope,prompt_memory_line_count,pending_dialogue_line_count,available_action_count,director_guidance,input_received_utc,decision_started_utc,model_response_utc,action_dispatch_utc,action_dispatch_completed_utc,latency_input_to_model_ms,latency_input_to_action_ms,trace_request_id,source_event_id,action_labels_all,action_labels_added,action_labels_removed,action_memory_pairs");

                var jsonl = new StringBuilder();
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    csv.AppendLine(ToCsvRow(
                        row.row_id.ToString(),
                        row.session_id,
                        row.run_id,
                        row.ts_utc,
                        row.ts_local,
                        row.source,
                        row.event_type,
                        row.system_mode,
                        row.take_number,
                        row.turn_index,
                        row.turn_id,
                        row.input_source,
                        row.actor_source,
                        row.actor_input,
                        row.pending_dialogue_batch,
                        row.prior_dialogue_context,
                        row.react_now,
                        row.selected_action,
                        row.selected_memory_trigger,
                        row.decision_note,
                        row.execution_succeeded,
                        row.execution_result,
                        row.registry_send,
                        row.neutral_return_send,
                        row.model,
                        row.raw_model_action,
                        row.fallback_used,
                        row.parse_error,
                        row.prompt_memory_scope,
                        row.prompt_memory_line_count,
                        row.pending_dialogue_line_count,
                        row.available_action_count,
                        row.director_guidance,
                        row.input_received_utc,
                        row.decision_started_utc,
                        row.model_response_utc,
                        row.action_dispatch_utc,
                        row.action_dispatch_completed_utc,
                        row.latency_input_to_model_ms,
                        row.latency_input_to_action_ms,
                        row.trace_request_id,
                        row.source_event_id.ToString(),
                        row.action_labels_all,
                        row.action_labels_added,
                        row.action_labels_removed,
                        row.action_memory_pairs));
                    jsonl.AppendLine(JsonUtility.ToJson(row));
                }

                File.WriteAllText(_uxTimelineCsvPath, csv.ToString());
                File.WriteAllText(_uxTimelineJsonlPath, jsonl.ToString());
            }
            catch
            {
                // UX export is derived data; never let it interrupt run shutdown.
            }
        }

        private List<UxTimelineRow> BuildUxTimelineRows()
        {
            var rows = new List<UxTimelineRow>();
            long rowId = 0;
            var lastActionLabelsBySource = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var lastStatusByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < _structuredEvents.Count; i++)
            {
                var e = _structuredEvents[i];
                if (e == null)
                    continue;

                UxTimelineRow row = null;
                if (string.Equals(e.event_type, "performer.turn_record", StringComparison.OrdinalIgnoreCase))
                    row = BuildPerformerTimelineRow(e);
                else if (string.Equals(e.event_type, "session_started", StringComparison.OrdinalIgnoreCase))
                    row = BuildSessionStartedTimelineRow(e);
                else if (string.Equals(e.event_type, "performer.action_labels_pulled", StringComparison.OrdinalIgnoreCase))
                    row = BuildActionLabelsPulledTimelineRow(e, lastActionLabelsBySource);
                else if (string.Equals(e.event_type, "bootstrap.generated", StringComparison.OrdinalIgnoreCase))
                    row = BuildBootstrapGeneratedTimelineRow(e);
                else if (string.Equals(e.event_type, "interview.summary_changed", StringComparison.OrdinalIgnoreCase))
                    row = BuildInterviewSummaryChangedTimelineRow(e);
                else if (string.Equals(e.event_type, "interview.followup_questions_changed", StringComparison.OrdinalIgnoreCase))
                    row = BuildInterviewFollowupQuestionsChangedTimelineRow(e);
                else if (IsInterviewUserInputEvent(e))
                    row = BuildInterviewUserInputTimelineRow(e);
                else if (IsActionMemoryMappingsEvent(e))
                    row = BuildActionMemoryMappingsTimelineRow(e);
                else if (IsSpeechReceiverEvent(e))
                    row = BuildSpeechReceiverTimelineRow(e);
                else if (IsDirectorGuidanceEvent(e))
                    row = BuildDirectorGuidanceTimelineRow(e);
                else if (IsStatusEvent(e))
                    row = BuildStatusTimelineRow(e, lastStatusByKey);
                else if (string.Equals(e.source, "annotation", StringComparison.OrdinalIgnoreCase))
                    row = BuildAnnotationTimelineRow(e);
                else if (string.Equals(e.source, "audio_note", StringComparison.OrdinalIgnoreCase) &&
                         (string.Equals(e.event_type, "audio_note_saved", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(e.event_type, "audio_note_failed", StringComparison.OrdinalIgnoreCase)))
                    row = BuildAudioNoteTimelineRow(e);
                else if (string.Equals(e.source, "audio_note_sidecar", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(e.event_type, "audio_note.transcript_ready", StringComparison.OrdinalIgnoreCase))
                    row = BuildAudioTranscriptTimelineRow(e);

                if (row == null)
                    continue;

                row.row_id = ++rowId;
                rows.Add(row);
            }

            return rows;
        }

        private static bool IsInterviewUserInputEvent(EventEnvelope e)
        {
            if (e == null)
                return false;

            var et = e.event_type ?? string.Empty;
            return string.Equals(et, "interview.user_input_changed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(et, "interview.user_input_submitted", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsActionMemoryMappingsEvent(EventEnvelope e)
        {
            if (e == null)
                return false;

            var et = e.event_type ?? string.Empty;
            return string.Equals(et, "action_memory.mappings_changed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(et, "action_memory.mappings_submitted", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(et, "action_memory.mapping_slot_added", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(et, "action_memory.mapping_submitted", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(et, "action_memory.mapping_removed", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSpeechReceiverEvent(EventEnvelope e)
        {
            if (e == null)
                return false;

            var et = (e.event_type ?? string.Empty).Trim();
            return et.StartsWith("receiver.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStatusEvent(EventEnvelope e)
        {
            if (e == null)
                return false;

            var et = e.event_type ?? string.Empty;
            if (!et.EndsWith(".status", StringComparison.OrdinalIgnoreCase))
                return false;

            var payload = e.payload_json ?? string.Empty;
            return payload.IndexOf("\"status\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsDirectorGuidanceEvent(EventEnvelope e)
        {
            if (e == null)
                return false;

            var et = e.event_type ?? string.Empty;
            return string.Equals(et, "performer.director_guidance_added", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(et, "performer.director_guidance_removed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(et, "performer.director_guidance_cleared", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(et, "performer.director_guidance_changed", StringComparison.OrdinalIgnoreCase);
        }

        private UxTimelineRow BuildStatusTimelineRow(EventEnvelope e, Dictionary<string, string> lastStatusByKey)
        {
            var payload = e.payload_json ?? "{}";
            var status = ExtractJsonString(payload, "status") ?? string.Empty;
            status = status.Trim();
            if (string.IsNullOrWhiteSpace(status))
                return null;

            var key = (e.source ?? string.Empty) + "|" + (e.event_type ?? string.Empty);
            if (lastStatusByKey != null && lastStatusByKey.TryGetValue(key, out var previous) &&
                string.Equals(previous ?? string.Empty, status, StringComparison.Ordinal))
            {
                return null;
            }

            if (lastStatusByKey != null)
                lastStatusByKey[key] = status;

            var row = BuildBaseTimelineRow(e, "status");
            row.input_source = "system_status";
            row.actor_source = e.source ?? string.Empty;
            row.actor_input = status;
            row.execution_succeeded = "true";
            row.execution_result = "Status update.";
            return row;
        }

        private UxTimelineRow BuildDirectorGuidanceTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "director_guidance");
            row.take_number = ExtractJsonNumber(payload, "take_number");
            row.input_source = "director_guidance";

            var et = e.event_type ?? string.Empty;
            if (string.Equals(et, "performer.director_guidance_added", StringComparison.OrdinalIgnoreCase))
            {
                row.actor_input = ExtractJsonString(payload, "added");
                row.director_guidance = ExtractJsonString(payload, "current");
                row.decision_note = ExtractJsonString(payload, "previous");
                row.execution_result = "Director guidance added.";
            }
            else if (string.Equals(et, "performer.director_guidance_removed", StringComparison.OrdinalIgnoreCase))
            {
                row.actor_input = ExtractJsonString(payload, "removed");
                row.director_guidance = ExtractJsonString(payload, "current");
                row.decision_note = ExtractJsonString(payload, "previous");
                row.execution_result = "Director guidance removed.";
            }
            else if (string.Equals(et, "performer.director_guidance_cleared", StringComparison.OrdinalIgnoreCase))
            {
                row.actor_input = "(cleared)";
                row.director_guidance = ExtractJsonString(payload, "current");
                row.decision_note = ExtractJsonString(payload, "previous");
                row.execution_result = "Director guidance cleared.";
            }
            else
            {
                row.actor_input = ExtractJsonString(payload, "current");
                row.director_guidance = ExtractJsonString(payload, "current");
                row.decision_note = ExtractJsonString(payload, "previous");
                row.execution_result = "Director guidance changed.";
            }

            row.execution_succeeded = "true";
            return row;
        }

        private UxTimelineRow BuildBaseTimelineRow(EventEnvelope e, string systemMode)
        {
            var payload = e != null ? (e.payload_json ?? "{}") : "{}";
            var runId = ExtractJsonString(payload, "run_id");
            if (string.IsNullOrWhiteSpace(runId))
                runId = CurrentRunId;

            return new UxTimelineRow
            {
                session_id = e.session_id ?? string.Empty,
                run_id = runId ?? string.Empty,
                ts_utc = e.ts_utc ?? string.Empty,
                ts_local = e.ts_local ?? string.Empty,
                source = e.source ?? string.Empty,
                event_type = e.event_type ?? string.Empty,
                system_mode = systemMode ?? string.Empty,
                source_event_id = e.event_id
            };
        }

        private UxTimelineRow BuildSessionStartedTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "session");
            row.input_source = "system";
            row.actor_input = ExtractJsonString(payload, "session_folder");
            row.decision_note = ExtractJsonString(payload, "persistent_data_path");
            row.execution_succeeded = "true";
            row.execution_result = "Session started.";
            return row;
        }

        private UxTimelineRow BuildBootstrapGeneratedTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "bootstrapper");
            row.input_source = "bootstrapper";
            row.actor_input = ExtractJsonString(payload, "objective");
            row.decision_note = ExtractJsonString(payload, "stance");
            row.execution_succeeded = "true";
            row.execution_result = "Objective/stance generated.";
            return row;
        }

        private UxTimelineRow BuildInterviewSummaryChangedTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "interview");
            row.input_source = "interview_summary";
            row.actor_input = ExtractJsonString(payload, "new_summary");
            row.decision_note = ExtractJsonString(payload, "previous_summary");
            row.execution_succeeded = "true";
            row.execution_result = "Interview summary changed.";
            return row;
        }

        private UxTimelineRow BuildInterviewFollowupQuestionsChangedTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "interview");
            row.input_source = "interview_followup_questions";
            row.actor_input = ExtractJsonString(payload, "new_questions");
            row.decision_note = ExtractJsonString(payload, "previous_questions");
            row.execution_succeeded = "true";
            row.execution_result = "Follow-up questions changed.";
            return row;
        }

        private UxTimelineRow BuildInterviewUserInputTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "interview");
            row.input_source = "interview_user_input";

            var field = ExtractJsonString(payload, "field");
            var previous = ExtractJsonString(payload, "previous");
            var current = ExtractJsonString(payload, "current");

            row.actor_source = field;
            row.actor_input = current;
            row.prior_dialogue_context = previous;
            row.execution_succeeded = "true";
            row.execution_result =
                string.Equals(e.event_type, "interview.user_input_submitted", StringComparison.OrdinalIgnoreCase)
                    ? "Interview input submitted."
                    : "Interview input edited.";
            return row;
        }

        private UxTimelineRow BuildActionMemoryMappingsTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "action_memory");
            row.input_source = "config_action_memory";

            if (string.Equals(e.event_type, "action_memory.mapping_slot_added", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.event_type, "action_memory.mapping_submitted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.event_type, "action_memory.mapping_removed", StringComparison.OrdinalIgnoreCase))
            {
                row.actor_source = ExtractJsonString(payload, "action_label");
                row.actor_input = ExtractJsonString(payload, "memory_trigger");
                row.decision_note = ExtractJsonNumber(payload, "index");
                row.execution_succeeded = "true";
                if (string.Equals(e.event_type, "action_memory.mapping_slot_added", StringComparison.OrdinalIgnoreCase))
                    row.execution_result = "Action-memory slot added.";
                else if (string.Equals(e.event_type, "action_memory.mapping_submitted", StringComparison.OrdinalIgnoreCase))
                    row.execution_result = "Action-memory mapping submitted.";
                else
                    row.execution_result = "Action-memory mapping removed.";
                return row;
            }

            var newPairs = ExtractJsonString(payload, "new_pairs");
            if (string.IsNullOrWhiteSpace(newPairs))
                newPairs = ExtractJsonString(payload, "pairs");

            row.action_memory_pairs = newPairs;
            row.action_labels_added = ExtractJsonString(payload, "added_labels");
            row.action_labels_removed = ExtractJsonString(payload, "removed_labels");

            row.actor_source = "action_memory";
            row.actor_input = newPairs;
            row.prior_dialogue_context = ExtractJsonString(payload, "previous_pairs");
            row.execution_succeeded = "true";
            row.execution_result =
                string.Equals(e.event_type, "action_memory.mappings_submitted", StringComparison.OrdinalIgnoreCase)
                    ? "Action-memory mappings submitted."
                    : "Action-memory mappings changed.";
            return row;
        }

        private UxTimelineRow BuildSpeechReceiverTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "speech");
            row.input_source = "speech_osc";

            var speakerId = ExtractJsonString(payload, "speaker_id");
            var text = ExtractJsonString(payload, "text");
            var reason = ExtractJsonString(payload, "reason");
            var channel = ExtractJsonNumber(payload, "channel");

            row.actor_source = !string.IsNullOrWhiteSpace(speakerId) ? speakerId : reason;
            row.actor_input = text;
            row.decision_note = channel;
            row.execution_succeeded = "true";
            row.execution_result = e.event_type ?? "receiver.event";

            // If it was an explicit drop, mark it as unsuccessful so it is easy to filter.
            if (!string.IsNullOrWhiteSpace(reason) &&
                (e.event_type ?? string.Empty).IndexOf("dropped", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                row.execution_succeeded = "false";
                row.execution_result = (e.event_type ?? "receiver.dropped") + ": " + reason;
            }

            return row;
        }

        private UxTimelineRow BuildActionLabelsPulledTimelineRow(
            EventEnvelope e,
            Dictionary<string, HashSet<string>> lastActionLabelsBySource)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "action_labels");
            row.input_source = "config_action_labels";

            // Different controllers used slightly different key names historically.
            var labelsCsv =
                ExtractJsonString(payload, "available_action_labels") ??
                ExtractJsonString(payload, "labels") ??
                string.Empty;
            var pairs = ExtractJsonString(payload, "action_memory_pairs") ?? string.Empty;

            var labelsNow = ParseCsvLabels(labelsCsv);
            var sourceKey = string.IsNullOrWhiteSpace(e.source) ? "unknown" : e.source;
            var hadPrevious = lastActionLabelsBySource.TryGetValue(sourceKey, out var previous) && previous != null;
            if (!hadPrevious)
                previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var added = DiffAdded(previous, labelsNow);
            var removed = DiffRemoved(previous, labelsNow);

            // High-signal filter: keep the first snapshot; afterwards keep only real changes.
            if (hadPrevious && added.Count == 0 && removed.Count == 0)
            {
                lastActionLabelsBySource[sourceKey] = labelsNow;
                return null;
            }

            row.action_labels_all = string.Join(",", ToSortedList(labelsNow).ToArray());
            row.action_labels_added = string.Join(",", added.ToArray());
            row.action_labels_removed = string.Join(",", removed.ToArray());
            row.action_memory_pairs = pairs;
            row.available_action_count = labelsNow.Count.ToString();

            row.actor_input = row.action_labels_added;
            row.pending_dialogue_batch = row.action_labels_all;
            row.execution_succeeded = "true";
            row.execution_result =
                hadPrevious
                    ? "Action labels changed (added=" + added.Count + ", removed=" + removed.Count + ")."
                    : "Action labels snapshot recorded (" + labelsNow.Count + " labels).";

            lastActionLabelsBySource[sourceKey] = labelsNow;
            return row;
        }

        private static HashSet<string> ParseCsvLabels(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var raw = (csv ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return set;

            var parts = raw.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                var s = (parts[i] ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    set.Add(s);
            }

            return set;
        }

        private static List<string> DiffAdded(HashSet<string> before, HashSet<string> after)
        {
            var added = new List<string>();
            if (after == null || after.Count == 0)
                return added;

            foreach (var s in after)
            {
                if (before == null || !before.Contains(s))
                    added.Add(s);
            }

            added.Sort(StringComparer.OrdinalIgnoreCase);
            return added;
        }

        private static List<string> DiffRemoved(HashSet<string> before, HashSet<string> after)
        {
            var removed = new List<string>();
            if (before == null || before.Count == 0)
                return removed;

            foreach (var s in before)
            {
                if (after == null || !after.Contains(s))
                    removed.Add(s);
            }

            removed.Sort(StringComparer.OrdinalIgnoreCase);
            return removed;
        }

        private static List<string> ToSortedList(HashSet<string> set)
        {
            var list = new List<string>();
            if (set == null || set.Count == 0)
                return list;

            foreach (var s in set)
            {
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private UxTimelineRow BuildPerformerTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, ExtractJsonString(payload, "turn_type"));
            row.take_number = ExtractJsonNumber(payload, "take_number");
            row.turn_index = ExtractJsonNumber(payload, "turn_index");
            row.turn_id = ExtractJsonString(payload, "turn_id");
            row.input_source = ExtractJsonString(payload, "input_source");
            row.actor_source = ExtractJsonString(payload, "actor_source");
            row.actor_input = ExtractJsonString(payload, "actor_input");
            row.pending_dialogue_batch = ExtractJsonString(payload, "pending_dialogue_batch");
            row.prior_dialogue_context = ExtractJsonString(payload, "prior_dialogue_context");
            row.react_now = ExtractJsonBool(payload, "react_now");
            row.selected_action = ExtractJsonString(payload, "selected_action");
            row.selected_memory_trigger = ExtractJsonString(payload, "selected_memory_trigger");
            row.decision_note = ExtractJsonString(payload, "decision_note");
            row.execution_succeeded = ExtractJsonBool(payload, "execution_succeeded");
            row.execution_result = ExtractJsonString(payload, "execution_result");
            row.registry_send = ExtractJsonString(payload, "registry_send");
            row.neutral_return_send = ExtractJsonString(payload, "neutral_return_send");
            row.model = ExtractJsonString(payload, "model");
            row.raw_model_action = ExtractJsonString(payload, "raw_model_action");
            row.fallback_used = ExtractJsonBool(payload, "fallback_used");
            row.parse_error = ExtractJsonString(payload, "parse_error");
            row.prompt_memory_scope = ExtractJsonString(payload, "prompt_memory_scope");
            row.prompt_memory_line_count = ExtractJsonNumber(payload, "prompt_memory_line_count");
            row.pending_dialogue_line_count = ExtractJsonNumber(payload, "pending_dialogue_line_count");
            row.available_action_count = ExtractJsonNumber(payload, "available_action_count");
            row.director_guidance = ExtractJsonString(payload, "director_guidance_snapshot");
            row.input_received_utc = ExtractJsonString(payload, "input_received_utc");
            row.decision_started_utc = ExtractJsonString(payload, "decision_started_utc");
            row.model_response_utc = ExtractJsonString(payload, "model_response_utc");
            row.action_dispatch_utc = ExtractJsonString(payload, "action_dispatch_utc");
            row.action_dispatch_completed_utc = ExtractJsonString(payload, "action_dispatch_completed_utc");
            row.latency_input_to_model_ms = ExtractJsonNumber(payload, "latency_input_to_model_ms");
            row.latency_input_to_action_ms = ExtractJsonNumber(payload, "latency_input_to_action_ms");
            row.trace_request_id = ExtractJsonString(payload, "trace_request_id");
            return row;
        }

        private UxTimelineRow BuildAnnotationTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "annotation");
            row.input_source = "researcher_note";
            row.actor_source = ExtractJsonString(payload, "label");
            row.actor_input = ExtractJsonString(payload, "body");
            if (string.IsNullOrWhiteSpace(row.actor_input))
                row.actor_input = ExtractJsonString(payload, "label");
            row.execution_succeeded = "true";
            row.execution_result = "Researcher annotation logged.";
            return row;
        }

        private UxTimelineRow BuildAudioNoteTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "audio_note");
            row.input_source = "researcher_audio_note";
            row.actor_source = ExtractJsonString(payload, "label");
            row.actor_input = ExtractJsonString(payload, "body");
            row.selected_action = ExtractJsonString(payload, "file_name");
            row.execution_succeeded = string.Equals(e.event_type, "audio_note_saved", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
            row.execution_result = ExtractJsonString(payload, "message");
            if (string.IsNullOrWhiteSpace(row.execution_result))
                row.execution_result = ExtractJsonString(payload, "reason");
            return row;
        }

        private UxTimelineRow BuildAudioTranscriptTimelineRow(EventEnvelope e)
        {
            var payload = e.payload_json ?? "{}";
            var row = BuildBaseTimelineRow(e, "audio_note_transcript");
            row.input_source = "audio_note_transcript";
            row.actor_input = ExtractJsonString(payload, "transcript_text");
            if (string.IsNullOrWhiteSpace(row.actor_input))
                row.actor_input = ExtractJsonString(payload, "raw");
            row.execution_succeeded = "true";
            row.execution_result = "Audio note transcript received.";
            return row;
        }

        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
                return string.Empty;

            var pattern = "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"";
            var match = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            return match.Success ? UnescapeJsonString(match.Groups[1].Value) : string.Empty;
        }

        private static string ExtractJsonNumber(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
                return string.Empty;

            var pattern = "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)";
            var match = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string ExtractJsonBool(string json, string key)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
                return string.Empty;

            var pattern = "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)";
            var match = Regex.Match(json, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : string.Empty;
        }

        private static string UnescapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c != '\\' || i + 1 >= value.Length)
                {
                    sb.Append(c);
                    continue;
                }

                var next = value[++i];
                switch (next)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < value.Length)
                        {
                            var hex = value.Substring(i + 1, 4);
                            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                        }
                        break;
                    default:
                        sb.Append(next);
                        break;
                }
            }

            return sb.ToString();
        }

        private static string ToCsvRow(params string[] values)
        {
            if (values == null || values.Length == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var v = values[i] ?? string.Empty;
                v = v.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
                sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
            }
            return sb.ToString();
        }

        private void TryInitializeSqlite()
        {
            _sqliteReady = false;
            _sqliteProviderName = string.Empty;
            _sqliteConnection = null;

            try
            {
                var monoConnType = Type.GetType("Mono.Data.Sqlite.SqliteConnection, Mono.Data.Sqlite", false);
                if (monoConnType != null)
                {
                    _sqliteConnection = Activator.CreateInstance(monoConnType, "Data Source=" + _sqlitePath + ";Version=3;");
                    InvokeIfExists(_sqliteConnection, "Open");
                    _sqliteReady = true;
                    _sqliteProviderName = "Mono.Data.Sqlite";
                }
                else
                {
                    var msConnType = Type.GetType("Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite", false);
                    if (msConnType != null)
                    {
                        _sqliteConnection = Activator.CreateInstance(msConnType, "Data Source=" + _sqlitePath);
                        InvokeIfExists(_sqliteConnection, "Open");
                        _sqliteReady = true;
                        _sqliteProviderName = "Microsoft.Data.Sqlite";
                    }
                }

                if (!_sqliteReady)
                    return;

                ExecSql(
                    "CREATE TABLE IF NOT EXISTS events (" +
                    "event_id INTEGER, session_id TEXT, ts_utc TEXT, ts_local TEXT, source TEXT, event_type TEXT, payload_json TEXT)");
                ExecSql("CREATE INDEX IF NOT EXISTS idx_events_type ON events(event_type)");
                ExecSql("CREATE INDEX IF NOT EXISTS idx_events_source ON events(source)");
                ExecSql(
                    "CREATE TABLE IF NOT EXISTS raw_logs (" +
                    "raw_id INTEGER, session_id TEXT, ts_utc TEXT, ts_local TEXT, log_type TEXT, message TEXT, stack_trace TEXT)");
                ExecSql("CREATE INDEX IF NOT EXISTS idx_raw_type ON raw_logs(log_type)");
            }
            catch
            {
                _sqliteReady = false;
                _sqliteProviderName = string.Empty;
                try
                {
                    if (_sqliteConnection != null)
                    {
                        InvokeIfExists(_sqliteConnection, "Close");
                        InvokeIfExists(_sqliteConnection, "Dispose");
                    }
                }
                catch { }
                _sqliteConnection = null;
            }
        }

        private void TryInsertSqliteEvent(EventEnvelope e)
        {
            if (!_sqliteReady || _sqliteConnection == null || e == null)
                return;

            var sql = "INSERT INTO events(event_id,session_id,ts_utc,ts_local,source,event_type,payload_json) VALUES(" +
                      e.event_id + "," +
                      "'" + EscapeSql(e.session_id) + "'," +
                      "'" + EscapeSql(e.ts_utc) + "'," +
                      "'" + EscapeSql(e.ts_local) + "'," +
                      "'" + EscapeSql(e.source) + "'," +
                      "'" + EscapeSql(e.event_type) + "'," +
                      "'" + EscapeSql(e.payload_json) + "')";
            ExecSql(sql);
        }

        private void TryInsertSqliteRawLog(RawLogEnvelope r)
        {
            if (!_sqliteReady || _sqliteConnection == null || r == null)
                return;

            var sql = "INSERT INTO raw_logs(raw_id,session_id,ts_utc,ts_local,log_type,message,stack_trace) VALUES(" +
                      r.raw_id + "," +
                      "'" + EscapeSql(r.session_id) + "'," +
                      "'" + EscapeSql(r.ts_utc) + "'," +
                      "'" + EscapeSql(r.ts_local) + "'," +
                      "'" + EscapeSql(r.log_type) + "'," +
                      "'" + EscapeSql(r.message) + "'," +
                      "'" + EscapeSql(r.stack_trace) + "')";
            ExecSql(sql);
        }

        private void ExecSql(string sql)
        {
            if (!_sqliteReady || _sqliteConnection == null || string.IsNullOrWhiteSpace(sql))
                return;

            try
            {
                var cmd = CreateCommand(_sqliteConnection, sql);
                if (cmd == null)
                    return;

                InvokeIfExists(cmd, "ExecuteNonQuery");
                InvokeIfExists(cmd, "Dispose");
            }
            catch
            {
                // Keep logging alive even if SQLite fails.
            }
        }

        private static object CreateCommand(object connection, string commandText)
        {
            if (connection == null)
                return null;

            var connType = connection.GetType();
            var createCmdMethod = connType.GetMethod("CreateCommand", BindingFlags.Public | BindingFlags.Instance);
            if (createCmdMethod == null)
                return null;

            var cmd = createCmdMethod.Invoke(connection, null);
            if (cmd == null)
                return null;

            var cmdType = cmd.GetType();
            var cmdTextProp = cmdType.GetProperty("CommandText", BindingFlags.Public | BindingFlags.Instance);
            if (cmdTextProp != null && cmdTextProp.CanWrite)
                cmdTextProp.SetValue(cmd, commandText, null);

            return cmd;
        }

        private static void InvokeIfExists(object target, string methodName)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName))
                return;

            var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            method?.Invoke(target, null);
        }

        private static string EscapeSql(string s)
        {
            return (s ?? string.Empty).Replace("'", "''");
        }

        private void OnGUI()
        {
            if (!Application.isPlaying || !ShowRunOverlay)
                return;

            var rect = new Rect(16f, 16f, 220f, 58f);
            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(rect);
            GUILayout.Label(CurrentRunTag);
            GUILayout.Label(string.IsNullOrWhiteSpace(_sessionFolderOpen) ? "session initializing..." : Path.GetFileName(_sessionFolderOpen));
            GUILayout.EndArea();
        }

        private void PlayStartBeep()
        {
            try
            {
                var clip = CreateBeepClip();
                if (clip != null)
                    AudioSource.PlayClipAtPoint(clip, Vector3.zero);
            }
            catch
            {
                // Beep is optional.
            }
        }

        private static AudioClip CreateBeepClip()
        {
            const int sampleRate = 44100;
            const float durationSeconds = 0.12f;
            const float frequency = 880f;
            var sampleCount = Mathf.CeilToInt(sampleRate * durationSeconds);
            var data = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                var t = (float)i / sampleRate;
                data[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * 0.2f;
            }

            var clip = AudioClip.Create("AaltoRunBeep", sampleCount, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static string ToJsonLine(EventEnvelope e)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"event_id\":").Append(e.event_id).Append(',');
            sb.Append("\"session_id\":\"").Append(EscapeJson(e.session_id)).Append("\",");
            sb.Append("\"ts_utc\":\"").Append(EscapeJson(e.ts_utc)).Append("\",");
            sb.Append("\"ts_local\":\"").Append(EscapeJson(e.ts_local)).Append("\",");
            sb.Append("\"source\":\"").Append(EscapeJson(e.source)).Append("\",");
            sb.Append("\"event_type\":\"").Append(EscapeJson(e.event_type)).Append("\",");
            sb.Append("\"payload\":").Append(string.IsNullOrWhiteSpace(e.payload_json) ? "{}" : e.payload_json);
            sb.Append("}");
            return sb.ToString();
        }

        private static string ToJsonLine(RawLogEnvelope r)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"raw_id\":").Append(r.raw_id).Append(',');
            sb.Append("\"session_id\":\"").Append(EscapeJson(r.session_id)).Append("\",");
            sb.Append("\"ts_utc\":\"").Append(EscapeJson(r.ts_utc)).Append("\",");
            sb.Append("\"ts_local\":\"").Append(EscapeJson(r.ts_local)).Append("\",");
            sb.Append("\"log_type\":\"").Append(EscapeJson(r.log_type)).Append("\",");
            sb.Append("\"message\":\"").Append(EscapeJson(r.message)).Append("\",");
            sb.Append("\"stack_trace\":\"").Append(EscapeJson(r.stack_trace)).Append("\"");
            sb.Append("}");
            return sb.ToString();
        }

        public static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            var sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 32)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
