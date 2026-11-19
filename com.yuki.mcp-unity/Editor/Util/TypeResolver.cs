using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace McpUnity
{
    /// <summary>Finds types by short or full name.</summary>
    internal static class TypeResolver
    {
        static Dictionary<string, Type> s_ObjectTypesByName;

        public static Type FindComponentType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new McpToolException("A component type name is required.");
            name = name.Trim();

            var candidates = TypeCache.GetTypesDerivedFrom<Component>();
            var matches = new List<Type>();
            foreach (var t in candidates)
            {
                if (t.FullName == name || t.AssemblyQualifiedName == name) return t;
                if (t.Name == name) matches.Add(t);
            }
            if (matches.Count == 0)
            {
                foreach (var t in candidates)
                    if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase))
                        matches.Add(t);
            }

            if (matches.Count > 1)
            {
                // Prefer concrete, non-generic types, then UnityEngine's own.
                var concrete = matches.Where(t => !t.IsAbstract && !t.IsGenericType).ToList();
                if (concrete.Count == 1) return concrete[0];
                var unity = concrete.Where(t => t.Namespace != null && t.Namespace.StartsWith("UnityEngine")).ToList();
                if (unity.Count == 1) return unity[0];
                throw new McpToolException($"Component type '{name}' is ambiguous: {string.Join(", ", matches.Select(t => t.FullName))}. Use the full name.");
            }
            if (matches.Count == 1) return matches[0];

            var suggestions = candidates
                .Where(t => t.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(t => t.Name)
                .Distinct()
                .Take(10)
                .ToList();
            var hint = suggestions.Count > 0 ? " Similar: " + string.Join(", ", suggestions) + "." : string.Empty;
            throw new McpToolException($"Component type '{name}' not found.{hint} " +
                                       "If it is a script you just created, call refresh_assets first so Unity compiles it.");
        }

        /// <summary>UnityEngine.Object-derived type by short name (used for PPtr&lt;$Type&gt; property types).</summary>
        public static Type FindObjectType(string name)
        {
            if (s_ObjectTypesByName == null)
            {
                var map = new Dictionary<string, Type>(StringComparer.Ordinal);
                foreach (var t in TypeCache.GetTypesDerivedFrom<Object>())
                {
                    if (!map.TryGetValue(t.Name, out var existing) ||
                        (existing.Namespace == null || !existing.Namespace.StartsWith("UnityEngine")) &&
                        t.Namespace != null && t.Namespace.StartsWith("UnityEngine"))
                        map[t.Name] = t;
                }
                map["Object"] = typeof(Object);
                s_ObjectTypesByName = map;
            }
            return s_ObjectTypesByName.TryGetValue(name, out var type) ? type : null;
        }

        /// <summary>Any loaded type, by full name or (if unique) short name.</summary>
        public static Type FindType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new McpToolException("A type name is required.");
            name = name.Trim();
            var assemblies = LoadedAssemblies();
            foreach (var assembly in assemblies)
            {
                var t = assembly.GetType(name, false);
                if (t != null) return t;
            }

            var matches = new List<Type>();
            foreach (var assembly in assemblies)
            {
                if (assembly.IsDynamic) continue;
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                catch (Exception) { continue; }
                foreach (var t in types)
                    if (t.Name == name || t.FullName == name)
                        matches.Add(t);
            }
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
            {
                // Prefer project code over engine/editor/system types.
                var project = matches.Where(t => IsProjectAssembly(t.Assembly)).ToList();
                if (project.Count == 1) return project[0];
                throw new McpToolException($"Type '{name}' is ambiguous: {string.Join(", ", matches.Take(10).Select(t => $"{t.FullName} ({t.Assembly.GetName().Name})"))}. Use the full name.");
            }
            throw new McpToolException($"Type '{name}' not found. Use the full name including the namespace, and make sure the script compiled (refresh_assets).");
        }

        static List<Assembly> LoadedAssemblies()
        {
#if UNITY_6000_6_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies().ToList();
#else
            return AppDomain.CurrentDomain.GetAssemblies().ToList();
#endif
        }

        static bool IsProjectAssembly(Assembly assembly)
        {
            var n = assembly.GetName().Name;
            return !(n.StartsWith("Unity") || n.StartsWith("System") || n.StartsWith("Mono") ||
                     n == "mscorlib" || n == "netstandard" || n.StartsWith("Microsoft") || n.StartsWith("nunit"));
        }
    }
}
