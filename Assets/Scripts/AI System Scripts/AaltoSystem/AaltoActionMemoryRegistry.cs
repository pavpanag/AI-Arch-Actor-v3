using System;
using System.Collections.Generic;
using UnityEngine;

namespace AaltoSystemV3
{
    [Serializable]
    public class AaltoActionMemoryPair
    {
        [InspectorName("Action (User or Agent Label)")]
        public string actionLabel;

        [InspectorName("Memory Trigger (OSC Payload)")]
        public string memoryTrigger;
    }

    /// <summary>
    /// Phase 4.1 registry: maps scenic action labels to memory trigger strings.
    /// </summary>
    public sealed class AaltoActionMemoryRegistry : MonoBehaviour
    {
        [Header("Architect Setup")]
        [Tooltip("Define up to 20 action labels and the memory trigger each one should send. You can remove or add entries in the Inspector as needed.")]
        [InspectorName("Action Label -> Memory Trigger Pairs")]
        public List<AaltoActionMemoryPair> mappings = new List<AaltoActionMemoryPair>
        {
            new AaltoActionMemoryPair { actionLabel = "yes", memoryTrigger = "memory 1" },
            new AaltoActionMemoryPair { actionLabel = "no", memoryTrigger = "memory 2" },
            new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 3" },
            new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 4" },
            new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 5" },
            new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 6" },
            new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 7" },
            new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 8" }
        };

        [Header("Agent Produced")]
        [Tooltip("Optional OSC sender used to forward resolved memory triggers to the light controller.")]
        [InspectorName("OSC Sender (Agent Produced)")]
        public AaltoOscSender OscSender;

        [Header("Agent Produced")]
        [TextArea(2, 6)]
        [InspectorName("Last Resolved Memory Trigger")]
        public string LastResolvedMemoryTrigger;
        [TextArea(2, 6)]
        [InspectorName("Last Resolved Action Label")]
        public string LastResolvedActionLabel;
        [TextArea(2, 6)]
        [InspectorName("Last Send Status")]
        public string LastSendStatus;

        public bool TryResolveMemoryTrigger(string actionLabel, out string memoryTrigger)
        {
            memoryTrigger = null;
            if (string.IsNullOrWhiteSpace(actionLabel) || mappings == null || mappings.Count == 0)
                return false;

            for (int i = 0; i < mappings.Count; i++)
            {
                var m = mappings[i];
                if (m == null || string.IsNullOrWhiteSpace(m.actionLabel) || string.IsNullOrWhiteSpace(m.memoryTrigger))
                    continue;

                if (string.Equals(m.actionLabel.Trim(), actionLabel.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    memoryTrigger = m.memoryTrigger.Trim();
                    return true;
                }
            }

            return false;
        }

        public bool TrySendMemoryTriggerForActionLabel(string actionLabel, out string error)
        {
            error = null;
            LastResolvedActionLabel = actionLabel;
            LastResolvedMemoryTrigger = null;
            LastSendStatus = null;

            if (!TryResolveMemoryTrigger(actionLabel, out var memoryTrigger))
            {
                error = $"No memory trigger mapped for action label '{actionLabel}'.";
                LastSendStatus = error;
                return false;
            }

            LastResolvedMemoryTrigger = memoryTrigger;

            if (OscSender == null)
            {
                error = "OSC sender is not assigned.";
                LastSendStatus = error;
                return false;
            }

            if (OscSender.TrySendMemoryTrigger(memoryTrigger, out var sendError))
            {
                LastSendStatus = $"Sent '{actionLabel}' -> '{memoryTrigger}'";
                return true;
            }

            error = sendError;
            LastSendStatus = error;
            return false;
        }

        [ContextMenu("Action Memory/Reset Default Mappings")]
        public void ResetDefaultMappings()
        {
            mappings = new List<AaltoActionMemoryPair>
            {
                new AaltoActionMemoryPair { actionLabel = "yes", memoryTrigger = "memory 1" },
                new AaltoActionMemoryPair { actionLabel = "no", memoryTrigger = "memory 2" },
                new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 3" },
                new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 4" },
                new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 5" },
                new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 6" },
                new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 7" },
                new AaltoActionMemoryPair { actionLabel = "", memoryTrigger = "memory 8" }
            };
        }

        [ContextMenu("Action Memory/Clear All Mappings")]
        public void ClearAllMappings()
        {
            mappings.Clear();
        }

        [ContextMenu("Action Memory/Trim Empty Mappings")]
        public void TrimEmptyMappings()
        {
            for (int i = mappings.Count - 1; i >= 0; i--)
            {
                var m = mappings[i];
                if (m == null || (string.IsNullOrWhiteSpace(m.actionLabel) && string.IsNullOrWhiteSpace(m.memoryTrigger)))
                    mappings.RemoveAt(i);
            }
        }

        [ContextMenu("Action Memory/Log All Mappings")]
        public void LogAllMappings()
        {
            if (mappings == null || mappings.Count == 0)
            {
                Debug.Log("[AaltoActionMemoryRegistry] No mappings configured.");
                return;
            }

            for (int i = 0; i < mappings.Count; i++)
            {
                var m = mappings[i];
                if (m == null)
                    continue;

                Debug.Log($"[AaltoActionMemoryRegistry] {i + 1}: '{m.actionLabel}' -> '{m.memoryTrigger}'");
            }
        }
    }
}
