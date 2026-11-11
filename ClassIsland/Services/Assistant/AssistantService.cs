using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Abstractions.Services.SpeechService;
using ClassIsland.Models.Actions;
using ClassIsland.Models.Assistant;
using ClassIsland.Services.Assistant.Dsh;
using ClassIsland.Shared;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services.Assistant;

/// <summary>
/// 对话消息角色。
/// </summary>
public enum AssistantMessageRole
{
    User,
    Assistant,
    Tool,
    Error
}

/// <summary>
/// 显示在助手界面中的一条消息。
/// </summary>
public partial class AssistantChatMessage : ObservableObject
{
    [ObservableProperty] private AssistantMessageRole _role;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private DateTime _time = DateTime.Now;

    public bool IsUser => Role == AssistantMessageRole.User;
    public bool IsAssistant => Role == AssistantMessageRole.Assistant;
    public bool IsTool => Role == AssistantMessageRole.Tool;
    public bool IsError => Role == AssistantMessageRole.Error;
}

/// <summary>
/// 助手当前在做什么，桌面宠物据此切换动作。
/// </summary>
public enum AssistantActivity
{
    Idle,
    Listening,
    Thinking,
    Working,
    Replying,
    Error
}

/// <summary>
/// 一个可被 AI 调用的工具。
/// </summary>
/// <param name="Name">函数名</param>
/// <param name="DisplayName">展示名称</param>
/// <param name="Description">给模型看的描述</param>
/// <param name="Parameters">JSON Schema 参数定义</param>
/// <param name="IsDangerous">是否为高风险操作（需要确认）</param>
/// <param name="Handler">处理函数，返回给模型的结果文本</param>
public record AssistantTool(
    string Name,
    string DisplayName,
    string Description,
    JsonObject Parameters,
    bool IsDangerous,
    Func<JsonObject, CancellationToken, Task<string>> Handler);

/// <summary>
/// AI 助手服务：管理会话、调用模型并执行工具（桌面操作、课表查询、提醒等）。
/// </summary>
public partial class AssistantService : ObservableObject, IAsyncDisposable
{
    private const int MaxToolRounds = 8;

    /// <summary>只提供给内置 API 后端的联网工具（DSH 自带同类工具）。</summary>
    private static readonly HashSet<string> WebToolNames = ["web_search", "web_fetch"];

    /// <summary>不属于桌面操作的 ClassIsland 工具，关闭桌面操作时仍提供给 DSH。</summary>
    private static readonly HashSet<string> ClassIslandToolNames =
        ["get_today_schedule", "get_current_status", "get_weather", "show_notification", "speak", "set_main_window_visible", "open_settings"];

    private SettingsService SettingsService { get; }
    private DesktopOperationService Desktop { get; }
    private ILessonsService LessonsService { get; }
    private IProfileService ProfileService { get; }
    private IWeatherService WeatherService { get; }
    private IActionService ActionService { get; }
    private ILogger<AssistantService> Logger { get; }
    private AiChatClient Client { get; } = new();

    private readonly List<JsonNode> _history = [];
    private CancellationTokenSource? _currentCts;

    public AssistantSettings Settings => SettingsService.Settings.AssistantSettings;

    /// <summary>
    /// 界面显示的消息列表。
    /// </summary>
    public ObservableCollection<AssistantChatMessage> Messages { get; } = [];

    /// <summary>
    /// 已注册的工具。
    /// </summary>
    public IReadOnlyList<AssistantTool> Tools { get; }

    [ObservableProperty] private bool _isBusy;

    /// <summary>助手当前活动，供桌面宠物显示。</summary>
    [ObservableProperty] private AssistantActivity _activity = AssistantActivity.Idle;

    /// <summary>最近一条完整回复，供桌面宠物气泡显示。</summary>
    [ObservableProperty] private string _lastReply = "";

    private readonly SemaphoreSlim _dshLock = new(1, 1);
    private AcpClient? _acp;
    private string? _dshSessionId;
    private bool _dshSessionPrimed;
    private ClassIslandMcpServer? _mcpServer;
    private System.Diagnostics.Process? _dshWebProcess;

    /// <summary>
    /// 高风险操作确认回调。返回 true 表示允许。未设置时高风险操作将被拒绝（除非关闭了确认）。
    /// </summary>
    public Func<string, Task<bool>>? ConfirmHandler { get; set; }

    /// <summary>
    /// 请求显示助手窗口。
    /// </summary>
    public event EventHandler? ShowWindowRequested;

