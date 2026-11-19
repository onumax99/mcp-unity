using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace McpUnity
{
    /// <summary>Marker for tools that manage Undo themselves (no automatic undo group).</summary>
    internal interface INoUndoGroup { }

    /// <summary>All tools discovered via TypeCache. Immutable after <see cref="Build"/> (safe to read from any thread).</summary>
    internal sealed class ToolRegistry
    {
        readonly Dictionary<string, McpTool> m_Tools = new Dictionary<string, McpTool>(StringComparer.Ordinal);

        /// <summary>Pre-serialized JSON array for tools/list.</summary>
        public string ListJson { get; private set; } = "[]";

        public IReadOnlyCollection<McpTool> Tools => m_Tools.Values;

        public McpTool Get(string name) => name != null && m_Tools.TryGetValue(name, out var tool) ? tool : null;

        public static ToolRegistry Build()
        {
            var registry = new ToolRegistry();
            foreach (var type in TypeCache.GetTypesDerivedFrom<McpTool>())
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    Debug.LogWarning($"[MCP] Tool {type.FullName} has no public parameterless constructor and was skipped.");
                    continue;
                }
                try
                {
                    var tool = (McpTool)Activator.CreateInstance(type);
                    if (string.IsNullOrEmpty(tool.Name)) continue;
                    if (registry.m_Tools.ContainsKey(tool.Name))
                    {
                        Debug.LogWarning($"[MCP] Duplicate tool name '{tool.Name}' ({type.FullName}); keeping the first one.");
                        continue;
                    }
                    registry.m_Tools.Add(tool.Name, tool);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MCP] Failed to create tool {type.FullName}: {e.Message}");
                }
            }

            var list = new JArray();
            foreach (var tool in registry.m_Tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                try
                {
                    var entry = new JObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description ?? string.Empty,
                        ["inputSchema"] = tool.InputSchema ?? Schema.Object(),
                    };
                    if (!string.IsNullOrEmpty(tool.Title)) entry["title"] = tool.Title;
                    var annotations = new JObject { ["readOnlyHint"] = tool.ReadOnly };
                    if (!tool.ReadOnly)
                    {
                        annotations["destructiveHint"] = tool.Destructive;
                        annotations["idempotentHint"] = tool.Idempotent;
                    }
                    annotations["openWorldHint"] = false;
                    entry["annotations"] = annotations;
                    list.Add(entry);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MCP] Tool '{tool.Name}' has an invalid definition: {e.Message}");
                }
            }
            registry.ListJson = list.ToString(Formatting.None);
            return registry;
        }
    }

    /// <summary>Bridges background HTTP threads and tool execution on the main thread.</summary>
    internal static class ToolDispatcher
    {
        const string k_PlayModeWarning =
            "Note: the Editor is in Play Mode. Changes to scene objects are lost when Play Mode stops.";

        /// <summary>Called on an HTTP thread. Blocks until the tool finishes on the main thread.</summary>
        public static JObject InvokeBlocking(McpTool tool, JObject args, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            var call = PendingCalls.Begin(tool.Name);
            if (call == null)
            {
                return ToolResults.Error(
                    "Unity is reloading scripts (domain reload) or the MCP server is stopping. Wait a few seconds and retry.");
            }

            JObject result;
            try
            {
                MainThread.Post(() => RunOnMainThread(tool, args ?? new JObject(), call));
                if (call.Task.Wait(timeout))
                {
                    result = call.Task.Result;
                }
                else
                {
                    result = ToolResults.Error(
                        $"Timed out after {timeout.TotalSeconds:0}s waiting for Unity to finish '{tool.Name}'. " +
                        "The Editor may be busy (importing, compiling, or showing a modal dialog).");
                    call.Complete(result);
                }
            }
            finally
            {
                PendingCalls.End(call);
            }

            RequestLog.Add(tool.Name, stopwatch.Elapsed.TotalMilliseconds, ToolResults.IsError(result));
            if (McpSettings.VerboseLogging)
                Debug.Log($"[MCP] {tool.Name} finished in {stopwatch.ElapsedMilliseconds} ms");
            return result;
        }

        static async void RunOnMainThread(McpTool tool, JObject args, PendingCall call)
        {
            if (call.Task.IsCompleted) return; // timed out before it could start

            int undoGroup = -1;
            try
            {
                if (!tool.ReadOnly && !(tool is INoUndoGroup))
                {
                    Undo.IncrementCurrentGroup();
                    undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("MCP: " + tool.Name);
                }

                var context = new ToolContext(tool.Name, call);
                var value = await tool.ExecuteAsync(args, context);
                var result = ToolResults.ToCallResult(value);
                if (!tool.ReadOnly && EditorApplication.isPlaying)
                    ToolResults.AppendText(result, k_PlayModeWarning);
                call.Complete(result);
            }
            catch (Exception e)
            {
                call.Complete(ToolResults.Error(FormatException(e)));
            }
            finally
            {
                if (undoGroup >= 0)
                {
                    try { Undo.CollapseUndoOperations(undoGroup); }
                    catch (Exception) { /* ignore */ }
                }
            }
        }

        internal static string FormatException(Exception e)
        {
            while ((e is TargetInvocationException || e is AggregateException) && e.InnerException != null)
                e = e.InnerException;
            if (e is McpToolException) return e.Message;
            return $"{e.GetType().Name}: {e.Message}";
        }
    }
}
