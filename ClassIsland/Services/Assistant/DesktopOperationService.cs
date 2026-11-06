using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services.Assistant;

/// <summary>
/// 桌面媒体/系统按键。
/// </summary>
public enum DesktopMediaKey
{
    VolumeUp,
    VolumeDown,
    VolumeMute,
    PlayPause,
    NextTrack,
    PreviousTrack,
    Stop
}

/// <summary>
/// 电源操作。
/// </summary>
public enum DesktopPowerOperation
{
    Shutdown,
    Restart,
    Sleep,
    LogOff
}

/// <summary>
/// 提供桌面操作能力（打开程序/网址、媒体控制、锁屏、显示桌面、电源、进程管理、命令执行等）。
/// 这些能力同时提供给自动化行动和 AI 助手的工具调用使用。
/// </summary>
public class DesktopOperationService(ILogger<DesktopOperationService> logger)
{
    private ILogger<DesktopOperationService> Logger { get; } = logger;

    private const int MaxOutputLength = 4000;

    #region Win32

    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const byte VK_LWIN = 0x5B;
    private const byte VK_SHIFT = 0x10;
    private const byte VK_D = 0x44;
    private const byte VK_S = 0x53;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    private static byte GetVirtualKey(DesktopMediaKey key) => key switch
    {
        DesktopMediaKey.VolumeUp => 0xAF,
        DesktopMediaKey.VolumeDown => 0xAE,
        DesktopMediaKey.VolumeMute => 0xAD,
        DesktopMediaKey.PlayPause => 0xB3,
        DesktopMediaKey.NextTrack => 0xB0,
        DesktopMediaKey.PreviousTrack => 0xB1,
        DesktopMediaKey.Stop => 0xB2,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    private static void PressKeys(params byte[] keys)
    {
        foreach (var k in keys)
            keybd_event(k, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        foreach (var k in keys.Reverse())
            keybd_event(k, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    #endregion

    /// <summary>
    /// 打开应用程序、文件、文件夹或网址。
    /// </summary>
    public void Open(string target, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("目标不能为空。", nameof(target));
        target = target.Trim().Trim('"');
        Logger.LogInformation("打开目标：{} {}", target, arguments);

        if (LooksLikeUrl(target) && !target.Contains("://"))
            target = "https://" + target;

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(target)
            {
                Arguments = arguments ?? "",
                UseShellExecute = true
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open", string.IsNullOrEmpty(arguments) ? [target] : [target, "--args", arguments]));
        }
        else
        {
            if (!string.IsNullOrEmpty(arguments) || !(target.Contains("://") || System.IO.Path.Exists(target)))
                Process.Start(new ProcessStartInfo(target, arguments ?? "") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", [target]));
        }
    }

    private static bool LooksLikeUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 发送媒体按键。
    /// </summary>
    public void SendMediaKey(DesktopMediaKey key, int repeat = 1)
    {
        repeat = Math.Clamp(repeat, 1, 50);
        Logger.LogInformation("发送媒体按键：{} x{}", key, repeat);
        if (OperatingSystem.IsWindows())
        {
            var vk = GetVirtualKey(key);
            for (var i = 0; i < repeat; i++)
                PressKeys(vk);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            for (var i = 0; i < repeat; i++)
            {
                var (file, args) = key switch
                {
                    DesktopMediaKey.VolumeUp => ("pactl", "set-sink-volume @DEFAULT_SINK@ +2%"),
                    DesktopMediaKey.VolumeDown => ("pactl", "set-sink-volume @DEFAULT_SINK@ -2%"),
                    DesktopMediaKey.VolumeMute => ("pactl", "set-sink-mute @DEFAULT_SINK@ toggle"),
                    DesktopMediaKey.PlayPause => ("playerctl", "play-pause"),
                    DesktopMediaKey.NextTrack => ("playerctl", "next"),
                    DesktopMediaKey.PreviousTrack => ("playerctl", "previous"),
                    DesktopMediaKey.Stop => ("playerctl", "stop"),
                    _ => throw new ArgumentOutOfRangeException(nameof(key))
                };
                Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(2000);
            }
            return;
        }

        throw new PlatformNotSupportedException("当前平台不支持媒体按键。");
    }

    /// <summary>
    /// 调整系统音量。每一步约为 2%。
    /// </summary>
    public void ChangeVolume(int steps)
    {
        if (steps == 0) return;
        SendMediaKey(steps > 0 ? DesktopMediaKey.VolumeUp : DesktopMediaKey.VolumeDown, Math.Abs(steps));
    }

    /// <summary>
    /// 锁定计算机。
    /// </summary>
    public void LockScreen()
    {
        Logger.LogInformation("锁定屏幕");
        if (OperatingSystem.IsWindows())
        {
            if (!LockWorkStation())
                throw new InvalidOperationException($"锁定失败（错误代码 {Marshal.GetLastWin32Error()}）。");
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            Process.Start(new ProcessStartInfo("loginctl", "lock-session") { UseShellExecute = false });
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("pmset", "displaysleepnow") { UseShellExecute = false });
            return;
        }
        throw new PlatformNotSupportedException();
    }

    /// <summary>
    /// 显示桌面（最小化所有窗口 / 再次调用恢复）。
    /// </summary>
    public void ShowDesktop()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("仅 Windows 支持显示桌面。");
        PressKeys(VK_LWIN, VK_D);
    }

    /// <summary>
    /// 打开系统截图工具（Win+Shift+S）。
    /// </summary>
    public void OpenScreenSnip()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("仅 Windows 支持截图工具快捷键。");
        PressKeys(VK_LWIN, VK_SHIFT, VK_S);
    }

    /// <summary>
    /// 执行电源操作。
    /// </summary>
    public void Power(DesktopPowerOperation operation, int delaySeconds = 0)
    {
        delaySeconds = Math.Clamp(delaySeconds, 0, 3600);
        Logger.LogWarning("执行电源操作：{}，延迟 {} 秒", operation, delaySeconds);
        if (OperatingSystem.IsWindows())
        {
            var psi = operation switch
            {
                DesktopPowerOperation.Shutdown => new ProcessStartInfo("shutdown.exe", $"/s /t {delaySeconds}"),
                DesktopPowerOperation.Restart => new ProcessStartInfo("shutdown.exe", $"/r /t {delaySeconds}"),
                DesktopPowerOperation.LogOff => new ProcessStartInfo("shutdown.exe", "/l"),
                DesktopPowerOperation.Sleep => new ProcessStartInfo("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            var args = operation switch
            {
                DesktopPowerOperation.Shutdown => "poweroff",
                DesktopPowerOperation.Restart => "reboot",
                DesktopPowerOperation.Sleep => "suspend",
                DesktopPowerOperation.LogOff => "terminate-session self",
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            Process.Start(new ProcessStartInfo(operation == DesktopPowerOperation.LogOff ? "loginctl" : "systemctl", args)
                { UseShellExecute = false });
            return;
        }
        throw new PlatformNotSupportedException();
    }

    /// <summary>
    /// 取消计划中的关机/重启。
    /// </summary>
    public void CancelPendingShutdown()
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("shutdown.exe", "/a") { UseShellExecute = false, CreateNoWindow = true });
        else
            throw new PlatformNotSupportedException();
    }

    /// <summary>
    /// 列出带有窗口的进程。
    /// </summary>
    public IReadOnlyList<(string ProcessName, int Id, string Title)> ListWindowedProcesses()
    {
        var result = new List<(string, int, string)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    continue;
                result.Add((p.ProcessName, p.Id, OperatingSystem.IsWindows() ? p.MainWindowTitle : ""));
            }
            catch
            {
                // 无权限访问的进程
            }
            finally
            {
                p.Dispose();
            }
        }
        return result.OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 结束指定名称的进程，返回结束的数量。
    /// </summary>
    public int KillProcess(string processName)
    {
        processName = processName.Trim();
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            processName = processName[..^4];
        if (string.Equals(processName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不能结束 ClassIsland 自身。");
        var count = 0;
        foreach (var p in Process.GetProcessesByName(processName))
        {
            try
            {
                if (OperatingSystem.IsWindows() && p.CloseMainWindow() && p.WaitForExit(3000))
                {
                    count++;
                    continue;
                }
                p.Kill(entireProcessTree: true);
                count++;
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "无法结束进程 {}({})", p.ProcessName, p.Id);
            }
            finally
            {
                p.Dispose();
            }
        }
        Logger.LogInformation("结束进程 {}：{} 个", processName, count);
        return count;
    }

    /// <summary>
    /// 执行命令行命令并返回输出。
    /// </summary>
    public async Task<string> RunCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Logger.LogWarning("执行命令：{}", command);
        using var process = new Process();
        process.StartInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/c", command])
            : new ProcessStartInfo("/bin/bash", ["-c", command]);
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        if (OperatingSystem.IsWindows())
        {
            process.StartInfo.StandardOutputEncoding = Encoding.Default;
            process.StartInfo.StandardErrorEncoding = Encoding.Default;
        }

        var output = new StringBuilder();
        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (output)
            {
                if (output.Length < MaxOutputLength)
                    output.AppendLine(e.Data);
            }
        }
        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignored */ }
            lock (output) output.AppendLine("（命令超时或被取消，已终止）");
        }

        string text;
        lock (output) text = output.ToString();
        if (text.Length >= MaxOutputLength)
            text += "…（输出已截断）";
        return $"退出代码：{(process.HasExited ? process.ExitCode : -1)}\n{text}";
    }

    /// <summary>
    /// 获取系统基本信息。
    /// </summary>
    public string GetSystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"操作系统：{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"计算机名：{Environment.MachineName}");
        sb.AppendLine($"用户名：{Environment.UserName}");
        sb.AppendLine($"处理器数：{Environment.ProcessorCount}");
        sb.AppendLine($"系统已运行：{TimeSpan.FromMilliseconds(Environment.TickCount64):d\\天hh\\:mm\\:ss}");
        sb.AppendLine($"当前时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss dddd}");
        var gc = GC.GetGCMemoryInfo();
        if (gc.TotalAvailableMemoryBytes > 0)
            sb.AppendLine($"物理内存：{gc.TotalAvailableMemoryBytes / 1024 / 1024} MB");
        return sb.ToString();
    }
}
