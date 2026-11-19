using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace McpUnity
{
    /// <summary>Window > MCP for Unity: server status, settings and connection instructions.</summary>
    internal sealed class McpWindow : EditorWindow
    {
        Vector2 m_Scroll;
        int m_Port;
        bool m_ShowTools;
        int m_LastLogVersion = -1;
        GUIStyle m_Wrapped;

        [MenuItem("Window/MCP for Unity")]
        public static void Open()
        {
            var window = GetWindow<McpWindow>();
            window.titleContent = new GUIContent("MCP for Unity");
            window.minSize = new Vector2(380, 320);
            window.Show();
        }

        void OnEnable()
        {
            m_Port = McpSettings.Port;
            McpServer.StateChanged += Repaint;
            EditorApplication.update += Poll;
        }

        void OnDisable()
        {
            McpServer.StateChanged -= Repaint;
            EditorApplication.update -= Poll;
        }

        void Poll()
        {
            if (RequestLog.Version == m_LastLogVersion) return;
            m_LastLogVersion = RequestLog.Version;
            Repaint();
        }

        void OnGUI()
        {
            if (m_Wrapped == null) m_Wrapped = new GUIStyle(EditorStyles.label) { wordWrap = true };
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            DrawStatus();
            EditorGUILayout.Space(8);
            DrawSettings();
            EditorGUILayout.Space(8);
            DrawConnect();
            EditorGUILayout.Space(8);
            DrawTools();
            EditorGUILayout.Space(8);
            DrawRecent();

            EditorGUILayout.EndScrollView();
        }

        void DrawStatus()
        {
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                var color = McpServer.IsRunning ? new Color(0.25f, 0.8f, 0.35f) : McpServer.LastError != null ? new Color(0.9f, 0.3f, 0.3f) : Color.gray;
                var rect = GUILayoutUtility.GetRect(12, 12, GUILayout.Width(12), GUILayout.Height(18));
                rect.y += 4;
                rect.height = 10;
                rect.width = 10;
                EditorGUI.DrawRect(rect, color);
                GUILayout.Label(McpServer.IsRunning ? "Running" : "Stopped", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"v{McpServer.Version}", EditorStyles.miniLabel);
            }
            if (McpServer.IsRunning)
            {
                EditorGUILayout.SelectableLabel(McpServer.Url, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.LabelField($"{McpServer.Tools.Count} tools", EditorStyles.miniLabel);
            }
            if (!string.IsNullOrEmpty(McpServer.LastError))
                EditorGUILayout.HelpBox(McpServer.LastError, MessageType.Error);
        }

        void DrawSettings()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            m_Port = EditorGUILayout.IntField("Port", m_Port);
            var autoStart = EditorGUILayout.Toggle("Start automatically", McpSettings.AutoStart);
            if (autoStart != McpSettings.AutoStart) McpSettings.AutoStart = autoStart;
            var verbose = EditorGUILayout.Toggle("Verbose logging", McpSettings.VerboseLogging);
            if (verbose != McpSettings.VerboseLogging) McpSettings.VerboseLogging = verbose;

            using (new EditorGUILayout.HorizontalScope())
            {
                bool portChanged = m_Port != McpSettings.Port;
                if (GUILayout.Button(McpServer.IsRunning ? (portChanged ? "Apply port && Restart" : "Restart") : "Start"))
                {
                    if (m_Port <= 0 || m_Port > 65535)
                    {
                        m_Port = McpSettings.Port;
                    }
                    else
                    {
                        McpSettings.Port = m_Port;
                        McpServer.Start();
                    }
                }
                using (new EditorGUI.DisabledScope(!McpServer.IsRunning))
                {
                    if (GUILayout.Button("Stop")) McpServer.Stop();
                }
            }
            if (McpServer.IsRunning && McpServer.Port != McpServer.EffectivePort)
                EditorGUILayout.HelpBox("The port differs from the running server; press Restart to apply.", MessageType.Info);
        }

        void DrawConnect()
        {
            EditorGUILayout.LabelField("Connect from Claude Code", EditorStyles.boldLabel);
            var url = $"http://127.0.0.1:{(McpServer.IsRunning ? McpServer.Port : McpServer.EffectivePort)}/mcp";
            var command = $"claude mcp add --transport http unity {url}";
            EditorGUILayout.LabelField("Run this in a terminal (once per project):", m_Wrapped);
            EditorGUILayout.SelectableLabel(command, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Copy command")) EditorGUIUtility.systemCopyBuffer = command;
                if (GUILayout.Button("Write .mcp.json"))
                {
                    try
                    {
                        var path = WriteMcpJson(url);
                        EditorUtility.DisplayDialog("MCP for Unity", $"Wrote {path}.\nStart Claude Code in the project folder and approve the 'unity' server.", "OK");
                    }
                    catch (Exception e)
                    {
                        EditorUtility.DisplayDialog("MCP for Unity", "Could not write .mcp.json: " + e.Message, "OK");
                    }
                }
            }
            EditorGUILayout.LabelField(
                "Other MCP clients: use the Streamable HTTP URL above. For stdio-only clients, bridge with e.g. 'npx mcp-remote <url>'.",
                EditorStyles.wordWrappedMiniLabel);
        }

        void DrawTools()
        {
            m_ShowTools = EditorGUILayout.Foldout(m_ShowTools, $"Tools ({McpServer.Tools.Count})", true);
            if (!m_ShowTools) return;
            EditorGUI.indentLevel++;
            foreach (var tool in McpServer.Tools.OrderBy(t => t.Name))
                EditorGUILayout.LabelField(tool.Name, tool.ReadOnly ? "read-only" : string.Empty);
            EditorGUI.indentLevel--;
        }

        void DrawRecent()
        {
            EditorGUILayout.LabelField("Recent calls", EditorStyles.boldLabel);
            var entries = RequestLog.Snapshot();
            if (entries.Length == 0)
            {
                EditorGUILayout.LabelField("No calls yet.", EditorStyles.miniLabel);
                return;
            }
            foreach (var e in entries.Reverse().Take(20))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(e.Time.ToString("HH:mm:ss"), EditorStyles.miniLabel, GUILayout.Width(55));
                    GUILayout.Label(e.Tool, GUILayout.MinWidth(120));
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(e.IsError ? "error" : "ok", EditorStyles.miniLabel, GUILayout.Width(32));
                    GUILayout.Label($"{e.Milliseconds:0} ms", EditorStyles.miniLabel, GUILayout.Width(60));
                }
            }
        }

        /// <summary>Adds/updates the "unity" server in .mcp.json at the project root (Claude Code project config).</summary>
        static string WriteMcpJson(string url)
        {
            var path = Path.Combine(McpSettings.ProjectRoot, ".mcp.json");
            var root = File.Exists(path) ? Json.Parse(File.ReadAllText(path)) as JObject ?? new JObject() : new JObject();
            if (!(root["mcpServers"] is JObject servers))
            {
                servers = new JObject();
                root["mcpServers"] = servers;
            }
            servers["unity"] = new JObject { ["type"] = "http", ["url"] = url };
            File.WriteAllText(path, root.ToString(Formatting.Indented));
            return path;
        }
    }
}
