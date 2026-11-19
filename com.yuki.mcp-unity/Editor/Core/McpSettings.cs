using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace McpUnity
{
    /// <summary>Per-project, per-user settings (stored in UserSettings/EditorUserSettings.asset).</summary>
    internal static class McpSettings
    {
        public const int DefaultPort = 8890;

        const string k_Port = "McpUnity.Port";
        const string k_AutoStart = "McpUnity.AutoStart";
        const string k_Verbose = "McpUnity.VerboseLogging";

        static volatile bool s_Verbose;
        static bool s_VerboseLoaded;

        public static int Port
        {
            get
            {
                var s = EditorUserSettings.GetConfigValue(k_Port);
                return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0 && p <= 65535
                    ? p
                    : DefaultPort;
            }
            set => EditorUserSettings.SetConfigValue(k_Port, value.ToString(CultureInfo.InvariantCulture));
        }

        public static bool AutoStart
        {
            get => EditorUserSettings.GetConfigValue(k_AutoStart) != "false";
            set => EditorUserSettings.SetConfigValue(k_AutoStart, value ? "true" : "false");
        }

        /// <summary>Thread-safe after the first read on the main thread.</summary>
        public static bool VerboseLogging
        {
            get
            {
                if (!s_VerboseLoaded && MainThread.IsMainThread)
                {
                    s_Verbose = EditorUserSettings.GetConfigValue(k_Verbose) == "true";
                    s_VerboseLoaded = true;
                }
                return s_Verbose;
            }
            set
            {
                s_Verbose = value;
                s_VerboseLoaded = true;
                EditorUserSettings.SetConfigValue(k_Verbose, value ? "true" : "false");
            }
        }

        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath)?.Replace('\\', '/');
    }
}
