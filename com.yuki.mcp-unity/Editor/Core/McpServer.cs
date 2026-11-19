using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using McpUnity.Server;
using UnityEditor;
using UnityEngine;

namespace McpUnity
{
    /// <summary>
    /// Entry point: owns the HTTP listener and restarts it after every domain reload.
    /// </summary>
    [InitializeOnLoad]
    public static class McpServer
    {
        public const string Version = "0.1.0";

        const string k_StoppedByUserKey = "McpUnity.StoppedByUser";
        const string k_AnnouncedKey = "McpUnity.Announced";
        const int k_MaxRetries = 10;

        static HttpServer s_Http;
        static ToolRegistry s_Registry;
        static int s_RetryCount;
        static double s_RetryAt;

        public static bool IsRunning => s_Http != null;

        public static int Port { get; private set; }

        public static string LastError { get; private set; }

        public static string Url => $"http://127.0.0.1:{(IsRunning ? Port : EffectivePort)}/mcp";

        public static IReadOnlyCollection<McpTool> Tools => (IReadOnlyCollection<McpTool>)s_Registry?.Tools ?? Array.Empty<McpTool>();

        /// <summary>Raised on the main thread when the server starts or stops.</summary>
        public static event Action StateChanged;

        internal static ToolRegistry Registry => s_Registry;

        /// <summary>Port from the -mcpPort command line argument, or the project setting.</summary>
        internal static int EffectivePort => CommandLinePort() ?? McpSettings.Port;

        static McpServer()
        {
            MainThread.Initialize();
            ConsoleLogs.Initialize();
            CompilationTracker.Initialize();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += () => StopInternal("Unity is quitting.", 1000);

            if (ShouldAutoStart())
                StartInternal(false);
        }

        /// <summary>Starts (or restarts) the server.</summary>
        public static void Start()
        {
            SessionState.SetBool(k_StoppedByUserKey, false);
            s_RetryCount = 0;
            StartInternal(false);
        }

        /// <summary>Stops the server for the rest of this editor session (until Start is called).</summary>
        public static void Stop()
        {
            SessionState.SetBool(k_StoppedByUserKey, true);
            StopInternal("The MCP server was stopped.", 2000);
            StateChanged?.Invoke();
        }

        static bool ShouldAutoStart()
        {
            if (Application.isBatchMode)
            {
                // Never start in CI/batch builds unless explicitly requested.
                return HasCommandLineFlag("-mcpServer") ||
                       Environment.GetEnvironmentVariable("UNITY_MCP_BATCHMODE") == "1";
            }
            if (SessionState.GetBool(k_StoppedByUserKey, false)) return false;
            return McpSettings.AutoStart;
        }

        static void StartInternal(bool isRetry)
        {
            StopInternal("The MCP server is restarting.", 1000);
            EditorApplication.update -= RetryTick;

            int port = EffectivePort;
            try
            {
                s_Registry = ToolRegistry.Build();
                var handler = new McpRequestHandler(s_Registry);
                var http = new HttpServer(port, handler.Handle);
                http.Start();
                s_Http = http;
                Port = port;
                LastError = null;
                s_RetryCount = 0;
                PendingCalls.Open();

                if (!SessionState.GetBool(k_AnnouncedKey, false) || McpSettings.VerboseLogging)
                {
                    SessionState.SetBool(k_AnnouncedKey, true);
                    Debug.Log($"[MCP] Server listening on {Url} ({s_Registry.Tools.Count} tools). Window > MCP for Unity shows connection settings.");
                }
            }
            catch (SocketException e)
            {
                LastError = $"Could not listen on port {port}: {e.Message}";
                ScheduleRetry();
            }
            catch (Exception e)
            {
                LastError = $"Failed to start the MCP server: {e.Message}";
                Debug.LogException(e);
            }
            StateChanged?.Invoke();
        }

        static void StopInternal(string reason, int drainTimeoutMs)
        {
            PendingCalls.CloseAll(reason);
            var http = s_Http;
            s_Http = null;
            http?.Stop(drainTimeoutMs);
        }

        /// <summary>The port can stay busy for a moment right after a domain reload; retry a few times.</summary>
        static void ScheduleRetry()
        {
            if (s_RetryCount >= k_MaxRetries)
            {
                Debug.LogWarning($"[MCP] {LastError}. Another program (or another Unity instance) may be using it. " +
                                 "Change the port in Window > MCP for Unity.");
                return;
            }
            s_RetryCount++;
            s_RetryAt = EditorApplication.timeSinceStartup + 0.5;
            EditorApplication.update -= RetryTick;
            EditorApplication.update += RetryTick;
        }

        static void RetryTick()
        {
            if (EditorApplication.timeSinceStartup < s_RetryAt) return;
            EditorApplication.update -= RetryTick;
            if (!IsRunning) StartInternal(true);
        }

        static void OnBeforeAssemblyReload()
        {
            ConsoleLogs.Persist();
            // Answer in-flight calls (tools may have registered a "reload result") before the listener goes away.
            StopInternal(
                "Unity started a domain reload (script recompilation or entering Play Mode) while this request was running. " +
                "Wait a few seconds, then retry.",
                2500);
        }

        static int? CommandLinePort()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-mcpPort", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) &&
                    port > 0 && port <= 65535)
                    return port;
            }
            return null;
        }

        static bool HasCommandLineFlag(string flag)
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
