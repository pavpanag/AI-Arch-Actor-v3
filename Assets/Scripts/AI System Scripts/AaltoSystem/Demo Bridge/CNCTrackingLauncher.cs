using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CNCDemo
{
    /// <summary>
    /// Opens the external position tracker (position tracking v4 copy.py — YOLO camera tracking
    /// that sends "/position x y" OSC to port 3334) and closes it again. Built for showcasing:
    /// it launches when this component is ENABLED and closes when it is disabled — so putting it
    /// on a "Position Tracking" GameObject that is inactive by default gives you a one-checkbox
    /// switch in the Inspector, and the heavy YOLO process never runs unless you flip it on.
    ///
    /// Fully additive: only spawns/kills a process. The tracker script and the existing
    /// ReceivePosition/OSC receiver pipeline are untouched.
    /// </summary>
    public sealed class CNCTrackingLauncher : MonoBehaviour
    {
        [Header("Launch")]
        [Tooltip("Open the tracker whenever this component/GameObject is enabled.")]
        public bool LaunchOnEnable = true;
        [Tooltip("Close the tracker when this component/GameObject is disabled (and on quit).")]
        public bool CloseOnDisable = true;

        [Header("Paths (blank = sensible defaults for this machine)")]
        [Tooltip("Python with cv2/ultralytics/torch/matplotlib/pythonosc. Blank = the Python 3.11 " +
                 "install (verified to have them), else 'python' on PATH. NOT the project venv — it lacks torch.")]
        public string PythonExe = "";
        [Tooltip("The tracker script. Blank = 'position tracking v4 copy.py' beside the Aalto scripts.")]
        public string ScriptPath = "";

        [Header("Status (read-only)")]
        [TextArea(1, 3)] public string Status;

        private Process _proc;

        private void OnEnable()
        {
            if (LaunchOnEnable) Launch();
        }

        private void OnDisable()
        {
            if (CloseOnDisable) Close();
        }

        private void OnApplicationQuit() => Close();
        private void OnDestroy() => Close();

        [ContextMenu("Launch tracker now")]
        public void Launch()
        {
            if (_proc != null && !_proc.HasExited)
            {
                Status = "Tracker already running.";
                return;
            }

            var python = ResolvePython();
            var script = ResolveScript();

            if (!File.Exists(script))
            {
                Status = "Script not found: " + script;
                Debug.LogWarning("[CNCTrackingLauncher] " + Status);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = "\"" + script + "\"",
                    // CWD = the script's folder so YOLO finds yolov8n.pt next to it.
                    WorkingDirectory = Path.GetDirectoryName(script) ?? string.Empty,
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                _proc = Process.Start(psi);
                Status = "Launched tracker (pid " + (_proc != null ? _proc.Id.ToString() : "?") +
                         "). YOLO warms up ~8s, then the camera-view window appears.";
                Debug.Log("[CNCTrackingLauncher] " + Status);

                // If it dies immediately (bad python, crashed import), say so instead of
                // looking like "nothing opened".
                CancelInvoke(nameof(ReportEarlyExit));
                Invoke(nameof(ReportEarlyExit), 6f);
            }
            catch (Exception ex)
            {
                _proc = null;
                Status = "Failed to launch: " + ex.Message + "  (python: " + python + ")";
                Debug.LogWarning("[CNCTrackingLauncher] " + Status);
            }
        }

        private void ReportEarlyExit()
        {
            if (_proc == null) return;
            try
            {
                if (_proc.HasExited)
                {
                    Status = "Tracker exited right after launch (code " + _proc.ExitCode + "). " +
                             "Run it once manually to see the error: right-click the script folder in a terminal and " +
                             "\"python position tracking v4 copy.py\".";
                    Debug.LogWarning("[CNCTrackingLauncher] " + Status);
                    _proc = null;
                }
            }
            catch { /* process handle gone — nothing to report */ }
        }

        [ContextMenu("Close tracker now")]
        public void Close()
        {
            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    _proc.Kill();
                    _proc.WaitForExit(2000);
                    Status = "Tracker closed.";
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CNCTrackingLauncher] Close failed: " + ex.Message);
            }
            finally
            {
                _proc = null;
            }
        }

        private string ResolvePython()
        {
            if (!string.IsNullOrWhiteSpace(PythonExe)) return PythonExe.Trim();

            // The tracker needs torch/ultralytics, which the project venv does NOT have.
            // Prefer the full Python 3.11 install (verified), else whatever PATH offers.
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var py311 = Path.Combine(localAppData, "Programs", "Python", "Python311", "python.exe");
            return File.Exists(py311) ? py311 : "python";
        }

        private string ResolveScript()
        {
            if (!string.IsNullOrWhiteSpace(ScriptPath)) return ScriptPath.Trim();
            var root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return Path.Combine(root,
                "Assets", "Scripts", "AI System Scripts", "AaltoSystem", "position tracking v4 copy.py");
        }
    }
}
