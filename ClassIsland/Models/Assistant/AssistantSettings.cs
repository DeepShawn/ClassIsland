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
        nameof(EffectiveModel), nameof(IsApiKeyMissing))]
    private string _providerTemplateId = AiProviderTemplate.DefaultId;

    /// <summary>API 密钥。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiKeyMissing))]
    private string _apiKey = "";

    /// <summary>自定义模板的接口地址。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveApiBaseUrl))]
    private string _apiBaseUrl = "";

    /// <summary>自定义模板的模型名称。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveModel))]
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

    /// <summary>采样温度。</summary>
    [ObservableProperty] private double _temperature = 0.6;

    /// <summary>请求超时（秒）。</summary>
    [ObservableProperty] private double _requestTimeoutSeconds = 60;

    /// <summary>附加系统提示词。</summary>
    [ObservableProperty] private string _customSystemPrompt = "";

    /// <summary>保留的历史消息条数。</summary>
    [ObservableProperty] private int _maxHistoryMessages = 20;

    /// <summary>是否允许 AI 调用桌面操作工具。</summary>
    [ObservableProperty] private bool _isDesktopToolsEnabled = true;

    /// <summary>是否允许 AI 执行任意命令行命令（高风险）。</summary>
    [ObservableProperty] private bool _isCommandToolEnabled = false;

    /// <summary>执行高风险操作（关机、结束进程、运行命令等）前是否需要确认。</summary>
    [ObservableProperty] private bool _confirmDangerousOperations = true;

    /// <summary>是否朗读 AI 回复。</summary>
    [ObservableProperty] private bool _isSpeakReplyEnabled = false;

    /// <summary>是否启用语音助手（语音唤醒）。</summary>
    [ObservableProperty] private bool _isVoiceAssistantEnabled = false;

    /// <summary>唤醒词。</summary>
    [ObservableProperty] private string _wakeWord = "小岛小岛";

    /// <summary>语音识别区域（如 zh-CN），留空使用系统默认。</summary>
    [ObservableProperty] private string _recognitionCulture = "zh-CN";

    /// <summary>识别置信度阈值。</summary>
    [ObservableProperty] private double _recognitionConfidenceThreshold = 0.5;

    /// <summary>唤醒时是否自动弹出助手窗口。</summary>
    [ObservableProperty] private bool _showWindowOnWake = true;
}
