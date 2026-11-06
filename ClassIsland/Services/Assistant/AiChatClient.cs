using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.Models.Assistant;

namespace ClassIsland.Services.Assistant;

/// <summary>
/// AI 返回的一次工具调用。
/// </summary>
public record AiToolCall(string Id, string Name, string Arguments);

/// <summary>
/// 一轮对话补全的结果。
/// </summary>
public record AiCompletionResult(string Content, IReadOnlyList<AiToolCall> ToolCalls, string? FinishReason);

/// <summary>
/// OpenAI 兼容（/chat/completions）接口的流式客户端。可接入 OpenAI、DeepSeek、通义千问、Kimi、智谱、Ollama 等。
/// </summary>
public class AiChatClient
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    /// <summary>
    /// 发起一次流式对话补全。
    /// </summary>
    /// <param name="settings">助手设置</param>
    /// <param name="messages">消息列表（OpenAI 格式）</param>
    /// <param name="tools">工具定义（OpenAI 格式），可为空</param>
    /// <param name="onContentDelta">收到文本增量时的回调</param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task<AiCompletionResult> CompleteStreamingAsync(AssistantSettings settings,
        IEnumerable<JsonNode> messages,
        JsonArray? tools,
        Action<string>? onContentDelta,
        CancellationToken cancellationToken)
    {
        var baseUrl = settings.EffectiveApiBaseUrl;
        var model = settings.EffectiveModel;
        if (settings.IsApiKeyMissing)
            throw new InvalidOperationException($"还没有填写 {settings.Template.Name} 的 API Key，请在助手窗口或【设置 → AI 助手】中粘贴 API Key。");
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("尚未配置 AI 接口地址，请在【设置 → AI 助手】中填写。");
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("尚未配置模型名称，请在【设置 → AI 助手】中填写。");

        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["temperature"] = settings.Temperature,
            ["messages"] = new JsonArray(messages.Select(x => x.DeepClone()).ToArray())
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = "auto";
        }

        var url = baseUrl.TrimEnd('/') + "/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey.Trim());

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, settings.RequestTimeoutSeconds)));

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            throw new HttpRequestException($"AI 接口返回错误 {(int)response.StatusCode} {response.ReasonPhrase}：{Truncate(ExtractErrorMessage(err), 500)}");
        }

        var content = new StringBuilder();
        var toolCalls = new SortedDictionary<int, (StringBuilder Id, StringBuilder Name, StringBuilder Args)>();
        string? finishReason = null;

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // 部分服务在 stream=true 时仍返回普通 JSON
        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonNode.Parse(await reader.ReadToEndAsync(timeoutCts.Token));
            var choice = json?["choices"]?[0];
            var message = choice?["message"];
            var text = message?["content"]?.GetValue<string>() ?? "";
            onContentDelta?.Invoke(text);
            var calls = message?["tool_calls"]?.AsArray()
                .Select(x => new AiToolCall(
                    x?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                    x?["function"]?["name"]?.GetValue<string>() ?? "",
                    x?["function"]?["arguments"]?.GetValue<string>() ?? "{}"))
                .ToList() ?? [];
            return new AiCompletionResult(text, calls, choice?["finish_reason"]?.GetValue<string>());
        }

        while (await reader.ReadLineAsync(timeoutCts.Token) is { } line)
        {
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var data = line[5..].Trim();
            if (data == "[DONE]")
                break;

            JsonNode? chunk;
            try
            {
                chunk = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            var choice = chunk?["choices"]?[0];
            if (choice == null) continue;
            if (choice["finish_reason"] is JsonValue fr && fr.TryGetValue<string>(out var frs))
                finishReason = frs;

            var delta = choice["delta"];
            if (delta == null) continue;

            if (delta["content"] is JsonValue cv && cv.TryGetValue<string>(out var piece) && piece.Length > 0)
            {
                content.Append(piece);
                onContentDelta?.Invoke(piece);
            }

            if (delta["tool_calls"] is JsonArray tcs)
            {
                foreach (var tc in tcs)
                {
                    if (tc == null) continue;
                    var index = tc["index"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : toolCalls.Count;
                    if (!toolCalls.TryGetValue(index, out var acc))
                    {
                        acc = (new StringBuilder(), new StringBuilder(), new StringBuilder());
                        toolCalls[index] = acc;
                    }
                    if (tc["id"] is JsonValue idv && idv.TryGetValue<string>(out var id))
                        acc.Id.Append(id);
                    var fn = tc["function"];
                    if (fn?["name"] is JsonValue nv && nv.TryGetValue<string>(out var name))
                        acc.Name.Append(name);
                    if (fn?["arguments"] is JsonValue av && av.TryGetValue<string>(out var args))
                        acc.Args.Append(args);
                }
            }
        }

        var result = toolCalls.Values
            .Where(x => x.Name.Length > 0)
            .Select(x => new AiToolCall(
                x.Id.Length > 0 ? x.Id.ToString() : "call_" + Guid.NewGuid().ToString("N"),
                x.Name.ToString(),
                x.Args.Length > 0 ? x.Args.ToString() : "{}"))
            .ToList();
        return new AiCompletionResult(content.ToString(), result, finishReason);
    }

    /// <summary>
    /// 测试连接，返回模型回复。
    /// </summary>
    public async Task<string> TestAsync(AssistantSettings settings, CancellationToken cancellationToken = default)
    {
        var r = await CompleteStreamingAsync(settings,
            [new JsonObject { ["role"] = "user", ["content"] = "请只回复“连接成功”四个字。" }],
            null, null, cancellationToken);
        return r.Content;
    }

    private static string ExtractErrorMessage(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            var msg = node?["error"]?["message"]?.GetValue<string>() ?? node?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(msg))
                return msg;
        }
        catch
        {
            // ignored
        }
        return raw;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
