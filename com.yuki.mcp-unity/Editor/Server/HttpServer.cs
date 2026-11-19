using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace McpUnity.Server
{
    internal sealed class HttpRequest
    {
        public string Method;
        public string Path;
        public string Query;
        public string Version;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();

        public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

        public string BodyText => Encoding.UTF8.GetString(Body);
    }

    internal sealed class HttpResponse
    {
        public int Status = 200;
        public string ContentType;
        public byte[] Body = Array.Empty<byte>();
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static HttpResponse Json(int status, string json) => new HttpResponse
        {
            Status = status,
            ContentType = "application/json; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(json),
        };

        public static HttpResponse Text(int status, string text) => new HttpResponse
        {
            Status = status,
            ContentType = "text/plain; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(text ?? string.Empty),
        };

        public static HttpResponse Empty(int status) => new HttpResponse { Status = status };

        public void WriteTo(Stream stream)
        {
            var head = new StringBuilder(256);
            head.Append("HTTP/1.1 ").Append(Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(Status)).Append("\r\n");
            if (ContentType != null) head.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            head.Append("Content-Length: ").Append(Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: close\r\n");
            foreach (var kv in Headers) head.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            head.Append("\r\n");

            var headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (Body.Length > 0) stream.Write(Body, 0, Body.Length);
            stream.Flush();
        }

        static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 413: return "Payload Too Large";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Status";
            }
        }
    }

    internal sealed class HttpException : Exception
    {
        public readonly int Status;
        public HttpException(int status, string message) : base(message) { Status = status; }
    }

    /// <summary>Minimal HTTP/1.1 request parser (Content-Length and chunked bodies).</summary>
    internal sealed class HttpReader
    {
        const int k_MaxHeaderBytes = 64 * 1024;
        const int k_MaxBodyBytes = 64 * 1024 * 1024;

        readonly Stream m_Stream;
        readonly byte[] m_Buffer = new byte[16 * 1024];
        int m_Pos;
        int m_Len;

        public HttpReader(Stream stream) { m_Stream = stream; }

        /// <summary>Returns null if the connection closed before a request arrived.</summary>
        public HttpRequest ReadRequest(Action sendContinue)
        {
            int budget = k_MaxHeaderBytes;
            string requestLine;
            do
            {
                requestLine = ReadLine(ref budget);
                if (requestLine == null) return null;
            } while (requestLine.Length == 0);

            var parts = requestLine.Split(' ');
            if (parts.Length < 3) throw new HttpException(400, "Malformed request line.");

            var request = new HttpRequest { Method = parts[0].ToUpperInvariant(), Version = parts[2] };
            var target = parts[1];
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                int slash = target.IndexOf('/', target.IndexOf("//", StringComparison.Ordinal) + 2);
                target = slash >= 0 ? target.Substring(slash) : "/";
            }
            int q = target.IndexOf('?');
            request.Path = q >= 0 ? target.Substring(0, q) : target;
            request.Query = q >= 0 ? target.Substring(q + 1) : string.Empty;

            while (true)
            {
                var line = ReadLine(ref budget);
                if (line == null) throw new IOException("Connection closed while reading headers.");
                if (line.Length == 0) break;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                request.Headers[name] = request.Headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
            }

            if (string.Equals(request.Header("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase))
                sendContinue?.Invoke();

            var transferEncoding = request.Header("Transfer-Encoding");
            if (transferEncoding != null && transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                request.Body = ReadChunked();
            }
            else if (request.Header("Content-Length") is string contentLength)
            {
                if (!long.TryParse(contentLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length < 0)
                    throw new HttpException(400, "Invalid Content-Length.");
                if (length > k_MaxBodyBytes) throw new HttpException(413, "Request body too large.");
                request.Body = new byte[length];
                ReadExactly(request.Body, 0, (int)length);
            }
            return request;
        }

        byte[] ReadChunked()
        {
            using (var body = new MemoryStream())
            {
                while (true)
                {
                    int budget = 1024;
                    var sizeLine = ReadLine(ref budget) ?? throw new IOException("Connection closed inside a chunked body.");
                    int semicolon = sizeLine.IndexOf(';');
                    if (semicolon >= 0) sizeLine = sizeLine.Substring(0, semicolon);
                    if (!int.TryParse(sizeLine.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                        throw new HttpException(400, "Invalid chunk size.");
                    if (size == 0)
                    {
                        int trailerBudget = k_MaxHeaderBytes;
                        while (!string.IsNullOrEmpty(ReadLine(ref trailerBudget))) { }
                        return body.ToArray();
                    }
                    if (body.Length + size > k_MaxBodyBytes) throw new HttpException(413, "Request body too large.");
                    var chunk = new byte[size];
                    ReadExactly(chunk, 0, size);
                    body.Write(chunk, 0, size);
                    int crlfBudget = 16;
                    ReadLine(ref crlfBudget);
                }
            }
        }

        int ReadByte()
        {
            if (m_Pos >= m_Len)
            {
                m_Len = m_Stream.Read(m_Buffer, 0, m_Buffer.Length);
                m_Pos = 0;
                if (m_Len <= 0) return -1;
            }
            return m_Buffer[m_Pos++];
        }

        string ReadLine(ref int budget)
        {
            var bytes = new List<byte>(128);
            while (true)
            {
                int b = ReadByte();
                if (b < 0)
                {
                    if (bytes.Count == 0) return null;
                    break;
                }
                if (--budget < 0) throw new HttpException(431, "Request headers too large.");
                if (b == '\n') break;
                bytes.Add((byte)b);
            }
            if (bytes.Count > 0 && bytes[bytes.Count - 1] == '\r') bytes.RemoveAt(bytes.Count - 1);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        void ReadExactly(byte[] destination, int offset, int count)
        {
            while (count > 0)
            {
                if (m_Pos < m_Len)
                {
                    int n = Math.Min(count, m_Len - m_Pos);
                    Buffer.BlockCopy(m_Buffer, m_Pos, destination, offset, n);
                    m_Pos += n;
                    offset += n;
                    count -= n;
                }
                else
                {
                    int n = m_Stream.Read(destination, offset, count);
                    if (n <= 0) throw new IOException("Connection closed while reading the request body.");
                    offset += n;
                    count -= n;
                }
            }
        }
    }

    /// <summary>
    /// Small thread-per-connection HTTP server bound to the loopback interface only.
    /// Implemented on TcpListener so it works without URL ACLs or admin rights.
    /// </summary>
    internal sealed class HttpServer
    {
        readonly int m_Port;
        readonly Func<HttpRequest, HttpResponse> m_Handler;
        readonly List<TcpListener> m_Listeners = new List<TcpListener>();
        readonly HashSet<TcpClient> m_Clients = new HashSet<TcpClient>();
        volatile bool m_Running;
        int m_Active;

        public HttpServer(int port, Func<HttpRequest, HttpResponse> handler)
        {
            m_Port = port;
            m_Handler = handler;
        }

        public int ActiveConnections => Volatile.Read(ref m_Active);

        public void Start()
        {
            var v4 = new TcpListener(IPAddress.Loopback, m_Port);
            v4.Start(); // throws SocketException if the port is taken
            m_Listeners.Add(v4);

            // Also listen on [::1] so "localhost" works when it resolves to IPv6. Optional.
            try
            {
                if (Socket.OSSupportsIPv6)
                {
                    var v6 = new TcpListener(IPAddress.IPv6Loopback, m_Port);
                    v6.Start();
                    m_Listeners.Add(v6);
                }
            }
            catch (Exception)
            {
                // IPv6 loopback unavailable or taken; IPv4 is enough.
            }

            m_Running = true;
            foreach (var listener in m_Listeners)
            {
                var thread = new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "MCP HTTP Accept" };
                thread.Start();
            }
        }

        public void Stop(int drainTimeoutMs)
        {
            m_Running = false;
            foreach (var listener in m_Listeners)
            {
                try { listener.Stop(); }
                catch (Exception) { /* ignore */ }
            }
            m_Listeners.Clear();

            // Let connections that already have their answer finish writing it.
            var stopwatch = Stopwatch.StartNew();
            while (ActiveConnections > 0 && stopwatch.ElapsedMilliseconds < drainTimeoutMs)
                Thread.Sleep(5);

            lock (m_Clients)
            {
                foreach (var client in m_Clients)
                {
                    try { client.Close(); }
                    catch (Exception) { /* ignore */ }
                }
                m_Clients.Clear();
            }
        }

        void AcceptLoop(TcpListener listener)
        {
            while (m_Running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (!m_Running) break;
                    Thread.Sleep(50);
                    continue;
                }

                if (!m_Running)
                {
                    try { client.Close(); }
                    catch (Exception) { /* ignore */ }
                    break;
                }

                Interlocked.Increment(ref m_Active);
                var thread = new Thread(() => HandleClient(client)) { IsBackground = true, Name = "MCP HTTP Connection" };
                thread.Start();
            }
        }

        void HandleClient(TcpClient client)
        {
            lock (m_Clients) m_Clients.Add(client);
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                var stream = client.GetStream();

                HttpResponse response;
                try
                {
                    var request = new HttpReader(stream).ReadRequest(() => WriteContinue(stream));
                    if (request == null) return;
                    response = m_Handler(request);
                }
                catch (HttpException e)
                {
                    response = HttpResponse.Text(e.Status, e.Message);
                }
                catch (IOException)
                {
                    return; // client went away or timed out
                }
                catch (Exception e)
                {
                    response = HttpResponse.Text(500, "Internal server error: " + e.Message);
                }

                response.WriteTo(stream);

                // Graceful close: let the client close first (avoids RST truncating the response).
                try
                {
                    client.ReceiveTimeout = 2000;
                    var scratch = new byte[256];
                    while (stream.Read(scratch, 0, scratch.Length) > 0) { }
                }
                catch (Exception)
                {
                    // timeout or reset: fine
                }
            }
            catch (Exception e)
            {
                if (m_Running && McpSettings.VerboseLogging)
                    Debug.Log("[MCP] Connection error: " + e.Message);
            }
            finally
            {
                lock (m_Clients) m_Clients.Remove(client);
                try { client.Close(); }
                catch (Exception) { /* ignore */ }
                Interlocked.Decrement(ref m_Active);
            }
        }

        static void WriteContinue(Stream stream)
        {
            var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }
}
