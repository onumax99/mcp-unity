using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace McpUnity
{
    /// <summary>Lenient accessors for tool arguments.</summary>
    public static class JsonArgs
    {
        /// <summary>Returns the argument, or null if it is missing or JSON null.</summary>
        public static JToken Arg(this JObject args, string name)
        {
            if (args == null) return null;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) return null;
            return token;
        }

        public static bool Has(this JObject args, string name) => args.Arg(name) != null;

        /// <summary>True if the key is present, even when its value is null.</summary>
        public static bool HasKey(this JObject args, string name) => args != null && args.ContainsKey(name);

        public static JToken Require(this JObject args, string name)
        {
            var token = args.Arg(name);
            if (token == null) throw new McpToolException($"Missing required argument '{name}'.");
            return token;
        }

        public static string GetString(this JObject args, string name, string defaultValue = null)
        {
            var token = args.Arg(name);
            if (token == null) return defaultValue;
            return token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None);
        }

        public static string RequireString(this JObject args, string name)
        {
            var value = args.GetString(name);
            if (string.IsNullOrWhiteSpace(value)) throw new McpToolException($"Missing required argument '{name}'.");
            return value.Trim();
        }

        public static int GetInt(this JObject args, string name, int defaultValue)
        {
            var token = args.Arg(name);
            if (token == null) return defaultValue;
            try
            {
                if (token.Type == JTokenType.String)
                    return int.Parse((string)token, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return Convert.ToInt32(((JValue)token).Value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new McpToolException($"Argument '{name}' must be an integer.");
            }
        }

        public static float GetFloat(this JObject args, string name, float defaultValue)
        {
            var token = args.Arg(name);
            if (token == null) return defaultValue;
            try
            {
                if (token.Type == JTokenType.String)
                    return float.Parse((string)token, NumberStyles.Float, CultureInfo.InvariantCulture);
                return Convert.ToSingle(((JValue)token).Value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new McpToolException($"Argument '{name}' must be a number.");
            }
        }

        public static bool GetBool(this JObject args, string name, bool defaultValue)
        {
            var token = args.Arg(name);
            if (token == null) return defaultValue;
            switch (token.Type)
            {
                case JTokenType.Boolean: return (bool)token;
                case JTokenType.Integer: return (long)token != 0;
                case JTokenType.String:
                    if (bool.TryParse((string)token, out var b)) return b;
                    break;
            }
            throw new McpToolException($"Argument '{name}' must be true or false.");
        }

        /// <summary>Accepts a single string, an array of strings, or a JSON-encoded array.</summary>
        public static List<string> GetStringList(this JObject args, string name)
        {
            var list = new List<string>();
            var token = Unwrap(args.Arg(name));
            if (token == null) return list;
            if (token is JArray array)
            {
                foreach (var item in array)
                    if (item.Type != JTokenType.Null)
                        list.Add(item.Type == JTokenType.String ? (string)item : item.ToString(Formatting.None));
            }
            else
            {
                list.Add(token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None));
            }
            return list;
        }

        /// <summary>Returns the argument as a list of tokens (a single value becomes a one-element list).</summary>
        public static List<JToken> GetList(this JObject args, string name)
        {
            var list = new List<JToken>();
            var token = Unwrap(args.Arg(name));
            if (token == null) return list;
            if (token is JArray array) list.AddRange(array);
            else list.Add(token);
            return list;
        }

        /// <summary>Returns an object argument. JSON-encoded strings are parsed (models sometimes send those).</summary>
        public static JObject GetObject(this JObject args, string name)
        {
            var token = Unwrap(args.Arg(name));
            if (token == null) return null;
            if (token is JObject obj) return obj;
            throw new McpToolException($"Argument '{name}' must be a JSON object.");
        }

        /// <summary>Parses strings that contain a JSON object or array.</summary>
        internal static JToken Unwrap(JToken token)
        {
            if (token == null || token.Type != JTokenType.String) return token;
            var s = ((string)token).Trim();
            if (s.Length < 2 || !(s[0] == '{' && s[s.Length - 1] == '}' || s[0] == '[' && s[s.Length - 1] == ']'))
                return token;
            try { return Json.Parse(s); }
            catch (JsonException) { return token; }
        }
    }
}
