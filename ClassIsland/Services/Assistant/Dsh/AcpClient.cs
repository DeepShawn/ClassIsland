using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services.Assistant.Dsh;

/// <summary>
/// Agent Client Protocol（ACP）客户端：启动 <c>dsh --profile acp</c> 子进程，并通过 stdio 上按行分隔的 JSON-RPC 2.0 与之通信。
/// </summary>
/// <remarks>协议说明：https://agentclientprotocol.com</remarks>
public sealed class AcpClient : IAsyncDisposable
{
    private const int ProtocolVersion = 1;

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private StreamWriter? _stdin;
    private long _nextId;

    /// <summary>收到 <c>session/update</c> 通知。参数为 (sessionId, update)。</summary>
    public event Action<string, JsonObject>? SessionUpdate;

    /// <summary>
    /// 处理 Agent 发起的 <c>session/request_permission</c> 请求，返回所选的 optionId；返回 null 表示取消。
    /// </summary>
    public Func<JsonObject, Task<string?>>? PermissionHandler { get; set; }

    /// <summary>子进程退出。</summary>
    public event EventHandler? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public AcpClient(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 启动 DSH 子进程并完成 <c>initialize</c> 握手。
    /// </summary>
    /// <param name="command">启动命令，例如 <c>npx -y @deepseek-ai/dsh</c> 或 <c>dsh</c></param>
    /// <param name="workingDirectory">子进程工作目录</param>
    /// <param name="environment">额外的环境变量</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task StartAsync(string command, string workingDirectory, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var fullCommand = command.Trim() + " --profile acp";
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", fullCommand } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", fullCommand } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.WorkingDirectory = workingDirectory;
        foreach (var (key, value) in environment)
            psi.Environment[key] = value;

        _logger.LogInformation("启动 DSH：{}", fullCommand);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += (_, _) => OnProcessExited();
        if (!process.Start())
            throw new InvalidOperationException("无法启动 DSH 进程。");
        _process = process;
        _stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _ = Task.Run(() => ReadStdoutLoopAsync(process));
        _ = Task.Run(() => ReadStderrLoopAsync(process));

        // 首次通过 npx 运行需要下载依赖，因此握手超时放宽到 3 分钟。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                ["terminal"] = false
            },
            ["clientInfo"] = new JsonObject { ["name"] = "ClassIsland", ["version"] = "2" }
        }, timeout.Token);
        _logger.LogInformation("DSH 已连接：{}", result?.ToJsonString());
    }

    /// <summary>
    /// 新建会话并返回 sessionId。
    /// </summary>
    public async Task<string> NewSessionAsync(string cwd, JsonArray mcpServers, CancellationToken cancellationToken)
    {
        var result = await RequestAsync("session/new", new JsonObject
        {
            ["cwd"] = cwd,
            ["mcpServers"] = mcpServers
        }, cancellationToken);
        return result?["sessionId"]?.GetValue<string>()
               ?? throw new InvalidOperationException("DSH 没有返回会话 ID。");
    }

    /// <summary>
    /// 发送一条文本提示词，等待本轮结束并返回 stopReason。回复内容通过 <see cref="SessionUpdate"/> 流式送达。
    /// </summary>
    public async Task<string> PromptAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() => _ = NotifyAsync("session/cancel",
            new JsonObject { ["sessionId"] = sessionId }));
        // 取消时 Agent 会以 stopReason=cancelled 结束本轮，因此这里不直接传入取消令牌。
        var result = await RequestAsync("session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
        }, CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        return result?["stopReason"]?.GetValue<string>() ?? "end_turn";
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonObject @params, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params
            });
            await using var _ = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, JsonObject @params) => WriteAsync(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = @params
    });

    private async Task WriteAsync(JsonObject message)
    {
        var stdin = _stdin ?? throw new InvalidOperationException("DSH 未运行。");
        var line = message.ToJsonString();
        await _writeLock.WaitAsync();
        try
        {
            await stdin.WriteLineAsync(line);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadStdoutLoopAsync(Process process)
    {
        try
        {
            var reader = process.StandardOutput;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (JsonException)
                {
                    _logger.LogDebug("DSH stdout（非 JSON）：{}", line);
                    continue;
                }
                if (message != null)
                    HandleMessage(message);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "读取 DSH 输出失败");
        }
    }

    private async Task ReadStderrLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                _logger.LogDebug("DSH：{}", line);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "读取 DSH 错误输出失败");
        }
    }

    private void HandleMessage(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        var idNode = message["id"];
        if (method == null)
        {
            // 对我们请求的响应
            if (idNode == null || !long.TryParse(idNode.ToString(), out var id) || !_pending.TryGetValue(id, out var tcs))
                return;
            if (message["error"] is JsonObject error)
                tcs.TrySetException(new InvalidOperationException(
                    $"DSH 返回错误：{error["message"]?.ToString() ?? error.ToJsonString()}"));
            else
                tcs.TrySetResult(message["result"]?.DeepClone());
            return;
        }

        var @params = message["params"] as JsonObject ?? new JsonObject();
        if (idNode == null)
        {
            if (method == "session/update" && @params["update"] is JsonObject update)
                SessionUpdate?.Invoke(@params["sessionId"]?.ToString() ?? "", update);
            return;
        }

        _ = HandleAgentRequestAsync(idNode.DeepClone(), method, @params);
    }

    private async Task HandleAgentRequestAsync(JsonNode id, string method, JsonObject @params)
    {
        JsonObject response;
        try
        {
            JsonNode result = method switch
            {
                "session/request_permission" => await HandlePermissionAsync(@params),
                _ => throw new NotSupportedException(method)
            };
            response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (NotSupportedException)
        {
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id,
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" }
            };
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "处理 DSH 请求 {} 失败", method);
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id,
                ["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.Message }
            };
        }

        try
        {
            await WriteAsync(response);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "回复 DSH 请求失败");
        }
    }

    private async Task<JsonNode> HandlePermissionAsync(JsonObject @params)
    {
        var optionId = PermissionHandler != null ? await PermissionHandler(@params) : null;
        return optionId == null
            ? new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } }
            : new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = optionId } };
    }

    private void OnProcessExited()
    {
        _logger.LogInformation("DSH 进程已退出");
        foreach (var tcs in _pending.Values)
            tcs.TrySetException(new InvalidOperationException("DSH 进程已退出。请检查是否已安装 Node.js，以及 DSH 启动命令是否正确。"));
        _pending.Clear();
        Exited?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        var process = _process;
        _process = null;
        if (process == null)
            return;
        try
        {
            if (_stdin != null)
                await _stdin.DisposeAsync();
        }
        catch
        {
            // 进程可能已经退出
        }
        try
        {
            if (!process.HasExited)
            {
                // 通过 cmd/sh 启动时，真正的 node 进程是子进程，需要结束整个进程树。
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "结束 DSH 进程失败");
        }
        process.Dispose();
    }
}
