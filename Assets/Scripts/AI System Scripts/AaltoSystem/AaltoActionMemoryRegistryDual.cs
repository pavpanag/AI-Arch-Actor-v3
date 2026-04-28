using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AaltoSystemV3
{
    public enum AaltoActionMemoryRouteMode
    {
        [InspectorName("Light Only")]
        LightOnly = 0,

        [InspectorName("Sound Only")]
        SoundOnly = 1,

        [InspectorName("Both")]
        Both = 2
    }

    [Serializable]
    public class AaltoActionMemoryRoute
    {
        [InspectorName("Action (User or Agent Label)")]
        public string actionLabel;

        [InspectorName("Route Mode")]
        public AaltoActionMemoryRouteMode routeMode = AaltoActionMemoryRouteMode.LightOnly;

        [InspectorName("Light Memory Trigger (OSC Payload)")]
        public string lightMemoryTrigger;

        [InspectorName("Sound Memory Trigger (OSC Payload)")]
        public string soundMemoryTrigger;
    }

    /// <summary>
    /// Dual-route action memory registry for Aalto.
    /// Keeps the original light-only registry untouched while allowing optional sound triggers.
    /// </summary>
    public sealed class AaltoActionMemoryRegistryDual : MonoBehaviour
    {
        [Header("Architect Setup")]
        [Tooltip("Define action labels and choose whether each one drives light, sound, or both.")]
        [InspectorName("Action Label -> Memory Route Pairs")]
        public List<AaltoActionMemoryRoute> mappings = new List<AaltoActionMemoryRoute>
        {
            new AaltoActionMemoryRoute { actionLabel = "yes", routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 1", soundMemoryTrigger = "sound 1" },
            new AaltoActionMemoryRoute { actionLabel = "no", routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 2", soundMemoryTrigger = "sound 2" },
            new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 3", soundMemoryTrigger = string.Empty },
            new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 4", soundMemoryTrigger = string.Empty },
            new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 5", soundMemoryTrigger = string.Empty },
            new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 6", soundMemoryTrigger = string.Empty },
            new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 7", soundMemoryTrigger = string.Empty },
            new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 8", soundMemoryTrigger = string.Empty }
        };

        [Header("Execution")]
        [Tooltip("Optional OSC sender used for light controller triggers.")]
        [InspectorName("Light OSC Sender")]
        public AaltoOscSender LightOscSender;

        [Tooltip("Port used for light controller OSC messages.")]
        public int LightOscPort = 4444;

        [Tooltip("Optional OSC sender used for sound controller triggers.")]
        [InspectorName("Sound OSC Sender")]
        public AaltoOscSender SoundOscSender;

        [Tooltip("Port used for sound v11 OSC messages.")]
        public int SoundOscPort = 5555;

        [Tooltip("When true, this component keeps the assigned sender ports aligned to the values above.")]
        public bool AutoConfigureSenderEndpoints = true;

        [Header("Agent Produced")]
        [TextArea(2, 6)]
        [InspectorName("Last Resolved Action Label")]
        public string LastResolvedActionLabel;

        [TextArea(2, 6)]
        [InspectorName("Last Resolved Light Memory Trigger")]
        public string LastResolvedLightMemoryTrigger;

        [TextArea(2, 6)]
        [InspectorName("Last Resolved Sound Memory Trigger")]
        public string LastResolvedSoundMemoryTrigger;

        [TextArea(2, 6)]
        [InspectorName("Last Send Status")]
        public string LastSendStatus;

        private void Awake()
        {
            EnsureSendersReady(allowCreate: true);
            ApplySenderEndpointSettings();
        }

        private void OnValidate()
        {
            EnsureSendersReady(allowCreate: false);
            ApplySenderEndpointSettings();
        }

        private void ApplySenderEndpointSettings()
        {
            EnsureSendersReady(allowCreate: Application.isPlaying);

            if (!AutoConfigureSenderEndpoints)
                return;

            if (LightOscSender != null)
            {
                LightOscSender.Port = Mathf.Max(1, LightOscPort);
                LightOscSender.Address = string.IsNullOrWhiteSpace(LightOscSender.Address) ? "/memory" : LightOscSender.Address;
            }

            if (SoundOscSender != null)
            {
                SoundOscSender.Port = Mathf.Max(1, SoundOscPort);
                SoundOscSender.Address = string.IsNullOrWhiteSpace(SoundOscSender.Address) ? "/memory" : SoundOscSender.Address;
            }
        }

        private void EnsureSendersReady(bool allowCreate)
        {
            AutoResolveSender(ref LightOscSender, LightOscPort, "Light", allowCreate, SoundOscSender);
            AutoResolveSender(ref SoundOscSender, SoundOscPort, "Sound", allowCreate, LightOscSender);

            // A single sender cannot target two different ports at once.
            if (LightOscSender != null && SoundOscSender != null && ReferenceEquals(LightOscSender, SoundOscSender))
            {
                if (Mathf.Max(1, LightOscPort) == Mathf.Max(1, SoundOscPort))
                    return;

                if (!allowCreate)
                    return;

                var cloned = gameObject.AddComponent<AaltoOscSender>();
                cloned.Host = LightOscSender.Host;
                cloned.Address = LightOscSender.Address;
                cloned.UseRawUtf8Fallback = LightOscSender.UseRawUtf8Fallback;
                cloned.LogSends = LightOscSender.LogSends;
                cloned.Port = Mathf.Max(1, SoundOscPort);
                SoundOscSender = cloned;

                Debug.Log("[AaltoActionMemoryRegistryDual] Created separate Sound OSC sender because Light and Sound were sharing one sender with different ports.");
            }
        }

        private void AutoResolveSender(ref AaltoOscSender targetField, int desiredPort, string role, bool allowCreate, AaltoOscSender excluded)
        {
            if (targetField != null && !ReferenceEquals(targetField, excluded))
                return;

            var desired = Mathf.Max(1, desiredPort);
            AaltoOscSender matchByPort = null;
            AaltoOscSender fallback = null;

            var localSenders = GetComponents<AaltoOscSender>();
            for (int i = 0; i < localSenders.Length; i++)
            {
                var candidate = localSenders[i];
                if (candidate == null || ReferenceEquals(candidate, excluded))
                    continue;

                if (candidate.Port == desired && matchByPort == null)
                    matchByPort = candidate;

                if (fallback == null)
                    fallback = candidate;
            }

            targetField = matchByPort != null ? matchByPort : fallback;
            if (targetField != null)
            {
                Debug.Log("[AaltoActionMemoryRegistryDual] Auto-assigned " + role + " OSC sender from existing components.");
                return;
            }

            if (!allowCreate)
                return;

            var created = gameObject.AddComponent<AaltoOscSender>();
            created.Port = desired;
            created.Address = "/memory";
            targetField = created;
            Debug.Log("[AaltoActionMemoryRegistryDual] Auto-created missing " + role + " OSC sender on this GameObject.");
        }

        public bool TryResolveActionMemory(string actionLabel, out AaltoActionMemoryRoute route)
        {
            route = null;
            if (string.IsNullOrWhiteSpace(actionLabel) || mappings == null || mappings.Count == 0)
                return false;

            for (int i = 0; i < mappings.Count; i++)
            {
                var candidate = mappings[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.actionLabel))
                    continue;

                if (string.Equals(candidate.actionLabel.Trim(), actionLabel.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    route = candidate;
                    return true;
                }
            }

            return false;
        }

        public bool TryResolveLightTrigger(string actionLabel, out string lightMemoryTrigger)
        {
            lightMemoryTrigger = null;
            if (!TryResolveActionMemory(actionLabel, out var route))
                return false;

            var trigger = (route.lightMemoryTrigger ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trigger))
                return false;

            lightMemoryTrigger = trigger;
            return true;
        }

        public bool TryResolveSoundTrigger(string actionLabel, out string soundMemoryTrigger)
        {
            soundMemoryTrigger = null;
            if (!TryResolveActionMemory(actionLabel, out var route))
                return false;

            var trigger = (route.soundMemoryTrigger ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trigger))
                return false;

            soundMemoryTrigger = trigger;
            return true;
        }

        public List<AaltoActionMemoryRoute> BuildExecutableMappingSnapshot()
        {
            var snapshot = new List<AaltoActionMemoryRoute>();
            if (mappings == null || mappings.Count == 0)
                return snapshot;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < mappings.Count; i++)
            {
                var mapping = mappings[i];
                if (mapping == null)
                    continue;

                var label = (mapping.actionLabel ?? string.Empty).Trim();
                var lightTrigger = (mapping.lightMemoryTrigger ?? string.Empty).Trim();
                var soundTrigger = (mapping.soundMemoryTrigger ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(lightTrigger) && string.IsNullOrWhiteSpace(soundTrigger))
                    continue;

                if (!seen.Add(label))
                    continue;

                snapshot.Add(new AaltoActionMemoryRoute
                {
                    actionLabel = label,
                    routeMode = mapping.routeMode,
                    lightMemoryTrigger = lightTrigger,
                    soundMemoryTrigger = soundTrigger
                });
            }

            return snapshot;
        }

        public string BuildExecutableMappingSignature()
        {
            var snapshot = BuildExecutableMappingSnapshot();
            if (snapshot.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (i > 0)
                    sb.Append("|");

                sb.Append(snapshot[i].actionLabel)
                    .Append("=>M:")
                    .Append(snapshot[i].routeMode)
                    .Append("=>L:")
                    .Append(snapshot[i].lightMemoryTrigger)
                    .Append(",S:")
                    .Append(snapshot[i].soundMemoryTrigger);
            }

            return sb.ToString();
        }

        public bool TrySendForActionLabel(string actionLabel, bool sendLight, bool sendSound, out string error)
        {
            EnsureSendersReady(allowCreate: Application.isPlaying);
            error = null;
            LastResolvedActionLabel = actionLabel;
            LastResolvedLightMemoryTrigger = null;
            LastResolvedSoundMemoryTrigger = null;
            LastSendStatus = null;

            if (!TryResolveActionMemory(actionLabel, out var route))
            {
                error = $"No memory route mapped for action label '{actionLabel}'.";
                LastSendStatus = error;
                return false;
            }

            var hadRequest = false;
            var hadSuccess = false;
            var status = new StringBuilder();
            var errors = new List<string>();
            var canSendLight = route.routeMode == AaltoActionMemoryRouteMode.LightOnly || route.routeMode == AaltoActionMemoryRouteMode.Both;
            var canSendSound = route.routeMode == AaltoActionMemoryRouteMode.SoundOnly || route.routeMode == AaltoActionMemoryRouteMode.Both;

            if (sendLight)
            {
                hadRequest = true;
                if (!canSendLight)
                {
                    errors.Add("light route is disabled by mode");
                }
                else
                {
                    var trigger = (route.lightMemoryTrigger ?? string.Empty).Trim();
                    LastResolvedLightMemoryTrigger = trigger;

                    if (string.IsNullOrWhiteSpace(trigger))
                    {
                        errors.Add("light trigger is empty");
                    }
                    else if (LightOscSender == null)
                    {
                        errors.Add("light OSC sender is not assigned");
                    }
                    else if (LightOscSender.TrySendMemoryTrigger(trigger, out var lightError))
                    {
                        hadSuccess = true;
                        status.Append($"Light sent '{actionLabel}' -> '{trigger}'");
                    }
                    else
                    {
                        errors.Add(string.IsNullOrWhiteSpace(lightError) ? "light send failed" : lightError);
                    }
                }
            }

            if (sendSound)
            {
                hadRequest = true;
                if (!canSendSound)
                {
                    errors.Add("sound route is disabled by mode");
                }
                else
                {
                    var trigger = (route.soundMemoryTrigger ?? string.Empty).Trim();
                    LastResolvedSoundMemoryTrigger = trigger;

                    if (string.IsNullOrWhiteSpace(trigger))
                    {
                        errors.Add("sound trigger is empty");
                    }
                    else if (SoundOscSender == null)
                    {
                        errors.Add("sound OSC sender is not assigned");
                    }
                    else if (SoundOscSender.TrySendMemoryTrigger(trigger, out var soundError))
                    {
                        hadSuccess = true;
                        if (status.Length > 0) status.Append(" | ");
                        status.Append($"Sound sent '{actionLabel}' -> '{trigger}'");
                    }
                    else
                    {
                        errors.Add(string.IsNullOrWhiteSpace(soundError) ? "sound send failed" : soundError);
                    }
                }
            }

            if (!hadRequest)
            {
                error = "No target routes were requested.";
                LastSendStatus = error;
                return false;
            }

            if (hadSuccess && errors.Count == 0)
            {
                LastSendStatus = status.ToString();
                return true;
            }

            if (hadSuccess && errors.Count > 0)
            {
                LastSendStatus = status.Length > 0 ? status.Append(" | ").Append(string.Join("; ", errors)).ToString() : string.Join("; ", errors);
                return false;
            }

            error = string.Join("; ", errors);
            LastSendStatus = error;
            return false;
        }

        public bool TrySendLightForActionLabel(string actionLabel, out string error)
        {
            return TrySendForActionLabel(actionLabel, sendLight: true, sendSound: false, out error);
        }

        public bool TrySendSoundForActionLabel(string actionLabel, out string error)
        {
            return TrySendForActionLabel(actionLabel, sendLight: false, sendSound: true, out error);
        }

        public bool TrySendLightMemoryTrigger(string memoryTrigger, out string error)
        {
            EnsureSendersReady(allowCreate: Application.isPlaying);
            error = null;
            LastResolvedActionLabel = "(direct-light)";
            LastResolvedLightMemoryTrigger = null;
            LastResolvedSoundMemoryTrigger = null;
            LastSendStatus = null;

            var trigger = (memoryTrigger ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trigger))
            {
                error = "Light memory trigger is empty.";
                LastSendStatus = error;
                return false;
            }

            LastResolvedLightMemoryTrigger = trigger;

            if (LightOscSender == null)
            {
                error = "Light OSC sender is not assigned.";
                LastSendStatus = error;
                return false;
            }

            if (LightOscSender.TrySendMemoryTrigger(trigger, out var sendError))
            {
                LastSendStatus = $"Sent direct light memory trigger '{trigger}'";
                return true;
            }

            error = sendError;
            LastSendStatus = error;
            return false;
        }

        public bool TrySendSoundMemoryTrigger(string memoryTrigger, out string error)
        {
            EnsureSendersReady(allowCreate: Application.isPlaying);
            error = null;
            LastResolvedActionLabel = "(direct-sound)";
            LastResolvedLightMemoryTrigger = null;
            LastResolvedSoundMemoryTrigger = null;
            LastSendStatus = null;

            var trigger = (memoryTrigger ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trigger))
            {
                error = "Sound memory trigger is empty.";
                LastSendStatus = error;
                return false;
            }

            LastResolvedSoundMemoryTrigger = trigger;

            if (SoundOscSender == null)
            {
                error = "Sound OSC sender is not assigned.";
                LastSendStatus = error;
                return false;
            }

            if (SoundOscSender.TrySendMemoryTrigger(trigger, out var sendError))
            {
                LastSendStatus = $"Sent direct sound memory trigger '{trigger}'";
                return true;
            }

            error = sendError;
            LastSendStatus = error;
            return false;
        }

        [ContextMenu("Action Memory/Reset Default Mappings")]
        public void ResetDefaultMappings()
        {
            mappings = new List<AaltoActionMemoryRoute>
            {
                new AaltoActionMemoryRoute { actionLabel = "yes", routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 1", soundMemoryTrigger = "sound 1" },
                new AaltoActionMemoryRoute { actionLabel = "no", routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 2", soundMemoryTrigger = "sound 2" },
                new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 3", soundMemoryTrigger = string.Empty },
                new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 4", soundMemoryTrigger = string.Empty },
                new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 5", soundMemoryTrigger = string.Empty },
                new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 6", soundMemoryTrigger = string.Empty },
                new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 7", soundMemoryTrigger = string.Empty },
                new AaltoActionMemoryRoute { actionLabel = string.Empty, routeMode = AaltoActionMemoryRouteMode.LightOnly, lightMemoryTrigger = "memory 8", soundMemoryTrigger = string.Empty }
            };
        }

        [ContextMenu("Action Memory/Clear All Mappings")]
        public void ClearAllMappings()
        {
            if (mappings == null)
                mappings = new List<AaltoActionMemoryRoute>();
            else
                mappings.Clear();
        }

        [ContextMenu("Action Memory/Trim Empty Mappings")]
        public void TrimEmptyMappings()
        {
            if (mappings == null)
                return;

            for (int i = mappings.Count - 1; i >= 0; i--)
            {
                var mapping = mappings[i];
                if (mapping == null ||
                    (string.IsNullOrWhiteSpace(mapping.actionLabel) &&
                     string.IsNullOrWhiteSpace(mapping.lightMemoryTrigger) &&
                     string.IsNullOrWhiteSpace(mapping.soundMemoryTrigger)))
                {
                    mappings.RemoveAt(i);
                }
            }
        }

        [ContextMenu("Action Memory/Log All Mappings")]
        public void LogAllMappings()
        {
            if (mappings == null || mappings.Count == 0)
            {
                Debug.Log("[AaltoActionMemoryRegistryDual] No mappings configured.");
                return;
            }

            for (int i = 0; i < mappings.Count; i++)
            {
                var mapping = mappings[i];
                if (mapping == null)
                    continue;

                Debug.Log($"[AaltoActionMemoryRegistryDual] {i + 1}: '{mapping.actionLabel}' -> light:'{mapping.lightMemoryTrigger}' sound:'{mapping.soundMemoryTrigger}'");
            }
        }
    }
}
