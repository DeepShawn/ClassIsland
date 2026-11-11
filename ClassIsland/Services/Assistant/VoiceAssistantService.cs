using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Speech.Recognition;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.SpeechService;
using ClassIsland.Models.Assistant;
using ClassIsland.Shared;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services.Assistant;

/// <summary>
/// 语音助手：基于系统语音识别实现唤醒词监听与语音指令，识别结果交给 <see cref="AssistantService"/> 处理并朗读回复。
/// 目前仅支持 Windows（需要安装对应语言的语音识别组件）。
/// </summary>
public partial class VoiceAssistantService : ObservableObject
{
    private SettingsService SettingsService { get; }
    private AssistantService AssistantService { get; }
    private ILogger<VoiceAssistantService> Logger { get; }

    private AssistantSettings Settings => SettingsService.Settings.AssistantSettings;

    private object? _engine; // SpeechRecognitionEngine，避免在非 Windows 平台加载类型
    private bool _awaitingCommand;
    private bool _initialized;
    private bool _isPaused;

    /// <summary>当前状态描述。</summary>
    [ObservableProperty] private string _status = "未启动";

    /// <summary>是否正在后台监听唤醒词。</summary>
    [ObservableProperty] private bool _isListening;

    /// <summary>是否正在进行一次性语音输入。</summary>
    [ObservableProperty] private bool _isCapturing;

    /// <summary>当前平台是否支持语音识别。</summary>
    public bool IsSupported => OperatingSystem.IsWindows();

    public VoiceAssistantService(SettingsService settingsService, AssistantService assistantService,
        ILogger<VoiceAssistantService> logger)
    {
        SettingsService = settingsService;
        AssistantService = assistantService;
        Logger = logger;
    }

