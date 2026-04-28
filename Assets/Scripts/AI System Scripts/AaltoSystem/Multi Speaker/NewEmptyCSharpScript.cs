using UnityEngine;

namespace AaltoSystemV3
{
    /// <summary>
    /// Placeholder for the Vosk transcriber that was mistakenly saved as a .cs file.
    /// Keep this lightweight stub in the project to avoid C# compilation errors.
    /// Move the original Python application to a .py file outside the Unity C# scripts folder.
    /// </summary>
    public class VoskTranscriberStub : MonoBehaviour
    {
        [TextArea(2,6)]
        public string Note = "Original Python Vosk transcriber removed from this .cs file. Move the .py elsewhere.";

        private void Awake()
        {
            Debug.Log("[VoskTranscriberStub] Placeholder active.\n" + Note);
        }
    }
}
