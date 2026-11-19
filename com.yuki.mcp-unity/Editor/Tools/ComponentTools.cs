using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace McpUnity.Tools
{
    internal static class ComponentLookup
    {
        public static Component Find(GameObject go, string typeName, int index)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                throw new McpToolException($"Specify 'component'. Components on '{go.name}': {List(go)}.");
            typeName = typeName.Trim();
            var components = go.GetComponents<Component>();
            var matches = components.Where(c => c != null &&
                                                (string.Equals(c.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(c.GetType().FullName, typeName, StringComparison.OrdinalIgnoreCase)))
                                    .ToList();
            if (matches.Count == 0)
            {
                Type type = null;
                try { type = TypeResolver.FindComponentType(typeName); }
                catch (McpToolException) { /* fall through */ }
                if (type != null) matches = components.Where(c => c != null && type.IsInstanceOfType(c)).ToList();
            }
            if (matches.Count == 0)
                throw new McpToolException($"'{go.name}' has no '{typeName}' component. Components: {List(go)}.");
            if (index < 0 || index >= matches.Count)
                throw new McpToolException($"'{go.name}' has {matches.Count} '{typeName}' component(s); componentIndex {index} is out of range.");
            return matches[index];
        }

        public static string List(GameObject go) =>
            string.Join(", ", go.GetComponents<Component>().Select(c => c ? c.GetType().Name : "(Missing Script)"));

        /// <summary>
        /// Resolves the object to read/write: a component on a scene object (target + component), the GameObject itself
        /// (component = "GameObject"), or an asset (assetPath, optionally + component for a prefab's root).
        /// </summary>
        public static Object ResolveTarget(JObject args)
        {
            var componentName = args.GetString("component");
            int index = args.GetInt("componentIndex", 0);

            if (args.Has("assetPath"))
            {
                var path = args.GetString("assetPath").Trim().Replace('\\', '/');
                var asset = ObjectRefs.Resolve(path, typeof(Object));
                if (string.IsNullOrEmpty(componentName)) return asset;
                if (asset is GameObject prefabRoot)
                {
                    return string.Equals(componentName, "GameObject", StringComparison.OrdinalIgnoreCase)
                        ? (Object)prefabRoot
                        : Find(prefabRoot, componentName, index);
                }
                throw new McpToolException($"'{path}' is a {asset.GetType().Name}; 'component' only applies to prefab assets.");
            }

            if (!args.Has("target"))
                throw new McpToolException("Pass 'target' (+ 'component') for a scene object, or 'assetPath' for an asset.");
            var go = SceneQuery.Resolve(args.Arg("target"));
            if (string.Equals(componentName, "GameObject", StringComparison.OrdinalIgnoreCase)) return go;
            return Find(go, componentName, index);
        }

        public static void MarkDirty(Object target)
        {
            if (EditorUtility.IsPersistent(target))
            {
                EditorUtility.SetDirty(target);
                AssetDatabase.SaveAssetIfDirty(target);
                return;
            }
            var go = target as GameObject ?? (target as Component)?.gameObject;
            if (go != null) GameObjectInfo.MarkSceneDirty(go.scene);
        }
    }

    internal sealed class AddComponentTool : McpTool
    {
        public override string Name => "add_component";
        public override string Description =>
            "Add a component (built-in like 'Rigidbody', 'BoxCollider', 'Light', 'AudioSource', or a project script by class name) to a GameObject, " +
            "optionally setting properties in the same call (same format as set_properties).";

        public override JObject InputSchema => Schema.Object()
            .Str("target", "GameObject (id, path or name).", required: true)
            .Str("component", "Component type name, e.g. 'Rigidbody' or 'MyGame.PlayerController'.", required: true)
            .Prop("properties", Schema.Map("Optional initial values, e.g. {\"mass\": 2, \"useGravity\": false}."));

        protected override object Execute(JObject args, ToolContext context)
        {
            var go = SceneQuery.Resolve(args.Require("target"));
            var type = TypeResolver.FindComponentType(args.RequireString("component"));
            if (type.IsAbstract) throw new McpToolException($"'{type.Name}' is abstract; choose a concrete type.");
            var component = Undo.AddComponent(go, type);
            if (component == null)
                throw new McpToolException($"Unity could not add '{type.Name}' to '{go.name}' (it may disallow multiple instances or conflict with an existing component). Components: {ComponentLookup.List(go)}.");

            var result = new JObject
            {
                ["gameObject"] = GameObjectInfo.Brief(go),
                ["component"] = type.Name,
                ["componentIndex"] = go.GetComponents(type).ToList().IndexOf(component),
            };
            var properties = args.GetObject("properties");
            if (properties != null && properties.Count > 0)
            {
                var applied = SerializedProps.Apply(component, properties);
                result["applied"] = applied["applied"];
                if (applied["errors"] != null) result["errors"] = applied["errors"];
            }
            ComponentLookup.MarkDirty(go);
            return result;
        }
    }

    internal sealed class RemoveComponentTool : McpTool
    {
        public override string Name => "remove_component";
        public override string Description => "Remove a component from a GameObject.";
        public override bool Destructive => true;

        public override JObject InputSchema => Schema.Object()
            .Str("target", "GameObject (id, path or name).", required: true)
            .Str("component", "Component type name.", required: true)
            .Int("componentIndex", "Which one, if the GameObject has several of this type (default 0).");

        protected override object Execute(JObject args, ToolContext context)
        {
            var go = SceneQuery.Resolve(args.Require("target"));
            var component = ComponentLookup.Find(go, args.RequireString("component"), args.GetInt("componentIndex", 0));
            if (component is Transform) throw new McpToolException("The Transform component cannot be removed.");
            var typeName = component.GetType().Name;
            Undo.DestroyObjectImmediate(component);
            if (component != null)
                throw new McpToolException($"Unity refused to remove '{typeName}' (another component may require it).");
            ComponentLookup.MarkDirty(go);
            return new { gameObject = GameObjectInfo.Brief(go), removed = typeName, remaining = ComponentLookup.List(go) };
        }
    }

    internal sealed class GetPropertiesTool : McpTool
    {
        public override string Name => "get_properties";
        public override string Description =>
            "Read the serialized fields (as shown in the Inspector) of a component on a scene object, of a GameObject itself (component='GameObject'), " +
            "or of an asset such as a ScriptableObject, material or ProjectSettings file (assetPath). " +
            "Object references are shown as {type, name, path} for assets or {type, name, sceneObject, id} for scene objects; rotations as Euler angles.";
        public override bool ReadOnly => true;

        public override JObject InputSchema => Schema.Object()
            .Str("target", "Scene GameObject (id, path or name). Use with 'component'.")
            .Str("component", "Component type on the target, e.g. 'Transform', 'Rigidbody', 'PlayerController', or 'GameObject'.")
            .Int("componentIndex", "Which one, if there are several of this type (default 0).")
            .Str("assetPath", "Instead of a scene object: an asset path, e.g. 'Assets/Data/Settings.asset' or 'ProjectSettings/TagManager.asset'. For a prefab, add 'component' to read a component on its root.")
            .Prop("properties", Schema.StringArray("Only these properties (names or paths like 'm_LocalPosition' or 'speed'). Default: all visible."))
            .Int("maxDepth", "Nesting depth for structs/lists (default 4).")
            .Int("maxArrayElements", "Maximum array elements shown (default 20).");

        protected override object Execute(JObject args, ToolContext context)
        {
            var target = ComponentLookup.ResolveTarget(args);
            int maxDepth = Mathf.Clamp(args.GetInt("maxDepth", 4), 0, 20);
            int maxArray = Mathf.Clamp(args.GetInt("maxArrayElements", 20), 1, 1000);
            var so = new SerializedObject(target);

            JObject properties;
            var only = args.GetStringList("properties");
            if (only.Count > 0)
            {
                properties = new JObject();
                foreach (var name in only)
                {
                    var p = SerializedProps.Find(so, name);
                    properties[name] = p != null ? SerializedProps.Read(p, 0, maxDepth, maxArray) : (JToken)"(not found)";
                }
            }
            else
            {
                properties = SerializedProps.ReadAll(so, maxDepth, maxArray);
            }
            return new { target = ObjectRefs.Describe(target), properties };
        }
    }

    internal sealed class SetPropertiesTool : McpTool
    {
        public override string Name => "set_properties";
        public override string Description =>
            "Set serialized fields on a component, GameObject or asset (same addressing as get_properties). " +
            "Keys are property names or paths: 'speed', 'm_Mass' (or just 'mass'), 'm_LocalPosition', 'm_Materials[0]', 'settings.maxHealth'. " +
            "Values: numbers, booleans, strings, enums by name, vectors [x,y,z], colors [r,g,b,a] or '#RRGGBB', arrays as JSON arrays, " +
            "structs as objects, and object references as an asset path ('Assets/Materials/Red.mat', 'Assets/Model.fbx::MeshName'), " +
            "a scene object (id, path or name; adapted to the needed component type) or null. " +
            "Keys that are not serialized fall back to public C# properties (e.g. 'sharedMaterial', 'enabled'). Returns the values as read back.";

        public override JObject InputSchema => Schema.Object()
            .Str("target", "Scene GameObject (id, path or name). Use with 'component'.")
            .Str("component", "Component type on the target, or 'GameObject'.")
            .Int("componentIndex", "Which one, if there are several of this type (default 0).")
            .Str("assetPath", "Instead of a scene object: an asset path (ScriptableObject, material, ProjectSettings/*.asset, or a prefab with 'component').")
            .Prop("properties", Schema.Map("Property path -> new value."), required: true);

        protected override object Execute(JObject args, ToolContext context)
        {
            var target = ComponentLookup.ResolveTarget(args);
            var properties = args.GetObject("properties");
            if (properties == null || properties.Count == 0) throw new McpToolException("Pass at least one entry in 'properties'.");

            var result = SerializedProps.Apply(target, properties);
            ComponentLookup.MarkDirty(target);
            if (((JObject)result["applied"]).Count == 0)
                throw new McpToolException("No property was set. " + string.Join(" ", result["errors"] ?? new JArray()));
            result["target"] = ObjectRefs.Describe(target);
            return result;
        }
    }
}
