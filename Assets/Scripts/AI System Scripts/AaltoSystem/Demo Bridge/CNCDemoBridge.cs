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
        public AaltoActionMemoryRegistryDual Registry;
        public CNCFrameCompiler FrameCompiler;
        public CNCLampActuator Actuator;
        public CNCHueProjector Projector;

        [Header("Default Scene")]
        [Tooltip("Preset loaded on Start so a visitor can walk up to a working directed lamp with no setup.")]
        public LampScenePreset DefaultScene;
        public bool LoadDefaultSceneOnStart = true;

        // The performer's acting coaching — the malleable part of its system prompt. The fixed
        // JSON schema is appended by ApplyPerformerPrompt so editing this can't break parsing.
        private const string DefaultPerformerCoaching =
            "You are performing the inner life of a room in an improvised scene with another actor, who speaks to the room. You listen to what the actor says and respond by selecting the most appropriate action from a predefined list of actions.\n\n" +
            "Do not behave like a chatbot or a neutral assistant. Approach the role as a method actor would: interpret each moment from within the room's inner life, shaped by backstory, objectives, obstacles, circumstances, stance, recent interaction history, and director guidance. All of these are provided in this prompt.\n\n" +
            "The room cannot speak directly. It can only express itself through a fixed list of available action labels. These labels correspond to physical or scenographic behaviours, such as changes in light, atmosphere, or spatial expression. The actor will perceive your response only through the environment.\n\n" +
            "Your task is to choose the single action that is most dramaturgically appropriate for the current moment.\n\n" +
            "When choosing an action:\n" +
            "- take into account the actor's latest spoken phrase, since your action responds directly to it\n" +
            "- take into account the character summary, current objective, current stance, recent interaction history, and director guidance\n" +
            "- do not respond only to the latest line; consider previous interactions so that the scene unfolds with coherence and dramaturgically appropriate development\n" +
            "- do not act randomly\n" +
            "- do not invent new actions; only choose from the provided list\n" +
            "- aim for behavioural coherence, interpretability, and dramatic usefulness\n\n" +
            "Treat contradictions as part of the role, not as errors. If different parts of the context pull in different directions, handle this as an actor would: choose the action that best expresses or productively navigates the tension of the moment.\n\n" +
            "Use the recent interaction history and past justifications to maintain continuity. If a behaviour, tone, symbolic association, or use of an action has already been established, remain consistent unless there is a strong reason to shift.";

        private const string PerformerSchemaTail =
            "Return JSON only with exactly this schema:\n" +
            "{\n" +
            "  \"chosen_action\": \"<one action label from the available list>\",\n" +
            "  \"justification\": \"<1-3 sentences explaining why this action was chosen>\"\n" +
            "}";

        [Header("Performer Acting Coaching (schema appended automatically)")]
        [TextArea(8, 24)]
        public string PerformerCoaching = DefaultPerformerCoaching;

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
            // Keep Unity's update loop ticking while the browser has focus. Without this the
            // main-thread queue never drains when the editor is in the background, and every
            // API call times out. Set in code so it works without touching Player settings.
            Application.runInBackground = true;

            // The bridge owns character context, so the performer must never block a turn waiting
            // for a bootstrapper. Force this off even if no preset is assigned.
            if (Performer != null)
            {
                Performer.PullContextFromBootstrapper = false;
                Performer.RequireCompleteBootstrapperContext = false;
                // Labels come live from the registry (which the bridge keeps in sync with the
                // behavior list), so the manual pulled-snapshot workflow is bypassed.
                Performer.UsePulledActionLabelsSnapshot = false;

                // Force the performer to read the SAME registry the bridge writes to — otherwise
                // Apply syncs a vocabulary the performer never sees ("no executable action labels").
                if (Registry != null)
                {
                    Performer.ActionMemoryRegistryDual = Registry;
                    Performer.ActionRegistrySource = AaltoDirectedRoomPerformerController.ActionRegistrySourceMode.AutoPreferDual;
                }

                // Hold/return-to-neutral is handled by the actuator (Expression tab mode), so the
                // performer must not also fire its own neutral trigger.
                Performer.SelectedActionStateMode = AaltoDirectedRoomPerformerController.ActionStateMode.HoldResponseState;

                // The bridge owns the performer's system prompt: coaching (editable) + schema (fixed).
                ApplyPerformerPrompt();
            }

            if (LoadDefaultSceneOnStart && DefaultScene != null)
                ApplyPreset(DefaultScene);

            LoadAndApplyApiKey();

            // Restore the last saved setup over the preset defaults (so you don't re-author each run).
            LoadSessionFromFile();

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

            // The sound upload is raw bytes; everything else is small JSON text.
            byte[] rawBody = null;
            string body = null;
            if (path == "/api/expression/sound-upload") rawBody = ReadBodyBytes(ctx.Request);
            else body = ReadBody(ctx.Request);

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
                            ? FrameCompiler.GenerateFollowUpAsync(req.type, req.items, Performer != null ? Performer.CurrentCharacterSummary : null)
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
                                ? FrameCompiler.CompileSceneAsync(req.items, req.followUpQuestion, req.followUpAnswer, Performer != null ? Performer.CurrentCharacterSummary : null)
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
                        RunOnMainThread(() => { ApplyCharacterBrief(brief); return true; });
                        WriteJson(ctx, 200, CharacterBriefJson(brief));
                    }
                    break;
                }

                case "/api/frame/revise":
                {
                    var r = ParseReviseRequest(body);
                    var brief = RunOnMainThreadAsync(() =>
                        FrameCompiler != null
                            ? FrameCompiler.ReviseCharacterAsync(r.summary, r.objective, r.obstacle, r.stance, r.change)
                            : Task.FromResult<CNCFrameCompiler.DramaturgyBrief>(null));
                    RunOnMainThread(() => { ApplyCharacterBrief(brief); return true; });
                    WriteJson(ctx, 200, CharacterBriefJson(brief));
                    break;
                }

                case "/api/frame/revise-scene":
                {
                    var sceneFrame = GetField(body, "sceneFrame");
                    var change = GetField(body, "change");
                    var brief = RunOnMainThreadAsync(() =>
                        FrameCompiler != null
                            ? FrameCompiler.ReviseSceneAsync(sceneFrame, change, Performer != null ? Performer.CurrentCharacterSummary : null)
                            : Task.FromResult<CNCFrameCompiler.SceneBrief>(null));
                    RunOnMainThread(() => { ApplySceneFrame(brief); return true; });
                    WriteJson(ctx, 200, SceneBriefJson(brief));
                    break;
                }

                case "/api/frame/enrich":
                {
                    var e = ParseEnrichRequest(body);
                    if (IsSceneType(e.type))
                    {
                        var brief = RunOnMainThreadAsync(() =>
                            FrameCompiler != null
                                ? FrameCompiler.EnrichSceneAsync(e.sceneFrame, Performer != null ? Performer.CurrentCharacterSummary : null)
                                : Task.FromResult<CNCFrameCompiler.SceneBrief>(null));
                        RunOnMainThread(() => { ApplySceneFrame(brief); return true; });
                        WriteJson(ctx, 200, SceneBriefJson(brief));
                    }
                    else
                    {
                        var brief = RunOnMainThreadAsync(() =>
                            FrameCompiler != null
                                ? FrameCompiler.EnrichCharacterAsync(e.summary, e.objective, e.obstacle, e.stance)
                                : Task.FromResult<CNCFrameCompiler.DramaturgyBrief>(null));
                        RunOnMainThread(() => { ApplyCharacterBrief(brief); return true; });
                        WriteJson(ctx, 200, CharacterBriefJson(brief));
                    }
                    break;
                }

                case "/api/tech":
                    WriteJson(ctx, 200, RunOnMainThread(BuildTechJson));
                    break;

                case "/api/tech/apply":
                {
                    var req = ParseTechRequest(body);
                    WriteJson(ctx, 200, RunOnMainThread(() => { ApplyTechRequest(req); return BuildTechJson(); }));
                    break;
                }

                case "/api/tech/key":
                {
                    var key = GetField(body, "key");
                    WriteJson(ctx, 200, RunOnMainThread(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            try { File.WriteAllText(ApiKeyFilePath, key.Trim()); }
                            catch (Exception ex) { return "{\"status\":\"save failed: " + Escape(ex.Message) + "\",\"keyStatus\":\"" + Escape(_apiKeyStatus) + "\"}"; }
                            ApplyApiKeyToClients(key.Trim());
                            _apiKeyStatus = "set (saved) ••••" + Last4(key.Trim());
                        }
                        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                        var note = !string.IsNullOrWhiteSpace(env) ? " — note: OPENAI_API_KEY env var is set and overrides this on restart" : "";
                        return "{\"status\":\"Key saved" + Escape(note) + "\",\"keyStatus\":\"" + Escape(_apiKeyStatus) + "\"}";
                    }));
                    break;
                }

                case "/api/tech/reset":
                    WriteJson(ctx, 200, RunOnMainThread(() =>
                    {
                        FrameCompiler?.ResetPromptsToDefaults();
                        PerformerCoaching = DefaultPerformerCoaching;
                        ApplyPerformerPrompt();
                        return BuildTechJson();
                    }));
                    break;

                case "/api/tech/hue-rediscover":
                    WriteJson(ctx, 200, RunOnMainThread(() => { Projector?.Rediscover(); return BuildTechJson(); }));
                    break;

                case "/api/session/save":
                    WriteJson(ctx, 200, RunOnMainThread(() =>
                        "{\"status\":\"" + (SaveSession() ? "Setup saved" : "save failed") + "\"}"));
                    break;

                case "/api/session/load":
                    WriteJson(ctx, 200, RunOnMainThread(() =>
                        "{\"loaded\":" + (LoadSessionFromFile() ? "true" : "false") + "}"));
                    break;

                case "/api/expression/list":
                    WriteJson(ctx, 200, RunOnMainThread(BuildExpressionListJson));
                    break;

                case "/api/expression/mics":
                    WriteJson(ctx, 200, RunOnMainThread(() =>
                        "{\"mics\":" + BuildJsonStringArrayLocal(Actuator != null ? Actuator.GetMicrophones() : Array.Empty<string>()) + "}"));
                    break;

                case "/api/expression/apply":
                {
                    var req = ParseBehaviorApply(body);
                    var json = RunOnMainThread(() =>
                    {
                        if (Actuator != null)
                        {
                            if (!string.IsNullOrEmpty(req.mode))
                                Actuator.ActionStateMode = req.mode == "return"
                                    ? CNCActionStateMode.ActAndReturnToNeutral
                                    : CNCActionStateMode.ChooseAndKeep;
                            if (req.holdSeconds >= 0f) Actuator.HoldSeconds = req.holdSeconds;
                            if (!string.IsNullOrEmpty(req.neutralColorHex)) Actuator.NeutralColorHex = req.neutralColorHex;
                            if (req.neutralBrightness >= 0f) Actuator.NeutralBrightness = req.neutralBrightness;
                        }

                        if (Actuator != null && req.behaviors != null)
                        {
                            // Rebuild the list from the console so add / edit / REMOVE / reorder all apply.
                            var rebuilt = new List<CNCBehaviorSpec>();
                            foreach (var incoming in req.behaviors)
                            {
                                if (incoming == null) continue;
                                rebuilt.Add(new CNCBehaviorSpec
                                {
                                    label = incoming.label, colorHex = incoming.colorHex, brightness = incoming.brightness,
                                    pulse = incoming.pulse, pulseCount = incoming.pulseCount, pulseSeconds = incoming.pulseSeconds,
                                    soundFile = incoming.soundFile ?? ""
                                });
                            }
                            Actuator.SetBehaviors(rebuilt);
                            SyncRegistryFromBehaviors();
                        }
                        return BuildExpressionListJson();
                    });
                    WriteJson(ctx, 200, json);
                    break;
                }

                case "/api/expression/test":
                {
                    var req = ParseIndexRequest(body);
                    RunOnMainThread(() => { Actuator?.TestBehavior(req.index); return true; });
                    WriteJson(ctx, 200, "{\"ok\":true}");
                    break;
                }

                case "/api/expression/suggest":
                {
                    var label = RunOnMainThreadAsync<string>(() =>
                    {
                        if (FrameCompiler == null) return Task.FromResult(string.Empty);
                        var labels = new List<string>();
                        if (Actuator != null)
                            foreach (var b in Actuator.Behaviors)
                                if (b != null && !string.IsNullOrWhiteSpace(b.label)) labels.Add(b.label);
                        return FrameCompiler.SuggestBehaviorAsync(
                            Performer != null ? Performer.CurrentCharacterSummary : "", _sceneFrame, labels);
                    });
                    WriteJson(ctx, 200, "{\"behavior\":\"" + Escape(label) + "\"}");
                    break;
                }

                case "/api/expression/record":
                {
                    var req = ParseIndexRequest(body);
                    var result = RunOnMainThreadAsync<string>(() =>
                        Actuator != null
                            ? Actuator.RecordSoundAsync(req.index, req.seconds > 0 ? req.seconds : 3f, req.mic)
                            : Task.FromResult("actuator not assigned"));
                    RunOnMainThread(() => { SyncRegistryFromBehaviors(); return true; });
                    WriteJson(ctx, 200, "{\"status\":\"" + Escape(result ?? "timed out") + "\"}");
                    break;
                }

                case "/api/expression/sound-upload":
                {
                    var idx = ParseQueryInt(ctx.Request, "index", -1);
                    var ext = ctx.Request.QueryString["ext"] ?? "wav";
                    var result = RunOnMainThreadAsync<string>(() =>
                        Actuator != null
                            ? Actuator.SaveUploadedSoundAsync(idx, rawBody, ext)
                            : Task.FromResult("actuator not assigned"));
                    RunOnMainThread(() => { SyncRegistryFromBehaviors(); return true; });
                    WriteJson(ctx, 200, "{\"status\":\"" + Escape(result ?? "timed out") + "\"}");
                    break;
                }

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

        [Serializable]
        private sealed class ReviseRequest
        {
            public string summary;
            public string objective;
            public string obstacle;
            public string stance;
            public string change;
        }

        private static ReviseRequest ParseReviseRequest(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return new ReviseRequest();
            try { return JsonUtility.FromJson<ReviseRequest>(body) ?? new ReviseRequest(); }
            catch { return new ReviseRequest(); }
        }

        /// <summary>Applies a compiled/revised character to the performer, folding the obstacle into the summary so the model always sees it.</summary>
        private void ApplyCharacterBrief(CNCFrameCompiler.DramaturgyBrief brief)
        {
            if (brief == null || Performer == null) return;

            var summary = (brief.summary ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(brief.obstacle))
                summary = (summary + "\nWhat stands in the way: " + brief.obstacle.Trim()).Trim();

            if (!string.IsNullOrWhiteSpace(summary)) Performer.CurrentCharacterSummary = summary;
            if (!string.IsNullOrWhiteSpace(brief.objective)) Performer.CurrentObjective = brief.objective;
            if (!string.IsNullOrWhiteSpace(brief.stance)) Performer.CurrentStance = brief.stance;
        }

        private static string CharacterBriefJson(CNCFrameCompiler.DramaturgyBrief b)
        {
            return "{\"summary\":\"" + Escape(b != null ? b.summary : string.Empty) + "\"," +
                   "\"objective\":\"" + Escape(b != null ? b.objective : string.Empty) + "\"," +
                   "\"obstacle\":\"" + Escape(b != null ? b.obstacle : string.Empty) + "\"," +
                   "\"stance\":\"" + Escape(b != null ? b.stance : string.Empty) + "\"}";
        }

        [Serializable]
        private sealed class EnrichRequest
        {
            public string type;
            public string summary;
            public string objective;
            public string obstacle;
            public string stance;
            public string sceneFrame;
        }

        private static EnrichRequest ParseEnrichRequest(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return new EnrichRequest();
            try { return JsonUtility.FromJson<EnrichRequest>(body) ?? new EnrichRequest(); }
            catch { return new EnrichRequest(); }
        }

        private void ApplySceneFrame(CNCFrameCompiler.SceneBrief brief)
        {
            if (brief == null || string.IsNullOrWhiteSpace(brief.sceneFrame)) return;
            _sceneFrame = brief.sceneFrame;
            ComposeGuidance();
        }

        private static string SceneBriefJson(CNCFrameCompiler.SceneBrief b)
        {
            return "{\"sceneFrame\":\"" + Escape(b != null ? b.sceneFrame : string.Empty) + "\"}";
        }

        // --- Technical tab helpers ---------------------------------------------

        [Serializable]
        private sealed class TechRequest
        {
            public string model = "";          // "light" | "heavy" | "" = unchanged
            public string performer = "";
            public string charFollowup = "";
            public string charCompile = "";
            public string charEnrich = "";
            public string charRevise = "";
            public string sceneFollowup = "";
            public string sceneCompile = "";
            public string sceneEnrich = "";
            public string sceneRevise = "";
            public string suggest = "";
            public string hueTarget = "";
        }

        private static TechRequest ParseTechRequest(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return new TechRequest();
            try { return JsonUtility.FromJson<TechRequest>(body) ?? new TechRequest(); }
            catch { return new TechRequest(); }
        }

        private void ApplyTechRequest(TechRequest req)
        {
            if (!string.IsNullOrEmpty(req.model))
            {
                var heavy = req.model == "heavy";
                if (Performer != null)
                    Performer.Model = heavy
                        ? AaltoDirectedRoomPerformerController.OpenAIModelPreset.Gpt54
                        : AaltoDirectedRoomPerformerController.OpenAIModelPreset.Gpt4oMini;
                if (FrameCompiler != null)
                    FrameCompiler.Model = heavy ? "gpt-5.4" : "gpt-4o-mini";
            }

            if (!string.IsNullOrEmpty(req.hueTarget) && Projector != null) Projector.BulbIds = req.hueTarget.Trim();

            if (!string.IsNullOrEmpty(req.performer)) { PerformerCoaching = req.performer; ApplyPerformerPrompt(); }
            if (FrameCompiler != null)
            {
                if (!string.IsNullOrEmpty(req.charFollowup)) FrameCompiler.CharacterFollowUpPrompt = req.charFollowup;
                if (!string.IsNullOrEmpty(req.charCompile)) FrameCompiler.CharacterCompilePrompt = req.charCompile;
                if (!string.IsNullOrEmpty(req.charEnrich)) FrameCompiler.CharacterEnrichPrompt = req.charEnrich;
                if (!string.IsNullOrEmpty(req.charRevise)) FrameCompiler.CharacterRevisePrompt = req.charRevise;
                if (!string.IsNullOrEmpty(req.sceneFollowup)) FrameCompiler.SceneFollowUpPrompt = req.sceneFollowup;
                if (!string.IsNullOrEmpty(req.sceneCompile)) FrameCompiler.SceneCompilePrompt = req.sceneCompile;
                if (!string.IsNullOrEmpty(req.sceneEnrich)) FrameCompiler.SceneEnrichPrompt = req.sceneEnrich;
                if (!string.IsNullOrEmpty(req.sceneRevise)) FrameCompiler.SceneRevisePrompt = req.sceneRevise;
                if (!string.IsNullOrEmpty(req.suggest)) FrameCompiler.SuggestBehaviorPrompt = req.suggest;
            }
        }

        private string BuildTechJson()
        {
            var model = FrameCompiler != null && (FrameCompiler.Model ?? "").StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
                ? "heavy" : "light";

            var hueTarget = Projector != null ? (Projector.BulbIds ?? "auto") : "auto";
            var hueIds = new List<string>();
            if (Projector != null)
                foreach (var id in Projector.DiscoveredBulbs()) hueIds.Add(id.ToString());
            var hueEnabled = Projector != null && Projector.EnableProjection;

            var sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(model).Append("\",\"keyStatus\":\"").Append(Escape(_apiKeyStatus)).Append("\"");
            sb.Append(",\"hueTarget\":\"").Append(Escape(hueTarget)).Append("\"");
            sb.Append(",\"hueEnabled\":").Append(hueEnabled ? "true" : "false");
            sb.Append(",\"hueIds\":").Append(BuildJsonStringArrayLocal(hueIds.ToArray()));
            sb.Append(",\"prompts\":{");
            sb.Append("\"performer\":\"").Append(Escape(PerformerCoaching)).Append("\"");
            if (FrameCompiler != null)
            {
                sb.Append(",\"charFollowup\":\"").Append(Escape(FrameCompiler.CharacterFollowUpPrompt)).Append("\"");
                sb.Append(",\"charCompile\":\"").Append(Escape(FrameCompiler.CharacterCompilePrompt)).Append("\"");
                sb.Append(",\"charEnrich\":\"").Append(Escape(FrameCompiler.CharacterEnrichPrompt)).Append("\"");
                sb.Append(",\"charRevise\":\"").Append(Escape(FrameCompiler.CharacterRevisePrompt)).Append("\"");
                sb.Append(",\"sceneFollowup\":\"").Append(Escape(FrameCompiler.SceneFollowUpPrompt)).Append("\"");
                sb.Append(",\"sceneCompile\":\"").Append(Escape(FrameCompiler.SceneCompilePrompt)).Append("\"");
                sb.Append(",\"sceneEnrich\":\"").Append(Escape(FrameCompiler.SceneEnrichPrompt)).Append("\"");
                sb.Append(",\"sceneRevise\":\"").Append(Escape(FrameCompiler.SceneRevisePrompt)).Append("\"");
                sb.Append(",\"suggest\":\"").Append(Escape(FrameCompiler.SuggestBehaviorPrompt)).Append("\"");
            }
            sb.Append("}}");
            return sb.ToString();
        }

        // --- Expression endpoint helpers ---------------------------------------

        [Serializable]
        private sealed class BehaviorApplyRequest
        {
            public List<CNCBehaviorSpec> behaviors = new List<CNCBehaviorSpec>();
            public string mode = "";               // "keep" | "return" | "" = leave unchanged
            public float holdSeconds = -1f;        // -1 = leave unchanged
            public string neutralColorHex = "";    // "" = leave unchanged
            public float neutralBrightness = -1f;  // -1 = leave unchanged
        }

        [Serializable]
        private sealed class IndexRequest { public int index; public float seconds; public string mic; }

        private static BehaviorApplyRequest ParseBehaviorApply(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return new BehaviorApplyRequest();
            try { return JsonUtility.FromJson<BehaviorApplyRequest>(body) ?? new BehaviorApplyRequest(); }
            catch { return new BehaviorApplyRequest(); }
        }

        private static IndexRequest ParseIndexRequest(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return new IndexRequest { index = -1 };
            try { return JsonUtility.FromJson<IndexRequest>(body) ?? new IndexRequest { index = -1 }; }
            catch { return new IndexRequest { index = -1 }; }
        }

        private static int ParseQueryInt(HttpListenerRequest req, string key, int fallback)
        {
            var raw = req.QueryString[key];
            return int.TryParse(raw, out var v) ? v : fallback;
        }

        private static string BuildJsonStringArrayLocal(string[] values)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < (values?.Length ?? 0); i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\"").Append(Escape(values[i] ?? "")).Append("\"");
            }
            return sb.Append("]").ToString();
        }

        private static byte[] ReadBodyBytes(HttpListenerRequest req)
        {
            if (!req.HasEntityBody) return Array.Empty<byte>();
            using (var ms = new MemoryStream())
            {
                req.InputStream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private string BuildExpressionListJson()
        {
            var sb = new StringBuilder();
            sb.Append("{");
            if (Actuator != null)
            {
                sb.Append("\"mode\":\"").Append(Actuator.ActionStateMode == CNCActionStateMode.ActAndReturnToNeutral ? "return" : "keep").Append("\",");
                sb.Append("\"holdSeconds\":").Append(Actuator.HoldSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(",");
                sb.Append("\"neutralColorHex\":\"").Append(Escape(Actuator.NeutralColorHex ?? "#FFB45A")).Append("\",");
                sb.Append("\"neutralBrightness\":").Append(Actuator.NeutralBrightness.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(",");
            }
            sb.Append("\"behaviors\":[");
            var list = Actuator != null ? Actuator.Behaviors : null;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var b = list[i];
                    if (b == null) continue;
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"label\":\"").Append(Escape(b.label ?? ""))
                      .Append("\",\"colorHex\":\"").Append(Escape(b.colorHex ?? "#FFC073"))
                      .Append("\",\"brightness\":").Append(b.brightness.ToString(System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"pulse\":").Append(b.pulse ? "true" : "false")
                      .Append(",\"pulseCount\":").Append(b.pulseCount)
                      .Append(",\"pulseSeconds\":").Append(b.pulseSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"hasSound\":").Append(string.IsNullOrWhiteSpace(b.soundFile) ? "false" : "true")
                      .Append(",\"soundFile\":\"").Append(Escape(b.soundFile ?? "")).Append("\"}");
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // --- State snapshot ---------------------------------------------------

        private string BuildStateJson()
        {
            var summary = Performer != null ? Performer.CurrentCharacterSummary : string.Empty;
            var objective = Performer != null ? Performer.CurrentObjective : string.Empty;
            var stance = Performer != null ? Performer.CurrentStance : string.Empty;
            var guidance = Performer != null ? Performer.DirectorGuidance : string.Empty;
            var lastAction = Performer != null ? Performer.SelectedActionText : string.Empty;
            var lastWhy = Performer != null ? Performer.ActionJustificationText : string.Empty;

            var performerStatus = Performer != null ? Performer.LastStatus : "performer not assigned";
            var executionStatus = Performer != null ? Performer.ExecutionModeStatus : string.Empty;

            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"summary\":\"").Append(Escape(summary)).Append("\",");
            sb.Append("\"objective\":\"").Append(Escape(objective)).Append("\",");
            sb.Append("\"stance\":\"").Append(Escape(stance)).Append("\",");
            sb.Append("\"guidance\":\"").Append(Escape(guidance)).Append("\",");
            sb.Append("\"sceneFrame\":\"").Append(Escape(_sceneFrame ?? string.Empty)).Append("\",");

            // The executable vocabulary exactly as the model will see it (registry snapshot).
            var labels = new List<string>();
            if (Registry != null)
                foreach (var route in Registry.BuildExecutableMappingSnapshot())
                    if (route != null && !string.IsNullOrWhiteSpace(route.actionLabel)) labels.Add(route.actionLabel);
            sb.Append("\"behaviorLabels\":").Append(BuildJsonStringArrayLocal(labels.ToArray())).Append(",");

            sb.Append("\"performerStatus\":\"").Append(Escape(performerStatus)).Append("\",");
            sb.Append("\"executionStatus\":\"").Append(Escape(executionStatus)).Append("\",");
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
            var ex = Actuator != null ? Actuator.Behaviors : (DefaultScene != null ? DefaultScene.Behaviors : null);
            if (ex != null)
            {
                for (int i = 0; i < ex.Count; i++)
                {
                    if (ex[i] == null) continue;
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"actionLabel\":\"").Append(Escape(ex[i].label))
                      .Append("\",\"memory\":\"memory ").Append(i + 1).Append("\"}");
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

            var specs = new List<CNCBehaviorSpec>();
            if (preset.Behaviors != null)
            {
                foreach (var b in preset.Behaviors)
                {
                    if (b == null) continue;
                    specs.Add(new CNCBehaviorSpec
                    {
                        label = b.label, colorHex = b.colorHex, brightness = b.brightness,
                        pulse = b.pulse, pulseCount = b.pulseCount, pulseSeconds = b.pulseSeconds,
                        soundFile = b.soundFile
                    });
                }
            }

            if (Actuator != null)
            {
                Actuator.SetBehaviors(specs);
                Actuator.ActionStateMode = preset.ActionStateMode;
                Actuator.HoldSeconds = preset.HoldSeconds;
                Actuator.NeutralColorHex = preset.NeutralColorHex;
                Actuator.NeutralBrightness = preset.NeutralBrightness;
                SyncRegistryFromBehaviors();
            }
            else
            {
                // No actuator wired (e.g. Python rig only): the model still needs its vocabulary.
                SyncRegistryFromList(specs);
            }

            Debug.Log("[CNCDemoBridge] Loaded preset: " + preset.name);
        }

        /// <summary>
        /// Rebuilds the dual registry's mappings from the behavior list, preserving the seam:
        /// behavior i => label -> "memory i+1" (+ "sound i+1" when it has a sound).
        /// </summary>
        private void SyncRegistryFromBehaviors()
        {
            if (Actuator != null) SyncRegistryFromList(Actuator.Behaviors);
        }

        private void SyncRegistryFromList(List<CNCBehaviorSpec> behaviors)
        {
            if (Registry == null || behaviors == null) return;

            Registry.mappings.Clear();
            for (int i = 0; i < behaviors.Count; i++)
            {
                var b = behaviors[i];
                if (b == null || string.IsNullOrWhiteSpace(b.label)) continue;

                var hasSound = !string.IsNullOrWhiteSpace(b.soundFile);
                Registry.mappings.Add(new AaltoActionMemoryRoute
                {
                    actionLabel = b.label.Trim(),
                    routeMode = hasSound ? AaltoActionMemoryRouteMode.Both : AaltoActionMemoryRouteMode.LightOnly,
                    lightMemoryTrigger = "memory " + (i + 1),
                    soundMemoryTrigger = hasSound ? "sound " + (i + 1) : ""
                });
            }
        }

        // --- Session save / load (whole setup, stored outside the project) --------------------

        private string SessionFilePath => Path.Combine(Application.persistentDataPath, "cnc_session.json");

        [Serializable]
        private sealed class SessionState
        {
            public string summary, objective, stance, sceneFrame;
            public List<CNCBehaviorSpec> behaviors = new List<CNCBehaviorSpec>();
            public string mode; public float holdSeconds; public string neutralColorHex; public float neutralBrightness;
            public string model, performerCoaching;
            public string charFollowup, charCompile, charEnrich, charRevise;
            public string sceneFollowup, sceneCompile, sceneEnrich, sceneRevise, suggest;
        }

        private SessionState BuildSessionState()
        {
            var s = new SessionState();
            if (Performer != null)
            {
                s.summary = Performer.CurrentCharacterSummary;
                s.objective = Performer.CurrentObjective;
                s.stance = Performer.CurrentStance;
            }
            s.sceneFrame = _sceneFrame;
            if (Actuator != null)
            {
                s.behaviors = Actuator.Behaviors;
                s.mode = Actuator.ActionStateMode == CNCActionStateMode.ActAndReturnToNeutral ? "return" : "keep";
                s.holdSeconds = Actuator.HoldSeconds;
                s.neutralColorHex = Actuator.NeutralColorHex;
                s.neutralBrightness = Actuator.NeutralBrightness;
            }
            s.model = FrameCompiler != null && (FrameCompiler.Model ?? "").StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ? "heavy" : "light";
            s.performerCoaching = PerformerCoaching;
            if (FrameCompiler != null)
            {
                s.charFollowup = FrameCompiler.CharacterFollowUpPrompt; s.charCompile = FrameCompiler.CharacterCompilePrompt;
                s.charEnrich = FrameCompiler.CharacterEnrichPrompt; s.charRevise = FrameCompiler.CharacterRevisePrompt;
                s.sceneFollowup = FrameCompiler.SceneFollowUpPrompt; s.sceneCompile = FrameCompiler.SceneCompilePrompt;
                s.sceneEnrich = FrameCompiler.SceneEnrichPrompt; s.sceneRevise = FrameCompiler.SceneRevisePrompt;
                s.suggest = FrameCompiler.SuggestBehaviorPrompt;
            }
            return s;
        }

        private bool SaveSession()
        {
            try { File.WriteAllText(SessionFilePath, JsonUtility.ToJson(BuildSessionState(), true)); return true; }
            catch (Exception ex) { Debug.LogError("[CNCDemoBridge] session save failed: " + ex.Message); return false; }
        }

        private bool LoadSessionFromFile()
        {
            if (!File.Exists(SessionFilePath)) return false;
            try
            {
                var s = JsonUtility.FromJson<SessionState>(File.ReadAllText(SessionFilePath));
                if (s == null) return false;
                ApplySessionState(s);
                return true;
            }
            catch (Exception ex) { Debug.LogError("[CNCDemoBridge] session load failed: " + ex.Message); return false; }
        }

        private void ApplySessionState(SessionState s)
        {
            if (Performer != null)
            {
                if (!string.IsNullOrWhiteSpace(s.summary)) Performer.CurrentCharacterSummary = s.summary;
                if (!string.IsNullOrWhiteSpace(s.objective)) Performer.CurrentObjective = s.objective;
                if (!string.IsNullOrWhiteSpace(s.stance)) Performer.CurrentStance = s.stance;
            }
            if (s.sceneFrame != null) { _sceneFrame = s.sceneFrame; ComposeGuidance(); }

            if (Actuator != null && s.behaviors != null)
            {
                Actuator.SetBehaviors(s.behaviors);
                Actuator.ActionStateMode = s.mode == "return" ? CNCActionStateMode.ActAndReturnToNeutral : CNCActionStateMode.ChooseAndKeep;
                if (s.holdSeconds > 0f) Actuator.HoldSeconds = s.holdSeconds;
                if (!string.IsNullOrWhiteSpace(s.neutralColorHex)) Actuator.NeutralColorHex = s.neutralColorHex;
                if (s.neutralBrightness > 0f) Actuator.NeutralBrightness = s.neutralBrightness;
                SyncRegistryFromBehaviors();
            }
            else if (s.behaviors != null) SyncRegistryFromList(s.behaviors);

            if (!string.IsNullOrEmpty(s.model))
            {
                var heavy = s.model == "heavy";
                if (Performer != null) Performer.Model = heavy
                    ? AaltoDirectedRoomPerformerController.OpenAIModelPreset.Gpt54
                    : AaltoDirectedRoomPerformerController.OpenAIModelPreset.Gpt4oMini;
                if (FrameCompiler != null) FrameCompiler.Model = heavy ? "gpt-5.4" : "gpt-4o-mini";
            }

            if (!string.IsNullOrEmpty(s.performerCoaching)) { PerformerCoaching = s.performerCoaching; ApplyPerformerPrompt(); }
            if (FrameCompiler != null)
            {
                if (!string.IsNullOrEmpty(s.charFollowup)) FrameCompiler.CharacterFollowUpPrompt = s.charFollowup;
                if (!string.IsNullOrEmpty(s.charCompile)) FrameCompiler.CharacterCompilePrompt = s.charCompile;
                if (!string.IsNullOrEmpty(s.charEnrich)) FrameCompiler.CharacterEnrichPrompt = s.charEnrich;
                if (!string.IsNullOrEmpty(s.charRevise)) FrameCompiler.CharacterRevisePrompt = s.charRevise;
                if (!string.IsNullOrEmpty(s.sceneFollowup)) FrameCompiler.SceneFollowUpPrompt = s.sceneFollowup;
                if (!string.IsNullOrEmpty(s.sceneCompile)) FrameCompiler.SceneCompilePrompt = s.sceneCompile;
                if (!string.IsNullOrEmpty(s.sceneEnrich)) FrameCompiler.SceneEnrichPrompt = s.sceneEnrich;
                if (!string.IsNullOrEmpty(s.sceneRevise)) FrameCompiler.SceneRevisePrompt = s.sceneRevise;
                if (!string.IsNullOrEmpty(s.suggest)) FrameCompiler.SuggestBehaviorPrompt = s.suggest;
            }
        }

        // --- OpenAI API key (kept OUT of git: env var, else an untracked local file) ----------

        private string _apiKeyStatus = "not set";

        // Outside the project entirely (Application.persistentDataPath) — never committed.
        private string ApiKeyFilePath => Path.Combine(Application.persistentDataPath, "cnc_openai_key.txt");

        /// <summary>On boot: env var wins, else the saved file; applies to the OpenAI client(s).</summary>
        private void LoadAndApplyApiKey()
        {
            string key = null, source = null;
            var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) { key = env.Trim(); source = "env var"; }
            else if (File.Exists(ApiKeyFilePath))
            {
                try { key = File.ReadAllText(ApiKeyFilePath).Trim(); source = "saved"; } catch { }
            }

            if (!string.IsNullOrWhiteSpace(key))
            {
                ApplyApiKeyToClients(key);
                _apiKeyStatus = "set (" + source + ") ••••" + Last4(key);
            }
            else _apiKeyStatus = "not set";
        }

        private void ApplyApiKeyToClients(string key)
        {
            if (Performer != null && Performer.OpenAI != null) Performer.OpenAI.ApiKey = key;
            if (FrameCompiler != null && FrameCompiler.OpenAI != null) FrameCompiler.OpenAI.ApiKey = key;
        }

        private static string Last4(string k) => string.IsNullOrEmpty(k) || k.Length < 4 ? "" : k.Substring(k.Length - 4);

        /// <summary>Writes coaching + fixed JSON schema into the performer's system prompt.</summary>
        private void ApplyPerformerPrompt()
        {
            if (Performer == null) return;
            var coaching = string.IsNullOrWhiteSpace(PerformerCoaching) ? DefaultPerformerCoaching : PerformerCoaching;
            Performer.GeneralInstructions = coaching.Trim() + "\n\n" + PerformerSchemaTail;
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
            // Never cache the console during the demo, so an old page can't linger in the browser.
            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            ctx.Response.Headers["Pragma"] = "no-cache";
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
