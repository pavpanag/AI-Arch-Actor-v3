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
            public string audio_notes_folder;
            public bool closed_cleanly;
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

        private static string BuildSessionFolderNameOpen(int runNumber, DateTime localStart)
        {
            var run = "RUN_" + Mathf.Max(0, runNumber).ToString("000");
            var stamp = localStart.ToString("yyyy-MM-dd_HH-mm-ss");
            return run + "__session_" + stamp + "__to__OPEN";
        }

        private static string BuildSessionFolderNameFinal(string openFolderName, DateTime localEnd)
        {
            var endStamp = localEnd.ToString("yyyy-MM-dd_HH-mm-ss");
            if (!string.IsNullOrWhiteSpace(openFolderName) && openFolderName.EndsWith("__to__OPEN", StringComparison.OrdinalIgnoreCase))
                return openFolderName.Substring(0, openFolderName.Length - "OPEN".Length) + endStamp;

            return openFolderName + "__to__" + endStamp;
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

            _rootFolder = Path.Combine(Application.persistentDataPath, "AaltoLaunchLogs");
            Directory.CreateDirectory(_rootFolder);

            _sessionFolderOpen = Path.Combine(_rootFolder, BuildSessionFolderNameOpen(CurrentRunNumber, _startLocal));
            Directory.CreateDirectory(_sessionFolderOpen);
            _audioNotesFolder = Path.Combine(_sessionFolderOpen, "audio-notes");
            Directory.CreateDirectory(_audioNotesFolder);

            _eventsPath = Path.Combine(_sessionFolderOpen, "events.jsonl");
            _rawLogsPath = Path.Combine(_sessionFolderOpen, "unity-raw.jsonl");
            _eventsCsvPath = Path.Combine(_sessionFolderOpen, "events.csv");
            _rawCsvPath = Path.Combine(_sessionFolderOpen, "unity-raw.csv");
            _sqlitePath = Path.Combine(_sessionFolderOpen, "session.db");
            _manifestPath = Path.Combine(_sessionFolderOpen, "session-manifest.json");
            _summaryPath = Path.Combine(_sessionFolderOpen, "session-summary.md");
            _transcriptsPath = Path.Combine(_sessionFolderOpen, "audio-note-transcripts.jsonl");

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
                    audio_notes_folder = _audioNotesFolder,
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
                sb.AppendLine("- events.jsonl");
                sb.AppendLine("- events.csv");
                sb.AppendLine("- unity-raw.jsonl");
                sb.AppendLine("- unity-raw.csv");
                sb.AppendLine("- audio-notes/ (WAV audio notes)");
                sb.AppendLine("- audio-note-transcripts.jsonl (from Vosk sidecar)");
                sb.AppendLine("- audio-note-transcripts.csv (from Vosk sidecar)");
                sb.AppendLine("- audio-notes/transcripts/*.txt (from Vosk sidecar)");
                sb.AppendLine("- session.db (SQLite, when provider available)");
                sb.AppendLine("- session-manifest.json");
                sb.AppendLine("- session-summary.md");
                sb.AppendLine();

                sb.AppendLine("## Storage Formats");
                sb.AppendLine("- JSONL: Full fidelity append logs");
                sb.AppendLine("- CSV: Spreadsheet-friendly flat export");
                sb.AppendLine("- SQLite: Queryable database (provider: " + (string.IsNullOrWhiteSpace(_sqliteProviderName) ? "unavailable" : _sqliteProviderName) + ")");
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
