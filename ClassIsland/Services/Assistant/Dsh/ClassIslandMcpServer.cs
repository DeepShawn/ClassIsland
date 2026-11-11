using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services.Assistant.Dsh;

/// <summary>
/// 仅监听 127.0.0.1 的极简 MCP（Streamable HTTP）服务，把 ClassIsland 的助手工具提供给 DSH。
/// 每次启动生成随机令牌，只有携带该令牌的请求才会被处理。
/// </summary>
/// <remarks>
/// 使用 <see cref="TcpListener"/> 而不是 HttpListener：在 Windows 上 HttpListener 依赖 http.sys，
/// 非管理员用户注册 URL 前缀可能失败。
/// </remarks>
public sealed class ClassIslandMcpServer : IDisposable
{
    private const string ServerName = "classisland";
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    private readonly ILogger _logger;
    private readonly Func<IEnumerable<AssistantTool>> _toolsProvider;
    private readonly Func<string, JsonObject, CancellationToken, Task<string>> _invokeTool;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public string Url { get; private set; } = "";

    public ClassIslandMcpServer(ILogger logger,
        Func<IEnumerable<AssistantTool>> toolsProvider,
        Func<string, JsonObject, CancellationToken, Task<string>> invokeTool)
    {
        _logger = logger;
        _toolsProvider = toolsProvider;
        _invokeTool = invokeTool;
    }

    public void Start()
    {
        if (_listener != null)
            return;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://127.0.0.1:{port}/mcp";
        _logger.LogInformation("ClassIsland MCP 服务已启动：{}", Url);
        _ = AcceptLoopAsync(_listener, _cts.Token);
    }

    /// <summary>
    /// 生成传给 ACP <c>session/new</c> 的 MCP 服务器声明。
    /// </summary>
    public JsonObject ToAcpDeclaration() => new()
    {
        ["type"] = "http",
        ["name"] = ServerName,
        ["url"] = Url,
        ["headers"] = new JsonArray(new JsonObject
        {
            ["name"] = "Authorization",
            ["value"] = "Bearer " + Token
        })
    };

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "MCP 服务接受连接失败");
                continue;
            }
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            // 保持连接，按顺序处理同一连接上的多个请求。
            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await ReadRequestAsync(stream, cancellationToken);
                if (request == null)
                    return;
                var (status, contentType, body) = await HandleRequestAsync(request, cancellationToken);
                await WriteResponseAsync(stream, status, contentType, body, cancellationToken);
                if (request.Headers.TryGetValue("connection", out var conn) &&
                    conn.Equals("close", StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException)
        {
            // 连接被对方关闭
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "处理 MCP 请求失败");
        }
    }

    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>(1024);
        var buffer = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(buffer, cancellationToken);
            if (n == 0)
                return null;
            headerBytes.Add(buffer[0]);
            var c = headerBytes.Count;
            if (c >= 4 && headerBytes[c - 4] == '\r' && headerBytes[c - 3] == '\n' && headerBytes[c - 2] == '\r' && headerBytes[c - 1] == '\n')
                break;
            if (c > 64 * 1024)
                throw new IOException("请求头过长");
        }

        var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
            throw new IOException("无效的请求行");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx > 0)
                headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        var length = headers.TryGetValue("content-length", out var lengthText) && int.TryParse(lengthText, out var l) ? l : 0;
        if (length is < 0 or > MaxBodyBytes)
            throw new IOException("请求体过大");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return new HttpRequest(requestLine[0], requestLine[1], headers, body);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string? contentType, byte[] body,
        CancellationToken cancellationToken)
    {
        var reason = status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error"
        };
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {reason}\r\n");
        if (contentType != null)
            sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), cancellationToken);
        if (body.Length > 0)
            await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task<(int Status, string? ContentType, byte[] Body)> HandleRequestAsync(HttpRequest request,
        CancellationToken cancellationToken)
    {
        var path = request.Path.Split('?')[0];
        if (path != "/mcp")
            return (404, null, []);
        if (!request.Headers.TryGetValue("authorization", out var auth) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth), Encoding.UTF8.GetBytes("Bearer " + Token)))
            return (401, null, []);
        // 本服务不支持服务器主动推送（GET SSE 流），也不维护会话（DELETE）。
        if (request.Method != "POST")
            return (405, null, []);

        JsonNode? payload;
        try
        {
            payload = JsonNode.Parse(request.Body);
        }
        catch (JsonException)
        {
            return Json(400, Error(null, -32700, "Parse error"));
        }

        if (payload is JsonArray batch)
        {
            var responses = new JsonArray();
            foreach (var item in batch.OfType<JsonObject>())
            {
                if (await HandleRpcAsync(item, cancellationToken) is { } r)
                    responses.Add(r);
            }
            return responses.Count == 0 ? (202, null, []) : Json(200, responses);
        }

        if (payload is not JsonObject message)
            return Json(400, Error(null, -32600, "Invalid Request"));
        var response = await HandleRpcAsync(message, cancellationToken);
        return response == null ? (202, null, []) : Json(200, response);
    }

    private static (int, string?, byte[]) Json(int status, JsonNode node) =>
        (status, "application/json", Encoding.UTF8.GetBytes(node.ToJsonString()));

    private async Task<JsonObject?> HandleRpcAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var id = message["id"]?.DeepClone();
        var method = message["method"]?.GetValue<string>();
        if (method == null || id == null)
            return null; // 通知或响应，无需回复

        var @params = message["params"] as JsonObject ?? new JsonObject();
        try
        {
            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = @params["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "ClassIsland", ["version"] = "2" },
                    ["instructions"] = "ClassIsland 是用户桌面上的课表软件。这些工具可以查询今日课表、当前课程状态和天气，显示提醒，打开 ClassIsland 设置，以及执行常见桌面操作。"
                },
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray(_toolsProvider().Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["title"] = t.DisplayName,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.Parameters.DeepClone()
                    }).ToArray())
                },
                "tools/call" => await CallToolAsync(@params, cancellationToken),
                _ => throw new MissingMethodException(method)
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (MissingMethodException)
        {
            return Error(id, -32601, $"Method not found: {method}");
        }
        catch (Exception e)
        {
            return Error(id, -32603, e.Message);
        }
    }

    private async Task<JsonNode> CallToolAsync(JsonObject @params, CancellationToken cancellationToken)
    {
        var name = @params["name"]?.GetValue<string>() ?? "";
        var args = @params["arguments"] as JsonObject ?? new JsonObject();
        var isError = false;
        string text;
        try
        {
            text = await _invokeTool(name, (JsonObject)args.DeepClone(), cancellationToken);
        }
        catch (Exception e)
        {
            isError = true;
            text = "执行失败：" + e.Message;
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        };
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        _listener = null;
    }
}
