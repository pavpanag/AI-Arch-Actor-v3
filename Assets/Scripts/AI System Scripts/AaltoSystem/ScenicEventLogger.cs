using System;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AaltoSystemV3
{
    /// <summary>
    /// Writes replayable scenic decisions to JSONL under a folder separate from API traces.
    /// </summary>
    public static class ScenicEventLogger
    {
        private static readonly object _lock = new object();

        private static string _sessionId = Guid.NewGuid().ToString();
        private static int _nextTurnIndex = 1;
        private static string _folderPath;
        private static string _sessionFilePath;

        public static string CurrentSessionId => _sessionId;
        public static string LogFolderPath => EnsureFolderPath();
        public static string SessionFilePath => EnsureSessionFilePath();
        public static int NextTurnIndex => _nextTurnIndex;

        public static event Action<ScenicEventLogEntry, string> ScenicEventLogged;

        public static void StartNewSession(string sessionId = null)
        {
            lock (_lock)
            {
                _sessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString() : sessionId.Trim();
                _nextTurnIndex = 1;
                _sessionFilePath = null;
            }

            Debug.Log($"[ScenicEventLogger] New session started: {_sessionId}");
            Debug.Log($"[ScenicEventLogger] Scenic log file: {SessionFilePath}");
        }

        public static ScenicEventLogEntry LogEvent(ScenicEventLogEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            ScenicEventLogEntry finalized;
            string path;
            lock (_lock)
            {
                finalized = FinalizeEntry(entry);
                path = EnsureSessionFilePath();

                var line = JsonUtility.ToJson(finalized);
                File.AppendAllText(path, line + Environment.NewLine);
            }

            ScenicEventLogged?.Invoke(finalized, path);
            return finalized;
        }

        public static ScenicEventLogEntry LogEvent(
            string latestMoveText,
            string dialogueWindowUsed,
            string characterSummary,
            string previousObjective,
            string previousStance,
            string updatedObjective,
            string updatedStance,
            string intendedAction,
            string selectedActionLabel,
            string resolvedMemoryId,
            string resolvedMemoryLabel,
            string reason,
            bool executionSucceeded,
            string executionResult,
            string traceSessionId = null,
            string traceRequestId = null,
            string traceContextTag = null,
            string rawResponseReference = null)
        {
            var entry = new ScenicEventLogEntry
            {
                latestMoveText = latestMoveText,
                dialogueWindowUsed = dialogueWindowUsed,
                characterSummary = characterSummary,
                previousObjective = previousObjective,
                previousStance = previousStance,
                updatedObjective = updatedObjective,
                updatedStance = updatedStance,
                intendedAction = intendedAction,
                selectedActionLabel = selectedActionLabel,
                resolvedMemoryId = resolvedMemoryId,
                resolvedMemoryLabel = resolvedMemoryLabel,
                reason = reason,
                executionSucceeded = executionSucceeded,
                executionResult = executionResult,
                traceSessionId = traceSessionId,
                traceRequestId = traceRequestId,
                traceContextTag = traceContextTag,
                rawResponseReference = rawResponseReference
            };

            return LogEvent(entry);
        }

        public static ScenicEventLogEntry WriteFakeEventForTest()
        {
            return LogEvent(
                latestMoveText: "Please let me leave this room.",
                dialogueWindowUsed: "User: I need to get out. | Aalto: You belong here.",
                characterSummary: "A possessive room that fears abandonment.",
                previousObjective: "Keep the visitor inside without panic escalation.",
                previousStance: "firm",
                updatedObjective: "Reassure while still preventing exit.",
                updatedStance: "enclosing",
                intendedAction: "Defuse urgency and redirect attention inward.",
                selectedActionLabel: "calm down",
                resolvedMemoryId: "4",
                resolvedMemoryLabel: "memory 4",
                reason: "The user expressed urgency; de-escalation best preserves control.",
                executionSucceeded: true,
                executionResult: "OSC sent successfully to light controller.",
                traceSessionId: ChatTraceLogger.CurrentSessionId,
                traceRequestId: "fake-request-id",
                traceContextTag: "Aalto:Test",
                rawResponseReference: ChatTraceLogger.LastSavedTraceFilePath
            );
        }

        public static void RevealLogFolder()
        {
            var path = LogFolderPath;
            Debug.Log($"[ScenicEventLogger] Scenic log folder: {path}");
#if UNITY_EDITOR
            EditorUtility.RevealInFinder(path);
#endif
        }

        public static void RevealSessionFile()
        {
            var path = SessionFilePath;
            Debug.Log($"[ScenicEventLogger] Scenic session file: {path}");
#if UNITY_EDITOR
            EditorUtility.RevealInFinder(path);
#endif
        }

        private static ScenicEventLogEntry FinalizeEntry(ScenicEventLogEntry source)
        {
            return new ScenicEventLogEntry
            {
                sessionId = string.IsNullOrWhiteSpace(source.sessionId) ? _sessionId : source.sessionId,
                turnIndex = source.turnIndex > 0 ? source.turnIndex : _nextTurnIndex++,
                timestampUtc = string.IsNullOrWhiteSpace(source.timestampUtc) ? DateTime.UtcNow.ToString("o") : source.timestampUtc,
                latestMoveText = source.latestMoveText ?? string.Empty,
                dialogueWindowUsed = source.dialogueWindowUsed ?? string.Empty,
                characterSummary = source.characterSummary ?? string.Empty,
                previousObjective = source.previousObjective ?? string.Empty,
                previousStance = source.previousStance ?? string.Empty,
                updatedObjective = source.updatedObjective ?? string.Empty,
                updatedStance = source.updatedStance ?? string.Empty,
                intendedAction = source.intendedAction ?? string.Empty,
                selectedActionLabel = source.selectedActionLabel ?? string.Empty,
                resolvedMemoryId = source.resolvedMemoryId ?? string.Empty,
                resolvedMemoryLabel = source.resolvedMemoryLabel ?? string.Empty,
                reason = source.reason ?? string.Empty,
                executionSucceeded = source.executionSucceeded,
                executionResult = source.executionResult ?? string.Empty,
                traceSessionId = source.traceSessionId,
                traceRequestId = source.traceRequestId,
                traceContextTag = source.traceContextTag,
                rawResponseReference = source.rawResponseReference
            };
        }

        private static string EnsureFolderPath()
        {
            if (!string.IsNullOrWhiteSpace(_folderPath)) return _folderPath;
            _folderPath = Path.Combine(Application.persistentDataPath, "scenic_event_logs");
            Directory.CreateDirectory(_folderPath);
            return _folderPath;
        }

        private static string EnsureSessionFilePath()
        {
            if (!string.IsNullOrWhiteSpace(_sessionFilePath)) return _sessionFilePath;
            var folder = EnsureFolderPath();
            _sessionFilePath = Path.Combine(folder, $"scenic_{_sessionId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jsonl");
            return _sessionFilePath;
        }
    }
}
