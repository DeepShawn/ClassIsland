using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Abstractions.Services.SpeechService;
using ClassIsland.Core.Attributes;
using ClassIsland.Models.Actions;
using ClassIsland.Services.Assistant;
using ClassIsland.Shared;

namespace ClassIsland.Services.Automation.Actions;

[ActionInfo("classisland.desktop.media", "媒体与音量控制", "\uf00d", addDefaultToMenu: false)]
public class DesktopMediaAction(DesktopOperationService desktop) : ActionBase<DesktopMediaActionSettings>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        var key = Settings.Key switch
        {
            0 => DesktopMediaKey.VolumeUp,
            1 => DesktopMediaKey.VolumeDown,
            2 => DesktopMediaKey.VolumeMute,
            3 => DesktopMediaKey.PlayPause,
            4 => DesktopMediaKey.NextTrack,
            5 => DesktopMediaKey.PreviousTrack,
            _ => DesktopMediaKey.Stop
        };
        desktop.SendMediaKey(key, (int)Math.Max(1, Settings.Repeat));
    }
}

[ActionInfo("classisland.desktop.lock", "锁定屏幕", "\ueaf0", addDefaultToMenu: false)]
public class LockScreenAction(DesktopOperationService desktop) : ActionBase
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        desktop.LockScreen();
    }
}

[ActionInfo("classisland.desktop.showDesktop", "显示桌面", "\ue62f", addDefaultToMenu: false)]
public class ShowDesktopAction(DesktopOperationService desktop) : ActionBase
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        desktop.ShowDesktop();
    }
}

[ActionInfo("classisland.desktop.power", "电源操作", "\uede9", addDefaultToMenu: false)]
public class DesktopPowerAction(DesktopOperationService desktop) : ActionBase<DesktopPowerActionSettings>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        desktop.Power(Settings.Operation switch
        {
            0 => DesktopPowerOperation.Shutdown,
            1 => DesktopPowerOperation.Restart,
            2 => DesktopPowerOperation.Sleep,
            _ => DesktopPowerOperation.LogOff
        }, (int)Settings.DelaySeconds);
    }
}

[ActionInfo("classisland.assistant.ask", "询问 AI 助手", "\ue276", addDefaultToMenu: false)]
public class AskAssistantAction(AssistantService assistant) : ActionBase<AskAssistantActionSettings>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        if (string.IsNullOrWhiteSpace(Settings.Prompt))
            return;
        var task = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (Settings.ShowWindow)
                assistant.RequestShowWindow();
            var reply = await assistant.SendAsync(Settings.Prompt, InterruptCancellationToken);
            if (Settings.SpeakReply && !assistant.Settings.IsSpeakReplyEnabled && !string.IsNullOrWhiteSpace(reply))
                IAppHost.GetService<ISpeechService>().EnqueueSpeechQueue(reply);
        });
        if (Settings.WaitForReply)
            await task;
    }
}
