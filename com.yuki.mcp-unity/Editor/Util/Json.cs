using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace McpUnity
{
    internal static class Json
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            Converters = new List<JsonConverter> { new UnityTypesConverter() },
        };

        public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        public static string Serialize(object value)
        {
            if (value is JToken token) return token.ToString(Formatting.None);
            return JsonConvert.SerializeObject(value, Formatting.None, Settings);
        }

        public static JToken ToToken(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is JToken token) return token;
            return JToken.FromObject(value, Serializer);
        }

        /// <summary>Parses JSON without turning date-like strings into DateTime.</summary>
        public static JToken Parse(string text)
        {
            using (var stringReader = new StringReader(text ?? string.Empty))
            using (var reader = new JsonTextReader(stringReader) { DateParseHandling = DateParseHandling.None })
            {
                var token = JToken.ReadFrom(reader);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment)
                        throw new JsonReaderException("Unexpected content after the JSON value.");
                }
                return token;
            }
        }

        /// <summary>Rounds for compact, readable output.</summary>
        public static double R(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            var r = Math.Round(value, 4);
            return r == 0 ? 0 : r;
        }

        public static JArray V(Vector2 v) => new JArray(R(v.x), R(v.y));
        public static JArray V(Vector3 v) => new JArray(R(v.x), R(v.y), R(v.z));
        public static JArray V(Vector4 v) => new JArray(R(v.x), R(v.y), R(v.z), R(v.w));
        public static JArray C(Color c) => new JArray(R(c.r), R(c.g), R(c.b), R(c.a));
        public static JObject Rect(Rect r) => new JObject { ["x"] = R(r.x), ["y"] = R(r.y), ["width"] = R(r.width), ["height"] = R(r.height) };
        public static JObject Bounds(Bounds b) => new JObject { ["center"] = V(b.center), ["size"] = V(b.size) };
    }

    /// <summary>Writes common Unity value types compactly and UnityEngine.Object references as descriptors.</summary>
    internal sealed class UnityTypesConverter : JsonConverter
    {
        public override bool CanRead => false;

        public override bool CanConvert(Type type)
        {
            return type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) ||
                   type == typeof(Quaternion) || type == typeof(Color) || type == typeof(Color32) ||
                   type == typeof(Rect) || type == typeof(Bounds) || type == typeof(Vector2Int) ||
                   type == typeof(Vector3Int) || type == typeof(Matrix4x4) ||
                   typeof(UnityEngine.Object).IsAssignableFrom(type);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            JToken token;
            switch (value)
            {
                case Vector2 v2: token = Json.V(v2); break;
                case Vector3 v3: token = Json.V(v3); break;
                case Vector4 v4: token = Json.V(v4); break;
                case Quaternion q: token = Json.V(q.eulerAngles); break;
                case Color c: token = Json.C(c); break;
                case Color32 c32: token = Json.C(c32); break;
                case Rect r: token = Json.Rect(r); break;
                case Bounds b: token = Json.Bounds(b); break;
                case Vector2Int v2i: token = new JArray(v2i.x, v2i.y); break;
                case Vector3Int v3i: token = new JArray(v3i.x, v3i.y, v3i.z); break;
                case Matrix4x4 m: token = new JValue(m.ToString()); break;
                case UnityEngine.Object o: token = ObjectRefs.Describe(o); break;
                default: token = JValue.CreateNull(); break;
            }
            token.WriteTo(writer);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
