using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace McpUnity
{
    /// <summary>Runs work posted from background threads on the Unity main thread (EditorApplication.update).</summary>
    internal static class MainThread
    {
        static readonly ConcurrentQueue<Action> s_Queue = new ConcurrentQueue<Action>();
        static int s_MainThreadId = -1;

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == s_MainThreadId;

        public static void Initialize()
        {
            s_MainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
        }

        public static void Post(Action action) => s_Queue.Enqueue(action);

        static void Pump()
        {
            int budget = 64;
            while (budget-- > 0 && s_Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }

    /// <summary>Awaitable helpers that resume on the main thread.</summary>
    internal static class EditorTask
    {
        /// <summary>Completes on the next EditorApplication.update tick.</summary>
        public static Task NextFrame()
        {
            var tcs = new TaskCompletionSource<bool>();
            EditorApplication.CallbackFunction callback = null;
            callback = () =>
            {
                EditorApplication.update -= callback;
                tcs.TrySetResult(true);
            };
            EditorApplication.update += callback;
            return tcs.Task;
        }

        /// <summary>Waits (frame by frame) until the condition is true. Returns false on timeout.</summary>
        public static async Task<bool> WaitUntil(Func<bool> condition, double timeoutSeconds)
        {
            double end = EditorApplication.timeSinceStartup + timeoutSeconds;
            while (!condition())
            {
                if (EditorApplication.timeSinceStartup > end) return false;
                await NextFrame();
            }
            return true;
        }

        public static async Task Delay(double seconds)
        {
            double end = EditorApplication.timeSinceStartup + seconds;
            while (EditorApplication.timeSinceStartup < end)
                await NextFrame();
        }
    }
}
