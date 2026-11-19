using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace McpUnity
{
    /// <summary>Lenient JSON -> Unity value conversion. Accepts arrays, {x,y,z} objects, scalars and strings.</summary>
    internal static class UnityValues
    {
        static readonly char[] s_Separators = { ',', ' ', ';', '(', ')', '[', ']' };

        static float[] Floats(JToken v, string what)
        {
            v = JsonArgs.Unwrap(v);
            switch (v)
            {
                case JArray array:
                    return array.Select(t => ToFloat(t, what)).ToArray();
                case JValue value when value.Type == JTokenType.Integer || value.Type == JTokenType.Float:
                    return new[] { ToFloat(value, what) };
                case JValue value when value.Type == JTokenType.String:
                    var parts = ((string)value).Split(s_Separators, StringSplitOptions.RemoveEmptyEntries);
                    try
                    {
                        return parts.Select(p => float.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                    }
                    catch (FormatException)
                    {
                        break;
                    }
            }
            throw new McpToolException($"Expected {what} as an array of numbers, got {Describe(v)}.");
        }

        public static float ToFloat(JToken v, string what = "a number")
        {
            if (v == null) throw new McpToolException($"Expected {what}, got null.");
            try
            {
                if (v.Type == JTokenType.String)
                    return float.Parse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (v.Type == JTokenType.Boolean) return (bool)v ? 1 : 0;
                return v.Value<float>();
            }
            catch (Exception)
            {
                throw new McpToolException($"Expected {what}, got {Describe(v)}.");
            }
        }

        static float Field(JObject o, string name, float fallback) =>
            o.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var t) ? ToFloat(t, name) : fallback;

        public static Vector2 ToVector2(JToken v)
        {
            v = JsonArgs.Unwrap(v);
            if (v is JObject o) return new Vector2(Field(o, "x", 0), Field(o, "y", 0));
            var f = Floats(v, "a Vector2 [x, y]");
            if (f.Length == 1) return new Vector2(f[0], f[0]);
            if (f.Length >= 2) return new Vector2(f[0], f[1]);
            throw new McpToolException("Expected a Vector2 [x, y].");
        }

        public static Vector3 ToVector3(JToken v)
        {
            v = JsonArgs.Unwrap(v);
            if (v is JObject o) return new Vector3(Field(o, "x", 0), Field(o, "y", 0), Field(o, "z", 0));
            var f = Floats(v, "a Vector3 [x, y, z]");
            if (f.Length == 1) return new Vector3(f[0], f[0], f[0]);
            if (f.Length == 2) return new Vector3(f[0], f[1], 0);
            if (f.Length >= 3) return new Vector3(f[0], f[1], f[2]);
            throw new McpToolException("Expected a Vector3 [x, y, z].");
        }

        public static Vector4 ToVector4(JToken v)
        {
            v = JsonArgs.Unwrap(v);
            if (v is JObject o) return new Vector4(Field(o, "x", 0), Field(o, "y", 0), Field(o, "z", 0), Field(o, "w", 0));
            var f = Floats(v, "a Vector4 [x, y, z, w]");
            var r = new Vector4();
            for (int i = 0; i < Math.Min(4, f.Length); i++) r[i] = f[i];
            if (f.Length == 1) r = new Vector4(f[0], f[0], f[0], f[0]);
            return r;
        }

        /// <summary>3 numbers = Euler angles in degrees, 4 numbers = raw quaternion (x, y, z, w).</summary>
        public static Quaternion ToQuaternion(JToken v)
        {
            v = JsonArgs.Unwrap(v);
            if (v is JObject o)
            {
                if (o.ContainsKey("w") || o.ContainsKey("W"))
                    return new Quaternion(Field(o, "x", 0), Field(o, "y", 0), Field(o, "z", 0), Field(o, "w", 1));
                return Quaternion.Euler(Field(o, "x", 0), Field(o, "y", 0), Field(o, "z", 0));
            }
            var f = Floats(v, "a rotation [x, y, z] in Euler degrees");
            if (f.Length == 4) return new Quaternion(f[0], f[1], f[2], f[3]).normalized;
            if (f.Length == 3) return Quaternion.Euler(f[0], f[1], f[2]);
            if (f.Length == 1) return Quaternion.Euler(0, f[0], 0);
            throw new McpToolException("Expected a rotation as Euler angles [x, y, z].");
        }

        /// <summary>[r,g,b(,a)] in 0..1 (or 0..255), {r,g,b,a}, "#RRGGBB(AA)" or a color name like "red".</summary>
        public static Color ToColor(JToken v)
        {
            v = JsonArgs.Unwrap(v);
            if (v is JObject o)
                return new Color(Field(o, "r", 0), Field(o, "g", 0), Field(o, "b", 0), Field(o, "a", 1));
            if (v != null && v.Type == JTokenType.String)
            {
                var s = ((string)v).Trim();
                if (ColorUtility.TryParseHtmlString(s, out var parsed)) return parsed;
                if (!s.StartsWith("#") && ColorUtility.TryParseHtmlString("#" + s, out parsed)) return parsed;
            }
            var f = Floats(v, "a color [r, g, b, a]");
            if (f.Length < 3) throw new McpToolException("Expected a color [r, g, b] or [r, g, b, a].");
            bool bytes = f.Take(3).Any(x => x > 1f);
            float scale = bytes ? 1f / 255f : 1f;
            float a = f.Length >= 4 ? (f[3] > 1f ? f[3] / 255f : f[3]) : 1f;
            return new Color(f[0] * scale, f[1] * scale, f[2] * scale, a);
        }

        public static Rect ToRect(JToken v)
        {
            v = JsonArgs.Unwrap(v);
            if (v is JObject o)
                return new Rect(Field(o, "x", 0), Field(o, "y", 0), Field(o, "width", 0), Field(o, "height", 0));
            var f = Floats(v, "a rect [x, y, width, height]");
            if (f.Length != 4) throw new McpToolException("Expected a rect [x, y, width, height].");
            return new Rect(f[0], f[1], f[2], f[3]);
        }

        public static Bounds ToBounds(JToken v)
        {
            v = JsonArgs.Unwrap(v);
            if (v is JObject o)
            {
                var center = o.TryGetValue("center", StringComparison.OrdinalIgnoreCase, out var c) ? ToVector3(c) : Vector3.zero;
                var size = o.TryGetValue("size", StringComparison.OrdinalIgnoreCase, out var s) ? ToVector3(s) : Vector3.zero;
                return new Bounds(center, size);
            }
            var f = Floats(v, "bounds {center: [x,y,z], size: [x,y,z]}");
            if (f.Length != 6) throw new McpToolException("Expected bounds as {center: [x,y,z], size: [x,y,z]}.");
            return new Bounds(new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5]));
        }

        /// <summary>[[time, value], ...] or [{time, value, inTangent?, outTangent?}, ...].</summary>
        public static AnimationCurve ToAnimationCurve(JToken v)
        {
            v = JsonArgs.Unwrap(v);
            if (v is JObject o && o["keys"] != null) v = o["keys"];
            if (!(v is JArray keys)) throw new McpToolException("Expected an animation curve as [[time, value], ...].");
            var curve = new AnimationCurve();
            foreach (var key in keys)
            {
                if (key is JObject k)
                {
                    curve.AddKey(new Keyframe(Field(k, "time", 0), Field(k, "value", 0), Field(k, "inTangent", 0), Field(k, "outTangent", 0)));
                }
                else
                {
                    var f = Floats(key, "a key [time, value]");
                    if (f.Length < 2) throw new McpToolException("Each curve key needs [time, value].");
                    curve.AddKey(f[0], f[1]);
                }
            }
            return curve;
        }

        /// <summary>Layer mask from an int, "Everything"/"Nothing", "A,B" or ["A", "B"].</summary>
        public static int ToLayerMask(JToken v)
        {
            v = JsonArgs.Unwrap(v);
            if (v == null || v.Type == JTokenType.Null) return 0;
            if (v.Type == JTokenType.Integer) return (int)(long)v;
            IEnumerable<string> names;
            if (v is JArray array) names = array.Select(t => (string)t);
            else
            {
                var s = ((string)v).Trim();
                if (int.TryParse(s, out var n)) return n;
                if (string.Equals(s, "Everything", StringComparison.OrdinalIgnoreCase)) return ~0;
                if (string.Equals(s, "Nothing", StringComparison.OrdinalIgnoreCase)) return 0;
                names = s.Split(',');
            }
            int mask = 0;
            foreach (var raw in names)
            {
                var name = raw?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                int layer = LayerMask.NameToLayer(name);
                if (layer < 0) throw new McpToolException($"Unknown layer '{name}'. Defined layers: {string.Join(", ", DefinedLayers())}");
                mask |= 1 << layer;
            }
            return mask;
        }

        public static IEnumerable<string> DefinedLayers()
        {
            for (int i = 0; i < 32; i++)
            {
                var n = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(n)) yield return $"{i}:{n}";
            }
        }

        public static JToken LayerMaskToJson(int mask)
        {
            if (mask == 0) return "Nothing";
            if (mask == ~0) return "Everything";
            var names = new JArray();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                var n = LayerMask.LayerToName(i);
                names.Add(string.IsNullOrEmpty(n) ? i.ToString() : n);
            }
            return names;
        }

        /// <summary>General conversion used for reflection-based property setting and method invocation.</summary>
        public static object ConvertTo(JToken v, Type type)
        {
            if (v == null || v.Type == JTokenType.Null || v.Type == JTokenType.Undefined)
                return type.IsValueType && Nullable.GetUnderlyingType(type) == null ? Activator.CreateInstance(type) : null;

            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) type = underlying;

            if (type == typeof(Vector2)) return ToVector2(v);
            if (type == typeof(Vector3)) return ToVector3(v);
            if (type == typeof(Vector4)) return ToVector4(v);
            if (type == typeof(Quaternion)) return ToQuaternion(v);
            if (type == typeof(Color)) return ToColor(v);
            if (type == typeof(Color32)) return (Color32)ToColor(v);
            if (type == typeof(Rect)) return ToRect(v);
            if (type == typeof(Bounds)) return ToBounds(v);
            if (type == typeof(Vector2Int)) return Vector2Int.RoundToInt(ToVector2(v));
            if (type == typeof(Vector3Int)) return Vector3Int.RoundToInt(ToVector3(v));
            if (type == typeof(AnimationCurve)) return ToAnimationCurve(v);
            if (type == typeof(LayerMask)) return (LayerMask)ToLayerMask(v);
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return ObjectRefs.Resolve(v, type);
            if (type.IsEnum) return ToEnum(v, type);
            if (type == typeof(string)) return v.Type == JTokenType.String ? (string)v : v.ToString(Formatting.None);
            if (type == typeof(object)) return v is JValue jv ? jv.Value : v;
            if (typeof(JToken).IsAssignableFrom(type)) return v;

            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                var items = AsArray(v);
                var array = Array.CreateInstance(elementType, items.Count);
                for (int i = 0; i < items.Count; i++) array.SetValue(ConvertTo(items[i], elementType), i);
                return array;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var elementType = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type);
                foreach (var item in AsArray(v)) list.Add(ConvertTo(item, elementType));
                return list;
            }

            try
            {
                return v.ToObject(type, Json.Serializer);
            }
            catch (Exception e)
            {
                throw new McpToolException($"Cannot convert {Describe(v)} to {type.Name}: {e.Message}");
            }
        }

        static JArray AsArray(JToken v)
        {
            v = JsonArgs.Unwrap(v);
            return v as JArray ?? new JArray(v);
        }

        public static object ToEnum(JToken v, Type enumType)
        {
            if (v.Type == JTokenType.Integer) return Enum.ToObject(enumType, (long)v);
            var s = ((string)v).Trim();
            try
            {
                return Enum.Parse(enumType, s, true);
            }
            catch (ArgumentException)
            {
                throw new McpToolException($"'{s}' is not a valid {enumType.Name}. Valid values: {string.Join(", ", Enum.GetNames(enumType))}");
            }
        }

        static string Describe(JToken v)
        {
            if (v == null) return "null";
            var s = v.ToString(Formatting.None);
            return s.Length > 80 ? s.Substring(0, 80) + "..." : s;
        }
    }
}
