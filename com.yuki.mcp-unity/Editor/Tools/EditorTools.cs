using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace McpUnity.Tools
{
    internal sealed class GetEditorStateTool : McpTool
    {
        public override string Name => "get_editor_state";
        public override string Description =>
            "Overview of the Unity Editor: Unity version, project, render pipeline, play mode, compilation state, " +
            "open scenes, prefab editing stage, current selection, and console error/warning counts. Call this first.";
        public override bool ReadOnly => true;

        protected override object Execute(JObject args, ToolContext context)
        {
            var active = SceneManager.GetActiveScene();
            var scenes = new JArray();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                scenes.Add(GameObjectInfo.SceneInfo(scene, scene == active));
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            var counts = ConsoleLogs.GetCounts();
            var last = CompilationTracker.Last;

            return new
            {
                unityVersion = Application.unityVersion,
                project = new { name = Application.productName, path = McpSettings.ProjectRoot },
                buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                renderPipeline = RenderPipelineInfo.Name,
                colorSpace = PlayerSettings.colorSpace.ToString(),
                playMode = new
                {
                    isPlaying = EditorApplication.isPlaying,
                    isPaused = EditorApplication.isPaused,
                    isChanging = EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying,
                },
                compilation = new
                {
                    isCompiling = EditorApplication.isCompiling,
                    hasCompileErrors = EditorUtility.scriptCompilationFailed,
                    lastResult = last == null ? null : new { last.Success, last.ErrorCount, last.WarningCount, last.FinishedAt },
                },
                isUpdatingAssets = EditorApplication.isUpdating,
                scenes,
                prefabStage = stage == null ? null : new
                {
                    stage.assetPath,
                    root = stage.prefabContentsRoot != null ? stage.prefabContentsRoot.name : null,
                },
                selection = Selection.objects.Where(o => o).Take(20).Select(ObjectRefs.Describe).ToArray(),
                console = new { errors = counts.Errors, warnings = counts.Warnings, logs = counts.Logs },
                server = new { version = McpServer.Version, url = McpServer.Url },
            };
        }
    }

    internal sealed class RefreshAssetsTool : McpTool
    {
        const string k_ReloadNote =
            "Scripts compiled successfully and Unity is reloading the script domain. " +
            "The MCP server will be unavailable for a few seconds; wait before the next call.";

        public override string Name => "refresh_assets";
        public override string Description =>
            "Import changed files (AssetDatabase.Refresh) and, if C# scripts changed, wait for compilation and report compiler errors and warnings. " +
            "Call this after creating or editing scripts, shaders or other files on disk (Unity does not notice external edits while unfocused). " +
            "A successful compilation is followed by a domain reload, during which the server is briefly unavailable.";

        public override JObject InputSchema => Schema.Object()
            .Bool("waitForCompilation", "Wait for script compilation to finish and return its result (default true).")
            .Bool("forceRecompile", "Request a script recompilation even if no script changed (default false). Useful to re-collect compiler errors.")
            .Num("timeoutSeconds", "Maximum seconds to wait for compilation (default 120).");

        public override async Task<object> ExecuteAsync(JObject args, ToolContext context)
        {
            bool wait = args.GetBool("waitForCompilation", true);
            bool force = args.GetBool("forceRecompile", false);
            double timeout = Mathf.Clamp(args.GetFloat("timeoutSeconds", 120), 5, 600);
            int before = CompilationTracker.Sequence;

            // If compilation succeeds, Unity reloads the domain (possibly inside Refresh itself), and this is the answer.
            context.SetDomainReloadResult(new
            {
                refreshed = true,
                compilation = new { success = true, domainReload = true },
                note = k_ReloadNote,
            });

            AssetDatabase.Refresh(ImportAssetOptions.Default);
            if (force) CompilationPipeline.RequestScriptCompilation();

            if (!wait)
                return new { refreshed = true, isCompiling = EditorApplication.isCompiling };

            double start = EditorApplication.timeSinceStartup;
            // Give Unity a moment to start compiling.
            await EditorTask.WaitUntil(() => EditorApplication.isCompiling || CompilationTracker.Sequence != before, force ? 5 : 1.5);

            if (!EditorApplication.isCompiling && CompilationTracker.Sequence == before)
            {
                return new
                {
                    refreshed = true,
                    compilation = "not needed (no script changes detected)",
                    hasCompileErrors = EditorUtility.scriptCompilationFailed,
                };
            }

            await EditorTask.WaitUntil(
                () => CompilationTracker.Sequence != before && !EditorApplication.isCompiling,
                timeout - (EditorApplication.timeSinceStartup - start));

            if (CompilationTracker.Sequence == before)
                return new { refreshed = true, compilation = $"still running after {timeout:0}s", isCompiling = EditorApplication.isCompiling };

            var r = CompilationTracker.Last;
            return new
            {
                refreshed = true,
                compilation = new
                {
                    success = r.Success,
                    errorCount = r.ErrorCount,
                    warningCount = r.WarningCount,
                    errors = r.Errors,
                    warnings = r.Warnings.Take(10),
                    durationSeconds = r.DurationSeconds,
                },
                note = r.Success ? k_ReloadNote : "Fix the errors above, then call refresh_assets again.",
            };
        }
    }

    internal sealed class GetCompilationStatusTool : McpTool
    {
        public override string Name => "get_compilation_status";
        public override string Description =>
            "Report whether scripts are compiling and the errors/warnings of the most recent compilation (does not trigger a refresh; use refresh_assets for that).";
        public override bool ReadOnly => true;

        protected override object Execute(JObject args, ToolContext context)
        {
            var last = CompilationTracker.Last;
            bool failed = EditorUtility.scriptCompilationFailed;
            return new
            {
                isCompiling = EditorApplication.isCompiling,
                hasCompileErrors = failed,
                last = last == null ? null : new
                {
                    success = last.Success,
                    errorCount = last.ErrorCount,
                    warningCount = last.WarningCount,
                    errors = last.Errors,
                    warnings = last.Warnings.Take(10),
                    finishedAt = last.FinishedAt,
                },
                hint = failed && (last == null || last.Success)
                    ? "Compile errors exist but were reported before the server started. Call refresh_assets with forceRecompile=true to collect them."
                    : null,
            };
        }
    }

    internal sealed class GetConsoleLogsTool : McpTool
    {
        public override string Name => "get_console_logs";
        public override string Description =>
            "Read recent Unity console messages (Debug.Log/LogWarning/LogError, exceptions), oldest first. " +
            "Use 'sinceId' with the last id you saw to get only new messages. Compiler errors from the last failed compilation are included as type 'compiler'.";
        public override bool ReadOnly => true;

        public override JObject InputSchema => Schema.Object()
            .Prop("types", Schema.Array(Schema.Enum(null, "error", "warning", "log"), "Filter by type (default: all). 'error' includes exceptions and asserts."))
            .Int("limit", "Maximum number of entries, newest kept (default 50, max 500).")
            .Str("search", "Only entries whose message contains this text (case-insensitive).")
            .Int("sinceId", "Only entries with an id greater than this.")
            .Bool("includeStackTrace", "Include full stack traces (default false; the first project frame is always given as 'at').")
            .Bool("clear", "Clear the console after reading (default false).");

        protected override object Execute(JObject args, ToolContext context)
        {
            var types = new HashSet<string>(args.GetStringList("types").Select(t => t.ToLowerInvariant()));
            int limit = Mathf.Clamp(args.GetInt("limit", 50), 1, 500);
            string search = args.GetString("search");
            long sinceId = args.GetInt("sinceId", 0);
            bool stack = args.GetBool("includeStackTrace", false);

            bool Wanted(string type)
            {
                if (types.Count == 0) return true;
                if (ConsoleLogs.IsErrorType(type) || type == "compiler") return types.Contains("error");
                return types.Contains(type);
            }

            var all = ConsoleLogs.Snapshot();
            var filtered = all.Where(e => e.Id > sinceId && Wanted(e.Type) &&
                                          (string.IsNullOrEmpty(search) || e.Message.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
                              .ToList();
            int total = filtered.Count;
            var entries = new JArray();
            foreach (var e in filtered.Skip(Math.Max(0, filtered.Count - limit)))
            {
                var j = new JObject { ["id"] = e.Id, ["type"] = e.Type, ["time"] = e.Time, ["message"] = e.Message };
                var at = ConsoleLogs.FirstLocation(e.StackTrace);
                if (at != null) j["at"] = at;
                if (stack && !string.IsNullOrEmpty(e.StackTrace)) j["stackTrace"] = e.StackTrace;
                entries.Add(j);
            }

            // Compiler errors are not always routed through Debug.Log; surface them explicitly.
            var compile = CompilationTracker.Last;
            if (EditorUtility.scriptCompilationFailed && compile != null && !compile.Success && Wanted("compiler"))
            {
                foreach (var message in compile.Errors)
                {
                    if (!string.IsNullOrEmpty(search) && message.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (filtered.Any(e => e.Message == message)) continue;
                    entries.Add(new JObject { ["type"] = "compiler", ["message"] = message });
                }
            }

            long lastId = all.Count > 0 ? all[all.Count - 1].Id : sinceId;
            if (args.GetBool("clear", false)) ConsoleLogs.Clear();
            return new { total, returned = entries.Count, lastId, entries };
        }
    }

    internal sealed class PlayModeTool : McpTool
    {
        public override string Name => "play_mode";
        public override string Description =>
            "Control Play Mode: 'play', 'stop', 'pause', 'resume' or 'step' (advance one frame while paused). " +
            "Waits until the change has taken effect. Entering Play Mode may reload the script domain, briefly disconnecting the server.";

        public override JObject InputSchema => Schema.Object()
            .Enum("action", "What to do.", new[] { "play", "stop", "pause", "resume", "step" }, required: true);

        public override async Task<object> ExecuteAsync(JObject args, ToolContext context)
        {
            var action = args.RequireString("action").ToLowerInvariant();
            switch (action)
            {
                case "play":
                    if (EditorApplication.isPlaying) return State("already playing");
                    if (EditorUtility.scriptCompilationFailed)
                        throw new McpToolException("Cannot enter Play Mode while there are script compile errors. Use get_compilation_status or refresh_assets to see them.");
                    context.SetDomainReloadResult(new
                    {
                        action,
                        result = "Entering Play Mode (the script domain is reloading; the MCP server will be back in a few seconds).",
                    });
                    EditorApplication.EnterPlaymode();
                    if (!await EditorTask.WaitUntil(() => EditorApplication.isPlaying, 60))
                        return State("Play Mode was requested but has not started yet");
                    return State("entered Play Mode");
                case "stop":
                    if (!EditorApplication.isPlaying) return State("not playing");
                    EditorApplication.ExitPlaymode();
                    await EditorTask.WaitUntil(() => !EditorApplication.isPlaying, 60);
                    return State("exited Play Mode");
                case "pause":
                    EditorApplication.isPaused = true;
                    return State("paused");
                case "resume":
                case "unpause":
                    EditorApplication.isPaused = false;
                    return State("resumed");
                case "step":
                    if (!EditorApplication.isPlaying) throw new McpToolException("'step' requires Play Mode.");
                    EditorApplication.Step();
                    await EditorTask.NextFrame();
                    return State("stepped one frame");
                default:
                    throw new McpToolException("action must be one of: play, stop, pause, resume, step.");
            }
        }

        static object State(string result) => new
        {
            result,
            isPlaying = EditorApplication.isPlaying,
            isPaused = EditorApplication.isPaused,
        };
    }

    internal sealed class ExecuteMenuItemTool : McpTool
    {
        public override string Name => "execute_menu_item";
        public override string Description =>
            "Run a Unity Editor menu command by path, e.g. 'GameObject/3D Object/Cube', 'Edit/Frame Selected', 'Assets/Create/Folder', " +
            "or a project's own [MenuItem]. Avoid commands that open modal dialogs (they block the Editor until closed by a human).";

        public override JObject InputSchema => Schema.Object()
            .Str("menuPath", "Full menu path, e.g. 'Window/General/Console'.", required: true);

        protected override object Execute(JObject args, ToolContext context)
        {
            var path = args.RequireString("menuPath");
            context.SetDomainReloadResult(new { menuPath = path, executed = true, note = "The command triggered a domain reload." });
            if (!EditorApplication.ExecuteMenuItem(path))
                throw new McpToolException($"Menu item '{path}' does not exist or is currently disabled.");
            return new { menuPath = path, executed = true };
        }
    }

    internal sealed class UndoTool : McpTool, INoUndoGroup
    {
        public override string Name => "undo";
        public override string Description =>
            "Undo (or redo) Editor operations. Each modifying MCP tool call is a single undo step.";

        public override JObject InputSchema => Schema.Object()
            .Int("steps", "Number of steps (default 1, max 50).")
            .Bool("redo", "Redo instead of undo (default false).");

        protected override object Execute(JObject args, ToolContext context)
        {
            int steps = Mathf.Clamp(args.GetInt("steps", 1), 1, 50);
            bool redo = args.GetBool("redo", false);
            var names = new List<string>();
            for (int i = 0; i < steps; i++)
            {
                names.Add(Undo.GetCurrentGroupName());
                if (redo) Undo.PerformRedo();
                else Undo.PerformUndo();
            }
            return new { performed = redo ? "redo" : "undo", steps };
        }
    }

    internal sealed class SetSelectionTool : McpTool
    {
        public override string Name => "set_selection";
        public override string Description =>
            "Select GameObjects and/or assets in the Editor (an empty list clears the selection). Optionally frame them in the Scene view or ping them in the Project window.";
        public override bool ReadOnly => true;

        public override JObject InputSchema => Schema.Object()
            .Prop("targets", Schema.Array(Schema.String(), "GameObjects (id, path or name) and/or asset paths ('Assets/...')."), required: true)
            .Bool("frame", "Frame the selection in the Scene view (default false).")
            .Bool("ping", "Highlight the first object in the Hierarchy/Project window (default false).");

        protected override object Execute(JObject args, ToolContext context)
        {
            var objects = new List<Object>();
            foreach (var t in args.GetList("targets"))
            {
                if (t.Type == JTokenType.String && ObjectRefs.LooksLikeAssetPath((string)t))
                    objects.Add(ObjectRefs.Resolve(t, typeof(Object)));
                else
                    objects.Add(SceneQuery.Resolve(t));
            }
            Selection.objects = objects.ToArray();
            if (args.GetBool("frame", false) && objects.Count > 0 && SceneView.lastActiveSceneView != null)
                SceneView.lastActiveSceneView.FrameSelected();
            if (args.GetBool("ping", false) && objects.Count > 0)
                EditorGUIUtility.PingObject(objects[0]);
            return new { selected = objects.Select(ObjectRefs.Describe).ToArray() };
        }
    }
}
