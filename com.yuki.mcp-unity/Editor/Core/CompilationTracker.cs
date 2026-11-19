using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;

namespace McpUnity
{
    /// <summary>
    /// Records the result of the most recent script compilation. Stored in SessionState because a successful
    /// compilation is followed by a domain reload that wipes static state.
    /// </summary>
    internal static class CompilationTracker
    {
        internal sealed class Result
        {
            public int Sequence;
            public string FinishedAt;
            public double DurationSeconds;
            public bool Success;
            public int ErrorCount;
            public int WarningCount;
            public List<string> Errors = new List<string>();
            public List<string> Warnings = new List<string>();
        }

        const string k_LastKey = "McpUnity.Compilation.Last";
        const string k_SequenceKey = "McpUnity.Compilation.Sequence";
        const int k_MaxErrors = 100;
        const int k_MaxWarnings = 30;

        static readonly List<CompilerMessage> s_Messages = new List<CompilerMessage>();
        static DateTime s_StartedAt = DateTime.Now;
        static bool s_Initialized;

        public static void Initialize()
        {
            if (s_Initialized) return;
            s_Initialized = true;
            CompilationPipeline.compilationStarted += OnStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyFinished;
            CompilationPipeline.compilationFinished += OnFinished;
        }

        /// <summary>Incremented every time a compilation finishes (this editor session).</summary>
        public static int Sequence => SessionState.GetInt(k_SequenceKey, 0);

        public static Result Last
        {
            get
            {
                var json = SessionState.GetString(k_LastKey, null);
                if (string.IsNullOrEmpty(json)) return null;
                try { return JsonConvert.DeserializeObject<Result>(json); }
                catch (Exception) { return null; }
            }
        }

        static void OnStarted(object context)
        {
            s_Messages.Clear();
            s_StartedAt = DateTime.Now;
        }

        static void OnAssemblyFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages != null) s_Messages.AddRange(messages);
        }

        static void OnFinished(object context)
        {
            var result = new Result
            {
                Sequence = Sequence + 1,
                FinishedAt = DateTime.Now.ToString("HH:mm:ss"),
                DurationSeconds = Math.Round((DateTime.Now - s_StartedAt).TotalSeconds, 2),
            };
            var seen = new HashSet<string>();
            foreach (var m in s_Messages)
            {
                if (!seen.Add(m.type + m.message)) continue;
                if (m.type == CompilerMessageType.Error)
                {
                    result.ErrorCount++;
                    if (result.Errors.Count < k_MaxErrors) result.Errors.Add(m.message);
                }
                else if (m.type == CompilerMessageType.Warning)
                {
                    result.WarningCount++;
                    if (result.Warnings.Count < k_MaxWarnings) result.Warnings.Add(m.message);
                }
            }
            result.Success = result.ErrorCount == 0;
            s_Messages.Clear();

            SessionState.SetString(k_LastKey, JsonConvert.SerializeObject(result));
            SessionState.SetInt(k_SequenceKey, result.Sequence);
        }
    }
}
