using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassIsland.Models.Actions;

/// <summary>
/// “媒体与音量控制”行动设置。
/// </summary>
public partial class DesktopMediaActionSettings : ObservableRecipient
{
    /// <summary>
    /// 按键：0 音量+，1 音量-，2 静音切换，3 播放/暂停，4 下一首，5 上一首，6 停止。
    /// </summary>
    [ObservableProperty] private int _key = 3;

    /// <summary>重复次数（音量调节时每次约 2%）。</summary>
    [ObservableProperty] private double _repeat = 1;
}

/// <summary>
/// “电源操作”行动设置。
/// </summary>
public partial class DesktopPowerActionSettings : ObservableRecipient
{
    /// <summary>0 关机，1 重启，2 睡眠，3 注销。</summary>
    [ObservableProperty] private int _operation = 0;

    /// <summary>延迟秒数。</summary>
    [ObservableProperty] private double _delaySeconds = 60;
}

/// <summary>
/// “询问 AI 助手”行动设置。
/// </summary>
public partial class AskAssistantActionSettings : ObservableRecipient
{
    /// <summary>要发送给 AI 助手的内容。</summary>
    [ObservableProperty] private string _prompt = "";

    /// <summary>是否显示助手窗口。</summary>
    [ObservableProperty] private bool _showWindow = true;

    /// <summary>是否朗读回复。</summary>
    [ObservableProperty] private bool _speakReply = false;

    /// <summary>是否等待 AI 回复完成后再继续执行后续行动。</summary>
    [ObservableProperty] private bool _waitForReply = true;
}
