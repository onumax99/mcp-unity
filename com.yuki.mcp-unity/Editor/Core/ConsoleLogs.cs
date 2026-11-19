using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace McpUnity
{
    /// <summary>
    /// Captures Debug.Log output into a ring buffer that survives domain reloads (via SessionState).
    /// Independent of the Console window's filters and collapse mode.
    /// </summary>
    internal static class ConsoleLogs
    {
        internal sealed class Entry
        {
            public long Id;
            public string Type;      // log | warning | error | exception | assert
            public string Message;
            public string StackTrace;
            public string Time;      // HH:mm:ss.fff local time
        }

        internal struct Counts
        {
            public int Errors;
            public int Warnings;
            public int Logs;
        }

        const string k_EntriesKey = "McpUnity.ConsoleLogs.Entries";
        const string k_NextIdKey = "McpUnity.ConsoleLogs.NextId";
        const int k_Capacity = 2000;
        const int k_PersistCount = 500;
        const int k_MaxMessageLength = 4000;
        const int k_MaxStackLength = 4000;

        static readonly object s_Lock = new object();
        static readonly Queue<Entry> s_Entries = new Queue<Entry>();
        static long s_NextId = 1;
        static readonly Regex s_Location = new Regex(@"\(at (?<file>[^)]+?):(?<line>\d+)\)", RegexOptions.Compiled);

        public static void Initialize()
        {
            Restore();
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            var entry = new Entry
            {
                Type = MapType(type),
                Message = Truncate(condition, k_MaxMessageLength),
                StackTrace = string.IsNullOrEmpty(stackTrace) ? null : Truncate(stackTrace, k_MaxStackLength),
                Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            };
            lock (s_Lock)
            {
                entry.Id = s_NextId++;
                s_Entries.Enqueue(entry);
                while (s_Entries.Count > k_Capacity) s_Entries.Dequeue();
            }
        }

        static string MapType(LogType type)
        {
            switch (type)
            {
                case LogType.Error: return "error";
                case LogType.Exception: return "exception";
                case LogType.Assert: return "assert";
                case LogType.Warning: return "warning";
                default: return "log";
            }
        }

        public static bool IsErrorType(string type) => type == "error" || type == "exception" || type == "assert";

        public static List<Entry> Snapshot()
        {
            lock (s_Lock) return s_Entries.ToList();
        }

        public static Counts GetCounts()
        {
            var counts = new Counts();
            lock (s_Lock)
            {
                foreach (var e in s_Entries)
                {
                    if (IsErrorType(e.Type)) counts.Errors++;
                    else if (e.Type == "warning") counts.Warnings++;
                    else counts.Logs++;
                }
            }
            return counts;
        }

        /// <summary>Extracts "Assets/Foo.cs:12" from the first user frame of a stack trace.</summary>
        public static string FirstLocation(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return null;
            foreach (Match m in s_Location.Matches(stackTrace))
            {
                var file = m.Groups["file"].Value;
                if (file.StartsWith("Assets/") || file.StartsWith("Packages/"))
                    return file + ":" + m.Groups["line"].Value;
            }
            return null;
        }

        /// <summary>Clears the buffer and (best effort) the Unity Console window.</summary>
        public static void Clear()
        {
            lock (s_Lock) s_Entries.Clear();
            try
            {
                var logEntries = typeof(EditorWindow).Assembly.GetType("UnityEditor.LogEntries");
                logEntries?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(null, null);
            }
            catch (Exception)
            {
                // Internal API; ignore if it changed.
            }
        }

        /// <summary>Saves recent entries so they survive the upcoming domain reload.</summary>
        public static void Persist()
        {
            try
            {
                List<Entry> recent;
                long nextId;
                lock (s_Lock)
                {
                    recent = s_Entries.Skip(Math.Max(0, s_Entries.Count - k_PersistCount)).ToList();
                    nextId = s_NextId;
                }
                SessionState.SetString(k_EntriesKey, JsonConvert.SerializeObject(recent));
                SessionState.SetString(k_NextIdKey, nextId.ToString());
            }
            catch (Exception)
            {
                // Never block a domain reload.
            }
        }

        static void Restore()
        {
            try
            {
                if (long.TryParse(SessionState.GetString(k_NextIdKey, "1"), out var next))
                    s_NextId = Math.Max(s_NextId, next);
                var json = SessionState.GetString(k_EntriesKey, null);
                if (string.IsNullOrEmpty(json)) return;
                SessionState.EraseString(k_EntriesKey);
                var restored = JsonConvert.DeserializeObject<List<Entry>>(json);
                if (restored == null) return;
                lock (s_Lock)
                {
                    var current = s_Entries.ToList();
                    s_Entries.Clear();
                    foreach (var e in restored.Concat(current)) s_Entries.Enqueue(e);
                    while (s_Entries.Count > k_Capacity) s_Entries.Dequeue();
                }
            }
            catch (Exception)
            {
                // Corrupt state: start fresh.
            }
        }

        static string Truncate(string s, int max)
        {
            if (s == null) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + $"... [{s.Length - max} more chars]";
        }
    }
}
