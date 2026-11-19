using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace McpUnity
{
    /// <summary>A tools/call request waiting for its result from the main thread.</summary>
    internal sealed class PendingCall
    {
        readonly TaskCompletionSource<JObject> m_Tcs =
            new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingCall(string toolName) { ToolName = toolName; }

        public string ToolName { get; }

        public Task<JObject> Task => m_Tcs.Task;

        /// <summary>Result to send if a domain reload interrupts the call (set by the tool, main thread).</summary>
        public JObject ReloadResult { get; set; }

        public bool Complete(JObject result) => m_Tcs.TrySetResult(result);
    }

    /// <summary>
    /// Tracks in-flight tool calls so they can be answered before a domain reload tears down the server.
    /// </summary>
    internal static class PendingCalls
    {
        static readonly object s_Lock = new object();
        static readonly HashSet<PendingCall> s_Calls = new HashSet<PendingCall>();
        static bool s_Closed;

        /// <summary>Returns null when the server is shutting down (domain reload / stop).</summary>
        public static PendingCall Begin(string toolName)
        {
            lock (s_Lock)
            {
                if (s_Closed) return null;
                var call = new PendingCall(toolName);
                s_Calls.Add(call);
                return call;
            }
        }

        public static void End(PendingCall call)
        {
            lock (s_Lock) s_Calls.Remove(call);
        }

        public static void Open()
        {
            lock (s_Lock) s_Closed = false;
        }

        public static int Count
        {
            get { lock (s_Lock) return s_Calls.Count; }
        }

        /// <summary>Completes every in-flight call and refuses new ones until <see cref="Open"/>.</summary>
        public static void CloseAll(string reason)
        {
            PendingCall[] calls;
            lock (s_Lock)
            {
                s_Closed = true;
                calls = s_Calls.ToArray();
            }
            foreach (var call in calls)
                call.Complete(call.ReloadResult ?? ToolResults.Error(reason));
        }
    }

    /// <summary>Recent tool calls, shown in the MCP window.</summary>
    internal static class RequestLog
    {
        internal struct Entry
        {
            public DateTime Time;
            public string Tool;
            public double Milliseconds;
            public bool IsError;
        }

        const int k_Capacity = 50;
        static readonly object s_Lock = new object();
        static readonly List<Entry> s_Entries = new List<Entry>();
        static int s_Version;

        public static int Version => Volatile.Read(ref s_Version);

        public static void Add(string tool, double milliseconds, bool isError)
        {
            lock (s_Lock)
            {
                s_Entries.Add(new Entry { Time = DateTime.Now, Tool = tool, Milliseconds = milliseconds, IsError = isError });
                if (s_Entries.Count > k_Capacity) s_Entries.RemoveAt(0);
            }
            Interlocked.Increment(ref s_Version);
        }

        public static Entry[] Snapshot()
        {
            lock (s_Lock) return s_Entries.ToArray();
        }
    }
}
