using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace McpUnity.Tools
{
    internal sealed class GetHierarchyTool : McpTool
    {
        public override string Name => "get_hierarchy";
        public override string Description =>
            "Get the GameObject tree of the open scenes (or of one object's subtree). Each node has name and id; " +
            "'childCount' marks nodes whose children were not expanded because of maxDepth/maxObjects.";
        public override bool ReadOnly => true;

        public override JObject InputSchema => Schema.Object()
            .Str("root", "Only return this GameObject's subtree (id, path or name).")
            .Int("maxDepth", "How many levels below the roots to expand (default 4).")
            .Bool("includeComponents", "List component types on each node (default false).")
            .Bool("includeInactive", "Include inactive GameObjects (default true).")
            .Int("maxObjects", "Stop after this many nodes (default 300, max 5000).");

        sealed class Options
        {
            public int MaxDepth;
            public bool Components;
            public bool Inactive;
            public int Remaining;
            public bool Truncated;
        }

        protected override object Execute(JObject args, ToolContext context)
        {
            var options = new Options
            {
                MaxDepth = Mathf.Clamp(args.GetInt("maxDepth", 4), 0, 100),
                Components = args.GetBool("includeComponents", false),
                Inactive = args.GetBool("includeInactive", true),
                Remaining = Mathf.Clamp(args.GetInt("maxObjects", 300), 1, 5000),
            };

            var result = new JObject();
            if (args.Has("root"))
            {
                var root = SceneQuery.Resolve(args.Arg("root"));
                result["root"] = Node(root, 0, options);
            }
            else
            {
                var scenes = new JArray();
                foreach (var scene in SceneQuery.GetScenes())
                {
                    var objects = new JArray();
                    foreach (var go in scene.GetRootGameObjects())
                    {
                        if (!options.Inactive && !go.activeSelf) continue;
                        var node = Node(go, 0, options);
                        if (node == null) break;
                        objects.Add(node);
                    }
                    var info = new JObject { ["name"] = scene.name, ["path"] = scene.path };
                    if (scene.isDirty) info["isDirty"] = true;
                    info["objects"] = objects;
                    scenes.Add(info);
                }
                result["scenes"] = scenes;
            }
            if (options.Truncated)
                result["truncated"] = "Output limited by maxObjects; use 'root' or a smaller maxDepth to drill down.";
            return result;
        }

        static JObject Node(GameObject go, int depth, Options options)
        {
            if (options.Remaining <= 0)
            {
                options.Truncated = true;
                return null;
            }
            options.Remaining--;

            var node = new JObject { ["name"] = go.name, ["id"] = Ids.Of(go) };
            if (!go.activeSelf) node["active"] = false;
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
                node["prefab"] = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            if (options.Components)
            {
                node["components"] = new JArray(go.GetComponents<Component>()
                    .Where(c => !(c is Transform))
                    .Select(c => c ? c.GetType().Name : "(Missing Script)"));
            }

            var t = go.transform;
            if (t.childCount == 0) return node;
            if (depth >= options.MaxDepth)
            {
                node["childCount"] = t.childCount;
                return node;
            }
            var children = new JArray();
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i).gameObject;
                if (!options.Inactive && !child.activeSelf) continue;
                var childNode = Node(child, depth + 1, options);
                if (childNode == null)
                {
                    node["childCount"] = t.childCount;
                    break;
                }
                children.Add(childNode);
            }
            if (children.Count > 0) node["children"] = children;
            return node;
        }
    }

    internal sealed class FindGameObjectsTool : McpTool
    {
        public override string Name => "find_gameobjects";
        public override string Description =>
            "Search GameObjects in the open scenes by name (substring, or wildcards * and ?), tag, layer and/or component type. Returns name, id and hierarchy path.";
        public override bool ReadOnly => true;

        public override JObject InputSchema => Schema.Object()
            .Str("name", "Name filter: case-insensitive substring, or a wildcard pattern like 'Enemy*'.")
            .Str("tag", "Only objects with this tag.")
            .Str("layer", "Only objects on this layer (name or index).")
            .Str("component", "Only objects that have this component type, e.g. 'Rigidbody', 'Camera', 'PlayerController'.")
            .Bool("includeInactive", "Include inactive objects (default true).")
            .Int("limit", "Maximum results (default 50, max 1000).");

        protected override object Execute(JObject args, ToolContext context)
        {
            var name = args.GetString("name");
            var tag = args.GetString("tag");
            int? layer = args.Has("layer") ? GameObjectInfo.ParseLayer(args.Arg("layer")) : (int?)null;
            Type componentType = args.Has("component") ? TypeResolver.FindComponentType(args.GetString("component")) : null;
            bool inactive = args.GetBool("includeInactive", true);
            int limit = Mathf.Clamp(args.GetInt("limit", 50), 1, 1000);

            Func<string, bool> nameMatch = null;
            if (!string.IsNullOrEmpty(name))
            {
                if (name.IndexOfAny(new[] { '*', '?' }) >= 0)
                {
                    var regex = new Regex("^" + Regex.Escape(name).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
                    nameMatch = regex.IsMatch;
                }
                else
                {
                    nameMatch = n => n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }

            var results = new JArray();
            int total = 0;
            foreach (var go in SceneQuery.AllGameObjects(inactive))
            {
                if (nameMatch != null && !nameMatch(go.name)) continue;
                if (!string.IsNullOrEmpty(tag) && !go.CompareTag(tag)) continue;
                if (layer.HasValue && go.layer != layer.Value) continue;
                if (componentType != null && go.GetComponent(componentType) == null) continue;
                total++;
                if (results.Count < limit)
                {
                    var j = GameObjectInfo.Brief(go);
                    if (!go.activeInHierarchy) j["active"] = false;
                    results.Add(j);
                }
            }
            return new { total, returned = results.Count, results };
        }
    }

    internal sealed class GetGameObjectTool : McpTool
    {
        public override string Name => "get_gameobject";
        public override string Description =>
            "Details of one GameObject: active state, tag, layer, world and local transform, component list, parent, children and prefab link. " +
            "Use get_properties to read a component's fields.";
        public override bool ReadOnly => true;

        public override JObject InputSchema => Schema.Object()
            .Str("target", "GameObject: instance id, hierarchy path ('Parent/Child') or unique name.", required: true);

        protected override object Execute(JObject args, ToolContext context)
        {
            return GameObjectInfo.Full(SceneQuery.Resolve(args.Require("target")));
        }
    }

    internal sealed class CreateGameObjectTool : McpTool
    {
        static readonly string[] s_Primitives = { "Empty", "Cube", "Sphere", "Capsule", "Cylinder", "Plane", "Quad" };

        public override string Name => "create_gameobject";
        public override string Description =>
            "Create a GameObject in the active scene (or the open prefab stage): an empty object, a primitive, or an instance of a prefab asset. " +
            "Optionally set parent, transform, tag, layer and add components with initial property values.";

        public override JObject InputSchema => Schema.Object()
            .Str("name", "Name of the new object (defaults to the primitive or prefab name).")
            .Enum("primitive", "Primitive mesh to create (default Empty). Ignored when 'prefab' is set.", s_Primitives)
            .Str("prefab", "Asset path of a prefab to instantiate, e.g. 'Assets/Prefabs/Enemy.prefab'. Keeps the prefab link.")
            .Str("parent", "Parent GameObject (id, path or name). Omit for a root object.")
            .Prop("position", Schema.Vector("[x, y, z]. Local to the parent unless worldSpace is true."))
            .Prop("rotation", Schema.Vector("Euler angles in degrees [x, y, z]."))
            .Prop("scale", Schema.Vector("Local scale [x, y, z] (a single number scales uniformly)."))
            .Bool("worldSpace", "Interpret position/rotation in world space (default false).")
            .Str("tag", "Tag (created if it does not exist).")
            .Str("layer", "Layer name or index.")
            .Prop("components", Schema.Array(Schema.Any("A component type name, or {\"type\": \"Rigidbody\", \"properties\": {\"mass\": 2}}."),
                "Components to add."));

        protected override object Execute(JObject args, ToolContext context)
        {
            GameObject parent = args.Has("parent") ? SceneQuery.Resolve(args.Arg("parent")) : null;
            if (parent == null)
            {
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null) parent = stage.prefabContentsRoot;
            }

            GameObject go;
            var prefabPath = args.GetString("prefab");
            if (!string.IsNullOrEmpty(prefabPath))
            {
                prefabPath = AssetPaths.Normalize(prefabPath);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                    throw new McpToolException($"No prefab at '{prefabPath}'. Use find_assets with filter 't:Prefab' to locate prefabs.");
                go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent != null ? parent.transform : null);
                if (go == null) throw new McpToolException($"Could not instantiate '{prefabPath}'.");
            }
            else
            {
                var primitive = args.GetString("primitive", "Empty");
                switch (primitive.Trim().ToLowerInvariant())
                {
                    case "":
                    case "empty": go = new GameObject("GameObject"); break;
                    case "cube": go = GameObject.CreatePrimitive(PrimitiveType.Cube); break;
                    case "sphere": go = GameObject.CreatePrimitive(PrimitiveType.Sphere); break;
                    case "capsule": go = GameObject.CreatePrimitive(PrimitiveType.Capsule); break;
                    case "cylinder": go = GameObject.CreatePrimitive(PrimitiveType.Cylinder); break;
                    case "plane": go = GameObject.CreatePrimitive(PrimitiveType.Plane); break;
                    case "quad": go = GameObject.CreatePrimitive(PrimitiveType.Quad); break;
                    default:
                        throw new McpToolException($"Unknown primitive '{primitive}'. Use one of: {string.Join(", ", s_Primitives)}.");
                }
                if (parent != null) go.transform.SetParent(parent.transform, false);
                else StageUtility.PlaceGameObjectInCurrentStage(go);
            }

            Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);

            try
            {
                var name = args.GetString("name");
                if (!string.IsNullOrEmpty(name)) go.name = name;
                GameObjectInfo.ApplyTransformArgs(go.transform, args, args.GetBool("worldSpace", false));
                var tag = args.GetString("tag");
                if (!string.IsNullOrEmpty(tag)) GameObjectInfo.SetTag(go, tag);
                if (args.Has("layer")) GameObjectInfo.SetLayer(go, args.Arg("layer"));

                var componentResults = new JArray();
                foreach (var spec in args.GetList("components"))
                {
                    string typeName;
                    JObject properties = null;
                    if (spec is JObject o)
                    {
                        typeName = (string)o["type"] ?? (string)o["component"];
                        properties = JsonArgs.Unwrap(o["properties"]) as JObject;
                    }
                    else
                    {
                        typeName = (string)spec;
                    }
                    var type = TypeResolver.FindComponentType(typeName);
                    // Reuse components the object already has (e.g. a primitive's MeshRenderer) so properties apply to them.
                    var component = go.GetComponent(type);
                    if (component == null) component = Undo.AddComponent(go, type);
                    if (component == null)
                        throw new McpToolException($"Could not add component '{typeName}' (abstract type, or a conflicting component).");
                    var entry = new JObject { ["type"] = type.Name };
                    if (properties != null)
                    {
                        var applied = SerializedProps.Apply(component, properties);
                        entry["applied"] = applied["applied"];
                        if (applied["errors"] != null) entry["errors"] = applied["errors"];
                    }
                    componentResults.Add(entry);
                }

                GameObjectInfo.MarkSceneDirty(go.scene);
                var result = GameObjectInfo.Full(go, includeChildren: false);
                if (componentResults.Count > 0) result["addedComponents"] = componentResults;
                return result;
            }
            catch
            {
                // Don't leave a half-configured object behind.
                Undo.DestroyObjectImmediate(go);
                throw;
            }
        }
    }

    internal sealed class ModifyGameObjectTool : McpTool
    {
        public override string Name => "modify_gameobject";
        public override string Description =>
            "Change a GameObject's name, active state, tag, layer, static flag, parent, sibling order and/or transform. Only the given fields change.";

        public override JObject InputSchema => Schema.Object()
            .Str("target", "GameObject: instance id, hierarchy path or unique name.", required: true)
            .Str("name", "New name.")
            .Bool("active", "Set active/inactive.")
            .Str("tag", "Tag (created if it does not exist).")
            .Str("layer", "Layer name or index.")
            .Bool("isStatic", "Mark as static (all static flags) or not.")
            .Str("parent", "New parent (id, path or name); empty string or null moves it to the scene root. World position is kept.")
            .Int("siblingIndex", "Position among its siblings (0 = first).")
            .Prop("position", Schema.Vector("[x, y, z]. Local unless worldSpace is true."))
            .Prop("rotation", Schema.Vector("Euler angles [x, y, z] in degrees."))
            .Prop("scale", Schema.Vector("Local scale [x, y, z]."))
            .Bool("worldSpace", "Interpret position/rotation in world space (default false).");

        protected override object Execute(JObject args, ToolContext context)
        {
            var go = SceneQuery.Resolve(args.Require("target"));
            Undo.RecordObject(go, "Modify " + go.name);
            Undo.RecordObject(go.transform, "Modify " + go.name);

            var name = args.GetString("name");
            if (!string.IsNullOrEmpty(name)) go.name = name;
            if (args.Has("active")) go.SetActive(args.GetBool("active", go.activeSelf));
            var tag = args.GetString("tag");
            if (!string.IsNullOrEmpty(tag)) GameObjectInfo.SetTag(go, tag);
            if (args.Has("layer")) GameObjectInfo.SetLayer(go, args.Arg("layer"));
            if (args.Has("isStatic")) go.isStatic = args.GetBool("isStatic", go.isStatic);

            if (args.HasKey("parent"))
            {
                var parentArg = args.Arg("parent");
                Transform newParent = null;
                if (parentArg != null && !(parentArg.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)parentArg)))
                {
                    newParent = SceneQuery.Resolve(parentArg).transform;
                    if (newParent == go.transform || newParent.IsChildOf(go.transform))
                        throw new McpToolException("Cannot parent a GameObject to itself or to one of its children.");
                }
                if (go.transform.parent != newParent)
                    Undo.SetTransformParent(go.transform, newParent, "Reparent " + go.name);
            }
            if (args.Has("siblingIndex")) go.transform.SetSiblingIndex(args.GetInt("siblingIndex", 0));

            GameObjectInfo.ApplyTransformArgs(go.transform, args, args.GetBool("worldSpace", false));
            GameObjectInfo.MarkSceneDirty(go.scene);
            return GameObjectInfo.Full(go, includeChildren: false);
        }
    }

    internal sealed class DeleteGameObjectsTool : McpTool
    {
        public override string Name => "delete_gameobjects";
        public override string Description => "Delete one or more GameObjects (and their children). Undoable with the undo tool.";
        public override bool Destructive => true;

        public override JObject InputSchema => Schema.Object()
            .Prop("targets", Schema.Array(Schema.String(), "GameObjects to delete (id, path or name)."), required: true);

        protected override object Execute(JObject args, ToolContext context)
        {
            var targets = args.GetList("targets");
            if (targets.Count == 0 && args.Has("target")) targets.Add(args.Arg("target"));
            if (targets.Count == 0) throw new McpToolException("Pass the GameObjects to delete in 'targets'.");

            var deleted = new JArray();
            var errors = new JArray();
            foreach (var t in targets)
            {
                try
                {
                    var go = SceneQuery.Resolve(t);
                    var info = GameObjectInfo.Brief(go);
                    var scene = go.scene;
                    Undo.DestroyObjectImmediate(go);
                    if (go != null) throw new McpToolException("Unity refused to delete it (e.g. it is part of a prefab instance; unpack it or delete the instance root).");
                    GameObjectInfo.MarkSceneDirty(scene);
                    deleted.Add(info);
                }
                catch (Exception e)
                {
                    errors.Add($"{t}: {ToolDispatcher.FormatException(e)}");
                }
            }
            if (deleted.Count == 0) throw new McpToolException("Nothing was deleted. " + string.Join(" ", errors));
            var result = new JObject { ["deleted"] = deleted };
            if (errors.Count > 0) result["errors"] = errors;
            return result;
        }
    }

    internal sealed class DuplicateGameObjectTool : McpTool
    {
        public override string Name => "duplicate_gameobject";
        public override string Description =>
            "Duplicate a GameObject like Edit > Duplicate (keeps prefab links). Optionally make several copies, each offset from the previous one.";

        public override JObject InputSchema => Schema.Object()
            .Str("target", "GameObject to copy (id, path or name).", required: true)
            .Int("count", "Number of copies (default 1, max 100).")
            .Str("name", "Name for the copies (numbered when count > 1).")
            .Prop("offset", Schema.Vector("World-space offset [x, y, z] added per copy (copy i is moved by offset * i)."));

        protected override object Execute(JObject args, ToolContext context)
        {
            var source = SceneQuery.Resolve(args.Require("target"));
            int count = Mathf.Clamp(args.GetInt("count", 1), 1, 100);
            var offset = args.Has("offset") ? UnityValues.ToVector3(args.Arg("offset")) : Vector3.zero;
            var name = args.GetString("name");
            var previousSelection = Selection.objects;

            var created = new JArray();
            try
            {
                for (int i = 1; i <= count; i++)
                {
                    var copy = DuplicateOne(source);
                    if (copy.transform.parent == source.transform.parent)
                        copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + i);
                    if (!string.IsNullOrEmpty(name)) copy.name = count == 1 ? name : $"{name} ({i})";
                    if (offset != Vector3.zero) copy.transform.position = source.transform.position + offset * i;
                    created.Add(GameObjectInfo.Brief(copy));
                }
            }
            finally
            {
                Selection.objects = previousSelection;
            }
            GameObjectInfo.MarkSceneDirty(source.scene);
            return new { source = GameObjectInfo.Brief(source), created };
        }

        static GameObject DuplicateOne(GameObject source)
        {
            Selection.objects = new Object[] { source };
            Unsupported.DuplicateGameObjectsUsingPasteboard();
            var copy = Selection.activeGameObject;
            if (copy != null && copy != source) return copy;

            copy = Object.Instantiate(source, source.transform.parent);
            copy.name = source.name;
            Undo.RegisterCreatedObjectUndo(copy, "Duplicate " + source.name);
            return copy;
        }
    }
}
