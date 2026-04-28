using UnityEngine;

namespace AaltoSystemV3
{
    /// <summary>
    /// Lightweight test harness for multi-speaker input.
    /// Provides inspector fields and context-menu commands to send simulated
    /// transcripts to AaltoMultiSpeakerDirectedRoomPerformerController.
    /// </summary>
    public class AaltoMultiSpeakerPlaceholder : MonoBehaviour
    {
        [Header("Target Performer")]
        public AaltoMultiSpeakerDirectedRoomPerformerController Performer;

        [Header("Channel -> Speaker Mapping")]
        public string Channel1SpeakerId = "actor_1";
        public string Channel2SpeakerId = "actor_2";
        public string Channel3SpeakerId = "actor_3";
        public string Channel4SpeakerId = "actor_4";

        [Header("Simulated Inputs")]
        [TextArea(1,3)] public string SimulatedSpeaker1Text;
        [TextArea(1,3)] public string SimulatedSpeaker2Text;
        [TextArea(1,3)] public string SimulatedSpeaker3Text;
        [TextArea(1,3)] public string SimulatedSpeaker4Text;

        [Header("Debug")]
        [TextArea(1,3)] public string LastStatus;

        private void Reset()
        {
            // Attempt to auto-link a performer in the scene
            if (Performer == null)
                Performer = FindObjectOfType<AaltoMultiSpeakerDirectedRoomPerformerController>();
        }

        private void Start()
        {
            if (Performer == null)
                Performer = FindObjectOfType<AaltoMultiSpeakerDirectedRoomPerformerController>();
        }

        [ContextMenu("MultiSpeakerPlaceholder/Submit Simulated Speaker 1")]
        public void SubmitSimulatedSpeaker1()
        {
            SubmitSimulated(1, Channel1SpeakerId, SimulatedSpeaker1Text);
        }

        [ContextMenu("MultiSpeakerPlaceholder/Submit Simulated Speaker 2")]
        public void SubmitSimulatedSpeaker2()
        {
            SubmitSimulated(2, Channel2SpeakerId, SimulatedSpeaker2Text);
        }

        [ContextMenu("MultiSpeakerPlaceholder/Submit Simulated Speaker 3")]
        public void SubmitSimulatedSpeaker3()
        {
            SubmitSimulated(3, Channel3SpeakerId, SimulatedSpeaker3Text);
        }

        [ContextMenu("MultiSpeakerPlaceholder/Submit Simulated Speaker 4")]
        public void SubmitSimulatedSpeaker4()
        {
            SubmitSimulated(4, Channel4SpeakerId, SimulatedSpeaker4Text);
        }

        [ContextMenu("MultiSpeakerPlaceholder/Submit All Simulated Inputs")]
        public void SubmitAllSimulated()
        {
            SubmitSimulatedSpeaker1();
            SubmitSimulatedSpeaker2();
            SubmitSimulatedSpeaker3();
            SubmitSimulatedSpeaker4();
        }

        private void SubmitSimulated(int channel, string speakerId, string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            LastStatus = "";
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                LastStatus = "No text for speaker " + channel + ".";
                Debug.Log("[AaltoMultiSpeakerPlaceholder] " + LastStatus);
                return;
            }

            if (Performer == null)
            {
                Performer = FindObjectOfType<AaltoMultiSpeakerDirectedRoomPerformerController>();
                if (Performer == null)
                {
                    LastStatus = "No Performer found in scene.";
                    Debug.LogWarning("[AaltoMultiSpeakerPlaceholder] " + LastStatus);
                    return;
                }
            }

            var id = string.IsNullOrWhiteSpace(speakerId) ? ("channel_" + channel) : speakerId.Trim();
            LastStatus = "Submitted simulated text for " + id + ": " + trimmed;
            Debug.Log("[AaltoMultiSpeakerPlaceholder] " + LastStatus);
            Performer.ReceiveExternalSpeech(id, trimmed, "simulated_placeholder");
        }
    }
}

