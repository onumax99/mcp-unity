using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    internal sealed class InvokeStaticMethodTool : McpTool
    {
        public override string Name => "invoke_static_method";
        public override string Description =>
            "Call a static C# method by reflection and return its result. Use it to run editor automation that has no dedicated tool: " +
            "write a static method in an Editor script (e.g. Assets/Editor/MyTools.cs), call refresh_assets, then invoke it. " +
            "Arguments are converted to the parameter types (numbers, strings, bools, enums by name, vectors [x,y,z], colors, asset paths or scene objects for UnityEngine.Object parameters). " +
            "Returned Tasks are awaited. Runs arbitrary code, so only call methods you understand.";

        public override JObject InputSchema => Schema.Object()
            .Str("typeName", "Type name, preferably with namespace, e.g. 'MyGame.Editor.LevelTools' or 'UnityEditor.AssetDatabase'.", required: true)
            .Str("methodName", "Static method name.", required: true)
            .Prop("arguments", Schema.Array(Schema.Any(), "Positional arguments (default none)."));

        public override async Task<object> ExecuteAsync(JObject args, ToolContext context)
        {
            var type = TypeResolver.FindType(args.RequireString("typeName"));
            var methodName = args.RequireString("methodName");
            var arguments = args.GetList("arguments");

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            var candidates = type.GetMethods(flags)
                .Where(m => m.Name == methodName && !m.IsGenericMethodDefinition)
                .OrderBy(m => Math.Abs(m.GetParameters().Length - arguments.Count))
                .ToList();
            if (candidates.Count == 0)
            {
                var names = type.GetMethods(flags).Select(m => m.Name).Distinct().OrderBy(n => n).Take(40);
                throw new McpToolException($"{type.FullName} has no static method '{methodName}'. Static methods: {string.Join(", ", names)}");
            }

            Exception lastError = null;
            foreach (var method in candidates)
            {
                var parameters = method.GetParameters();
                int required = parameters.Count(p => !p.HasDefaultValue);
                if (arguments.Count < required || arguments.Count > parameters.Length) continue;

                object[] values;
                try
                {
                    values = new object[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                        values[i] = i < arguments.Count
                            ? UnityValues.ConvertTo(arguments[i], parameters[i].ParameterType)
                            : parameters[i].DefaultValue;
                }
                catch (Exception e)
                {
                    lastError = e;
                    continue;
                }

                object result;
                try
                {
                    result = method.Invoke(null, values);
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    throw new McpToolException($"{method.Name} threw {e.InnerException.GetType().Name}: {e.InnerException.Message}\n{e.InnerException.StackTrace}");
                }

                if (result is Task task)
                {
                    await task;
                    var resultProperty = task.GetType().GetProperty("Result");
                    result = resultProperty != null && resultProperty.PropertyType.Name != "VoidTaskResult"
                        ? resultProperty.GetValue(task)
                        : null;
                }

                return new JObject
                {
                    ["method"] = $"{type.FullName}.{method.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})",
                    ["returnType"] = method.ReturnType.Name,
                    ["result"] = method.ReturnType == typeof(void) ? JValue.CreateNull() : SerializedProps.SafeJson(result),
                };
            }

            var signatures = string.Join("; ", candidates.Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})"));
            throw new McpToolException(lastError != null
                ? $"Could not convert the arguments: {ToolDispatcher.FormatException(lastError)} Overloads: {signatures}"
                : $"No overload of '{methodName}' takes {arguments.Count} argument(s). Overloads: {signatures}");
        }
    }

    internal sealed class BatchTool : McpTool
    {
        public override string Name => "batch";
        public override string Description =>
            "Run several tool calls in order in a single request (faster than separate calls; all changes form one undo step). " +
            "Each operation is {\"tool\": name, \"arguments\": {...}}. Images from tools like screenshot are omitted.";

        public override JObject InputSchema => Schema.Object()
            .Prop("operations", Schema.Array(
                Schema.Object()
                    .Str("tool", "Tool name, e.g. 'create_gameobject'.", required: true)
                    .Prop("arguments", Schema.Map("Arguments for that tool.")),
                "Operations to run in order."), required: true)
            .Bool("stopOnError", "Stop at the first failing operation (default true).");

        public override async Task<object> ExecuteAsync(JObject args, ToolContext context)
        {
            var operations = args.GetList("operations");
            if (operations.Count == 0) throw new McpToolException("Pass at least one operation.");
            bool stopOnError = args.GetBool("stopOnError", true);
            var registry = McpServer.Registry ?? throw new McpToolException("The tool registry is not available.");

            var results = new JArray();
            int succeeded = 0;
            foreach (var op in operations)
            {
                var operation = JsonArgs.Unwrap(op) as JObject;
                var toolName = (string)operation?["tool"] ?? (string)operation?["name"];
                var entry = new JObject { ["tool"] = toolName };
                try
                {
                    if (string.IsNullOrEmpty(toolName)) throw new McpToolException("Each operation needs a 'tool' name.");
                    if (toolName == Name) throw new McpToolException("batch cannot be nested.");
                    var tool = registry.Get(toolName) ?? throw new McpToolException($"Unknown tool '{toolName}'.");
                    var toolArgs = JsonArgs.Unwrap(operation["arguments"]) as JObject ?? new JObject();
                    var value = await tool.ExecuteAsync(toolArgs, context);
                    entry["ok"] = true;
                    entry["result"] = value is ToolResult toolResult ? (JToken)toolResult.TextOnly() : Json.ToToken(value);
                    succeeded++;
                }
                catch (Exception e)
                {
                    entry["ok"] = false;
                    entry["error"] = ToolDispatcher.FormatException(e);
                    results.Add(entry);
                    if (stopOnError) break;
                    continue;
                }
                results.Add(entry);
            }
            return new { succeeded, total = operations.Count, results };
        }
    }
}
