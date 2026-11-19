using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace McpUnity.Tools
{
    internal sealed class FindAssetsTool : McpTool
    {
        public override string Name => "find_assets";
        public override string Description =>
            "Search project assets with Unity's search syntax (as in the Project window): a name, 't:Type' (t:Prefab, t:Material, t:Scene, t:Script, t:Texture2D, t:ScriptableObject...), " +
            "'l:Label', or combinations like 't:Prefab enemy'. Returns asset paths and types.";
        public override bool ReadOnly => true;

        public override JObject InputSchema => Schema.Object()
            .Str("filter", "Search filter, e.g. 't:Material' or 'Player t:Prefab'. Empty matches everything.")
            .Prop("folders", Schema.StringArray("Folders to search (default ['Assets']). Use 'Packages/...' to search packages."))
            .Int("limit", "Maximum results (default 100, max 2000).");

        protected override object Execute(JObject args, ToolContext context)
        {
            var filter = args.GetString("filter", string.Empty);
            var folders = args.GetStringList("folders").Select(f => f.Trim().Replace('\\', '/').TrimEnd('/')).ToList();
            if (folders.Count == 0) folders.Add("Assets");
            foreach (var folder in folders)
                if (!AssetDatabase.IsValidFolder(folder))
                    throw new McpToolException($"Folder '{folder}' does not exist.");
            int limit = Mathf.Clamp(args.GetInt("limit", 100), 1, 2000);

            var guids = AssetDatabase.FindAssets(filter, folders.ToArray());
            var seen = new HashSet<string>();
            var results = new JArray();
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                if (results.Count >= limit) continue;
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                results.Add(new JObject { ["path"] = path, ["type"] = type != null ? type.Name : "Unknown" });
            }
            return new { total = seen.Count, returned = results.Count, results };
        }
    }

    internal sealed class MaterialTool : McpTool
    {
        public override string Name => "create_or_update_material";
        public override string Description =>
            "Create a material asset, or update an existing one, and set shader properties. Without 'shader', a new material uses the render pipeline's default lit shader " +
            "(URP: 'Universal Render Pipeline/Lit', HDRP: 'HDRP/Lit', Built-in: 'Standard'). Property names are shader properties such as _BaseColor, _BaseMap, _Metallic, _Smoothness (URP) " +
            "or _Color, _MainTex, _Glossiness (Built-in); 'color' and 'mainTexture' work as aliases. Colors: [r,g,b,a] or '#RRGGBB'; textures: asset path. " +
            "Assign it with set_properties on a Renderer, e.g. {\"m_Materials\": [\"Assets/Materials/Red.mat\"]}.";

        public override JObject InputSchema => Schema.Object()
            .Str("path", "Material asset path, e.g. 'Assets/Materials/Red.mat' (folders are created as needed).", required: true)
            .Str("shader", "Shader name (e.g. 'Universal Render Pipeline/Unlit') or shader asset path. Changes the shader of an existing material.")
            .Prop("properties", Schema.Map("Shader property -> value, e.g. {\"_BaseColor\": [1, 0, 0, 1], \"_Smoothness\": 0.2}."))
            .Prop("enableKeywords", Schema.StringArray("Shader keywords to enable."))
            .Prop("disableKeywords", Schema.StringArray("Shader keywords to disable."))
            .Int("renderQueue", "Render queue override (-1 = shader default).")
            .Bool("listProperties", "Include the shader's property names and types in the result (default false).");

        protected override object Execute(JObject args, ToolContext context)
        {
            var path = AssetPaths.Normalize(args.RequireString("path"), ".mat");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = false;
            var shaderArg = args.GetString("shader");

            if (material == null)
            {
                if (AssetPaths.Exists(path)) throw new McpToolException($"'{path}' exists but is not a material.");
                var shader = string.IsNullOrEmpty(shaderArg) ? RenderPipelineInfo.DefaultLitShader() : ResolveShader(shaderArg);
                if (shader == null) throw new McpToolException("Could not find a default shader; pass 'shader'.");
                material = new Material(shader);
                AssetPaths.EnsureParentFolder(path);
                AssetDatabase.CreateAsset(material, path);
                created = true;
            }
            else
            {
                Undo.RecordObject(material, "Modify " + material.name);
                if (!string.IsNullOrEmpty(shaderArg)) material.shader = ResolveShader(shaderArg);
            }

            var applied = new JObject();
            var errors = new JArray();
            var properties = args.GetObject("properties");
            if (properties != null)
            {
                foreach (var kv in properties)
                {
                    try
                    {
                        var name = ResolveAlias(material, kv.Key);
                        int index = material.shader.FindPropertyIndex(name);
                        if (index < 0)
                        {
                            errors.Add($"{kv.Key}: shader '{material.shader.name}' has no property '{name}'.");
                            continue;
                        }
                        applied[kv.Key] = SetProperty(material, name, material.shader.GetPropertyType(index), kv.Value);
                    }
                    catch (Exception e)
                    {
                        errors.Add($"{kv.Key}: {ToolDispatcher.FormatException(e)}");
                    }
                }
            }
            foreach (var keyword in args.GetStringList("enableKeywords")) material.EnableKeyword(keyword);
            foreach (var keyword in args.GetStringList("disableKeywords")) material.DisableKeyword(keyword);
            if (args.Has("renderQueue")) material.renderQueue = args.GetInt("renderQueue", -1);

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);

            var result = new JObject
            {
                ["path"] = path,
                ["shader"] = material.shader.name,
                ["created"] = created,
                ["applied"] = applied,
            };
            if (errors.Count > 0) result["errors"] = errors;
            if (errors.Count > 0 || args.GetBool("listProperties", false))
                result["shaderProperties"] = new JArray(RenderPipelineInfo.ShaderPropertyNames(material.shader));
            return result;
        }

        static Shader ResolveShader(string nameOrPath)
        {
            var shader = ObjectRefs.LooksLikeAssetPath(nameOrPath)
                ? AssetDatabase.LoadAssetAtPath<Shader>(nameOrPath.Replace('\\', '/'))
                : Shader.Find(nameOrPath);
            if (shader == null)
                throw new McpToolException($"Shader '{nameOrPath}' not found. Examples: 'Universal Render Pipeline/Lit', 'Universal Render Pipeline/Unlit', 'Standard', 'Unlit/Color'.");
            return shader;
        }

        static string ResolveAlias(Material material, string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "color":
                case "basecolor":
                case "maincolor":
                    return material.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
                case "maintexture":
                case "texture":
                case "basemap":
                case "maintex":
                    return material.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
                default:
                    if (!key.StartsWith("_") && !material.HasProperty(key) && material.HasProperty("_" + key)) return "_" + key;
                    return key;
            }
        }

        static JToken SetProperty(Material material, string name, ShaderPropertyType type, JToken value)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                    material.SetColor(name, UnityValues.ToColor(value));
                    return Json.C(material.GetColor(name));
                case ShaderPropertyType.Vector:
                    material.SetVector(name, UnityValues.ToVector4(value));
                    return Json.V(material.GetVector(name));
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    material.SetFloat(name, UnityValues.ToFloat(value));
                    return Json.R(material.GetFloat(name));
                case ShaderPropertyType.Int:
                    material.SetInteger(name, Mathf.RoundToInt(UnityValues.ToFloat(value)));
                    return material.GetInteger(name);
                case ShaderPropertyType.Texture:
                    material.SetTexture(name, (Texture)ObjectRefs.Resolve(value, typeof(Texture)));
                    return ObjectRefs.Describe(material.GetTexture(name));
                default:
                    throw new McpToolException($"Unsupported shader property type {type}.");
            }
        }
    }

    internal sealed class CreatePrefabTool : McpTool
    {
        public override string Name => "create_prefab";
        public override string Description =>
            "Save a scene GameObject (with its children) as a prefab asset and connect the scene object to it.";

        public override JObject InputSchema => Schema.Object()
            .Str("target", "Scene GameObject (id, path or name).", required: true)
            .Str("path", "Prefab asset path, e.g. 'Assets/Prefabs/Enemy.prefab' (folders are created as needed).", required: true)
            .Bool("overwrite", "Replace an existing prefab at that path (default false).");

        public override bool Destructive => true;

        protected override object Execute(JObject args, ToolContext context)
        {
            var go = SceneQuery.Resolve(args.Require("target"));
            var path = AssetPaths.Normalize(args.RequireString("path"), ".prefab");
            if (AssetPaths.Exists(path) && !args.GetBool("overwrite", false))
                throw new McpToolException($"'{path}' already exists. Pass overwrite=true to replace it.");
            AssetPaths.EnsureParentFolder(path);
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.UserAction, out bool success);
            if (!success || prefab == null) throw new McpToolException($"Failed to save '{go.name}' as a prefab at '{path}'.");
            return new { path, prefab = prefab.name, instance = GameObjectInfo.Brief(go) };
        }
    }

    internal sealed class ManageSceneTool : McpTool
    {
        public override string Name => "manage_scene";
        public override string Description =>
            "Scene management. 'list': open scenes, Build Settings scenes and scene assets. 'open': open a scene (single or additive). " +
            "'save': save the active scene (or all open scenes, or save-as to 'path'). 'new': create a scene (optionally saved to 'path'). " +
            "'set_active': make an open scene active. 'close': close an additively opened scene. Not available in Play Mode except 'list'.";

        public override JObject InputSchema => Schema.Object()
            .Enum("action", "Operation.", new[] { "list", "open", "save", "new", "set_active", "close" }, required: true)
            .Str("path", "Scene asset path, e.g. 'Assets/Scenes/Level1.unity' (open / save as / new / set_active / close).")
            .Enum("mode", "For open/new: replace the open scenes or add to them (default single).", new[] { "single", "additive" })
            .Enum("setup", "For new: 'default' (camera + light) or 'empty' (default 'default').", new[] { "default", "empty" })
            .Bool("all", "For save: save all open scenes (default false).")
            .Bool("saveCurrent", "For open/new in single mode: save modified open scenes first.")
            .Bool("discardChanges", "For open/new in single mode: discard unsaved changes in the open scenes.");

        protected override object Execute(JObject args, ToolContext context)
        {
            var action = args.RequireString("action").ToLowerInvariant();
            if (action != "list" && EditorApplication.isPlaying)
                throw new McpToolException("Scene management is not available in Play Mode. Stop Play Mode first (play_mode action='stop').");

            switch (action)
            {
                case "list": return List();
                case "open": return Open(args);
                case "save": return Save(args);
                case "new": return New(args);
                case "set_active":
                {
                    var scene = FindOpenScene(args.RequireString("path"));
                    SceneManager.SetActiveScene(scene);
                    return new { activeScene = scene.path };
                }
                case "close":
                {
                    var scene = FindOpenScene(args.RequireString("path"));
                    if (SceneManager.sceneCount <= 1) throw new McpToolException("Cannot close the only open scene; open another scene instead.");
                    if (scene.isDirty && !args.GetBool("discardChanges", false))
                        throw new McpToolException($"'{scene.name}' has unsaved changes. Save it first or pass discardChanges=true.");
                    EditorSceneManager.CloseScene(scene, true);
                    return new { closed = scene.path };
                }
                default:
                    throw new McpToolException("action must be one of: list, open, save, new, set_active, close.");
            }
        }

        static object List()
        {
            var active = SceneManager.GetActiveScene();
            var open = new JArray();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                open.Add(GameObjectInfo.SceneInfo(scene, scene == active));
            }
            var build = EditorBuildSettings.scenes.Select(s => new { s.path, s.enabled }).ToArray();
            var assets = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .ToList();
            return new { openScenes = open, buildSettingsScenes = build, sceneAssets = assets.Take(200), sceneAssetCount = assets.Count };
        }

        static void EnsureCanReplace(JObject args)
        {
            var dirty = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.isDirty) dirty.Add(s);
            }
            if (dirty.Count == 0) return;
            if (args.GetBool("saveCurrent", false))
            {
                foreach (var s in dirty)
                {
                    if (string.IsNullOrEmpty(s.path))
                        throw new McpToolException("The current scene has never been saved. Save it first with action='save' and a 'path'.");
                }
                EditorSceneManager.SaveScenes(dirty.ToArray());
                return;
            }
            if (args.GetBool("discardChanges", false)) return;
            throw new McpToolException(
                $"Open scene(s) have unsaved changes: {string.Join(", ", dirty.Select(s => string.IsNullOrEmpty(s.name) ? "Untitled" : s.name))}. " +
                "Pass saveCurrent=true to save them first, or discardChanges=true to discard them.");
        }

        static object Open(JObject args)
        {
            var path = AssetPaths.Normalize(args.RequireString("path"), ".unity");
            if (!AssetPaths.Exists(path)) throw new McpToolException($"No scene at '{path}'. Use manage_scene action='list' to see scene assets.");
            bool additive = args.GetString("mode", "single") == "additive";
            if (!additive) EnsureCanReplace(args);
            var scene = EditorSceneManager.OpenScene(path, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
            return new { opened = scene.path, rootObjects = scene.rootCount, mode = additive ? "additive" : "single" };
        }

        static object Save(JObject args)
        {
            if (args.GetBool("all", false))
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    if (string.IsNullOrEmpty(SceneManager.GetSceneAt(i).path))
                        throw new McpToolException("An open scene has never been saved; save it individually with a 'path'.");
                }
                if (!EditorSceneManager.SaveOpenScenes()) throw new McpToolException("Saving the open scenes failed.");
                return new { saved = "all open scenes" };
            }

            var scene = SceneManager.GetActiveScene();
            var pathArg = args.GetString("path");
            if (!string.IsNullOrEmpty(pathArg))
            {
                var path = AssetPaths.Normalize(pathArg, ".unity");
                AssetPaths.EnsureParentFolder(path);
                if (!EditorSceneManager.SaveScene(scene, path)) throw new McpToolException($"Saving the scene to '{path}' failed.");
                return new { saved = path };
            }
            if (string.IsNullOrEmpty(scene.path))
                throw new McpToolException("The active scene has never been saved; pass 'path', e.g. 'Assets/Scenes/MyScene.unity'.");
            if (!EditorSceneManager.SaveScene(scene)) throw new McpToolException("Saving the scene failed.");
            return new { saved = scene.path };
        }

        static object New(JObject args)
        {
            bool additive = args.GetString("mode", "single") == "additive";
            if (!additive) EnsureCanReplace(args);
            var setup = args.GetString("setup", "default") == "empty" ? NewSceneSetup.EmptyScene : NewSceneSetup.DefaultGameObjects;
            var scene = EditorSceneManager.NewScene(setup, additive ? NewSceneMode.Additive : NewSceneMode.Single);
            var pathArg = args.GetString("path");
            if (!string.IsNullOrEmpty(pathArg))
            {
                var path = AssetPaths.Normalize(pathArg, ".unity");
                AssetPaths.EnsureParentFolder(path);
                if (!EditorSceneManager.SaveScene(scene, path)) throw new McpToolException($"Saving the new scene to '{path}' failed.");
            }
            return new { created = string.IsNullOrEmpty(scene.path) ? "Untitled (not saved yet)" : scene.path, rootObjects = scene.rootCount };
        }

        static Scene FindOpenScene(string pathOrName)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.path == pathOrName || s.name == pathOrName || s.path == AssetPaths.Normalize(pathOrName, ".unity"))
                    return s;
            }
            throw new McpToolException($"'{pathOrName}' is not an open scene.");
        }
    }
}