    public AssistantService(SettingsService settingsService,
        DesktopOperationService desktopOperationService,
        ILessonsService lessonsService,
        IProfileService profileService,
        IWeatherService weatherService,
        IActionService actionService,
        ILogger<AssistantService> logger)
    {
        SettingsService = settingsService;
        Desktop = desktopOperationService;
        LessonsService = lessonsService;
        ProfileService = profileService;
        WeatherService = weatherService;
        ActionService = actionService;
        Logger = logger;
        Tools = BuildTools();

        MigrateSettings();
        AttachSettings();
        SettingsService.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Models.Settings.AssistantSettings))
                AttachSettings();
        };
    }

    private AssistantSettings? _attachedSettings;

    private void AttachSettings()
    {
        if (_attachedSettings != null)
            _attachedSettings.PropertyChanged -= OnAssistantSettingsChanged;
        _attachedSettings = SettingsService.Settings.AssistantSettings;
        _attachedSettings.PropertyChanged += OnAssistantSettingsChanged;
    }

    private void OnAssistantSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        SettingsService.SaveSettings("AssistantSettings." + e.PropertyName);
        if (e.PropertyName is nameof(AssistantSettings.Backend) or nameof(AssistantSettings.DshCommand)
            or nameof(AssistantSettings.DshWorkingDirectory) or nameof(AssistantSettings.IsDshClassIslandToolsEnabled)
            or nameof(AssistantSettings.IsDshApiKeyPassThroughEnabled) or nameof(AssistantSettings.IsDesktopToolsEnabled)
            or nameof(AssistantSettings.IsCommandToolEnabled) or nameof(AssistantSettings.IsFullAccessEnabled))
        {
            _ = ResetDshAsync();
        }
    }

    private void MigrateSettings()
    {
        var settings = SettingsService.Settings.AssistantSettings;
        if (settings.MigrationVersion < 1)
        {
            if (settings.WakeWord == AssistantSettings.LegacyDefaultWakeWord)
                settings.WakeWord = AssistantSettings.DefaultWakeWord;
            settings.MigrationVersion = 1;
        }
    }

    public void RequestShowWindow() => ShowWindowRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 清空会话。
    /// </summary>
    public void ClearConversation()
    {
        Cancel();
        _history.Clear();
        Messages.Clear();
        // DSH 的上下文保存在会话里，换一个新会话才能真正清空。
        _dshSessionId = null;
        _dshSessionPrimed = false;
    }

    /// <summary>
    /// 取消当前请求。
    /// </summary>
    public void Cancel()
    {
        _currentCts?.Cancel();
    }

    /// <summary>
    /// 测试连接。
    /// </summary>
    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!Settings.IsDshBackend)
            return await Client.TestAsync(Settings, cancellationToken);
        await _dshLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureDshSessionAsync(cancellationToken);
        }
        finally
        {
            _dshLock.Release();
        }
        return "已连接到 DeepSeek Harness（DSH）。";
    }

    /// <summary>
    /// 向助手发送一条消息，并返回最终回复文本。
    /// </summary>
    public async Task<string> SendAsync(string text, CancellationToken cancellationToken = default)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
            return "";
        if (!Settings.IsEnabled)
        {
            AddMessage(AssistantMessageRole.Error, "AI 助手未启用，请在【设置 → AI 助手】中启用。");
            return "";
        }

        if (IsBusy)
            Cancel();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentCts = cts;
        await RunOnUiAsync(() => IsBusy = true);
        AddMessage(AssistantMessageRole.User, text);
        _history.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        TrimHistory();

        var finalText = "";
        try
        {
            await RunOnUiAsync(() =>
            {
                LastReply = "";
                Activity = AssistantActivity.Thinking;
            });
            if (Settings.IsDshBackend)
            {
                finalText = await SendViaDshAsync(text, cts.Token);
            }
            else
            {
                finalText = await SendViaApiAsync(cts.Token);
            }

            await RunOnUiAsync(() =>
            {
                LastReply = finalText;
                Activity = AssistantActivity.Idle;
            });
            if (Settings.IsSpeakReplyEnabled && !string.IsNullOrWhiteSpace(finalText))
            {
                try
                {
                    IAppHost.GetService<ISpeechService>().EnqueueSpeechQueue(StripMarkdown(finalText));
                }
                catch (Exception e)
                {
                    Logger.LogWarning(e, "朗读回复失败");
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested && _currentCts != cts)
        {
            // 被新请求取代
        }
        catch (OperationCanceledException)
        {
            AddMessage(AssistantMessageRole.Error, "已停止。");
        }
        catch (Exception e)
        {
            Logger.LogError(e, "AI 助手请求失败");
            AddMessage(AssistantMessageRole.Error, e.Message);
            await RunOnUiAsync(() => Activity = AssistantActivity.Error);
        }
        finally
        {
            if (_currentCts == cts)
            {
                _currentCts = null;
                await RunOnUiAsync(() =>
                {
                    IsBusy = false;
                    if (Activity != AssistantActivity.Error)
                        Activity = AssistantActivity.Idle;
                    foreach (var m in Messages.Where(x => x.IsStreaming))
                        m.IsStreaming = false;
                });
            }
        }

        return finalText;
    }

    /// <summary>
    /// 通过 OpenAI 兼容接口完成一轮对话（含多轮工具调用）。
    /// </summary>
    private async Task<string> SendViaApiAsync(CancellationToken cancellationToken)
    {
        var tools = Settings.AllowDesktopTools ? BuildToolsJson() : null;
        for (var round = 0; round < MaxToolRounds; round++)
        {
            var uiMessage = AddMessage(AssistantMessageRole.Assistant, "", true);
            var messages = new List<JsonNode> { new JsonObject { ["role"] = "system", ["content"] = BuildSystemPrompt() } };
            messages.AddRange(_history);

            var result = await Client.CompleteStreamingAsync(Settings, messages, tools,
                delta => Dispatcher.UIThread.Post(() =>
                {
                    uiMessage.Text += delta;
                    Activity = AssistantActivity.Replying;
                }), cancellationToken);

            await RunOnUiAsync(() =>
            {
                uiMessage.Text = result.Content;
                uiMessage.IsStreaming = false;
                if (string.IsNullOrWhiteSpace(result.Content))
                    Messages.Remove(uiMessage);
            });

            var assistantNode = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = result.Content
            };
            if (result.ToolCalls.Count > 0)
            {
                assistantNode["tool_calls"] = new JsonArray(result.ToolCalls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments }
                }).ToArray());
            }
            _history.Add(assistantNode);

            if (result.ToolCalls.Count == 0)
                return result.Content;

            await RunOnUiAsync(() => Activity = AssistantActivity.Working);
            foreach (var call in result.ToolCalls)
            {
                var output = await ExecuteToolAsync(call, cancellationToken);
                _history.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.Id,
                    ["content"] = output
                });
            }
            await RunOnUiAsync(() => Activity = AssistantActivity.Thinking);
        }

        return "";
    }

    private async Task<string> ExecuteToolAsync(AiToolCall call, CancellationToken cancellationToken)
    {
        JsonObject args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return "错误：参数不是有效的 JSON。";
        }

        return await InvokeToolAsync(Tools.FirstOrDefault(x => x.Name == call.Name), call.Name, args, cancellationToken);
    }

    /// <summary>
    /// 执行一个工具：在对话中显示进度，高风险操作先请求确认。
    /// </summary>
    private async Task<string> InvokeToolAsync(AssistantTool? tool, string name, JsonObject args, CancellationToken cancellationToken)
    {
        if (tool == null)
            return $"错误：未知工具 {name}";

        var argText = args.Count == 0 ? "" : " " + args.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var uiMessage = AddMessage(AssistantMessageRole.Tool, $"⚙ {tool.DisplayName}{argText}", true);

        if (tool.IsDangerous && Settings.RequiresConfirmation)
        {
            var allowed = false;
            if (ConfirmHandler != null)
            {
                allowed = await Dispatcher.UIThread.InvokeAsync(() => ConfirmHandler($"AI 助手请求执行：{tool.DisplayName}{argText}\n\n是否允许？"));
            }
            if (!allowed)
            {
                await RunOnUiAsync(() =>
                {
                    uiMessage.Text += "  —  已拒绝";
                    uiMessage.IsStreaming = false;
                });
                return "用户拒绝了此操作。";
            }
        }

        try
        {
            Logger.LogInformation("执行 AI 工具 {}：{}", tool.Name, argText);
            var output = await tool.Handler(args, cancellationToken);
            await RunOnUiAsync(() =>
            {
                uiMessage.Text += "  ✓";
                uiMessage.IsStreaming = false;
            });
            return string.IsNullOrWhiteSpace(output) ? "完成。" : output;
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "AI 工具 {} 执行失败", tool.Name);
            await RunOnUiAsync(() =>
            {
                uiMessage.Text += $"  ✗ {e.Message}";
                uiMessage.IsStreaming = false;
            });
            return $"执行失败：{e.Message}";
        }
    }

    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是 ClassIsland 智能助手，运行在用户（通常是教师或学生）的电脑上，ClassIsland 是一款在桌面上显示课表、提醒上下课的软件。");
        sb.AppendLine("请使用简洁、友好的中文回答。回答会显示在小窗口中，也可能被语音朗读，所以尽量简短，避免冗长的 Markdown 表格。");
        sb.AppendLine("当用户的请求需要操作电脑或查询课表时，请调用提供的工具，不要编造结果；执行完后简要告诉用户结果。");
        sb.AppendLine("对于关机、重启、结束进程、执行命令等高风险操作，只有在用户明确要求时才调用。");
        sb.AppendLine($"当前时间：{DateTime.Now:yyyy-MM-dd HH:mm dddd}。");
        sb.AppendLine($"操作系统：{System.Runtime.InteropServices.RuntimeInformation.OSDescription}。");
        if (!Settings.AllowDesktopTools)
            sb.AppendLine("注意：用户已关闭工具调用功能，你无法操作电脑，只能进行对话。");
        else if (Settings.IsFullAccessEnabled)
            sb.AppendLine("用户已授予你完全控制电脑的权限：可以直接执行操作和命令，无需再征求同意；但关机、删除文件等不可逆操作仍应只在用户明确要求时进行。");
        else if (Settings.IsWebToolsEnabled)
            sb.AppendLine("需要最新信息（新闻、资料、价格等）时，先用 web_search 搜索，再用 web_fetch 打开相关网页阅读，并在回答中注明来源网址。");
        if (!string.IsNullOrWhiteSpace(Settings.CustomSystemPrompt))
        {
            sb.AppendLine();
            sb.AppendLine(Settings.CustomSystemPrompt);
        }
        return sb.ToString();
    }

    private void TrimHistory()
    {
        var max = Math.Max(2, Settings.MaxHistoryMessages);
        while (_history.Count > max)
        {
            _history.RemoveAt(0);
            // 不能以工具结果或带工具调用的助手消息开头
            while (_history.Count > 0 && _history[0]?["role"]?.GetValue<string>() is "tool" or "assistant")
                _history.RemoveAt(0);
        }
    }

    private AssistantChatMessage AddMessage(AssistantMessageRole role, string text, bool streaming = false)
    {
        var message = new AssistantChatMessage { Role = role, Text = text, IsStreaming = streaming };
        if (Dispatcher.UIThread.CheckAccess())
            Messages.Add(message);
        else
            Dispatcher.UIThread.Invoke(() => Messages.Add(message));
        return message;
    }

    private static Task RunOnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    private static string StripMarkdown(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"[`*_#>\[\]]", "");

    #region Tools

    private JsonArray BuildToolsJson()
    {
        return new JsonArray(Tools
            .Where(t => Settings.AllowCommandTool || t.Name != "run_command")
            .Where(t => Settings.IsWebToolsEnabled || !WebToolNames.Contains(t.Name))
            .Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone()
                }
            }).ToArray());
    }

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required, string[]? Enum)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in props)
        {
            var prop = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Enum != null)
                prop["enum"] = new JsonArray(p.Enum.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
            properties[p.Name] = prop;
            if (p.Required)
                required.Add(p.Name);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
    }

    private static string GetString(JsonObject args, string name, string fallback = "") =>
        args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : args[name]?.ToString() ?? fallback;

    private static int GetInt(JsonObject args, string name, int fallback = 0)
    {
        if (args[name] is not JsonValue v) return fallback;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : fallback;
    }

    private static Task<string> Ui(Func<string> func) => Dispatcher.UIThread.InvokeAsync(func).GetTask();

    private List<AssistantTool> BuildTools()
    {
        string[] mediaKeys = ["play_pause", "next", "previous", "stop", "mute"];
        string[] powerOps = ["shutdown", "restart", "sleep", "logoff", "cancel"];

        return
        [
            // ---------- ClassIsland ----------
            new("get_today_schedule", "查询今日课表",
                "获取今天的课表（每节课的时间和科目）。",
                Schema(), false, (_, _) => Ui(GetTodaySchedule)),
            new("get_current_status", "查询当前课程状态",
                "获取当前是上课/课间/放学，当前科目、下节课科目和剩余时间。",
                Schema(), false, (_, _) => Ui(GetCurrentStatus)),
            new("get_weather", "查询天气",
                "获取 ClassIsland 最近一次获取到的天气信息（JSON）。",
                Schema(), false, (_, _) => Ui(GetWeather)),
            new("show_notification", "显示提醒",
                "在 ClassIsland 主界面上显示一条提醒。",
                Schema(("title", "string", "提醒标题（简短）", true, null),
                       ("content", "string", "提醒正文", false, null),
                       ("speak", "boolean", "是否同时朗读", false, null)),
                false, async (a, _) =>
                {
                    var speak = a["speak"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
                    var set = new ActionSet { Name = "AI 助手提醒" };
                    set.ActionItems.Add(new ActionItem
                    {
                        Id = "classisland.showNotification",
                        Settings = new NotificationActionSettings
                        {
                            Mask = GetString(a, "title", "提醒"),
                            Content = GetString(a, "content"),
                            IsMaskSpeechEnabled = speak,
                            IsContentSpeechEnabled = speak,
                        }
                    });
                    await Dispatcher.UIThread.InvokeAsync(() => ActionService.InvokeActionSetAsync(set, false));
                    return "已显示提醒。";
                }),
            new("speak", "朗读文本",
                "使用 ClassIsland 的语音合成朗读一段文本。",
                Schema(("text", "string", "要朗读的文本", true, null)), false, (a, _) =>
                {
                    IAppHost.GetService<ISpeechService>().EnqueueSpeechQueue(GetString(a, "text"));
                    return Task.FromResult("已开始朗读。");
                }),
            new("set_main_window_visible", "显示/隐藏主界面",
                "显示或隐藏 ClassIsland 的课表主界面。",
                Schema(("visible", "boolean", "true 显示，false 隐藏", true, null)), false, (a, _) => Ui(() =>
                {
                    var visible = a["visible"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : !SettingsService.Settings.IsMainWindowVisible;
                    SettingsService.Settings.IsMainWindowVisible = visible;
                    return visible ? "主界面已显示。" : "主界面已隐藏。";
                })),
            new("open_settings", "打开设置",
                "打开 ClassIsland 设置窗口的某个页面。",
                Schema(("page", "string", "页面 ID", false,
                    ["general", "appearance", "components", "notification", "window", "weather", "automation", "assistant", "plugins", "about"])),
                false, (a, _) => Ui(() =>
                {
                    var page = GetString(a, "page", "general");
                    IAppHost.GetService<Views.SettingsWindowNew>().Open(string.IsNullOrWhiteSpace(page) ? "general" : page);
                    return "已打开设置。";
                })),

            // ---------- 联网 ----------
            new("web_search", "联网搜索",
                "在互联网上搜索，返回结果的标题、网址和摘要。",
                Schema(("query", "string", "搜索关键词", true, null),
                       ("count", "integer", "返回结果数，1-10，默认 6", false, null)),
                false, (a, ct) => WebTools.SearchAsync(GetString(a, "query"), GetInt(a, "count", 6), ct)),
            new("web_fetch", "读取网页",
                "打开一个网址并返回网页的文字内容（过长会截断）。",
                Schema(("url", "string", "http/https 网址", true, null)),
                false, (a, ct) => WebTools.FetchAsync(GetString(a, "url"), ct)),

            // ---------- 桌面操作 ----------
            new("open", "打开",
                "打开应用程序、文件、文件夹或网址。可以是程序名（如 notepad、calc、explorer、msedge、winword）、完整路径或 URL。搜索网页时可构造搜索引擎 URL。",
                Schema(("target", "string", "程序名、路径或网址", true, null),
                       ("arguments", "string", "启动参数（可选）", false, null)),
                false, (a, _) =>
                {
                    var target = GetString(a, "target");
                    Desktop.Open(target, GetString(a, "arguments"));
                    return Task.FromResult($"已打开 {target}。");
                }),
            new("media_control", "媒体控制",
                "控制正在播放的媒体：播放/暂停、下一首、上一首、停止、静音切换。",
                Schema(("action", "string", "操作", true, mediaKeys)), false, (a, _) =>
                {
                    var key = GetString(a, "action") switch
                    {
                        "play_pause" => DesktopMediaKey.PlayPause,
                        "next" => DesktopMediaKey.NextTrack,
                        "previous" => DesktopMediaKey.PreviousTrack,
                        "stop" => DesktopMediaKey.Stop,
                        "mute" => DesktopMediaKey.VolumeMute,
                        var x => throw new ArgumentException($"未知操作 {x}")
                    };
                    Desktop.SendMediaKey(key);
                    return Task.FromResult("完成。");
                }),
            new("change_volume", "调节音量",
                "调高或调低系统音量。steps 为正数调高、负数调低，每步约 2%（例如 +5 约提高 10%）。",
                Schema(("steps", "integer", "调节步数，范围 -50 到 50", true, null)), false, (a, _) =>
                {
                    var steps = Math.Clamp(GetInt(a, "steps"), -50, 50);
                    Desktop.ChangeVolume(steps);
                    return Task.FromResult($"音量已调节 {steps * 2:+#;-#;0}%。");
                }),
            new("lock_screen", "锁定屏幕", "锁定计算机屏幕。", Schema(), false, (_, _) =>
            {
                Desktop.LockScreen();
                return Task.FromResult("已锁屏。");
            }),
            new("show_desktop", "显示桌面", "最小化所有窗口以显示桌面（再次调用可还原）。", Schema(), false, (_, _) =>
            {
                Desktop.ShowDesktop();
                return Task.FromResult("完成。");
            }),
            new("screen_snip", "截图", "打开系统截图工具让用户框选截图。", Schema(), false, (_, _) =>
            {
                Desktop.OpenScreenSnip();
                return Task.FromResult("已打开截图工具。");
            }),
            new("get_system_info", "查询系统信息", "获取操作系统、计算机名、运行时间等信息。", Schema(), false,
                (_, _) => Task.FromResult(Desktop.GetSystemInfo())),
            new("list_windows", "列出打开的程序", "列出当前打开窗口的程序（进程名、PID、窗口标题）。", Schema(), false, (_, _) =>
            {
                var list = Desktop.ListWindowedProcesses();
                return Task.FromResult(list.Count == 0
                    ? "没有找到。"
                    : string.Join('\n', list.Take(80).Select(x => $"{x.ProcessName} (PID {x.Id}) {x.Title}")));
            }),
            new("close_program", "关闭程序", "关闭（结束）指定进程名的程序，如 notepad、chrome。", Schema(
                ("process_name", "string", "进程名（不含 .exe）", true, null)), true, (a, _) =>
            {
                var n = Desktop.KillProcess(GetString(a, "process_name"));
                return Task.FromResult(n == 0 ? "没有找到该程序。" : $"已关闭 {n} 个进程。");
            }),
            new("set_clipboard", "写入剪贴板", "把文本复制到剪贴板。", Schema(
                ("text", "string", "文本", true, null)), false, async (a, _) =>
            {
                var text = GetString(a, "text");
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var clipboard = IAppHost.GetService<MainWindow>().Clipboard;
                    if (clipboard != null)
                        await clipboard.SetTextAsync(text);
                });
                return "已复制到剪贴板。";
            }),
            new("power", "电源操作", "关机、重启、睡眠、注销，或取消计划中的关机（cancel）。", Schema(
                ("operation", "string", "操作", true, powerOps),
                ("delay_seconds", "integer", "延迟秒数（关机/重启有效）", false, null)), true, (a, _) =>
            {
                var op = GetString(a, "operation");
                if (op == "cancel")
                {
                    Desktop.CancelPendingShutdown();
                    return Task.FromResult("已取消计划中的关机。");
                }
                Desktop.Power(op switch
                {
                    "shutdown" => DesktopPowerOperation.Shutdown,
                    "restart" => DesktopPowerOperation.Restart,
                    "sleep" => DesktopPowerOperation.Sleep,
                    "logoff" => DesktopPowerOperation.LogOff,
                    _ => throw new ArgumentException($"未知操作 {op}")
                }, GetInt(a, "delay_seconds", 30));
                return Task.FromResult("已执行。");
            }),
            new("run_command", "执行命令",
                OperatingSystem.IsWindows()
                    ? "在 cmd.exe 中执行一条命令并返回输出（可用 powershell -Command 执行 PowerShell）。仅在必要时使用。"
                    : "在 bash 中执行一条命令并返回输出。仅在必要时使用。",
                Schema(("command", "string", "命令", true, null)), true, async (a, ct) =>
                {
                    if (!Settings.AllowCommandTool)
                        return "命令执行功能未启用。";
                    return await Desktop.RunCommandAsync(GetString(a, "command"), TimeSpan.FromSeconds(30), ct);
                }),
        ];
    }

    private string GetTodaySchedule()
    {
        var plan = LessonsService.CurrentClassPlan;
        if (plan == null)
            return "今天没有课表（可能是休息日或未加载课表）。";
        var sb = new StringBuilder();
        sb.AppendLine($"今日课表：{plan.Name}");
        var classIndex = 0;
        foreach (var item in plan.ValidTimeLayoutItems)
        {
            var time = $"{item.StartTime:hh\\:mm}-{item.EndTime:hh\\:mm}";
            switch (item.TimeType)
            {
                case 0:
                    var name = "（未设置）";
                    if (classIndex < plan.Classes.Count &&
                        ProfileService.Profile.Subjects.TryGetValue(plan.Classes[classIndex].SubjectId, out var subject))
                    {
                        name = subject.Name + (string.IsNullOrWhiteSpace(subject.TeacherName) ? "" : $"（{subject.TeacherName}）");
                    }
                    sb.AppendLine($"第{classIndex + 1}节 {time} {name}");
                    classIndex++;
                    break;
                case 1:
                    sb.AppendLine($"  {time} {item.BreakNameText}");
                    break;
            }
        }
        return sb.ToString();
    }

    private string GetCurrentStatus()
    {
        var sb = new StringBuilder();
        var state = LessonsService.CurrentState;
        sb.AppendLine("当前状态：" + state switch
        {
            TimeState.OnClass => "上课中",
            TimeState.Breaking => "课间",
            TimeState.PrepareOnClass => "即将上课",
            TimeState.AfterSchool => "已放学",
            _ => "无"
        });
        if (LessonsService.CurrentSubject is { } current && state == TimeState.OnClass)
            sb.AppendLine($"当前科目：{current.Name}，距离下课 {LessonsService.OnClassLeftTime:hh\\:mm\\:ss}");
        if (state is TimeState.Breaking or TimeState.PrepareOnClass)
            sb.AppendLine($"距离上课：{LessonsService.OnBreakingTimeLeftTime:hh\\:mm\\:ss}");
        if (LessonsService.NextClassSubject is { } next && !string.IsNullOrEmpty(next.Name))
            sb.AppendLine($"下节课：{next.Name}");
        return sb.ToString();
    }

    private string GetWeather()
    {
        var info = WeatherService.LastWeatherInfo;
        if (info == null)
            return "暂无天气信息。";
        var json = JsonSerializer.Serialize(new { info.Current, info.Alerts, info.Aqi, info.UpdateTime });
        return json.Length > 3000 ? json[..3000] : json;
    }

    #endregion

    #region DeepSeek Harness

    /// <summary>
    /// 提供给 DSH 的工具：ClassIsland 工具总是提供；桌面操作工具受设置控制；联网工具由 DSH 自带，不重复提供。
    /// </summary>
    private IEnumerable<AssistantTool> GetDshTools() => Tools
        .Where(t => !WebToolNames.Contains(t.Name))
        .Where(t => Settings.AllowDesktopTools || ClassIslandToolNames.Contains(t.Name))
        .Where(t => Settings.AllowCommandTool || t.Name != "run_command");

    private Task<string> InvokeToolFromDshAsync(string name, JsonObject args, CancellationToken cancellationToken)
    {
        var tool = GetDshTools().FirstOrDefault(t => t.Name == name);
        return InvokeToolAsync(tool, name, args, cancellationToken);
    }

    private string GetDshWorkingDirectory()
    {
        var dir = Settings.DshWorkingDirectory.Trim();
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
            dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return System.IO.Path.GetFullPath(dir);
    }

    private Dictionary<string, string> BuildDshEnvironment()
    {
        var env = new Dictionary<string, string>();
        if (Settings.IsDshApiKeyPassThroughEnabled && !string.IsNullOrWhiteSpace(Settings.ApiKey))
            env["DEEPSEEK_API_KEY"] = Settings.ApiKey.Trim();
        // DSH 的权限预设：danger-full-access 不使用工作区沙箱，且审批策略为 never。
        if (Settings.IsFullAccessEnabled)
            env["DSH_PERMISSION_MODE"] = "danger-full-access";
        return env;
    }

    /// <summary>
    /// 确保 DSH 进程已启动并且存在可用会话。调用方需持有 <see cref="_dshLock"/>。
    /// </summary>
    private async Task<string> EnsureDshSessionAsync(CancellationToken cancellationToken)
    {
        if (_acp is not { IsRunning: true })
        {
            if (_acp != null)
                await _acp.DisposeAsync();
            _dshSessionId = null;
            var acp = new AcpClient(Logger);
            acp.SessionUpdate += OnDshSessionUpdate;
            acp.PermissionHandler = HandleDshPermissionAsync;
            acp.Exited += (_, _) => _dshSessionId = null;
            _acp = acp;
            try
            {
                await acp.StartAsync(Settings.DshCommand, GetDshWorkingDirectory(), BuildDshEnvironment(), cancellationToken);
            }
            catch (Exception e)
            {
                // 握手未完成的进程不能复用，否则下次会被当作已连接。
                await acp.DisposeAsync();
                _acp = null;
                if (cancellationToken.IsCancellationRequested)
                    throw;
                var reason = e is OperationCanceledException ? "连接超时" : e.Message;
                throw new InvalidOperationException(
                    $"无法连接 DeepSeek Harness（DSH）：{reason}\n请确认已安装 Node.js 18+，并在【设置 → AI 助手】中点击“登录 DSH 账号”完成登录。", e);
            }
        }

        if (_dshSessionId != null)
            return _dshSessionId;

        var mcpServers = new JsonArray();
        if (Settings.IsDshClassIslandToolsEnabled)
        {
            if (_mcpServer == null)
            {
                _mcpServer = new ClassIslandMcpServer(Logger, GetDshTools, InvokeToolFromDshAsync);
                _mcpServer.Start();
            }
            mcpServers.Add(_mcpServer.ToAcpDeclaration());
        }

        _dshSessionId = await _acp.NewSessionAsync(GetDshWorkingDirectory(), mcpServers, cancellationToken);
        _dshSessionPrimed = false;
        return _dshSessionId;
    }

    private string BuildDshContextPrefix()
    {
        var sb = new StringBuilder();
        sb.AppendLine("【背景】你正在通过 ClassIsland 的桌面助手（桌宠“大肥鱼”）与用户对话。ClassIsland 是显示课表和天气的桌面软件。");
        sb.AppendLine("回复会显示在小气泡和小窗口中，也可能被语音朗读，请简短，少用表格。");
        if (Settings.IsDshClassIslandToolsEnabled)
            sb.AppendLine("可以使用名为 classisland 的 MCP 工具查询课表、课程状态、天气，显示提醒，打开 ClassIsland 设置或操作电脑。");
        sb.AppendLine("需要最新资料时请使用联网搜索 / 网页读取工具，并注明来源。");
        if (!string.IsNullOrWhiteSpace(Settings.CustomSystemPrompt))
            sb.AppendLine(Settings.CustomSystemPrompt);
        sb.AppendLine($"当前时间：{DateTime.Now:yyyy-MM-dd HH:mm dddd}。");
        sb.AppendLine();
        sb.Append("【用户】");
        return sb.ToString();
    }

    private AssistantChatMessage? _dshReplyMessage;
    private readonly Dictionary<string, AssistantChatMessage> _dshToolMessages = new();
    private readonly StringBuilder _dshReplyText = new();

    private async Task<string> SendViaDshAsync(string text, CancellationToken cancellationToken)
    {
        await _dshLock.WaitAsync(cancellationToken);
        try
        {
            var sessionId = await EnsureDshSessionAsync(cancellationToken);
            var prompt = _dshSessionPrimed ? text : BuildDshContextPrefix() + text;
            _dshSessionPrimed = true;
            await RunOnUiAsync(() =>
            {
                _dshReplyMessage = null;
                _dshReplyText.Clear();
                _dshToolMessages.Clear();
            });

            var stopReason = await _acp!.PromptAsync(sessionId, prompt, cancellationToken);
            Logger.LogInformation("DSH 本轮结束：{}", stopReason);

            var reply = "";
            await RunOnUiAsync(() =>
            {
                reply = _dshReplyText.ToString().Trim();
                if (_dshReplyMessage != null)
                {
                    _dshReplyMessage.IsStreaming = false;
                    if (string.IsNullOrWhiteSpace(_dshReplyMessage.Text))
                        Messages.Remove(_dshReplyMessage);
                }
                foreach (var m in _dshToolMessages.Values)
                    m.IsStreaming = false;
                _dshReplyMessage = null;
            });
            if (stopReason == "refusal")
                AddMessage(AssistantMessageRole.Error, "DSH 拒绝了这个请求。");
            else if (stopReason is "max_tokens" or "max_turn_requests")
                AddMessage(AssistantMessageRole.Error, "回复已达到长度或轮次上限，被截断。");
            return reply;
        }
        finally
        {
            _dshLock.Release();
        }
    }

    private void OnDshSessionUpdate(string sessionId, JsonObject update)
    {
        if (sessionId != _dshSessionId)
            return;
        var kind = update["sessionUpdate"]?.GetValue<string>();
        Dispatcher.UIThread.Post(() =>
        {
            switch (kind)
            {
                case "agent_message_chunk":
                    var chunk = update["content"]?["text"]?.GetValue<string>() ?? "";
                    if (chunk.Length == 0)
                        break;
                    // 工具调用之后的文字作为新的一条消息显示，保持与工具进度的先后顺序。
                    if (_dshReplyMessage == null || Messages.LastOrDefault() != _dshReplyMessage)
                    {
                        if (_dshReplyMessage != null)
                            _dshReplyMessage.IsStreaming = false;
                        _dshReplyMessage = new AssistantChatMessage { Role = AssistantMessageRole.Assistant, IsStreaming = true };
                        Messages.Add(_dshReplyMessage);
                        if (_dshReplyText.Length > 0)
                            _dshReplyText.AppendLine();
                    }
                    _dshReplyMessage.Text += chunk;
                    _dshReplyText.Append(chunk);
                    Activity = AssistantActivity.Replying;
                    break;
                case "agent_thought_chunk":
                    Activity = AssistantActivity.Thinking;
                    break;
                case "tool_call":
                {
                    var id = update["toolCallId"]?.ToString() ?? "";
                    var title = update["title"]?.ToString() ?? "工具";
                    var message = new AssistantChatMessage
                    {
                        Role = AssistantMessageRole.Tool,
                        Text = "⚙ " + title,
                        IsStreaming = true
                    };
                    _dshToolMessages[id] = message;
                    Messages.Add(message);
                    Activity = AssistantActivity.Working;
                    break;
                }
                case "tool_call_update":
                {
                    var id = update["toolCallId"]?.ToString() ?? "";
                    if (!_dshToolMessages.TryGetValue(id, out var message))
                        break;
                    if (update["title"]?.ToString() is { Length: > 0 } title)
                        message.Text = "⚙ " + title;
                    switch (update["status"]?.ToString())
                    {
                        case "completed":
                            message.Text += "  ✓";
                            message.IsStreaming = false;
                            Activity = AssistantActivity.Thinking;
                            break;
                        case "failed":
                            message.Text += "  ✗";
                            message.IsStreaming = false;
                            Activity = AssistantActivity.Thinking;
                            break;
                    }
                    break;
                }
            }
        });
    }

    private async Task<string?> HandleDshPermissionAsync(JsonObject request)
    {
        var options = request["options"] as JsonArray ?? [];
        string? Find(params string[] kinds) => options
            .OfType<JsonObject>()
            .FirstOrDefault(o => kinds.Contains(o["kind"]?.ToString()))?["optionId"]?.ToString();

        var allow = Find("allow_once", "allow_always");
        var reject = Find("reject_once", "reject_always");
        if (!Settings.RequiresConfirmation)
            return allow;

        var title = request["toolCall"]?["title"]?.ToString() ?? "执行操作";
        var allowed = false;
        if (ConfirmHandler != null)
            allowed = await Dispatcher.UIThread.InvokeAsync(() => ConfirmHandler($"DSH 请求执行：{title}\n\n是否允许？"));
        return allowed ? allow : reject;
    }

    /// <summary>
    /// 关闭当前 DSH 进程与会话，下次提问时按新设置重新连接。
    /// </summary>
    public async Task ResetDshAsync()
    {
        await _dshLock.WaitAsync();
        try
        {
            _dshSessionId = null;
            _dshSessionPrimed = false;
            if (_acp != null)
            {
                await _acp.DisposeAsync();
                _acp = null;
            }
            _mcpServer?.Dispose();
            _mcpServer = null;
        }
        finally
        {
            _dshLock.Release();
        }
    }

    /// <summary>
    /// 启动 DSH 网页版。DSH 会在浏览器中打开，可在其中登录 DeepSeek 账号；登录信息保存在 DSH 本地，与助手共用。
    /// </summary>
    public void OpenDshWeb()
    {
        if (_dshWebProcess is { HasExited: false })
        {
            Desktop.Open("http://127.0.0.1:3080", "");
            return;
        }
        var command = Settings.DshCommand.Trim() + " web";
        var psi = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", command } }
            : new System.Diagnostics.ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = GetDshWorkingDirectory();
        foreach (var (key, value) in BuildDshEnvironment())
            psi.Environment[key] = value;
        Logger.LogInformation("启动 DSH 网页版：{}", command);
        _dshWebProcess = System.Diagnostics.Process.Start(psi);
    }

    public async ValueTask DisposeAsync()
    {
        // 退出时可能有提问正在进行并持有锁，这里不等锁，直接结束子进程。
        _currentCts?.Cancel();
        if (_acp != null)
            await _acp.DisposeAsync();
        _acp = null;
        _mcpServer?.Dispose();
        _mcpServer = null;
        try
        {
            if (_dshWebProcess is { HasExited: false })
                _dshWebProcess.Kill(entireProcessTree: true);
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "结束 DSH 网页版失败");
        }
    }

    #endregion
}
