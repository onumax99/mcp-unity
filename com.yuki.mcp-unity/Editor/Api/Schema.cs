using Newtonsoft.Json.Linq;

namespace McpUnity
{
    /// <summary>Fluent builder for an "object" JSON Schema (tool arguments).</summary>
    public sealed class ObjectSchema
    {
        readonly JObject m_Properties = new JObject();
        readonly JArray m_Required = new JArray();
        string m_Description;

        public ObjectSchema Prop(string name, JObject schema, bool required = false)
        {
            m_Properties[name] = schema;
            if (required) m_Required.Add(name);
            return this;
        }

        public ObjectSchema Str(string name, string description, bool required = false) =>
            Prop(name, Schema.String(description), required);

        public ObjectSchema Int(string name, string description, bool required = false) =>
            Prop(name, Schema.Integer(description), required);

        public ObjectSchema Num(string name, string description, bool required = false) =>
            Prop(name, Schema.Number(description), required);

        public ObjectSchema Bool(string name, string description, bool required = false) =>
            Prop(name, Schema.Boolean(description), required);

        public ObjectSchema Enum(string name, string description, string[] values, bool required = false) =>
            Prop(name, Schema.Enum(description, values), required);

        public ObjectSchema Describe(string description)
        {
            m_Description = description;
            return this;
        }

        public JObject Build()
        {
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = m_Properties.DeepClone(),
            };
            if (m_Required.Count > 0) schema["required"] = m_Required.DeepClone();
            if (!string.IsNullOrEmpty(m_Description)) schema["description"] = m_Description;
            return schema;
        }

        public static implicit operator JObject(ObjectSchema schema) => schema?.Build();
    }

    /// <summary>Helpers for building JSON Schema fragments.</summary>
    public static class Schema
    {
        public static ObjectSchema Object() => new ObjectSchema();

        public static JObject String(string description = null) =>
            Describe(new JObject { ["type"] = "string" }, description);

        public static JObject Enum(string description, params string[] values) =>
            Describe(new JObject { ["type"] = "string", ["enum"] = new JArray(values) }, description);

        public static JObject Integer(string description = null) =>
            Describe(new JObject { ["type"] = "integer" }, description);

        public static JObject Number(string description = null) =>
            Describe(new JObject { ["type"] = "number" }, description);

        public static JObject Boolean(string description = null) =>
            Describe(new JObject { ["type"] = "boolean" }, description);

        public static JObject Array(JObject items, string description = null) =>
            Describe(new JObject { ["type"] = "array", ["items"] = items }, description);

        public static JObject StringArray(string description = null) => Array(String(), description);

        /// <summary>A vector given as an array of numbers, e.g. [x, y, z].</summary>
        public static JObject Vector(string description = null) =>
            Describe(new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" } }, description);

        /// <summary>An object with arbitrary keys.</summary>
        public static JObject Map(string description = null) =>
            Describe(new JObject { ["type"] = "object", ["additionalProperties"] = true }, description);

        /// <summary>Any JSON value.</summary>
        public static JObject Any(string description = null) => Describe(new JObject(), description);

        static JObject Describe(JObject schema, string description)
        {
            if (!string.IsNullOrEmpty(description)) schema["description"] = description;
            return schema;
        }
    }
}
