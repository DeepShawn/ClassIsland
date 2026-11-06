using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Controls;
using ClassIsland.Services.Assistant;
using FluentAvalonia.UI.Controls;

namespace ClassIsland.Views;

/// <summary>
/// AI 助手对话窗口。
/// </summary>
public partial class AssistantWindow : MyWindow
{
    public AssistantService AssistantService { get; }

    public VoiceAssistantService VoiceAssistantService { get; }

    private bool _isOpened;
    private bool _stickToBottom = true;

    public AssistantWindow(AssistantService assistantService, VoiceAssistantService voiceAssistantService)
    {
        AssistantService = assistantService;
        VoiceAssistantService = voiceAssistantService;
        InitializeComponent();
        DataContext = this;

        // 多行 TextBox 会自行处理 Enter，需在隧道阶段拦截
        TextBoxInput.AddHandler(KeyDownEvent, TextBoxInput_OnKeyDown, RoutingStrategies.Tunnel);

        // 自动滚动到底部（用户向上翻阅时暂停）
        ScrollViewerMessages.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 && _stickToBottom)
            {
                ScrollViewerMessages.ScrollToEnd();
                return;
            }
            if (e.OffsetDelta.Y != 0)
            {
                var sv = ScrollViewerMessages;
                _stickToBottom = sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 24;
            }
        };
        AssistantService.Messages.CollectionChanged += (_, _) =>
        {
            _stickToBottom = true;
            Dispatcher.UIThread.Post(() => ScrollViewerMessages.ScrollToEnd(), DispatcherPriority.Background);
        };
    }

    /// <summary>
    /// 打开（或激活）窗口。
    /// </summary>
    public void Open()
    {
        if (!_isOpened)
        {
            _isOpened = true;
            Show();
        }
        else
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        }
        TextBoxInput.Focus();
    }

    /// <summary>
    /// 弹出高风险操作确认对话框。
    /// </summary>
    public async Task<bool> ConfirmAsync(string message)
    {
        Open();
        var result = await new ContentDialog
        {
            Title = "确认操作",
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 360 },
            PrimaryButtonText = "允许",
            CloseButtonText = "拒绝",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync(this);
        return result == ContentDialogResult.Primary;
    }

    private async Task SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        TextBoxInput.Text = "";
        await AssistantService.SendAsync(text);
    }

    private void TextBoxInput_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;
        e.Handled = true;
        if (AssistantService.IsBusy)
            return;
        _ = SendAsync(TextBoxInput.Text ?? "");
    }

    private void ButtonSend_OnClick(object? sender, RoutedEventArgs e) => _ = SendAsync(TextBoxInput.Text ?? "");

    private void ButtonStop_OnClick(object? sender, RoutedEventArgs e) => AssistantService.Cancel();

    private void ButtonSuggestion_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string text })
            _ = SendAsync(text);
    }

    private void ButtonSaveApiKey_OnClick(object? sender, RoutedEventArgs e)
    {
        var key = TextBoxApiKey.Text?.Trim();
        if (string.IsNullOrEmpty(key))
            return;
        AssistantService.Settings.ApiKey = key;
        TextBoxApiKey.Text = "";
        TextBoxInput.Focus();
    }

    private void ButtonGetApiKey_OnClick(object? sender, RoutedEventArgs e)
    {
        var url = AssistantService.Settings.Template.ApiKeyUrl;
        if (!string.IsNullOrWhiteSpace(url))
            App.GetService<ClassIsland.Core.Abstractions.Services.IUriNavigationService>().Navigate(new Uri(url));
    }

    private void ButtonClear_OnClick(object? sender, RoutedEventArgs e) => AssistantService.ClearConversation();

    private void ButtonSettings_OnClick(object? sender, RoutedEventArgs e) =>
        App.GetService<SettingsWindowNew>().Open("assistant");

    private async void ButtonMic_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            TextBoxInput.Watermark = "正在聆听…";
            var text = await VoiceAssistantService.CaptureOnceAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                TextBoxInput.Watermark = "没有听清，请再试一次";
                return;
            }
            await SendAsync(text);
        }
        catch (Exception ex)
        {
            TextBoxInput.Watermark = "语音输入失败：" + ex.Message;
        }
        finally
        {
            await Task.Delay(1500);
            TextBoxInput.Watermark = "输入消息，Enter 发送，Shift+Enter 换行";
        }
    }

    private void AssistantWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            return;
        e.Cancel = true;
        Hide();
        _isOpened = false;
    }
}
