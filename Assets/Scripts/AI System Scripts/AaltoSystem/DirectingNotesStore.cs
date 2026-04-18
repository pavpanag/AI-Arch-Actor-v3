using System;
using System.Collections.Generic;
using System.Text;

namespace AaltoSystemV3
{

    [Serializable]
    public sealed class DirectingNoteEntry
    {
        public string timestampUtc;
        public string entryType;
        public string sceneContext;
        public string instruction;
        public string objectiveOverride;
        public string stanceOverride;
        public string interactionSnapshot;
    }

    // Shared directing notes across chat/interview controllers (persistent in-memory for session).
    public static class DirectingNotesStore
    {
        private static readonly List<DirectingNoteEntry> _entries = new List<DirectingNoteEntry>();

        public static IReadOnlyList<DirectingNoteEntry> Entries => _entries;

        public static string Notes => BuildHistoryText();

        public static void Append(string note)
        {
            AppendDirectorInstruction(note, null, null, null, null);
        }

        public static void AppendDirectorInstruction(string instruction, string sceneContext, string objectiveOverride, string stanceOverride, string interactionSnapshot)
        {
            var txt = (instruction ?? string.Empty).Trim();
            var context = (sceneContext ?? string.Empty).Trim();
            var objective = (objectiveOverride ?? string.Empty).Trim();
            var stance = (stanceOverride ?? string.Empty).Trim();
            var snapshot = (interactionSnapshot ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(txt) && string.IsNullOrWhiteSpace(context) && string.IsNullOrWhiteSpace(objective) && string.IsNullOrWhiteSpace(stance) && string.IsNullOrWhiteSpace(snapshot))
                return;

            _entries.Add(new DirectingNoteEntry
            {
                timestampUtc = DateTime.UtcNow.ToString("o"),
                entryType = "director_instruction",
                sceneContext = context,
                instruction = txt,
                objectiveOverride = objective,
                stanceOverride = stance,
                interactionSnapshot = snapshot
            });
        }

        public static void AppendTurnSnapshot(string sceneContext, string interactionSnapshot)
        {
            var context = (sceneContext ?? string.Empty).Trim();
            var snapshot = (interactionSnapshot ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(context) && string.IsNullOrWhiteSpace(snapshot))
                return;

            _entries.Add(new DirectingNoteEntry
            {
                timestampUtc = DateTime.UtcNow.ToString("o"),
                entryType = "turn_snapshot",
                sceneContext = context,
                interactionSnapshot = snapshot
            });
        }

        public static string BuildHistoryText(int maxEntries = 12)
        {
            if (_entries.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            var startIndex = Math.Max(0, _entries.Count - Math.Max(1, maxEntries));

            for (int i = startIndex; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry == null)
                    continue;

                if (sb.Length > 0)
                    sb.AppendLine();

                sb.AppendLine($"[{entry.timestampUtc}] {entry.entryType}");

                if (!string.IsNullOrWhiteSpace(entry.sceneContext))
                    sb.AppendLine($"scene_context: {entry.sceneContext}");

                if (!string.IsNullOrWhiteSpace(entry.instruction))
                    sb.AppendLine($"instruction: {entry.instruction}");

                if (!string.IsNullOrWhiteSpace(entry.objectiveOverride))
                    sb.AppendLine($"objective_override: {entry.objectiveOverride}");

                if (!string.IsNullOrWhiteSpace(entry.stanceOverride))
                    sb.AppendLine($"stance_override: {entry.stanceOverride}");

                if (!string.IsNullOrWhiteSpace(entry.interactionSnapshot))
                    sb.AppendLine($"interaction_snapshot: {entry.interactionSnapshot}");
            }

            return sb.ToString().Trim();
        }

        public static string GetObjectiveOverride() => GetLatestOverrideValue("objective_override");

        public static string GetStanceOverride() => GetLatestOverrideValue("stance_override");

        public static void Clear()
        {
            _entries.Clear();
        }

        private static string GetLatestOverrideValue(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || _entries.Count == 0)
                return string.Empty;

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry == null || !string.Equals(entry.entryType, "director_instruction", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(key, "objective_override", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.objectiveOverride))
                    return entry.objectiveOverride.Trim();

                if (string.Equals(key, "stance_override", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.stanceOverride))
                    return entry.stanceOverride.Trim();

                var combined = BuildEntryText(entry);
                var parsed = ExtractLatestOverrideValueFromText(combined, key);
                if (!string.IsNullOrWhiteSpace(parsed))
                    return parsed;
            }

            return string.Empty;
        }

        private static string BuildEntryText(DirectingNoteEntry entry)
        {
            if (entry == null)
                return string.Empty;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(entry.sceneContext)) sb.AppendLine(entry.sceneContext);
            if (!string.IsNullOrWhiteSpace(entry.instruction)) sb.AppendLine(entry.instruction);
            if (!string.IsNullOrWhiteSpace(entry.objectiveOverride)) sb.AppendLine("objective_override: " + entry.objectiveOverride);
            if (!string.IsNullOrWhiteSpace(entry.stanceOverride)) sb.AppendLine("stance_override: " + entry.stanceOverride);
            if (!string.IsNullOrWhiteSpace(entry.interactionSnapshot)) sb.AppendLine(entry.interactionSnapshot);
            return sb.ToString();
        }

        private static string ExtractLatestOverrideValueFromText(string text, string key)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(text)) return string.Empty;

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = (lines[i] ?? string.Empty).Trim();
                if (line.Length == 0) continue;

                var colonPrefix = key + ":";
                if (line.StartsWith(colonPrefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(colonPrefix.Length).Trim();

                var equalsPrefix = key + "=";
                if (line.StartsWith(equalsPrefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(equalsPrefix.Length).Trim();
            }

            return string.Empty;
        }
    }
}
