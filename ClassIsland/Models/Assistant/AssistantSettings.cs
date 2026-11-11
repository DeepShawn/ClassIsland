using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.Models.Assistant;

/// <summary>
/// AI 助手、语音助手与桌面操作相关设置。
/// </summary>
public partial class AssistantSettings : ObservableRecipient
{
    /// <summary>是否启用 AI 助手。</summary>
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>
    /// 连接模板 ID（见 <see cref="AiProviderTemplate.All"/>）。选择内置模板时接口地址和模型自动填充，只需填写 API Key。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Template), nameof(IsCustomProvider), nameof(EffectiveApiBaseUrl),
        nameof(EffectiveModel), nameof(IsApiKeyMissing), nameof(NeedsApiKeySetup), nameof(BackendDisplayName))]
    private string _providerTemplateId = AiProviderTemplate.DefaultId;

    /// <summary>API 密钥。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiKeyMissing), nameof(NeedsApiKeySetup))]
    private string _apiKey = "";

    /// <summary>自定义模板的接口地址。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveApiBaseUrl))]
    private string _apiBaseUrl = "";

    /// <summary>自定义模板的模型名称。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveModel), nameof(BackendDisplayName))]
    private string _model = "";

    /// <summary>当前连接模板。</summary>
    [JsonIgnore]
    public AiProviderTemplate Template => AiProviderTemplate.Get(ProviderTemplateId);

    /// <summary>是否为自定义模板。</summary>
    [JsonIgnore]
    public bool IsCustomProvider => Template.IsCustom;

    /// <summary>实际使用的接口地址（内置模板自动填充）。</summary>
    [JsonIgnore]
    public string EffectiveApiBaseUrl => Template.IsCustom ? ApiBaseUrl : Template.BaseUrl;

    /// <summary>实际使用的模型（内置模板自动填充）。</summary>
    [JsonIgnore]
    public string EffectiveModel => Template.IsCustom ? Model : Template.Model;

    /// <summary>是否还需要填写 API Key。</summary>
    [JsonIgnore]
    public bool IsApiKeyMissing => Template.RequiresApiKey && string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>使用内置 API 后端但还没有填写 API Key，助手窗口需要先显示填写卡片。</summary>
    [JsonIgnore]
    public bool NeedsApiKeySetup => Backend == AssistantBackend.Api && IsApiKeyMissing;

    /// <summary>采样温度。</summary>
    [ObservableProperty] private double _temperature = 0.6;

    /// <summary>请求超时（秒）。</summary>
    [ObservableProperty] private double _requestTimeoutSeconds = 60;

    /// <summary>附加系统提示词。</summary>
    [ObservableProperty] private string _customSystemPrompt = "";

    /// <summary>保留的历史消息条数。</summary>
    [ObservableProperty] private int _maxHistoryMessages = 20;

    /// <summary>是否允许 AI 调用桌面操作工具。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllowDesktopTools))]
    private bool _isDesktopToolsEnabled = true;

    /// <summary>是否允许 AI 执行任意命令行命令（高风险）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllowCommandTool))]
    private bool _isCommandToolEnabled = false;

    /// <summary>执行高风险操作（关机、结束进程、运行命令等）前是否需要确认。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiresConfirmation))]
    private bool _confirmDangerousOperations = true;

    /// <summary>
    /// 完全控制电脑：授予助手全部权限。开启后允许全部桌面操作与命令行命令、不再弹窗确认，
    /// DSH 以 danger-full-access 权限模式运行（不受工作区沙箱限制，也不请求批准）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllowDesktopTools), nameof(AllowCommandTool), nameof(RequiresConfirmation))]
    private bool _isFullAccessEnabled = true;

    /// <summary>实际是否允许桌面操作工具。</summary>
    [JsonIgnore]
    public bool AllowDesktopTools => IsFullAccessEnabled || IsDesktopToolsEnabled;

    /// <summary>实际是否允许执行命令行命令。</summary>
    [JsonIgnore]
    public bool AllowCommandTool => IsFullAccessEnabled || (IsDesktopToolsEnabled && IsCommandToolEnabled);

    /// <summary>执行高风险操作前实际是否需要确认。</summary>
    [JsonIgnore]
    public bool RequiresConfirmation => !IsFullAccessEnabled && ConfirmDangerousOperations;

    /// <summary>是否朗读 AI 回复。</summary>
    [ObservableProperty] private bool _isSpeakReplyEnabled = false;

    /// <summary>是否启用语音助手（语音唤醒）。识别在本机完成，不会上传录音。</summary>
    [ObservableProperty] private bool _isVoiceAssistantEnabled = true;

    /// <summary>旧版本的默认唤醒词，用于把未修改过唤醒词的用户迁移到新的默认值。</summary>
    public const string LegacyDefaultWakeWord = "小岛小岛";

    /// <summary>默认唤醒词。</summary>
    public const string DefaultWakeWord = "大肥鱼";

    /// <summary>唤醒词。</summary>
    [ObservableProperty] private string _wakeWord = DefaultWakeWord;

    /// <summary>设置迁移版本。1：默认唤醒词改为“大肥鱼”。</summary>
    [ObservableProperty] private int _migrationVersion = 0;

    /// <summary>语音识别区域（如 zh-CN），留空使用系统默认。</summary>
    [ObservableProperty] private string _recognitionCulture = "zh-CN";

    /// <summary>识别置信度阈值。</summary>
    [ObservableProperty] private double _recognitionConfidenceThreshold = 0.5;

    /// <summary>唤醒时是否自动弹出助手窗口。</summary>
    [ObservableProperty] private bool _showWindowOnWake = true;

    /// <summary>对话后端。默认使用 DeepSeek Harness（DSH）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDshBackend), nameof(IsApiBackend), nameof(NeedsApiKeySetup), nameof(BackendDisplayName))]
    private AssistantBackend _backend = AssistantBackend.Dsh;

    [JsonIgnore]
    public bool IsDshBackend
    {
        get => Backend == AssistantBackend.Dsh;
        set
        {
            if (value)
                Backend = AssistantBackend.Dsh;
        }
    }

    /// <summary>助手窗口标题栏显示的后端名称。</summary>
    [JsonIgnore]
    public string BackendDisplayName => Backend == AssistantBackend.Dsh ? "DeepSeek Harness" : EffectiveModel;

    [JsonIgnore]
    public bool IsApiBackend
    {
        get => Backend == AssistantBackend.Api;
        set
        {
            if (value)
                Backend = AssistantBackend.Api;
        }
    }

    /// <summary>
    /// 启动 DSH 的命令（不含 profile 参数）。默认通过 npx 运行，需要已安装 Node.js；
    /// 已全局安装时可改为 <c>dsh</c>。
    /// </summary>
    [ObservableProperty] private string _dshCommand = DefaultDshCommand;

    public const string DefaultDshCommand = "npx -y @deepseek-ai/dsh";

    /// <summary>DSH 会话的工作目录，留空使用用户主目录。</summary>
    [ObservableProperty] private string _dshWorkingDirectory = "";

    /// <summary>是否把 ClassIsland 的课表、天气、设置与桌面操作工具提供给 DSH（通过本机 MCP 服务）。</summary>
    [ObservableProperty] private bool _isDshClassIslandToolsEnabled = true;

    /// <summary>
    /// 是否把上方填写的 DeepSeek API Key 传给 DSH。未登录 DSH 账号时可用它代替。
    /// </summary>
    [ObservableProperty] private bool _isDshApiKeyPassThroughEnabled = false;

    /// <summary>是否显示桌面宠物。</summary>
    [ObservableProperty] private bool _isPetEnabled = true;

    /// <summary>桌面宠物缩放。</summary>
    [ObservableProperty] private double _petScale = 1.0;

    /// <summary>桌面宠物脚下中点的屏幕横坐标（像素）。小于 0 时放在主屏幕右下角。</summary>
    [ObservableProperty] private double _petPositionX = -1;

    /// <summary>桌面宠物脚下中点的屏幕纵坐标（像素）。小于 0 时放在主屏幕右下角。</summary>
    [ObservableProperty] private double _petPositionY = -1;

    /// <summary>是否允许桌面宠物空闲时动起来。关闭可进一步降低资源占用。</summary>
    [ObservableProperty] private bool _isPetAnimationEnabled = true;

    /// <summary>是否为内置 API 后端提供联网搜索与网页读取工具。</summary>
    [ObservableProperty] private bool _isWebToolsEnabled = true;
}

/// <summary>
/// AI 助手的对话后端。
/// </summary>
public enum AssistantBackend
{
    /// <summary>直接调用 OpenAI 兼容接口（DeepSeek API 等）。</summary>
    Api,
    /// <summary>通过 Agent Client Protocol 连接本机的 DeepSeek Harness（DSH）。</summary>
    Dsh
}
