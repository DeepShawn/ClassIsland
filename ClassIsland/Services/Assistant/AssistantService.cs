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
public partial class AssistantService : ObservableObject
{
    private const int MaxToolRounds = 8;

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

    private void OnAssistantSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        SettingsService.SaveSettings("AssistantSettings." + e.PropertyName);

    public void RequestShowWindow() => ShowWindowRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 清空会话。
    /// </summary>
    public void ClearConversation()
    {
        Cancel();
        _history.Clear();
        Messages.Clear();
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
    public Task<string> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        Client.TestAsync(Settings, cancellationToken);

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
            var tools = Settings.IsDesktopToolsEnabled ? BuildToolsJson() : null;
            for (var round = 0; round < MaxToolRounds; round++)
            {
                var uiMessage = AddMessage(AssistantMessageRole.Assistant, "", true);
                var messages = new List<JsonNode> { new JsonObject { ["role"] = "system", ["content"] = BuildSystemPrompt() } };
                messages.AddRange(_history);

                var result = await Client.CompleteStreamingAsync(Settings, messages, tools,
                    delta => Dispatcher.UIThread.Post(() => uiMessage.Text += delta), cts.Token);

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
                {
                    finalText = result.Content;
                    break;
                }

                foreach (var call in result.ToolCalls)
                {
                    var output = await ExecuteToolAsync(call, cts.Token);
                    _history.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = call.Id,
                        ["content"] = output
                    });
                }
            }

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
        }
        finally
        {
            if (_currentCts == cts)
            {
                _currentCts = null;
                await RunOnUiAsync(() =>
                {
                    IsBusy = false;
                    foreach (var m in Messages.Where(x => x.IsStreaming))
                        m.IsStreaming = false;
                });
            }
        }

        return finalText;
    }

    private async Task<string> ExecuteToolAsync(AiToolCall call, CancellationToken cancellationToken)
    {
        var tool = Tools.FirstOrDefault(x => x.Name == call.Name);
        if (tool == null)
            return $"错误：未知工具 {call.Name}";

        JsonObject args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return "错误：参数不是有效的 JSON。";
        }

        var argText = args.Count == 0 ? "" : " " + args.ToJsonString(new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var uiMessage = AddMessage(AssistantMessageRole.Tool, $"⚙ {tool.DisplayName}{argText}", true);

        if (tool.IsDangerous && Settings.ConfirmDangerousOperations)
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
            Logger.LogInformation("执行 AI 工具 {}：{}", tool.Name, call.Arguments);
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
        if (!Settings.IsDesktopToolsEnabled)
            sb.AppendLine("注意：用户已关闭工具调用功能，你无法操作电脑，只能进行对话。");
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
            .Where(t => Settings.IsCommandToolEnabled || t.Name != "run_command")
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
                    if (!Settings.IsCommandToolEnabled)
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
}
