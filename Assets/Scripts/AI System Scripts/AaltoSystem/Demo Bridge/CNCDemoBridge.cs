using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using AaltoSystemV3;

namespace CNCDemo
{
    /// <summary>
    /// Local HTTP bridge that serves the React directing console and exposes the existing
    /// Aalto controllers over a small REST surface. All reasoning/logic stays in Unity;
    /// this component is only a network front door.
    ///
    /// Threading: HttpListener callbacks run on background threads, but every Unity/controller
    /// call must run on the main thread. Requests that touch controllers are marshalled onto
    /// the main thread via RunOnMainThread(...) (drained in Update), mirroring the queue pattern
    /// used by the OSC speech receivers.
    /// </summary>
    public sealed class CNCDemoBridge : MonoBehaviour
    {
        [Header("Server")]
        [Tooltip("Port for the local console. Open http://localhost:<port> in a browser on this machine.")]
        public int Port = 8080;

        [Tooltip("Bind to all interfaces (http://+:port) so a phone/tablet on the same LAN can connect. Leave off for localhost-only during development.")]
        public bool AllowLanAccess = false;

        [Tooltip("Folder under StreamingAssets holding the built React bundle (index.html, assets/...).")]
        public string ConsoleFolder = "demo-console";

        [Header("Wired Controllers (assign in Inspector)")]
        public AaltoDirectedRoomPerformerController Performer;
        public AaltoInterviewController Interview;
        public AaltoObjectiveStanceBootstrapper Bootstrapper;
        public AaltoActionMemoryRegistryDual Registry;
        public CNCFrameCompiler FrameCompiler;

        [Header("Default Scene")]
        [Tooltip("Preset loaded on Start so a visitor can walk up to a working directed lamp with no setup.")]
        public LampScenePreset DefaultScene;
        public bool LoadDefaultSceneOnStart = true;

        [Header("Status (read-only)")]
        [TextArea(1, 3)] public string ServerStatus;
        [TextArea(1, 3)] public string LastRequest;

        private HttpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        // Bridge owns the guidance channel so scene frame + directing lines never fight the
        // performer's own DirectorGuidanceInputs rebuild. Composed into Performer.DirectorGuidance.
        private string _sceneFrame = string.Empty;
        private readonly List<string> _directingLines = new List<string>();

        private void Start()
        {
            if (LoadDefaultSceneOnStart && DefaultScene != null)
                ApplyPreset(DefaultScene);

            StartServer();
        }

        private void OnDestroy() => StopServer();

