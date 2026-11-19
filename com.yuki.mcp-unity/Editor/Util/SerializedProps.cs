using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace McpUnity
{
    /// <summary>Reads and writes Unity serialized properties (what the Inspector shows) as JSON.</summary>
    internal static class SerializedProps
    {
        static readonly Regex s_Index = new Regex(@"\[(\d+)\]", RegexOptions.Compiled);

        /// <summary>"materials[0]" -> "materials.Array.data[0]".</summary>
        public static string NormalizePath(string key)
        {
            key = key.Trim();
            if (key.IndexOf('[') < 0 || key.Contains(".Array.data[")) return key;
            return s_Index.Replace(key, ".Array.data[$1]");
        }

        /// <summary>Finds a property by path, also trying the "m_" prefix and case-insensitive names.</summary>
        public static SerializedProperty Find(SerializedObject so, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var path = NormalizePath(key);
            var p = so.FindProperty(path);
            if (p != null) return p;

            if (!path.StartsWith("m_", StringComparison.Ordinal))
            {
                p = so.FindProperty("m_" + char.ToUpperInvariant(path[0]) + path.Substring(1));
                if (p != null) return p;
            }

            int dot = path.IndexOf('.');
            var head = dot < 0 ? path : path.Substring(0, dot);
            var tail = dot < 0 ? string.Empty : path.Substring(dot);
            var it = so.GetIterator();
            if (it.Next(true))
            {
                do
                {
                    if (string.Equals(it.name, head, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(it.name, "m_" + head, StringComparison.OrdinalIgnoreCase))
                    {
                        var found = so.FindProperty(it.name + tail);
                        if (found != null) return found;
                    }
                } while (it.Next(false));
            }
            return null;
        }

        static SerializedProperty FindRelative(SerializedProperty parent, string key)
        {
            var p = parent.FindPropertyRelative(key);
            if (p != null) return p;
            if (!key.StartsWith("m_", StringComparison.Ordinal) && key.Length > 0)
            {
                p = parent.FindPropertyRelative("m_" + char.ToUpperInvariant(key[0]) + key.Substring(1));
                if (p != null) return p;
            }
            foreach (var child in Children(parent))
            {
                if (string.Equals(child.name, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(child.name, "m_" + key, StringComparison.OrdinalIgnoreCase))
                    return parent.FindPropertyRelative(child.name);
            }
            return null;
        }

        static IEnumerable<SerializedProperty> Children(SerializedProperty parent)
        {
            var child = parent.Copy();
            var end = parent.GetEndProperty();
            bool enter = true;
            while (child.Next(enter))
            {
                if (SerializedProperty.EqualContents(child, end)) yield break;
                enter = false;
                yield return child.Copy();
            }
        }

        public static List<string> TopLevelNames(SerializedObject so)
        {
            var names = new List<string>();
            var it = so.GetIterator();
            bool enter = true;
            while (it.NextVisible(enter))
            {
                enter = false;
                names.Add(it.name);
            }
            return names;
        }

        // ------------------------------------------------------------------ reading

        public static JObject ReadAll(SerializedObject so, int maxDepth, int maxArray)
        {
            var result = new JObject();
            var it = so.GetIterator();
            bool enter = true;
            while (it.NextVisible(enter))
            {
                enter = false;
                result[it.name] = Read(it, 0, maxDepth, maxArray);
            }
            return result;
        }

        public static JToken Read(SerializedProperty p, int depth, int maxDepth, int maxArray)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return Math.Round(p.doubleValue, 5);
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Color: return Json.C(p.colorValue);
                case SerializedPropertyType.ObjectReference: return ObjectRefs.Describe(p.objectReferenceValue);
                case SerializedPropertyType.LayerMask: return UnityValues.LayerMaskToJson(p.intValue);
                case SerializedPropertyType.Enum:
                {
                    int index = p.enumValueIndex;
                    var names = p.enumNames;
                    if (index >= 0 && index < names.Length) return names[index];
                    return p.intValue;
                }
                case SerializedPropertyType.Vector2: return Json.V(p.vector2Value);
                case SerializedPropertyType.Vector3: return Json.V(p.vector3Value);
                case SerializedPropertyType.Vector4: return Json.V(p.vector4Value);
                case SerializedPropertyType.Rect: return Json.Rect(p.rectValue);
                case SerializedPropertyType.ArraySize: return p.intValue;
                case SerializedPropertyType.Character: return ((char)p.intValue).ToString();
                case SerializedPropertyType.AnimationCurve:
                {
                    var curve = p.animationCurveValue;
                    if (curve == null) return JValue.CreateNull();
                    var keys = new JArray();
                    foreach (var k in curve.keys.Take(maxArray)) keys.Add(new JArray(Json.R(k.time), Json.R(k.value)));
                    return new JObject { ["keys"] = keys };
                }
                case SerializedPropertyType.Bounds: return Json.Bounds(p.boundsValue);
                case SerializedPropertyType.Gradient: return "(Gradient)";
                case SerializedPropertyType.Quaternion: return Json.V(p.quaternionValue.eulerAngles);
                case SerializedPropertyType.ExposedReference: return ObjectRefs.Describe(p.exposedReferenceValue);
                case SerializedPropertyType.FixedBufferSize: return p.fixedBufferSize;
                case SerializedPropertyType.Vector2Int: return new JArray(p.vector2IntValue.x, p.vector2IntValue.y);
                case SerializedPropertyType.Vector3Int: return new JArray(p.vector3IntValue.x, p.vector3IntValue.y, p.vector3IntValue.z);
                case SerializedPropertyType.RectInt:
                {
                    var r = p.rectIntValue;
                    return new JObject { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height };
                }
                case SerializedPropertyType.BoundsInt:
                {
                    var b = p.boundsIntValue;
                    return new JObject
                    {
                        ["position"] = new JArray(b.position.x, b.position.y, b.position.z),
                        ["size"] = new JArray(b.size.x, b.size.y, b.size.z),
                    };
                }
                case SerializedPropertyType.Hash128: return p.hash128Value.ToString();
            }

            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                int size = p.arraySize;
                if (depth >= maxDepth) return $"(array of {size})";
                var array = new JArray();
                int take = Math.Min(size, maxArray);
                for (int i = 0; i < take; i++) array.Add(Read(p.GetArrayElementAtIndex(i), depth + 1, maxDepth, maxArray));
                if (size > take) array.Add($"... {size - take} more (raise maxArrayElements to see them)");
                return array;
            }

            if (p.propertyType == SerializedPropertyType.ManagedReference && p.managedReferenceValue == null)
                return JValue.CreateNull();

            if (depth >= maxDepth) return "(...)";
            var obj = new JObject();
            if (p.propertyType == SerializedPropertyType.ManagedReference)
                obj["$type"] = p.managedReferenceFullTypename;
            var childIt = p.Copy();
            var end = p.GetEndProperty();
            bool enterChildren = true;
            while (childIt.NextVisible(enterChildren))
            {
                if (SerializedProperty.EqualContents(childIt, end)) break;
                enterChildren = false;
                obj[childIt.name] = Read(childIt.Copy(), depth + 1, maxDepth, maxArray);
            }
            return obj;
        }

        // ------------------------------------------------------------------ writing

        public static void Write(SerializedProperty p, JToken v)
        {
            v = JsonArgs.Unwrap(v) ?? JValue.CreateNull();
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: p.longValue = ToLong(v, p); return;
                case SerializedPropertyType.Boolean: p.boolValue = ToBool(v, p); return;
                case SerializedPropertyType.Float: p.doubleValue = ToDouble(v, p); return;
                case SerializedPropertyType.String:
                    p.stringValue = v.Type == JTokenType.Null ? string.Empty
                        : v.Type == JTokenType.String ? (string)v
                        : v.ToString(Formatting.None);
                    return;
                case SerializedPropertyType.Color: p.colorValue = UnityValues.ToColor(v); return;
                case SerializedPropertyType.ObjectReference: p.objectReferenceValue = ObjectRefs.Resolve(v, ObjectRefType(p)); return;
                case SerializedPropertyType.LayerMask: p.intValue = UnityValues.ToLayerMask(v); return;
                case SerializedPropertyType.Enum: WriteEnum(p, v); return;
                case SerializedPropertyType.Vector2: p.vector2Value = UnityValues.ToVector2(v); return;
                case SerializedPropertyType.Vector3: p.vector3Value = UnityValues.ToVector3(v); return;
                case SerializedPropertyType.Vector4: p.vector4Value = UnityValues.ToVector4(v); return;
                case SerializedPropertyType.Rect: p.rectValue = UnityValues.ToRect(v); return;
                case SerializedPropertyType.ArraySize: p.intValue = (int)ToLong(v, p); return;
                case SerializedPropertyType.Character:
                    p.intValue = v.Type == JTokenType.String && ((string)v).Length > 0 ? ((string)v)[0] : (int)ToLong(v, p);
                    return;
                case SerializedPropertyType.AnimationCurve: p.animationCurveValue = UnityValues.ToAnimationCurve(v); return;
                case SerializedPropertyType.Bounds: p.boundsValue = UnityValues.ToBounds(v); return;
                case SerializedPropertyType.Quaternion: p.quaternionValue = UnityValues.ToQuaternion(v); return;
                case SerializedPropertyType.Vector2Int: p.vector2IntValue = Vector2Int.RoundToInt(UnityValues.ToVector2(v)); return;
                case SerializedPropertyType.Vector3Int: p.vector3IntValue = Vector3Int.RoundToInt(UnityValues.ToVector3(v)); return;
                case SerializedPropertyType.RectInt:
                {
                    var r = UnityValues.ToRect(v);
                    p.rectIntValue = new RectInt(Mathf.RoundToInt(r.x), Mathf.RoundToInt(r.y), Mathf.RoundToInt(r.width), Mathf.RoundToInt(r.height));
                    return;
                }
                case SerializedPropertyType.BoundsInt:
                {
                    var b = UnityValues.ToBounds(v);
                    p.boundsIntValue = new BoundsInt(Vector3Int.RoundToInt(b.min), Vector3Int.RoundToInt(b.size));
                    return;
                }
                case SerializedPropertyType.Gradient:
                case SerializedPropertyType.ExposedReference:
                case SerializedPropertyType.FixedBufferSize:
                case SerializedPropertyType.Hash128:
                    throw new McpToolException($"Setting {p.propertyType} properties ('{p.propertyPath}') is not supported.");
            }

            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                if (v is JArray array)
                {
                    p.arraySize = array.Count;
                    for (int i = 0; i < array.Count; i++) Write(p.GetArrayElementAtIndex(i), array[i]);
                    return;
                }
                throw new McpToolException($"'{p.propertyPath}' is an array; pass a JSON array (or set a single element with '{p.propertyPath}[index]').");
            }

            if (p.propertyType == SerializedPropertyType.ManagedReference && v.Type == JTokenType.Null)
            {
                p.managedReferenceValue = null;
                return;
            }

            if (v is JObject obj)
            {
                foreach (var kv in obj)
                {
                    if (kv.Key == "$type") continue;
                    var child = FindRelative(p, kv.Key);
                    if (child == null)
                    {
                        var names = string.Join(", ", Children(p).Select(c => c.name).Distinct().Take(40));
                        throw new McpToolException($"'{p.propertyPath}' has no field '{kv.Key}'. Fields: {names}");
                    }
                    Write(child, kv.Value);
                }
                return;
            }

            throw new McpToolException($"Cannot assign {v.ToString(Formatting.None)} to '{p.propertyPath}' ({p.type}). Pass a JSON object with its fields.");
        }

        static void WriteEnum(SerializedProperty p, JToken v)
        {
            if (v.Type == JTokenType.String)
            {
                var s = ((string)v).Trim();
                int index = IndexOf(p.enumNames, s);
                if (index < 0) index = IndexOf(p.enumDisplayNames, s);
                if (index >= 0)
                {
                    p.enumValueIndex = index;
                    return;
                }
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    p.intValue = n;
                    return;
                }
                throw new McpToolException($"'{s}' is not a valid value for '{p.propertyPath}'. Valid values: {string.Join(", ", p.enumNames)}");
            }
            p.intValue = (int)ToLong(v, p);
        }

        static int IndexOf(string[] values, string s)
        {
            for (int i = 0; i < values.Length; i++)
                if (string.Equals(values[i], s, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(values[i].Replace(" ", ""), s.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        static Type ObjectRefType(SerializedProperty p)
        {
            var t = p.type; // e.g. "PPtr<$Material>"
            if (t != null && t.StartsWith("PPtr<", StringComparison.Ordinal))
            {
                var name = t.Substring(5).TrimEnd('>').TrimStart('$');
                var type = TypeResolver.FindObjectType(name);
                if (type != null) return type;
            }
            return typeof(Object);
        }

        static long ToLong(JToken v, SerializedProperty p)
        {
            switch (v.Type)
            {
                case JTokenType.Integer: return (long)v;
                case JTokenType.Float: return (long)Math.Round((double)v);
                case JTokenType.Boolean: return (bool)v ? 1 : 0;
                case JTokenType.String:
                    if (long.TryParse((string)v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                    break;
            }
            throw new McpToolException($"'{p.propertyPath}' expects an integer, got {v.ToString(Formatting.None)}.");
        }

        static double ToDouble(JToken v, SerializedProperty p)
        {
            switch (v.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float: return (double)v;
                case JTokenType.Boolean: return (bool)v ? 1 : 0;
                case JTokenType.String:
                    if (double.TryParse((string)v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                    break;
            }
            throw new McpToolException($"'{p.propertyPath}' expects a number, got {v.ToString(Formatting.None)}.");
        }

        static bool ToBool(JToken v, SerializedProperty p)
        {
            switch (v.Type)
            {
                case JTokenType.Boolean: return (bool)v;
                case JTokenType.Integer: return (long)v != 0;
                case JTokenType.String:
                    if (bool.TryParse((string)v, out var b)) return b;
                    break;
            }
            throw new McpToolException($"'{p.propertyPath}' expects true/false, got {v.ToString(Formatting.None)}.");
        }

        // ------------------------------------------------------------------ applying a set of changes

        /// <summary>
        /// Sets several properties on an object. Keys are serialized property paths (fuzzy: "mass" finds
        /// "m_Mass"); keys that are not serialized fall back to public C# properties/fields via reflection.
        /// Returns {"applied": {key: newValue}, "errors": [...]}.
        /// </summary>
        public static JObject Apply(Object target, JObject properties)
        {
            var applied = new JObject();
            var errors = new JArray();
            var so = new SerializedObject(target);
            var serialized = new List<KeyValuePair<string, string>>();
            var viaReflection = new List<KeyValuePair<string, JToken>>();

            foreach (var kv in properties)
            {
                try
                {
                    var p = Find(so, kv.Key);
                    if (p != null)
                    {
                        Write(p, kv.Value);
                        serialized.Add(new KeyValuePair<string, string>(kv.Key, p.propertyPath));
                    }
                    else
                    {
                        viaReflection.Add(new KeyValuePair<string, JToken>(kv.Key, kv.Value));
                    }
                }
                catch (Exception e)
                {
                    errors.Add($"{kv.Key}: {ToolDispatcher.FormatException(e)}");
                }
            }

            so.ApplyModifiedProperties();
            so.Update();
            foreach (var kv in serialized)
            {
                var p = so.FindProperty(kv.Value);
                applied[kv.Key] = p != null ? Read(p, 0, 3, 20) : "(applied)";
            }

            foreach (var kv in viaReflection)
            {
                try
                {
                    if (TrySetMember(target, kv.Key, kv.Value, out var newValue))
                        applied[kv.Key] = newValue;
                    else
                        errors.Add($"{kv.Key}: no serialized property or public field/property with this name on {target.GetType().Name}. " +
                                   $"Serialized properties: {string.Join(", ", TopLevelNames(so))}");
                }
                catch (Exception e)
                {
                    errors.Add($"{kv.Key}: {ToolDispatcher.FormatException(e)}");
                }
            }

            var result = new JObject { ["applied"] = applied };
            if (errors.Count > 0) result["errors"] = errors;
            return result;
        }

        static bool TrySetMember(Object target, string key, JToken value, out JToken newValue)
        {
            newValue = null;
            // In edit mode, renderer.material / meshFilter.mesh would create leaked copies.
            if (target is Renderer)
            {
                if (string.Equals(key, "material", StringComparison.OrdinalIgnoreCase)) key = "sharedMaterial";
                else if (string.Equals(key, "materials", StringComparison.OrdinalIgnoreCase)) key = "sharedMaterials";
            }
            else if (target is MeshFilter && string.Equals(key, "mesh", StringComparison.OrdinalIgnoreCase))
            {
                key = "sharedMesh";
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
            var type = target.GetType();
            var property = type.GetProperty(key, flags);
            if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0)
            {
                var converted = UnityValues.ConvertTo(value, property.PropertyType);
                Undo.RecordObject(target, "Set " + property.Name);
                property.SetValue(target, converted);
                MarkChanged(target);
                newValue = SafeJson(property.CanRead ? property.GetValue(target) : converted);
                return true;
            }

            var field = type.GetField(key, flags);
            if (field != null && !field.IsInitOnly && !field.IsLiteral)
            {
                var converted = UnityValues.ConvertTo(value, field.FieldType);
                Undo.RecordObject(target, "Set " + field.Name);
                field.SetValue(target, converted);
                MarkChanged(target);
                newValue = SafeJson(field.GetValue(target));
                return true;
            }
            return false;
        }

        static void MarkChanged(Object target)
        {
            EditorUtility.SetDirty(target);
            if (PrefabUtility.IsPartOfPrefabInstance(target))
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        internal static JToken SafeJson(object value)
        {
            try
            {
                var token = Json.ToToken(value);
                var text = token.ToString(Formatting.None);
                return text.Length > 4000 ? (JToken)(text.Substring(0, 4000) + "...") : token;
            }
            catch (Exception)
            {
                return value?.ToString();
            }
        }
    }
}
