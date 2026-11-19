using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace McpUnity.Server
{
    /// <summary>
    /// MCP "Streamable HTTP" transport (stateless): every JSON-RPC request is a POST to /mcp and is answered
    /// with a single application/json response. No session id is issued, so domain reloads are transparent
    /// to clients. Runs on HTTP threads; tool execution is marshalled to the main thread.
    /// </summary>
    internal sealed class McpRequestHandler
    {
        static readonly string[] s_SupportedProtocolVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
        static readonly TimeSpan s_ToolTimeout = TimeSpan.FromMinutes(10);

        readonly ToolRegistry m_Registry;
        readonly string m_Instructions;
        readonly string m_HealthJson;

        /// <summary>Must be constructed on the main thread (reads Application.* values).</summary>
        public McpRequestHandler(ToolRegistry registry)
        {
            m_Registry = registry;
            var project = Application.productName;
            m_Instructions =
                $"You are connected to a live Unity Editor (project \"{project}\", Unity {Application.unityVersion}). " +
                "Call get_editor_state first to orient yourself. " +
                "GameObject 'target' arguments accept an instance id (the 'id' field in results), a hierarchy path such as 'Parent/Child', or a unique name. " +
                "After creating or editing C# scripts or other files on disk, call refresh_assets so Unity imports and compiles them, and fix any compiler errors it reports. " +
                "Each modifying tool call is recorded as one Undo step (see the undo tool). " +
                "Scene changes are not saved automatically: use manage_scene with action 'save' when done. " +
                "Changes made while in Play Mode are lost when Play Mode stops.";
            m_HealthJson = new JObject
            {
                ["name"] = "mcp-unity",
                ["version"] = McpServer.Version,
                ["status"] = "ok",
                ["project"] = project,
                ["unityVersion"] = Application.unityVersion,
                ["endpoint"] = "/mcp",
                ["tools"] = registry.Tools.Count,
            }.ToString(Formatting.None);
        }

        public HttpResponse Handle(HttpRequest request)
        {
            if (!IsLoopbackHost(request.Header("Host")))
                return HttpResponse.Text(403, "Forbidden: unexpected Host header.");
            var origin = request.Header("Origin");
            if (!string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
                return HttpResponse.Text(403, "Forbidden: unexpected Origin.");

            var path = request.Path.TrimEnd('/');
            if (path.Length == 0 || path == "/health")
            {
                return request.Method == "GET"
                    ? HttpResponse.Json(200, m_HealthJson)
                    : MethodNotAllowed("GET");
            }
            if (path != "/mcp")
                return HttpResponse.Json(404, "{\"error\":\"Not found. The MCP endpoint is /mcp\"}");

            switch (request.Method)
            {
                case "POST": return HandlePost(request);
                case "OPTIONS":
                    var options = HttpResponse.Empty(204);
                    options.Headers["Allow"] = "POST, OPTIONS";
                    return options;
                default:
                    // GET (server-initiated SSE stream) and DELETE (session end) are not offered: stateless server.
                    return MethodNotAllowed("POST");
            }
        }

        static HttpResponse MethodNotAllowed(string allow)
        {
            var response = HttpResponse.Text(405, "Method Not Allowed");
            response.Headers["Allow"] = allow;
            return response;
        }

        HttpResponse HandlePost(HttpRequest request)
        {
            JToken message;
            try
            {
                message = Json.Parse(request.BodyText);
            }
            catch (Exception e)
            {
                return HttpResponse.Json(400, RpcError(null, -32700, "Parse error: " + e.Message).ToString(Formatting.None));
            }

            if (message is JArray batch)
            {
                if (batch.Count == 0)
                    return HttpResponse.Json(400, RpcError(null, -32600, "Invalid Request: empty batch").ToString(Formatting.None));
                var responses = new JArray();
                foreach (var item in batch)
                {
                    var response = HandleMessage(item as JObject);
                    if (response != null) responses.Add(response);
                }
                return responses.Count == 0
                    ? HttpResponse.Empty(202)
                    : HttpResponse.Json(200, responses.ToString(Formatting.None));
            }

            if (message is JObject single)
            {
                var response = HandleMessage(single);
                return response == null
                    ? HttpResponse.Empty(202)
                    : HttpResponse.Json(200, response.ToString(Formatting.None));
            }

            return HttpResponse.Json(400, RpcError(null, -32600, "Invalid Request").ToString(Formatting.None));
        }

        /// <summary>Returns the JSON-RPC response, or null for notifications and client responses.</summary>
        JObject HandleMessage(JObject message)
        {
            if (message == null) return RpcError(null, -32600, "Invalid Request");

            var method = message["method"]?.Type == JTokenType.String ? (string)message["method"] : null;
            bool hasId = message.TryGetValue("id", out var id) && id.Type != JTokenType.Null;
            if (method == null) return null;  // a response to a server request (we never send any)
            if (!hasId) return null;          // notification (initialized, cancelled, ...)

            try
            {
                var parameters = message["params"] as JObject;
                JToken result;
                switch (method)
                {
                    case "initialize": result = Initialize(parameters); break;
                    case "ping": result = new JObject(); break;
                    case "tools/list": result = new JObject { ["tools"] = new JRaw(m_Registry.ListJson) }; break;
                    case "tools/call": result = CallTool(parameters); break;
                    case "resources/list": result = new JObject { ["resources"] = new JArray() }; break;
                    case "resources/templates/list": result = new JObject { ["resourceTemplates"] = new JArray() }; break;
                    case "prompts/list": result = new JObject { ["prompts"] = new JArray() }; break;
                    case "logging/setLevel": result = new JObject(); break;
                    default: return RpcError(id, -32601, "Method not found: " + method);
                }
                return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (RpcException e)
            {
                return RpcError(id, e.Code, e.Message);
            }
            catch (Exception e)
            {
                return RpcError(id, -32603, "Internal error: " + e.Message);
            }
        }

        JObject Initialize(JObject parameters)
        {
            var requested = parameters?["protocolVersion"]?.Type == JTokenType.String ? (string)parameters["protocolVersion"] : null;
            var version = s_SupportedProtocolVersions.Contains(requested) ? requested : s_SupportedProtocolVersions[0];
            return new JObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false },
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = "mcp-unity",
                    ["title"] = "MCP for Unity",
                    ["version"] = McpServer.Version,
                },
                ["instructions"] = m_Instructions,
            };
        }

        JObject CallTool(JObject parameters)
        {
            var name = parameters?["name"]?.Type == JTokenType.String ? (string)parameters["name"] : null;
            if (string.IsNullOrEmpty(name)) throw new RpcException(-32602, "Invalid params: missing tool name");
            var tool = m_Registry.Get(name);
            if (tool == null) throw new RpcException(-32602, "Unknown tool: " + name);

            var rawArgs = JsonArgs.Unwrap(parameters["arguments"]);
            var args = rawArgs as JObject ?? new JObject();
            return ToolDispatcher.InvokeBlocking(tool, args, s_ToolTimeout);
        }

        static JObject RpcError(JToken id, int code, string message) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
        };

        static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return true; // HTTP/1.0 clients
            host = host.Trim();
            string name;
            if (host.StartsWith("["))
            {
                int end = host.IndexOf(']');
                name = end > 0 ? host.Substring(1, end - 1) : host;
            }
            else
            {
                int colon = host.IndexOf(':');
                name = colon >= 0 ? host.Substring(0, colon) : host;
            }
            return IsLoopbackName(name);
        }

        static bool IsLoopbackOrigin(string origin)
        {
            return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && IsLoopbackName(uri.Host.Trim('[', ']'));
        }

        static bool IsLoopbackName(string name)
        {
            return string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   name == "127.0.0.1" || name == "::1";
        }

        sealed class RpcException : Exception
        {
            public readonly int Code;
            public RpcException(int code, string message) : base(message) { Code = code; }
        }
    }
}