    /// <summary>
    /// 根据设置启动语音助手。应在主界面初始化后调用。
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        SettingsService.Settings.AssistantSettings.PropertyChanged += OnSettingsChanged;
        ApplySettings();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantSettings.IsVoiceAssistantEnabled)
            or nameof(AssistantSettings.WakeWord)
            or nameof(AssistantSettings.RecognitionCulture)
            or nameof(AssistantSettings.IsEnabled))
        {
            ApplySettings();
        }
    }

    private void ApplySettings()
    {
        Stop();
        if (Settings.IsEnabled && Settings.IsVoiceAssistantEnabled)
            Start();
    }

    /// <summary>
    /// 启动后台唤醒词监听。
    /// </summary>
    public void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            Status = "当前系统不支持语音识别（仅支持 Windows）";
            return;
        }
        try
        {
            StartWindows();
        }
        catch (Exception e)
        {
            Logger.LogError(e, "无法启动语音助手");
            Status = "启动失败：" + e.Message;
            IsListening = false;
        }
    }

    /// <summary>
    /// 停止后台监听。
    /// </summary>
    public void Stop()
    {
        if (OperatingSystem.IsWindows())
            StopWindows();
        IsListening = false;
        _awaitingCommand = false;
        Status = "未启动";
    }

    /// <summary>
    /// 进行一次语音输入并返回识别到的文本（按住说话/点击麦克风）。
    /// </summary>
    public async Task<string?> CaptureOnceAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("当前系统不支持语音识别（仅支持 Windows）。");
        if (IsCapturing) return null;
        IsCapturing = true;
        PauseWindows();
        try
        {
            return await Task.Run(RecognizeOnceWindows);
        }
        finally
        {
            ResumeWindows();
            IsCapturing = false;
        }
    }

    #region Windows

    [SupportedOSPlatform("windows")]
    private SpeechRecognitionEngine CreateEngine()
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        if (installed.Count == 0)
            throw new InvalidOperationException("系统中没有安装语音识别组件。请在 Windows 设置 → 时间和语言 → 语音 中安装。");
        RecognizerInfo? info = null;
        if (!string.IsNullOrWhiteSpace(Settings.RecognitionCulture))
        {
            try
            {
                var culture = new CultureInfo(Settings.RecognitionCulture);
                info = installed.FirstOrDefault(x => x.Culture.Equals(culture))
                       ?? installed.FirstOrDefault(x => x.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName);
            }
            catch (CultureNotFoundException)
            {
                // 使用默认
            }
        }
        info ??= installed.FirstOrDefault(x => x.Culture.Equals(CultureInfo.CurrentUICulture)) ?? installed[0];
        Logger.LogInformation("使用语音识别器：{} ({})", info.Name, info.Culture);
        var engine = new SpeechRecognitionEngine(info);
        engine.SetInputToDefaultAudioDevice();
        return engine;
    }

    [SupportedOSPlatform("windows")]
    private void StartWindows()
    {
        StopWindows();
        var engine = CreateEngine();
        var wakeWord = string.IsNullOrWhiteSpace(Settings.WakeWord) ? AssistantSettings.DefaultWakeWord : Settings.WakeWord.Trim();

        var wakeGrammar = new Grammar(new GrammarBuilder(wakeWord) { Culture = engine.RecognizerInfo.Culture })
        {
            Name = "wake",
            Weight = 1.0f,
            Priority = 127
        };
        // 后台只加载唤醒词和通配“垃圾”语法，不加载听写语法：听写会持续进行自由识别，在低配电脑上 CPU 占用明显。
        // 通配语法用来吸收无关的说话声，减少误唤醒；唤醒后再单独进行一次听写识别指令。
        var garbageBuilder = new GrammarBuilder { Culture = engine.RecognizerInfo.Culture };
        garbageBuilder.AppendWildcard();
        var garbage = new Grammar(garbageBuilder) { Name = "garbage", Weight = 0.2f };
        engine.LoadGrammar(wakeGrammar);
        engine.LoadGrammar(garbage);
        engine.SpeechRecognized += EngineOnSpeechRecognized;
        engine.RecognizeAsync(RecognizeMode.Multiple);
        _engine = engine;
        IsListening = true;
        Status = $"正在监听唤醒词“{wakeWord}”";
        Logger.LogInformation("语音助手已启动，唤醒词：{}", wakeWord);
    }

    [SupportedOSPlatform("windows")]
    private void StopWindows()
    {
        if (_engine is not SpeechRecognitionEngine engine) return;
        try
        {
            engine.SpeechRecognized -= EngineOnSpeechRecognized;
            engine.RecognizeAsyncCancel();
            engine.Dispose();
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "停止语音识别时出错");
        }
        _engine = null;
    }

    private void PauseWindows()
    {
        if (!OperatingSystem.IsWindows() || _engine is not SpeechRecognitionEngine engine) return;
        _isPaused = true;
        engine.RecognizeAsyncCancel();
    }

    private void ResumeWindows()
    {
        if (!OperatingSystem.IsWindows() || !_isPaused) return;
        _isPaused = false;
        if (_engine is SpeechRecognitionEngine engine)
        {
            try
            {
                engine.RecognizeAsync(RecognizeMode.Multiple);
            }
            catch (InvalidOperationException)
            {
                // 已经在识别
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private string? RecognizeOnceWindows()
    {
        using var engine = CreateEngine();
        engine.LoadGrammar(new DictationGrammar());
        engine.InitialSilenceTimeout = TimeSpan.FromSeconds(6);
        engine.EndSilenceTimeout = TimeSpan.FromSeconds(1.2);
        var result = engine.Recognize(TimeSpan.FromSeconds(20));
        return result?.Text;
    }

    [SupportedOSPlatform("windows")]
    private void EngineOnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var text = e.Result.Text?.Trim() ?? "";
        Logger.LogDebug("语音识别：{} ({:F2}, {})", text, e.Result.Confidence, e.Result.Grammar?.Name);

        if (e.Result.Confidence < Settings.RecognitionConfidenceThreshold || text.Length == 0)
            return;

        if (e.Result.Grammar?.Name == "wake" && !_awaitingCommand)
            OnWake();
    }

    #endregion

    private void OnWake()
    {
        _awaitingCommand = true;
        Dispatcher.UIThread.Post(() =>
        {
            Status = "我在听，请说出指令…";
            AssistantService.Activity = AssistantActivity.Listening;
            if (Settings.ShowWindowOnWake)
                AssistantService.RequestShowWindow();
        });
        TrySpeak("我在");
        // 不能在识别事件回调中停止识别引擎，因此切到后台线程进行。
        _ = Task.Run(ListenForCommandAsync);
    }

    private async Task ListenForCommandAsync()
    {
        string? command = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // 等“我在”播完，避免把提示音识别成指令。
                await Task.Delay(TimeSpan.FromSeconds(0.8));
                PauseWindows();
                try
                {
                    command = RecognizeOnceWindows();
                }
                finally
                {
                    ResumeWindows();
                }
            }
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "识别语音指令失败");
        }
        finally
        {
            _awaitingCommand = false;
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (AssistantService.Activity == AssistantActivity.Listening)
                    AssistantService.Activity = AssistantActivity.Idle;
                ResetStatus();
            });
            return;
        }
        await HandleCommandAsync(command.Trim());
    }

    private async Task HandleCommandAsync(string text)
    {
        Logger.LogInformation("语音指令：{}", text);
        await Dispatcher.UIThread.InvokeAsync(() => Status = $"正在处理：{text}");
        var speakEnabled = Settings.IsSpeakReplyEnabled;
        var reply = await Dispatcher.UIThread.InvokeAsync(() => AssistantService.SendAsync(text));
        // 语音交互时总是朗读回复（若未在设置中开启朗读，则此处补充朗读）
        if (!speakEnabled && !string.IsNullOrWhiteSpace(reply))
            TrySpeak(reply);
        await Dispatcher.UIThread.InvokeAsync(ResetStatus);
    }

    private void ResetStatus()
    {
        Status = IsListening ? $"正在监听唤醒词“{Settings.WakeWord}”" : "未启动";
    }

    private void TrySpeak(string text)
    {
        try
        {
            IAppHost.GetService<ISpeechService>().EnqueueSpeechQueue(text);
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "朗读失败");
        }
    }
}
