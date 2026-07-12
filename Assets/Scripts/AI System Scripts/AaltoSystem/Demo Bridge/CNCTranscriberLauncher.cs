using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CNCDemo
{
    /// <summary>
    /// Opens the external Vosk transcriber (Vosk SpeechToText.py) when the scene starts, so you
    /// don't have to launch it by hand, and closes it again when Play stops or the app quits.
    ///
    /// Fully additive: it only spawns a process. The transcriber itself is untouched and still
    /// standalone — it speaks OSC to CNCSpeechOscReceiver, which feeds the lamp. If launching fails
    /// (wrong Python, missing deps), the demo still runs; you just start the transcriber manually.
    /// </summary>
    public sealed class CNCTranscriberLauncher : MonoBehaviour
    {
        [Header("Launch")]
        [Tooltip("Open the transcriber automatically when the scene starts.")]
        public bool LaunchOnStart = true;
        [Tooltip("Close the transcriber when Play stops / the app quits.")]
        public bool CloseOnExit = true;

        [Header("Paths (blank = sensible defaults for this project)")]
        [Tooltip("Python interpreter to run the script with. Blank = the project venv " +
                 "(.venv-1) if present, else 'python' on PATH. Must have vosk, sounddevice, tkinter.")]
        public string PythonExe = "";
        [Tooltip("The transcriber script. Blank = 'Vosk SpeechToText.py' beside the Aalto scripts.")]
        public string ScriptPath = "";

        [Header("Status (read-only)")]
        [TextArea(1, 3)] public string Status;

        private Process _proc;

        private void Start()
        {
            if (LaunchOnStart) Launch();
        }

        private void OnApplicationQuit()
        {
            if (CloseOnExit) Close();
        }

        private void OnDestroy()
        {
            // Covers leaving Play mode in the editor, where OnApplicationQuit is unreliable.
            if (CloseOnExit) Close();
        }

        [ContextMenu("Launch transcriber now")]
        public void Launch()
        {
            if (_proc != null && !_proc.HasExited)
            {
                Status = "Transcriber already running.";
                return;
            }

            var python = ResolvePython();
            var script = ResolveScript();

            if (!File.Exists(script))
            {
                Status = "Script not found: " + script;
                Debug.LogWarning("[CNCTranscriberLauncher] " + Status);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    // Quote the script path — it contains spaces ("AI System Scripts", "Demo Bridge").
                    Arguments = "\"" + script + "\"",
                    WorkingDirectory = Path.GetDirectoryName(script) ?? string.Empty,
                    UseShellExecute = true,   // let the Tkinter window own its own UI thread
                    CreateNoWindow = false
                };
                _proc = Process.Start(psi);
                Status = "Launched transcriber (" + Path.GetFileName(python) + ", pid " +
                         (_proc != null ? _proc.Id.ToString() : "?") + ").";
                Debug.Log("[CNCTranscriberLauncher] " + Status);
            }
            catch (Exception ex)
            {
                _proc = null;
                Status = "Failed to launch: " + ex.Message + "  (python: " + python + ")";
                Debug.LogWarning("[CNCTranscriberLauncher] " + Status);
            }
        }

        [ContextMenu("Close transcriber now")]
        public void Close()
        {
            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    _proc.Kill();
                    _proc.WaitForExit(2000);
                    Status = "Transcriber closed.";
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CNCTranscriberLauncher] Close failed: " + ex.Message);
            }
            finally
            {
                _proc = null;
            }
        }

        private string ResolvePython()
        {
            if (!string.IsNullOrWhiteSpace(PythonExe)) return PythonExe.Trim();

            // Prefer the project venv that already has the deps; fall back to PATH.
            var venv = Path.Combine(ProjectRoot(), ".venv-1", "Scripts", "python.exe");
            return File.Exists(venv) ? venv : "python";
        }

        private string ResolveScript()
        {
            if (!string.IsNullOrWhiteSpace(ScriptPath)) return ScriptPath.Trim();
            return Path.Combine(ProjectRoot(),
                "Assets", "Scripts", "AI System Scripts", "AaltoSystem", "Vosk SpeechToText.py");
        }

        /// <summary>Application.dataPath is "&lt;project&gt;/Assets"; the project root is its parent.</summary>
        private static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        }
    }
}
