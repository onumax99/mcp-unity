using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace McpUnity
{
    /// <summary>Finds GameObjects in the open scenes (and the prefab editing stage) by id, path or name.</summary>
    internal static class SceneQuery
    {
        /// <summary>Prefab stage first (if open), then loaded scenes, then DontDestroyOnLoad in Play Mode.</summary>
        public static List<Scene> GetScenes()
        {
            var scenes = new List<Scene>();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) scenes.Add(stage.scene);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded) scenes.Add(scene);
            }
            if (Application.isPlaying)
            {
                var ddol = GetDontDestroyOnLoadScene();
                if (ddol.IsValid() && ddol.rootCount > 0) scenes.Add(ddol);
            }
            return scenes;
        }

        static Scene GetDontDestroyOnLoadScene()
        {
            GameObject probe = null;
            try
            {
                probe = new GameObject("__mcp_probe") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(probe);
                return probe.scene;
            }
            catch (Exception)
            {
                return default;
            }
            finally
            {
                if (probe) Object.DestroyImmediate(probe);
            }
        }

        public static IEnumerable<GameObject> AllGameObjects(bool includeInactive = true)
        {
            foreach (var scene in GetScenes())
            {
                var stack = new Stack<Transform>();
                var roots = scene.GetRootGameObjects();
                for (int i = roots.Length - 1; i >= 0; i--) stack.Push(roots[i].transform);
                while (stack.Count > 0)
                {
                    var t = stack.Pop();
                    if (!t) continue;
                    if (!includeInactive && !t.gameObject.activeInHierarchy) continue;
                    yield return t.gameObject;
                    for (int i = t.childCount - 1; i >= 0; i--) stack.Push(t.GetChild(i));
                }
            }
        }

        public static string GetPath(GameObject go)
        {
            if (!go) return null;
            var names = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            return string.Join("/", names);
        }

        public static GameObject Resolve(JToken target)
        {
            target = JsonArgs.Unwrap(target);
            if (target == null || target.Type == JTokenType.Null)
                throw new McpToolException("A target GameObject is required (instance id, hierarchy path or name).");

            switch (target.Type)
            {
                case JTokenType.Integer:
                    return ById(target.ToString(Formatting.None)) ?? throw new McpToolException($"No GameObject with id {target}.");
                case JTokenType.Object:
                    var o = (JObject)target;
                    if (o["id"] != null) return Resolve(o["id"]);
                    if (o["instanceId"] != null) return Resolve(o["instanceId"]);
                    if (o["path"] != null) return Resolve(o["path"]);
                    if (o["sceneObject"] != null) return Resolve(o["sceneObject"]);
                    if (o["name"] != null) return Resolve(o["name"]);
                    throw new McpToolException($"Cannot interpret {o.ToString(Formatting.None)} as a GameObject.");
                case JTokenType.String:
                    return Resolve((string)target);
                default:
                    return Resolve(target.ToString(Formatting.None));
            }
        }

        public static GameObject Resolve(string target)
        {
            var s = target?.Trim();
            if (string.IsNullOrEmpty(s)) throw new McpToolException("A target GameObject is required (instance id, hierarchy path or name).");

            var byId = ById(s);
            if (byId) return byId;

            var path = s.TrimStart('/');
            var matches = FindByPath(path);
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) throw Ambiguous(s, matches);

            if (path.IndexOf('/') < 0)
            {
                var byName = AllGameObjects().Where(g => g.name == path).ToList();
                if (byName.Count == 0)
                    byName = AllGameObjects().Where(g => string.Equals(g.name, path, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byName.Count == 1) return byName[0];
                if (byName.Count > 1) throw Ambiguous(s, byName);
            }

            throw new McpToolException($"GameObject '{s}' not found in the open scenes. " +
                                       "Use find_gameobjects or get_hierarchy to look up names and paths, or pass an instance id.");
        }

        static GameObject ById(string id)
        {
            if (!Ids.TryFind(id, out var o)) return null;
            if (o is GameObject go) return go;
            if (o is Component c) return c.gameObject;
            return null;
        }

        static List<GameObject> FindByPath(string path)
        {
            var parts = path.Split('/');
            var results = new List<GameObject>();
            foreach (var scene in GetScenes())
            {
                foreach (var root in scene.GetRootGameObjects())
                    if (root.name == parts[0])
                        Descend(root.transform, parts, 1, results);
            }
            return results;
        }

        static void Descend(Transform t, string[] parts, int index, List<GameObject> results)
        {
            if (index == parts.Length)
            {
                results.Add(t.gameObject);
                return;
            }
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (child.name == parts[index]) Descend(child, parts, index + 1, results);
            }
        }

        static McpToolException Ambiguous(string query, List<GameObject> matches)
        {
            var list = string.Join("; ", matches.Take(10).Select(g => $"id {Ids.Of(g)}: {GetPath(g)}"));
            var more = matches.Count > 10 ? $" (and {matches.Count - 10} more)" : string.Empty;
            return new McpToolException($"'{query}' matches {matches.Count} GameObjects: {list}{more}. Pass an instance id or a more specific path.");
        }
    }
}
