using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace AaltoSystemV3
{
    /// <summary>
    /// Phase 7.1 + 7.2:
    /// Reads scenic JSONL logs and replays logged sequence in order,
    /// while exposing text trace for analytical review.
    /// </summary>
    public sealed class ScenicReplayRunner : MonoBehaviour
    {
        [Header("Replay Source")]
        [Tooltip("If empty, runner loads latest scenic event .jsonl from full-event-logs/scenic-events.")]
        public string ReplayFilePath;
        [Tooltip("Default delay between replayed turns.")]
        [Range(0f, 3f)] public float DelayBetweenTurnsSeconds = 0.35f;

        [Header("Execution")]
        [Tooltip("When true, only logs what would be executed, no external trigger call.")]
        public bool DryRunOnly = true;
        [Tooltip("Optional callback target for memory triggers.")]
        public MonoBehaviour TriggerTarget;
        [Tooltip("Method on TriggerTarget with signature: void Method(string trigger)")]
        public string TriggerMethodName = "OnScenicReplayTrigger";

        [Header("Display")]
        public TMP_Text ReplayStatusText;
        public TMP_Text ReplayTraceText;
        [Tooltip("Append trace lines during replay.")]
        public bool AppendTrace = true;

        private bool _isReplaying;

        [Serializable]
        private sealed class ReplayEvent
        {
            public string sessionId;
            public int turnIndex;
            public string timestampUtc;

            public string latestMoveText;
            public string dialogueWindowUsed;
            public string characterSummary;

            public string previousObjective;
            public string previousStance;
            public string updatedObjective;
            public string updatedStance;

            public string intendedAction;
            public string selectedActionLabel;
            public string resolvedMemoryId;
            public string resolvedMemoryLabel;

            public string reason;
            public bool executionSucceeded;
            public string executionResult;
            public string rawResponseReference;
        }

        [ContextMenu("Scenic Replay/Run Latest")]
        public async void RunLatestReplay()
        {
            if (_isReplaying)
            {
                SetStatus("[scenic-replay] Replay already running.");
                return;
            }

            try
            {
                _isReplaying = true;
                var path = ResolveReplayPath();
                if (string.IsNullOrWhiteSpace(path))
                {
                    SetStatus("[scenic-replay] No replay file found.");
                    return;
                }

                var events = LoadEvents(path);
                if (events.Count == 0)
                {
                    SetStatus("[scenic-replay] Replay file has no events.");
                    return;
                }

                SetStatus($"[scenic-replay] Running {events.Count} events from: {path}");
                if (!AppendTrace && ReplayTraceText != null)
                    ReplayTraceText.text = string.Empty;

                for (int i = 0; i < events.Count; i++)
                {
                    var e = events[i];
                    await ReplaySingleEvent(e, i + 1, events.Count);

                    if (DelayBetweenTurnsSeconds > 0f)
                        await Task.Delay((int)(DelayBetweenTurnsSeconds * 1000f));
                }

                SetStatus("[scenic-replay] Replay complete.");
            }
            catch (Exception ex)
            {
                SetStatus("[scenic-replay] Replay failed: " + ex.Message);
            }
            finally
            {
                _isReplaying = false;
            }
        }

        [ContextMenu("Scenic Replay/Reveal Replay Folder")]
        public void RevealReplayFolder()
        {
            var folder = Path.Combine(AaltoLaunchSessionLogger.ResolveFullEventLogsDirectory(), "scenic-events");
            Debug.Log("[scenic-replay] folder: " + folder);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(folder);
#endif
        }

        private async Task ReplaySingleEvent(ReplayEvent e, int index, int total)
        {
            var trigger = ResolveTrigger(e);
            var header = $"[Replay {index}/{total}] turn={e.turnIndex} label={e.selectedActionLabel} trigger={trigger}";
            Debug.Log("[scenic-replay] " + header);

            // 7.2 text trace display
            AppendReplayTrace(
                $"{header}\n" +
                $"latest_move: {e.latestMoveText}\n" +
                $"objective: {e.updatedObjective}\n" +
                $"stance: {e.updatedStance}\n" +
                $"intended_action: {e.intendedAction}\n" +
                $"selected_action_label: {e.selectedActionLabel}\n" +
                $"reason: {e.reason}\n");

            if (string.IsNullOrWhiteSpace(trigger))
            {
                Debug.LogWarning("[scenic-replay] No trigger found for event; skipping execution.");
                return;
            }

            if (DryRunOnly)
            {
                Debug.Log("[scenic-replay] DryRunOnly=true; would trigger: " + trigger);
                return;
            }

            TryInvokeTrigger(trigger);
            await Task.Yield();
        }

        private string ResolveReplayPath()
        {
            if (!string.IsNullOrWhiteSpace(ReplayFilePath) && File.Exists(ReplayFilePath))
                return ReplayFilePath;

            var folder = Path.Combine(AaltoLaunchSessionLogger.ResolveFullEventLogsDirectory(), "scenic-events");
            if (!Directory.Exists(folder)) return null;

            var files = Directory.GetFiles(folder, "*.jsonl")
                .Where(path =>
                {
                    var file = Path.GetFileName(path) ?? string.Empty;
                    return file.StartsWith("scenic-events__", StringComparison.OrdinalIgnoreCase)
                           || file.StartsWith("scenic_", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();

            return files.Length > 0 ? files[0] : null;
        }

        private static List<ReplayEvent> LoadEvents(string path)
        {
            var list = new List<ReplayEvent>();
            var lines = File.ReadAllLines(path);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = (lines[i] ?? string.Empty).Trim();
                if (line.Length == 0) continue;

                try
                {
                    var e = JsonUtility.FromJson<ReplayEvent>(line);
                    if (e != null) list.Add(e);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[scenic-replay] Failed to parse line {i + 1}: {ex.Message}");
                }
            }

            list.Sort((a, b) =>
            {
                var ta = a != null ? a.turnIndex : 0;
                var tb = b != null ? b.turnIndex : 0;
                return ta.CompareTo(tb);
            });

            return list;
        }

        private static string ResolveTrigger(ReplayEvent e)
        {
            if (e == null) return null;

            if (!string.IsNullOrWhiteSpace(e.resolvedMemoryLabel)) return e.resolvedMemoryLabel.Trim();
            if (!string.IsNullOrWhiteSpace(e.resolvedMemoryId)) return "memory " + e.resolvedMemoryId.Trim();

            // Fallback to label for dry analytical replay if memory resolution was absent.
            if (!string.IsNullOrWhiteSpace(e.selectedActionLabel)) return "label:" + e.selectedActionLabel.Trim();

            return null;
        }

        private void TryInvokeTrigger(string trigger)
        {
            if (TriggerTarget == null || string.IsNullOrWhiteSpace(TriggerMethodName))
            {
                Debug.Log("[scenic-replay] No TriggerTarget/TriggerMethod configured. Trigger=" + trigger);
                return;
            }

            try
            {
                TriggerTarget.SendMessage(TriggerMethodName, trigger, SendMessageOptions.DontRequireReceiver);
                Debug.Log("[scenic-replay] Trigger sent: " + trigger);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[scenic-replay] Trigger invocation failed: " + ex.Message);
            }
        }

        private void AppendReplayTrace(string text)
        {
            if (ReplayTraceText == null) return;

            if (!AppendTrace)
            {
                ReplayTraceText.text = text;
                return;
            }

            var cur = ReplayTraceText.text ?? string.Empty;
            ReplayTraceText.text = string.IsNullOrWhiteSpace(cur) ? text : cur + "\n" + text;
        }

        private void SetStatus(string msg)
        {
            Debug.Log(msg);
            if (ReplayStatusText != null)
                ReplayStatusText.text = msg;
        }
    }
}
