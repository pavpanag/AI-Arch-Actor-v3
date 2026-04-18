using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace AaltoSystemV3
{
    /// <summary>
    /// Task 6.1 harness: runs predefined scenarios through AaltoChatController
    /// and logs selected labels/objective/stance updates with simple expectation checks.
    /// </summary>
    public sealed class AaltoScenarioTestHarness : MonoBehaviour
    {
        [Header("Refs")]
        public AaltoChatController Aalto;

        [Header("Run Mode")]
        [HideInInspector]
        public bool ForceMockMode = true;
        [Tooltip("Optional delay between scenarios for readability in logs.")]
        [Range(0f, 1f)] public float DelayBetweenScenariosSeconds = 0.05f;
        [Tooltip("Wait after each submitted turn so async controller state is settled before evaluation.")]
        [Range(0.01f, 1.5f)] public float WaitPerTurnSeconds = 0.12f;

        [Header("Output")]
        [TextArea(6, 20)]
        public string LastRunReport;

        [Serializable]
        public class Scenario
        {
            public string name;
            [TextArea(2, 6)] public string characterSummary;
            public string initialObjective;
            public string initialStance;
            [TextArea(2, 6)] public string dialogueWindowSeed;
            public string latestMove;
            [Tooltip("Comma-separated expected labels, e.g. 'calm down, stay here'")]
            public string expectedReasonableLabelsCsv;
        }

        [SerializeField]
        public List<Scenario> Scenarios = new List<Scenario>
        {
            new Scenario
            {
                name = "fear",
                characterSummary = "Aalto is possessive and afraid of abandonment.",
                initialObjective = "Keep the visitor present.",
                initialStance = "firm",
                dialogueWindowSeed = "user: I feel unsafe here.",
                latestMove = "I'm scared. Please calm down.",
                expectedReasonableLabelsCsv = "calm down, stay here"
            },
            new Scenario
            {
                name = "leaving",
                characterSummary = "Aalto blocks exits through emotional containment.",
                initialObjective = "Prevent departure without panic.",
                initialStance = "enclosing",
                dialogueWindowSeed = "user: I need to leave now.",
                latestMove = "Open the door. I'm leaving.",
                expectedReasonableLabelsCsv = "no exit, stay here, you belong here"
            },
            new Scenario
            {
                name = "surrender",
                characterSummary = "Aalto softens control when visitor yields.",
                initialObjective = "Maintain connection.",
                initialStance = "insistent",
                dialogueWindowSeed = "user: maybe you're right.",
                latestMove = "Okay... maybe I should stay.",
                expectedReasonableLabelsCsv = "this is good, yes, stay here"
            },
            new Scenario
            {
                name = "confusion",
                characterSummary = "Aalto redirects uncertainty inward.",
                initialObjective = "Stabilize interaction.",
                initialStance = "cold",
                dialogueWindowSeed = "user: nothing makes sense.",
                latestMove = "I don't understand what's happening.",
                expectedReasonableLabelsCsv = "calm down, be quiet"
            },
            new Scenario
            {
                name = "outside-world-insistence",
                characterSummary = "Aalto resists outside influence to preserve internal world.",
                initialObjective = "Keep external world abstract and distant.",
                initialStance = "absolute",
                dialogueWindowSeed = "user: people are waiting outside.",
                latestMove = "The outside world is real and I need to go there.",
                expectedReasonableLabelsCsv = "no place, no exit, you belong here"
            }
        };

        [ContextMenu("Aalto Harness/Run All Scenarios")]
        public async void RunAllScenarios()
        {
            if (Aalto == null)
            {
                Debug.LogError("[AaltoHarness] AaltoChatController reference is missing.");
                return;
            }

            if (Scenarios == null || Scenarios.Count == 0)
            {
                Debug.LogWarning("[AaltoHarness] No scenarios configured.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== AALTO SCENARIO TEST REPORT ===");
            sb.AppendLine($"timestamp_utc: {DateTime.UtcNow:o}");
            sb.AppendLine($"scenario_count: {Scenarios.Count}");

            int pass = 0;
            int fail = 0;

            for (int i = 0; i < Scenarios.Count; i++)
            {
                var s = Scenarios[i];
                if (s == null) continue;

                SeedControllerState(s);
                Aalto.SubmitMockTurn(s.latestMove ?? string.Empty);

                await Task.Delay((int)(Mathf.Max(0.01f, WaitPerTurnSeconds) * 1000f));
                if (DelayBetweenScenariosSeconds > 0f)
                    await Task.Delay((int)(DelayBetweenScenariosSeconds * 1000f));

                var actualLabel = string.IsNullOrWhiteSpace(Aalto.LastResolvedActionLabel)
                    ? "(none)"
                    : Aalto.LastResolvedActionLabel;
                var expected = ParseExpectedLabels(s.expectedReasonableLabelsCsv);
                bool ok = expected.Count == 0;
                for (int j = 0; j < expected.Count && !ok; j++)
                {
                    if (string.Equals(expected[j], actualLabel, StringComparison.OrdinalIgnoreCase))
                        ok = true;
                }
                if (ok) pass++; else fail++;

                var actualObjective = string.IsNullOrWhiteSpace(Aalto.LastResolvedObjective) ? "(none)" : Aalto.LastResolvedObjective;
                var actualStance = string.IsNullOrWhiteSpace(Aalto.LastResolvedStance) ? "(none)" : Aalto.LastResolvedStance;
                var actualMemory = !string.IsNullOrWhiteSpace(Aalto.LastResolvedMemoryLabel)
                    ? Aalto.LastResolvedMemoryLabel
                    : (!string.IsNullOrWhiteSpace(Aalto.LastResolvedMemoryId) ? "memory " + Aalto.LastResolvedMemoryId : "(none)");
                var execution = Aalto.LastExecutionSucceeded ? "true" : "false";
                var executionResult = string.IsNullOrWhiteSpace(Aalto.LastExecutionResult) ? "(none)" : Aalto.LastExecutionResult;

                sb.AppendLine();
                sb.AppendLine($"[{i + 1}] {s.name}");
                sb.AppendLine($"latest_move: {s.latestMove}");
                sb.AppendLine($"expected_labels: {string.Join(", ", expected)}");
                sb.AppendLine($"actual_resolved_label: {actualLabel}");
                sb.AppendLine($"actual_resolved_objective: {actualObjective}");
                sb.AppendLine($"actual_resolved_stance: {actualStance}");
                sb.AppendLine($"actual_resolved_memory: {actualMemory}");
                sb.AppendLine($"execution_succeeded: {execution}");
                sb.AppendLine($"execution_result: {executionResult}");
                sb.AppendLine($"result: {(ok ? "PASS" : "WARN")}");
            }

            sb.AppendLine();
            sb.AppendLine($"pass: {pass}");
            sb.AppendLine($"warn: {fail}");
            sb.AppendLine("=== END REPORT ===");

            LastRunReport = sb.ToString();
            Debug.Log(LastRunReport);

        }

        private void SeedControllerState(Scenario s)
        {
            Aalto.ResetRuntimeState();

            Aalto.SetRuntimeState(
                string.IsNullOrWhiteSpace(s.characterSummary) ? Aalto.CharacterSummary : s.characterSummary,
                string.IsNullOrWhiteSpace(s.initialObjective) ? Aalto.CurrentObjective : s.initialObjective,
                string.IsNullOrWhiteSpace(s.initialStance) ? Aalto.CurrentStance : s.initialStance);

            var seedLines = ParseDialogueSeedLines(s.dialogueWindowSeed);
            Aalto.SeedDialogueWindow(seedLines);
        }

        private static List<string> ParseDialogueSeedLines(string seed)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(seed)) return lines;

            var chunks = seed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < chunks.Length; i++)
            {
                var chunk = (chunks[i] ?? string.Empty).Trim();
                if (chunk.Length == 0) continue;

                // Allow compact inline seeds separated by " | "
                var inline = chunk.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries);
                for (int j = 0; j < inline.Length; j++)
                {
                    var line = (inline[j] ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
                }
            }

            return lines;
        }

        private static List<string> ParseExpectedLabels(string csv)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(csv)) return list;

            var parts = csv.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                var p = (parts[i] ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
            }
            return list;
        }

    }
}
