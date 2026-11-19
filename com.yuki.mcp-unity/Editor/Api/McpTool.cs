using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace McpUnity
{
    /// <summary>
    /// Base class for a tool exposed to MCP clients.
    /// Any non-abstract subclass with a public parameterless constructor, in any Editor assembly,
    /// is registered automatically when the server starts (so projects can add their own tools).
    /// </summary>
    public abstract class McpTool
    {
        /// <summary>Unique tool name (snake_case recommended).</summary>
        public abstract string Name { get; }

        /// <summary>Optional human-readable title.</summary>
        public virtual string Title => null;

        /// <summary>Description shown to the AI model: what the tool does and when to use it.</summary>
        public abstract string Description { get; }

        /// <summary>JSON Schema of the arguments. Build it with <see cref="Schema"/>.</summary>
        public virtual JObject InputSchema => Schema.Object();

        /// <summary>True if the tool never modifies scenes, assets or project settings.</summary>
        public virtual bool ReadOnly => false;

        /// <summary>True if the tool may delete or overwrite data.</summary>
        public virtual bool Destructive => false;

        /// <summary>True if repeating the call with the same arguments has no additional effect.</summary>
        public virtual bool Idempotent => false;

        /// <summary>
        /// Runs the tool. Always called on the Unity main thread, so Unity APIs can be used freely.
        /// Return a string, a <see cref="ToolResult"/>, or any object (it is serialized to JSON).
        /// Throw <see cref="McpToolException"/> to report a problem back to the model.
        /// </summary>
        public virtual Task<object> ExecuteAsync(JObject args, ToolContext context)
        {
            return Task.FromResult(Execute(args, context));
        }

        /// <summary>Synchronous variant of <see cref="ExecuteAsync"/>. Override one of the two.</summary>
        protected virtual object Execute(JObject args, ToolContext context)
        {
            throw new NotImplementedException($"Tool '{Name}' must override Execute or ExecuteAsync.");
        }
    }

    /// <summary>Per-call information passed to <see cref="McpTool.ExecuteAsync"/>.</summary>
    public sealed class ToolContext
    {
        internal ToolContext(string toolName, PendingCall call)
        {
            ToolName = toolName;
            Call = call;
        }

        public string ToolName { get; }

        internal PendingCall Call { get; }

        /// <summary>
        /// Sets the result that is returned to the client if Unity starts a domain reload
        /// (script recompilation, entering Play Mode) before this call finishes.
        /// </summary>
        public void SetDomainReloadResult(object result)
        {
            if (Call != null)
                Call.ReloadResult = ToolResults.ToCallResult(result);
        }
    }

    /// <summary>Explicit tool result with mixed text / image content.</summary>
    public sealed class ToolResult
    {
        internal JArray Content { get; } = new JArray();

        public bool IsError { get; set; }

        public ToolResult AddText(string text)
        {
            Content.Add(new JObject { ["type"] = "text", ["text"] = text ?? string.Empty });
            return this;
        }

        public ToolResult AddJson(object value) => AddText(Json.Serialize(value));

        public ToolResult AddImage(byte[] data, string mimeType = "image/png")
        {
            Content.Add(new JObject
            {
                ["type"] = "image",
                ["data"] = Convert.ToBase64String(data),
                ["mimeType"] = mimeType,
            });
            return this;
        }

        internal string TextOnly()
        {
            return string.Join("\n", Content.OfType<JObject>()
                .Where(c => (string)c["type"] == "text")
                .Select(c => (string)c["text"]));
        }
    }

    /// <summary>An error that is reported to the model as a tool error (isError: true).</summary>
    public class McpToolException : Exception
    {
        public McpToolException(string message) : base(message) { }
        public McpToolException(string message, Exception inner) : base(message, inner) { }
    }

    internal static class ToolResults
    {
        public static JObject ToCallResult(object value, bool isError = false)
        {
            if (value is ToolResult result)
            {
                var r = new JObject { ["content"] = result.Content.DeepClone() };
                if (result.IsError || isError) r["isError"] = true;
                return r;
            }

            string text;
            switch (value)
            {
                case null: text = "OK"; break;
                case string s: text = s; break;
                case JToken token: text = token.ToString(Formatting.None); break;
                default: text = Json.Serialize(value); break;
            }

            var call = new JObject
            {
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
            };
            if (isError) call["isError"] = true;
            return call;
        }

        public static JObject Error(string message) => ToCallResult(message, true);

        public static bool IsError(JObject callResult) => callResult != null && callResult.Value<bool?>("isError") == true;

        public static void AppendText(JObject callResult, string text)
        {
            if (callResult?["content"] is JArray content)
                content.Add(new JObject { ["type"] = "text", ["text"] = text });
        }
    }
}