        private void Update()
        {
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogError("[CNCDemoBridge] main-thread action failed: " + ex); }
            }
        }

        // --- Server lifecycle -------------------------------------------------

        private void StartServer()
        {
            try
            {
                _listener = new HttpListener();
                var host = AllowLanAccess ? "+" : "localhost";
                _listener.Prefixes.Add($"http://{host}:{Port}/");
                _listener.Start();
                _running = true;
                _listenerThread = new Thread(ListenLoop) { IsBackground = true };
                _listenerThread.Start();
                ServerStatus = $"Listening on http://{(AllowLanAccess ? "<this-machine-ip>" : "localhost")}:{Port}/";
                Debug.Log("[CNCDemoBridge] " + ServerStatus);
            }
            catch (Exception ex)
            {
                ServerStatus = "Failed to start: " + ex.Message +
                    (AllowLanAccess ? " (LAN bind may need admin/urlacl on Windows)" : "");
                Debug.LogError("[CNCDemoBridge] " + ServerStatus);
            }
        }

        private void StopServer()
        {
            _running = false;
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
        }

        private void ListenLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; } // listener stopped

                // Handle each request off the accept loop so a long model call (frame compile)
                // doesn't stall the live feed. Actual Unity access is still serialized via the
                // main-thread queue, so shared state stays safe.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { HandleRequest(ctx); }
                    catch (Exception ex)
                    {
                        try { WriteJson(ctx, 500, "{\"error\":\"" + Escape(ex.Message) + "\"}"); }
                        catch { }
                    }
                });
            }
        }

        // --- Routing ----------------------------------------------------------

        private void HandleRequest(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            var method = ctx.Request.HttpMethod;
            LastRequest = method + " " + (string.IsNullOrEmpty(path) ? "/" : path);

            if (!path.StartsWith("/api"))
            {
                ServeStaticFile(ctx, path);
                return;
            }

            var body = ReadBody(ctx.Request);

            switch (path)
            {
                case "/api/state":
                    WriteJson(ctx, 200, RunOnMainThread(BuildStateJson));
                    break;

                case "/api/frames":
                    WriteJson(ctx, 200, RunOnMainThread(BuildFramesJson));
                    break;

                case "/api/say":
                    RunOnMainThread(() => { Performer?.SubmitTurn(GetField(body, "text")); return true; });
                    WriteJson(ctx, 200, "{\"ok\":true}");
                    break;

                case "/api/direct":
                    RunOnMainThread(() =>
                    {
                        var line = GetField(body, "text");
                        if (!string.IsNullOrWhiteSpace(line)) _directingLines.Add(line.Trim());
                        ComposeGuidance();
                        return true;
                    });
                    WriteJson(ctx, 200, "{\"ok\":true}");
                    break;

                case "/api/scene":
                    RunOnMainThread(() =>
                    {
                        _sceneFrame = GetField(body, "sceneFrame");
                        ComposeGuidance();
                        return true;
                    });
                    WriteJson(ctx, 200, "{\"ok\":true}");
                    break;

                case "/api/dramaturgy":
                    RunOnMainThread(() =>
                    {
                        if (Performer != null)
                        {
                            var summary = GetField(body, "summary");
                            var objective = GetField(body, "objective");
                            var stance = GetField(body, "stance");
                            if (!string.IsNullOrWhiteSpace(summary)) Performer.CurrentCharacterSummary = summary;
                            if (!string.IsNullOrWhiteSpace(objective)) Performer.CurrentObjective = objective;
                            if (!string.IsNullOrWhiteSpace(stance)) Performer.CurrentStance = stance;
                        }
                        return true;
                    });
                    WriteJson(ctx, 200, "{\"ok\":true}");
                    break;

                case "/api/preset/load":
                    RunOnMainThread(() => { if (DefaultScene != null) ApplyPreset(DefaultScene); return true; });
                    WriteJson(ctx, 200, "{\"ok\":true}");
                    break;

                case "/api/frame/followup":
                {
                    var req = ParseFrameRequest(body);
                    var followUp = RunOnMainThreadAsync(() =>
                        FrameCompiler != null
                            ? FrameCompiler.GenerateFollowUpAsync(req.type, req.items)
                            : Task.FromResult(string.Empty));
                    WriteJson(ctx, 200, "{\"followUp\":\"" + Escape(followUp) + "\"}");
                    break;
                }

                case "/api/frame/compile":
                {
                    var req = ParseFrameRequest(body);
                    if (IsSceneType(req.type))
                    {
                        var brief = RunOnMainThreadAsync(() =>
                            FrameCompiler != null
                                ? FrameCompiler.CompileSceneAsync(req.items, req.followUpQuestion, req.followUpAnswer)
                                : Task.FromResult<CNCFrameCompiler.SceneBrief>(null));
                        RunOnMainThread(() =>
                        {
                            if (brief != null && !string.IsNullOrWhiteSpace(brief.sceneFrame))
                            {
                                _sceneFrame = brief.sceneFrame;
                                ComposeGuidance();
                            }
                            return true;
                        });
                        WriteJson(ctx, 200, "{\"sceneFrame\":\"" + Escape(brief != null ? brief.sceneFrame : string.Empty) + "\"}");
                    }
                    else
                    {
                        var brief = RunOnMainThreadAsync(() =>
                            FrameCompiler != null
                                ? FrameCompiler.CompileDramaturgyAsync(req.items, req.followUpQuestion, req.followUpAnswer)
                                : Task.FromResult<CNCFrameCompiler.DramaturgyBrief>(null));
                        RunOnMainThread(() =>
                        {
                            if (brief != null && Performer != null)
                            {
                                if (!string.IsNullOrWhiteSpace(brief.summary)) Performer.CurrentCharacterSummary = brief.summary;
                                if (!string.IsNullOrWhiteSpace(brief.objective)) Performer.CurrentObjective = brief.objective;
                                if (!string.IsNullOrWhiteSpace(brief.stance)) Performer.CurrentStance = brief.stance;
                            }
                            return true;
                        });
                        WriteJson(ctx, 200,
                            "{\"summary\":\"" + Escape(brief != null ? brief.summary : string.Empty) + "\"," +
                            "\"objective\":\"" + Escape(brief != null ? brief.objective : string.Empty) + "\"," +
                            "\"stance\":\"" + Escape(brief != null ? brief.stance : string.Empty) + "\"}");
                    }
                    break;
                }

                case "/api/expression":
                    WriteJson(ctx, 501, "{\"error\":\"not_implemented_yet\"}");
                    break;

                default:
                    WriteJson(ctx, 404, "{\"error\":\"unknown_endpoint\"}");
                    break;
            }
        }

        // --- Main-thread marshalling -----------------------------------------

        /// <summary>Runs func on the Unity main thread and blocks the listener thread until it returns.</summary>
        private T RunOnMainThread<T>(Func<T> func)
        {
            T result = default;
            using (var done = new ManualResetEventSlim(false))
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    try { result = func(); }
                    finally { done.Set(); }
                });
                done.Wait(TimeSpan.FromSeconds(5));
            }
            return result;
        }

        /// <summary>
        /// Starts an async operation on the Unity main thread (required for UnityWebRequest/model
        /// calls) and blocks the listener thread until it completes. Its await-continuations run on
        /// the main thread via Unity's sync context, so the main thread is not itself blocked.
        /// </summary>
        private T RunOnMainThreadAsync<T>(Func<Task<T>> asyncFunc, int timeoutSeconds = 40)
        {
            var tcs = new TaskCompletionSource<T>();
            _mainThreadQueue.Enqueue(() => _ = PumpAsync(asyncFunc, tcs));
            try
            {
                if (!tcs.Task.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
                    return default;
                return tcs.Task.Result;
            }
            catch (Exception ex)
            {
                Debug.LogError("[CNCDemoBridge] async op failed: " + ex.Message);
                return default;
            }
        }

        private static async Task PumpAsync<T>(Func<Task<T>> asyncFunc, TaskCompletionSource<T> tcs)
        {
            try { tcs.TrySetResult(await asyncFunc()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }

        // --- Incoming frame payload ------------------------------------------

        [Serializable]
        private sealed class FrameRequest
        {
            public string type;
            public List<FrameQuestion> items = new List<FrameQuestion>();
            public string followUpQuestion;
            public string followUpAnswer;
        }

        private static FrameRequest ParseFrameRequest(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return new FrameRequest();
            try { return JsonUtility.FromJson<FrameRequest>(body) ?? new FrameRequest(); }
            catch { return new FrameRequest(); }
        }

        private static bool IsSceneType(string type) =>
            !string.IsNullOrEmpty(type) && type.IndexOf("scene", StringComparison.OrdinalIgnoreCase) >= 0;

        // --- State snapshot ---------------------------------------------------

        private string BuildStateJson()
        {
            var summary = Performer != null ? Performer.CurrentCharacterSummary : string.Empty;
            var objective = Performer != null ? Performer.CurrentObjective : string.Empty;
            var stance = Performer != null ? Performer.CurrentStance : string.Empty;
            var guidance = Performer != null ? Performer.DirectorGuidance : string.Empty;
            var lastAction = Performer != null ? Performer.SelectedActionText : string.Empty;
            var lastWhy = Performer != null ? Performer.ActionJustificationText : string.Empty;

            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"summary\":\"").Append(Escape(summary)).Append("\",");
            sb.Append("\"objective\":\"").Append(Escape(objective)).Append("\",");
            sb.Append("\"stance\":\"").Append(Escape(stance)).Append("\",");
            sb.Append("\"guidance\":\"").Append(Escape(guidance)).Append("\",");
            sb.Append("\"lastAction\":\"").Append(Escape(lastAction)).Append("\",");
            sb.Append("\"lastJustification\":\"").Append(Escape(lastWhy)).Append("\",");

            // The decision feed: the full dialogue, each turn carrying the room's reason.
            // This is the cue of the whole system — it explains why it did what it did.
            sb.Append("\"dialogue\":[");
            var turns = Performer != null ? Performer.DialogTurns : null;
            if (turns != null)
            {
                for (int i = 0; i < turns.Count; i++)
                {
                    var t = turns[i];
                    if (t == null) continue;
                    if (i > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append("\"actor\":\"").Append(Escape(t.actorLine)).Append("\",");
                    sb.Append("\"action\":\"").Append(Escape(t.selectedResponse)).Append("\",");
                    sb.Append("\"why\":\"").Append(Escape(t.justification)).Append("\"");
                    sb.Append("}");
                }
            }
            sb.Append("]");
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>Exposes the preset's editable question lists + expression vocabulary to the console.</summary>
        private string BuildFramesJson()
        {
            var sb = new StringBuilder();
            sb.Append("{");

            sb.Append("\"dramaturgy\":");
            AppendFrameQuestions(sb, DefaultScene != null ? DefaultScene.DramaturgyQuestions : null);
            sb.Append(",");

            sb.Append("\"scene\":");
            AppendFrameQuestions(sb, DefaultScene != null ? DefaultScene.SceneQuestions : null);
            sb.Append(",");

            sb.Append("\"expressions\":[");
            var ex = DefaultScene != null ? DefaultScene.Expressions : null;
            if (ex != null)
            {
                for (int i = 0; i < ex.Count; i++)
                {
                    if (ex[i] == null) continue;
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"actionLabel\":\"").Append(Escape(ex[i].actionLabel))
                      .Append("\",\"memory\":\"").Append(Escape(ex[i].memory)).Append("\"}");
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendFrameQuestions(StringBuilder sb, List<FrameQuestion> qs)
        {
            sb.Append("[");
            if (qs != null)
            {
                for (int i = 0; i < qs.Count; i++)
                {
                    if (qs[i] == null) continue;
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"question\":\"").Append(Escape(qs[i].question))
                      .Append("\",\"answer\":\"").Append(Escape(qs[i].answer)).Append("\"}");
                }
            }
            sb.Append("]");
        }

        // --- Preset application ----------------------------------------------

        private void ApplyPreset(LampScenePreset preset)
        {
            if (preset == null) return;

            if (Performer != null)
            {
                // The bridge owns context directly for the demo, so the performer's per-turn
                // bootstrapper pull cannot overwrite what the console sets. This is a runtime
                // flag toggle on the existing component — no code change.
                Performer.PullContextFromBootstrapper = false;
                Performer.RequireCompleteBootstrapperContext = false;

                Performer.CurrentCharacterSummary = preset.CharacterSummary;
                Performer.CurrentObjective = preset.Objective;
                Performer.CurrentStance = preset.Stance;
            }

            _sceneFrame = preset.SceneFrame;
            _directingLines.Clear();
            ComposeGuidance();

            Debug.Log("[CNCDemoBridge] Loaded preset: " + preset.name);
        }

        /// <summary>Folds the scene frame and any directing lines into the performer's guidance channel.</summary>
        private void ComposeGuidance()
        {
            if (Performer == null) return;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(_sceneFrame))
                sb.Append(_sceneFrame.Trim());

            for (int i = 0; i < _directingLines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(_directingLines[i])) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("- ").Append(_directingLines[i].Trim());
            }

            Performer.DirectorGuidance = sb.ToString();
        }

        // --- Static file serving ---------------------------------------------

        private void ServeStaticFile(HttpListenerContext ctx, string path)
        {
            var root = Path.Combine(Application.streamingAssetsPath, ConsoleFolder);
            var rel = string.IsNullOrEmpty(path) || path == "/" ? "index.html" : path.TrimStart('/');
            var full = Path.GetFullPath(Path.Combine(root, rel));

            // Prevent path traversal outside the console folder.
            if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                var indexFallback = Path.Combine(root, "index.html"); // SPA fallback
                if (File.Exists(indexFallback)) full = indexFallback;
                else { WriteText(ctx, 404, "Console bundle not found. Build the React app into StreamingAssets/" + ConsoleFolder); return; }
            }

            var bytes = File.ReadAllBytes(full);
            ctx.Response.ContentType = ContentTypeFor(full);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static string ContentTypeFor(string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            switch (ext)
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "application/javascript";
                case ".css": return "text/css";
                case ".json": return "application/json";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".woff2": return "font/woff2";
                default: return "application/octet-stream";
            }
        }

        // --- Tiny HTTP/JSON helpers ------------------------------------------

        private static string ReadBody(HttpListenerRequest req)
        {
            if (!req.HasEntityBody) return string.Empty;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding))
                return reader.ReadToEnd();
        }

        /// <summary>Minimal flat-JSON string-field reader (avoids pulling in a JSON dependency for tiny payloads).</summary>
        private static string GetField(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return string.Empty;
            var needle = "\"" + key + "\"";
            var k = json.IndexOf(needle, StringComparison.Ordinal);
            if (k < 0) return string.Empty;
            var colon = json.IndexOf(':', k + needle.Length);
            if (colon < 0) return string.Empty;
            var q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return string.Empty;
            var sb = new StringBuilder();
            for (int i = q1 + 1; i < json.Length; i++)
            {
                var c = json[i];
                if (c == '\\' && i + 1 < json.Length) { sb.Append(Unescape(json[++i])); continue; }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static char Unescape(char c)
        {
            switch (c) { case 'n': return '\n'; case 't': return '\t'; case 'r': return '\r'; default: return c; }
        }

        private void WriteJson(HttpListenerContext ctx, int status, string json)
        {
            ctx.Response.ContentType = "application/json";
            WriteRaw(ctx, status, Encoding.UTF8.GetBytes(json ?? "{}"));
        }

        private void WriteText(HttpListenerContext ctx, int status, string text)
        {
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            WriteRaw(ctx, status, Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        private static void WriteRaw(HttpListenerContext ctx, int status, byte[] bytes)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
