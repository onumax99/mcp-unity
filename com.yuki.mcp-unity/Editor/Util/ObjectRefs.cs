using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace McpUnity
{
    /// <summary>
    /// Object ids for tool results, kept in one place because the API changes across Unity 6.x:
    /// newer versions use 64-bit EntityId (GetInstanceID is a compile error there).
    /// Ids beyond 2^53 are emitted as strings so JavaScript clients cannot round them.
    /// </summary>
    internal static class Ids
    {
        const ulong k_MaxSafeInteger = 9007199254740991UL;

#if UNITY_6000_6_OR_NEWER
        static ulong Raw(Object o) => EntityId.ToULong(o.GetEntityId());
        static Object FromRaw(ulong raw) => EditorUtility.EntityIdToObject(EntityId.FromULong(raw));
#else
#pragma warning disable CS0618, CS0612
        static ulong Raw(Object o) => unchecked((ulong)(long)o.GetInstanceID());
        static Object FromRaw(ulong raw) => EditorUtility.InstanceIDToObject(unchecked((int)(long)raw));
#pragma warning restore CS0618, CS0612
#endif

        public static JToken Of(Object o)
        {
            ulong raw = Raw(o);
            if (raw <= k_MaxSafeInteger) return (long)raw;
            long signed = unchecked((long)raw);
            if (signed < 0 && signed >= -(long)k_MaxSafeInteger) return signed;
            return raw.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public static Object Find(JToken token)
        {
            if (token == null) return null;
            return TryFind(token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None), out var o) ? o : null;
        }

        /// <summary>Parses an id (number or numeric string) and returns the live object, if any.</summary>
        public static bool TryFind(string text, out Object result)
        {
            result = null;
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim().Trim('"');
            ulong raw;
            if (ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var u))
                raw = u;
            else if (long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l))
                raw = unchecked((ulong)l);
            else
                return false;
            if (raw == 0) return false;
            result = FromRaw(raw);
            return result;
        }
    }

    /// <summary>Describes and resolves references to Unity objects (assets and scene objects).</summary>
    internal static class ObjectRefs
    {
        /// <summary>
        /// Asset:        {"type":"Material","name":"Red","path":"Assets/Red.mat"}
        /// Sub-asset:    {"type":"Mesh","name":"Cube","path":"Library/unity default resources::Cube"}
        /// Scene object: {"type":"Rigidbody","name":"Player","sceneObject":"Player","id":-1234}
        /// </summary>
        public static JToken Describe(Object o)
        {
            if (!o) return JValue.CreateNull();
            var j = new JObject { ["type"] = o.GetType().Name, ["name"] = o.name };
            var path = AssetDatabase.GetAssetPath(o);
            if (!string.IsNullOrEmpty(path))
            {
                j["path"] = AssetDatabase.IsMainAsset(o) ? path : path + "::" + o.name;
            }
            else
            {
                var go = o as GameObject ?? (o as Component)?.gameObject;
                if (go != null) j["sceneObject"] = SceneQuery.GetPath(go);
                j["id"] = Ids.Of(o);
            }
            return j;
        }

        public static bool LooksLikeAssetPath(string s)
        {
            s = s.Replace('\\', '/');
            return s.StartsWith("Assets/", StringComparison.Ordinal) || s == "Assets" ||
                   s.StartsWith("Packages/", StringComparison.Ordinal) ||
                   s.StartsWith("Library/", StringComparison.Ordinal) ||
                   s.StartsWith("ProjectSettings/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Accepts: null, instance id, "Assets/path.ext", "Assets/model.fbx::SubAssetName",
        /// a scene object path/name, or {"id":..} / {"path":..,"name":..} / {"sceneObject":..}.
        /// Adapts GameObject &lt;-&gt; Component to match <paramref name="expected"/>.
        /// </summary>
        public static Object Resolve(JToken v, Type expected)
        {
            expected = expected ?? typeof(Object);
            v = JsonArgs.Unwrap(v);
            if (v == null || v.Type == JTokenType.Null) return null;

            Object found;
            switch (v.Type)
            {
                case JTokenType.Integer:
                    found = Ids.Find(v);
                    if (!found) throw new McpToolException($"No object with id {v}.");
                    break;
                case JTokenType.String:
                    var s = ((string)v).Trim();
                    if (s.Length == 0) return null;
                    found = ResolveString(s, expected);
                    break;
                case JTokenType.Object:
                    found = ResolveObject((JObject)v, expected);
                    break;
                default:
                    throw new McpToolException($"Cannot interpret {v.ToString(Formatting.None)} as an object reference. Use an asset path, a scene object path, or an instance id.");
            }

            var adapted = Adapt(found, expected);
            if (!adapted)
            {
                var hint = found is GameObject ? $" (the GameObject has no {expected.Name} component)" : string.Empty;
                throw new McpToolException($"'{v.ToString(Formatting.None)}' resolved to {found.GetType().Name} '{found.name}', which is not a {expected.Name}{hint}.");
            }
            return adapted;
        }

        public static Object Adapt(Object o, Type expected)
        {
            if (!o) return null;
            if (expected.IsInstanceOfType(o)) return o;
            if (o is GameObject go && typeof(Component).IsAssignableFrom(expected)) return go.GetComponent(expected);
            if (o is Component c)
            {
                if (expected == typeof(GameObject)) return c.gameObject;
                if (typeof(Component).IsAssignableFrom(expected)) return c.GetComponent(expected);
            }
            return null;
        }

        static Object ResolveString(string s, Type expected)
        {
            string path = s, subName = null;
            int separator = s.IndexOf("::", StringComparison.Ordinal);
            if (separator >= 0)
            {
                path = s.Substring(0, separator);
                subName = s.Substring(separator + 2);
            }

            if (LooksLikeAssetPath(path)) return LoadAsset(path, subName, expected);

            if (s.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
            {
                var guidPath = AssetDatabase.GUIDToAssetPath(s.Substring(5).Trim());
                if (string.IsNullOrEmpty(guidPath)) throw new McpToolException($"No asset with {s}.");
                return LoadAsset(guidPath, null, expected);
            }

            if (Ids.TryFind(s, out var byId)) return byId;
            return SceneQuery.Resolve(s);
        }

        static Object ResolveObject(JObject o, Type expected)
        {
            if (o["id"] != null) return Resolve(o["id"], typeof(Object));
            if (o["instanceId"] != null) return Resolve(o["instanceId"], typeof(Object));
            var path = (string)o["path"] ?? (string)o["assetPath"];
            if (!string.IsNullOrEmpty(path))
            {
                var sub = (string)o["name"] ?? (string)o["subAsset"];
                int separator = path.IndexOf("::", StringComparison.Ordinal);
                if (separator >= 0)
                {
                    sub = path.Substring(separator + 2);
                    path = path.Substring(0, separator);
                }
                // A described main asset carries its name too; only treat "name" as a sub-asset when needed.
                var main = LoadAsset(path, null, expected, throwIfMissing: false);
                if (main && (sub == null || main.name == sub)) return main;
                return LoadAsset(path, sub, expected);
            }
            var scene = o["sceneObject"] ?? o["gameObject"] ?? o["target"];
            if (scene != null) return SceneQuery.Resolve(scene);
            throw new McpToolException($"Object reference {o.ToString(Formatting.None)} needs 'path', 'sceneObject' or 'id'.");
        }

        public static Object LoadAsset(string path, string subName, Type expected, bool throwIfMissing = true)
        {
            path = path.Replace('\\', '/').Trim();
            expected = expected ?? typeof(Object);

            if (!string.IsNullOrEmpty(subName))
            {
                var all = AssetDatabase.LoadAllAssetsAtPath(path);
                if (all == null || all.Length == 0)
                {
                    if (!throwIfMissing) return null;
                    throw new McpToolException($"No asset found at '{path}'.");
                }
                var match = all.FirstOrDefault(a => a && a.name == subName && Adapt(a, expected))
                            ?? all.FirstOrDefault(a => a && a.name == subName);
                if (match) return match;
                if (!throwIfMissing) return null;
                var names = string.Join(", ", all.Where(a => a).Take(40).Select(a => $"{a.name} ({a.GetType().Name})"));
                throw new McpToolException($"Sub-asset '{subName}' not found in '{path}'. Available: {names}");
            }

            var typed = AssetDatabase.LoadAssetAtPath(path, expected);
            if (typed) return typed;
            var mainAsset = AssetDatabase.LoadMainAssetAtPath(path);
            if (mainAsset)
            {
                if (Adapt(mainAsset, expected)) return mainAsset;
                // e.g. a Sprite or Mesh inside a texture/model
                var sub = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(a => a && Adapt(a, expected));
                return sub ? sub : mainAsset;
            }
            // ProjectSettings/*.asset and built-in resources are only reachable through LoadAllAssetsAtPath.
            var any = AssetDatabase.LoadAllAssetsAtPath(path);
            if (any != null && any.Length > 0)
                return any.FirstOrDefault(a => a && Adapt(a, expected)) ?? any[0];
            if (!throwIfMissing) return null;
            throw new McpToolException($"No asset found at '{path}'. Use find_assets to search the project.");
        }
    }
}
