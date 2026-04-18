using UnityEngine;

namespace AaltoSystemV3
{
    /// <summary>
    /// Optional button hooks for quickly testing scenic event logging from the Inspector.
    /// </summary>
    public class ScenicEventLogControls : MonoBehaviour
    {
        [ContextMenu("Scenic/Start New Session")]
        public void StartNewScenicSession() => ScenicEventLogger.StartNewSession();

        [ContextMenu("Scenic/Write Fake Event")]
        public void WriteFakeScenicEvent()
        {
            var e = ScenicEventLogger.WriteFakeEventForTest();
            Debug.Log($"[ScenicEventLogControls] Wrote fake scenic event turn={e.turnIndex} session={e.sessionId}");
        }

        [ContextMenu("Scenic/Reveal Log Folder")]
        public void RevealScenicLogFolder() => ScenicEventLogger.RevealLogFolder();

        [ContextMenu("Scenic/Reveal Session File")]
        public void RevealScenicSessionFile() => ScenicEventLogger.RevealSessionFile();
    }
}
