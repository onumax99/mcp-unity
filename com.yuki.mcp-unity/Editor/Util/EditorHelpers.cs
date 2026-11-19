using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace McpUnity
{
    /// <summary>Compact JSON descriptions of GameObjects for tool results.</summary>
    internal static class GameObjectInfo
    {
        public static JObject Brief(GameObject go) => new JObject
        {
            ["name"] = go.name,
            ["id"] = Ids.Of(go),
            ["path"] = SceneQuery.GetPath(go),
        };

        public static JObject Transform(Transform t)
        {
            var j = new JObject
            {
                ["position"] = Json.V(t.position),
                ["rotation"] = Json.V(t.eulerAngles),
                ["localPosition"] = Json.V(t.localPosition),
                ["localRotation"] = Json.V(t.localEulerAngles),
                ["localScale"] = Json.V(t.localScale),
            };
            if (t is RectTransform rt)
            {
                j["anchoredPosition"] = Json.V(rt.anchoredPosition);
                j["sizeDelta"] = Json.V(rt.sizeDelta);
                j["anchorMin"] = Json.V(rt.anchorMin);
                j["anchorMax"] = Json.V(rt.anchorMax);
                j["pivot"] = Json.V(rt.pivot);
            }
            return j;
        }

        /// <summary>[{type, index?, enabled?}] — index only when a type occurs more than once.</summary>
        public static JArray Components(GameObject go)
        {
            var array = new JArray();
            var seen = new Dictionary<Type, int>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    array.Add(new JObject { ["type"] = "(Missing Script)" });
                    continue;
                }
                var type = c.GetType();
                seen.TryGetValue(type, out var n);
                seen[type] = n + 1;
                var j = new JObject { ["type"] = type.Name };
                if (n > 0) j["index"] = n;
                if (EditorUtility.GetObjectEnabled(c) == 0) j["enabled"] = false;
                array.Add(j);
            }
            return array;
        }

        public static JObject Full(GameObject go, bool includeChildren = true)
        {
            var j = Brief(go);
            j["scene"] = go.scene.name;
            j["active"] = go.activeSelf;
            if (go.activeSelf && !go.activeInHierarchy) j["activeInHierarchy"] = false;
            j["tag"] = go.tag;
            j["layer"] = LayerMask.LayerToName(go.layer) is string layerName && layerName.Length > 0 ? layerName : go.layer.ToString();
            if (go.isStatic) j["isStatic"] = true;
            j["transform"] = Transform(go.transform);
            j["components"] = Components(go);

            var t = go.transform;
            if (t.parent != null) j["parent"] = Brief(t.parent.gameObject);
            j["childCount"] = t.childCount;
            if (includeChildren && t.childCount > 0)
            {
                var children = new JArray();
                for (int i = 0; i < Math.Min(t.childCount, 100); i++)
                {
                    var c = t.GetChild(i).gameObject;
                    children.Add(new JObject { ["name"] = c.name, ["id"] = Ids.Of(c) });
                }
                j["children"] = children;
            }

            if (PrefabUtility.IsPartOfPrefabInstance(go))
            {
                j["prefab"] = new JObject
                {
                    ["assetPath"] = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go),
                    ["isInstanceRoot"] = PrefabUtility.IsOutermostPrefabInstanceRoot(go),
                };
            }
            return j;
        }

        public static JObject SceneInfo(Scene scene, bool isActive)
        {
            var j = new JObject
            {
                ["name"] = scene.name,
                ["path"] = scene.path,
                ["isLoaded"] = scene.isLoaded,
                ["isDirty"] = scene.isDirty,
            };
            if (isActive) j["isActive"] = true;
            if (scene.isLoaded) j["rootCount"] = scene.rootCount;
            return j;
        }

        /// <summary>Marks a scene as modified. No-op in Play Mode, where EditorSceneManager throws.</summary>
        public static void MarkSceneDirty(Scene scene)
        {
            if (Application.isPlaying || !scene.IsValid()) return;
            EditorSceneManager.MarkSceneDirty(scene);
        }

        public static void SetTag(GameObject go, string tag)
        {
            if (!InternalEditorUtility.tags.Contains(tag))
                InternalEditorUtility.AddTag(tag);
            go.tag = tag;
        }

        public static void SetLayer(GameObject go, JToken layer)
        {
            go.layer = ParseLayer(layer);
        }

        public static int ParseLayer(JToken layer)
        {
            if (layer.Type == JTokenType.Integer)
            {
                int index = (int)(long)layer;
                if (index < 0 || index > 31) throw new McpToolException("Layer index must be between 0 and 31.");
                return index;
            }
            var name = ((string)layer).Trim();
            int byName = LayerMask.NameToLayer(name);
            if (byName >= 0) return byName;
            if (int.TryParse(name, out var parsed) && parsed >= 0 && parsed <= 31) return parsed;
            throw new McpToolException($"Unknown layer '{name}'. Defined layers: {string.Join(", ", UnityValues.DefinedLayers())}. " +
                                       "Add layers in Project Settings > Tags and Layers.");
        }

        /// <summary>Applies position / rotation / scale arguments (local unless worldSpace).</summary>
        public static void ApplyTransformArgs(Transform t, JObject args, bool worldSpace)
        {
            if (args.Has("position"))
            {
                var p = UnityValues.ToVector3(args.Arg("position"));
                if (worldSpace) t.position = p; else t.localPosition = p;
            }
            if (args.Has("rotation"))
            {
                var r = UnityValues.ToQuaternion(args.Arg("rotation"));
                if (worldSpace) t.rotation = r; else t.localRotation = r;
            }
            if (args.Has("scale"))
                t.localScale = UnityValues.ToVector3(args.Arg("scale"));
        }
    }

    internal static class AssetPaths
    {
        /// <summary>Normalizes to a project-relative path ("Assets/...") and optionally enforces an extension.</summary>
        public static string Normalize(string path, string requiredExtension = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new McpToolException("An asset path is required.");
            path = path.Trim().Replace('\\', '/');
            var root = McpSettings.ProjectRoot.TrimEnd('/') + "/";
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) path = path.Substring(root.Length);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal) && path != "Assets")
                path = "Assets/" + path.TrimStart('/');
            if (path.Contains("../")) throw new McpToolException("Asset paths must not contain '..'.");
            if (requiredExtension != null && !path.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
                path += requiredExtension;
            return path;
        }

        public static void EnsureFolder(string folder)
        {
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            var name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent)) throw new McpToolException($"Invalid folder '{folder}'.");
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        public static void EnsureParentFolder(string assetPath)
        {
            EnsureFolder(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));
        }

        public static bool Exists(string assetPath) =>
            !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets));
    }

    internal static class RenderPipelineInfo
    {
        public static string Name
        {
            get
            {
                var rp = GraphicsSettings.currentRenderPipeline;
                if (rp == null) return "Built-in";
                var typeName = rp.GetType().Name;
                if (typeName.Contains("Universal")) return "URP";
                if (typeName.StartsWith("HD")) return "HDRP";
                return typeName;
            }
        }

        public static Shader DefaultLitShader()
        {
            Shader shader = null;
            switch (Name)
            {
                case "URP": shader = Shader.Find("Universal Render Pipeline/Lit"); break;
                case "HDRP": shader = Shader.Find("HDRP/Lit"); break;
                case "Built-in": shader = Shader.Find("Standard"); break;
            }
            return shader
                   ? shader
                   : Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
        }

        public static IEnumerable<string> ShaderPropertyNames(Shader shader)
        {
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
                yield return $"{shader.GetPropertyName(i)} ({shader.GetPropertyType(i)})";
        }
    }
}
