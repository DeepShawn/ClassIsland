using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Threading;
using ClassIsland.Models.Assistant;
using ClassIsland.Services;
using ClassIsland.Services.Assistant;

namespace ClassIsland.Views;

/// <summary>
/// 桌面宠物“大肥鱼”：常驻桌面的 Q 版工作宠物，随助手状态切换动作并显示对话气泡。
/// </summary>
/// <remarks>
/// 为了在低配电脑上常驻，这里不使用逐帧动画：只有状态变化或定时器触发时才改变一次变换，
/// 由 <see cref="TransformOperationsTransition"/> 完成短暂的过渡，空闲时几乎不占用 CPU/GPU。
/// </remarks>
public partial class PetWindow : Window
{
    private const double BaseSpriteHeight = 150;
    private const double DragThreshold = 4;

    private static readonly Lazy<Bitmap> FrontSprite = new(() => LoadSprite("dafeiyu_front.png"));
    private static readonly Lazy<Bitmap> SideSprite = new(() => LoadSprite("dafeiyu_side.png"));

    private static readonly TransformOperations Rest = TransformOperations.Parse("none");
    private static readonly TransformOperations Hop = TransformOperations.Parse("translateY(-8px)");
    private static readonly TransformOperations Squash = TransformOperations.Parse("scale(1.06, 0.94)");
    private static readonly TransformOperations Mirror = TransformOperations.Parse("scaleX(-1)");

    public AssistantService AssistantService { get; }
    private SettingsService SettingsService { get; }
    private AssistantSettings Settings => SettingsService.Settings.AssistantSettings;

    private readonly DispatcherTimer _motionTimer = new();
    private readonly DispatcherTimer _bubbleHideTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _motionUp;
    private bool _initialized;
    private AssistantChatMessage? _watchedMessage;
    private AssistantActivity _previousActivity = AssistantActivity.Idle;

    private bool _isPlaced;
    private PixelPoint _anchor;
    private bool _isPressed;
    private bool _isDragging;
    private PixelPoint _pressScreenPoint;
    private PixelPoint _pressWindowPosition;

    public PetWindow(AssistantService assistantService, SettingsService settingsService)
    {
        AssistantService = assistantService;
        SettingsService = settingsService;
        InitializeComponent();
        DataContext = this;
        _motionTimer.Tick += (_, _) => OnMotionTick();
        _bubbleHideTimer.Tick += (_, _) =>
        {
            _bubbleHideTimer.Stop();
            if (AssistantService.Activity == AssistantActivity.Idle)
                HideBubble();
        };
        Opened += (_, _) => Dispatcher.UIThread.Post(PlaceWindow, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 根据设置显示桌宠并开始跟随助手状态。
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        AssistantService.PropertyChanged += OnAssistantPropertyChanged;
        AssistantService.Messages.CollectionChanged += (_, _) => WatchLatestMessage();
        SettingsService.Settings.AssistantSettings.PropertyChanged += OnSettingsChanged;
        ApplyScale();
        ApplyActivity();
        ApplyVisibility();
    }

    private static Bitmap LoadSprite(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://ClassIsland/Assets/Pet/{name}"));
        return new Bitmap(stream);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AssistantSettings.IsPetEnabled):
            case nameof(AssistantSettings.IsEnabled):
                ApplyVisibility();
                break;
            case nameof(AssistantSettings.PetScale):
                ApplyScale();
                break;
            case nameof(AssistantSettings.IsPetAnimationEnabled):
                ApplyActivity();
                break;
            case nameof(AssistantSettings.PetPositionX) or nameof(AssistantSettings.PetPositionY)
                when !_isDragging && IsVisible:
                PlaceWindow();
                break;
        }
    }

    private void ApplyVisibility()
    {
        var shouldShow = Settings.IsEnabled && Settings.IsPetEnabled;
        if (shouldShow && !IsVisible)
            Show();
        else if (!shouldShow && IsVisible)
            Hide();
        UpdateMotionTimer();
    }

    private void ApplyScale()
    {
        Sprite.Height = BaseSpriteHeight * Math.Clamp(Settings.PetScale, 0.5, 2.0);
    }

    /// <summary>
    /// 按保存的锚点（桌宠脚下的中点）放置窗口。气泡出现或消失时窗口大小会变化，以脚下为锚点才能让桌宠保持不动。
    /// </summary>
    private void PlaceWindow()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen == null)
            return;
        var area = screen.WorkingArea;
        var scaling = DesktopScaling;
        var size = new PixelSize((int)(ClientSize.Width * scaling), (int)(ClientSize.Height * scaling));
        var defaultAnchor = new PixelPoint(area.Right - size.Width / 2 - (int)(24 * scaling),
            area.Bottom - (int)(8 * scaling));
        var anchor = Settings.PetPositionX < 0 || Settings.PetPositionY < 0
            ? defaultAnchor
            : new PixelPoint((int)Settings.PetPositionX, (int)Settings.PetPositionY);
        // 屏幕布局变化后可能跑到屏幕外，拉回到默认位置。
        if (!Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(anchor.X, anchor.Y - 1))))
            anchor = defaultAnchor;
        _anchor = anchor;
        _isPlaced = true;
        ApplyAnchor();
    }

    /// <summary>
    /// 让桌宠脚下保持在锚点上，同时保证整个窗口（含气泡）不超出所在屏幕。
    /// </summary>
    private void ApplyAnchor()
    {
        var scaling = DesktopScaling;
        var width = (int)(ClientSize.Width * scaling);
        var height = (int)(ClientSize.Height * scaling);
        var x = _anchor.X - width / 2;
        var y = _anchor.Y - height;
        var screen = Screens.ScreenFromPoint(_anchor) ?? Screens.Primary;
        if (screen != null)
        {
            var area = screen.WorkingArea;
            x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width));
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));
        }
        Position = new PixelPoint(x, y);
    }

    private PixelPoint GetAnchor()
    {
        var scaling = DesktopScaling;
        return new PixelPoint(Position.X + (int)(ClientSize.Width * scaling / 2), Position.Y + (int)(ClientSize.Height * scaling));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty && _isPlaced && !_isDragging)
            ApplyAnchor();
    }

    #region 状态

    private void OnAssistantPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantService.Activity))
            ApplyActivity();
    }

    private void ApplyActivity()
    {
        var activity = AssistantService.Activity;
        Sprite.Source = activity is AssistantActivity.Thinking or AssistantActivity.Working
            ? SideSprite.Value
            : FrontSprite.Value;

        switch (activity)
        {
            case AssistantActivity.Listening:
                ShowBubble("我在听～ 请说");
                break;
            case AssistantActivity.Thinking:
                ShowBubble("让我想想…");
                break;
            case AssistantActivity.Working:
                ShowBubble("干活中：" + LatestText(AssistantMessageRole.Tool, "处理任务").TrimStart('⚙', ' '));
                break;
            case AssistantActivity.Replying:
                ShowBubble(Shorten(LatestText(AssistantMessageRole.Assistant, "…"), fromEnd: true));
                break;
            case AssistantActivity.Error:
                ShowBubble("呜，出错了：" + Shorten(LatestText(AssistantMessageRole.Error, "未知错误")), autoHide: true);
                break;
            case AssistantActivity.Idle:
                // 刚完成一轮对话时，用气泡展示最终回复，过一会儿自动收起。
                if (_previousActivity is not (AssistantActivity.Idle or AssistantActivity.Error)
                    && !string.IsNullOrWhiteSpace(AssistantService.LastReply))
                    ShowBubble(Shorten(AssistantService.LastReply), autoHide: true);
                else if (!_bubbleHideTimer.IsEnabled)
                    HideBubble();
                break;
        }
        _previousActivity = activity;
        UpdateMotionTimer();
    }

    private string LatestText(AssistantMessageRole role, string fallback) =>
        AssistantService.Messages.LastOrDefault(m => m.Role == role)?.Text is { Length: > 0 } text ? text : fallback;

    /// <summary>
    /// 回复以流式方式到达时，跟随最新一条助手消息更新气泡。
    /// </summary>
    private void WatchLatestMessage()
    {
        var latest = AssistantService.Messages.LastOrDefault(m => m.Role is AssistantMessageRole.Assistant or AssistantMessageRole.Tool);
        if (latest == _watchedMessage)
            return;
        if (_watchedMessage != null)
            _watchedMessage.PropertyChanged -= OnWatchedMessageChanged;
        _watchedMessage = latest;
        if (_watchedMessage != null)
            _watchedMessage.PropertyChanged += OnWatchedMessageChanged;
    }

    private void OnWatchedMessageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssistantChatMessage.Text) || sender is not AssistantChatMessage message)
            return;
        if (message.Role == AssistantMessageRole.Assistant && AssistantService.Activity == AssistantActivity.Replying)
            BubbleText.Text = Shorten(message.Text, fromEnd: true);
        else if (message.Role == AssistantMessageRole.Tool && AssistantService.Activity == AssistantActivity.Working)
            BubbleText.Text = "干活中：" + message.Text.TrimStart('⚙', ' ');
    }

    private static string Shorten(string text, bool fromEnd = false)
    {
        const int max = 120;
        text = text.Replace("\r", "").Trim();
        if (text.Length <= max)
            return text;
        return fromEnd ? "…" + text[^max..] : text[..max] + "…";
    }

    private void ShowBubble(string text, bool autoHide = false)
    {
        BubbleText.Text = text;
        Bubble.IsVisible = true;
        _bubbleHideTimer.Stop();
        if (autoHide)
            _bubbleHideTimer.Start();
    }

    private void HideBubble()
    {
        Bubble.IsVisible = false;
    }

    #endregion

    #region 动作

    private void UpdateMotionTimer()
    {
        var activity = AssistantService.Activity;
        TimeSpan? interval = activity switch
        {
            AssistantActivity.Working => TimeSpan.FromSeconds(0.5),
            AssistantActivity.Thinking => TimeSpan.FromSeconds(0.9),
            AssistantActivity.Listening or AssistantActivity.Replying => TimeSpan.FromSeconds(1.2),
            _ when Settings.IsPetAnimationEnabled => TimeSpan.FromSeconds(4),
            _ => null
        };
        if (interval == null || !IsVisible)
        {
            _motionTimer.Stop();
            _motionUp = false;
            Sprite.RenderTransform = Rest;
            return;
        }
        _motionTimer.Interval = interval.Value;
        if (!_motionTimer.IsEnabled)
            _motionTimer.Start();
    }

    private void OnMotionTick()
    {
        if (_isDragging)
            return;
        _motionUp = !_motionUp;
        // 空闲时上下浮动一次就落回，其它状态持续轻跳。
        Sprite.RenderTransform = _motionUp ? Hop : Rest;
        if (AssistantService.Activity == AssistantActivity.Idle && _motionUp)
            DispatcherTimer.RunOnce(() => Sprite.RenderTransform = Rest, TimeSpan.FromSeconds(0.5));
    }

    #endregion

    #region 交互

    private void Root_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _isPressed = true;
        _isDragging = false;
        _pressScreenPoint = this.PointToScreen(e.GetPosition(this));
        _pressWindowPosition = Position;
        Sprite.RenderTransform = Squash;
        e.Pointer.Capture(RootPanel);
        e.Handled = true;
    }

    private void Root_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPressed)
            return;
        var current = this.PointToScreen(e.GetPosition(this));
        var dx = current.X - _pressScreenPoint.X;
        var dy = current.Y - _pressScreenPoint.Y;
        if (!_isDragging && Math.Abs(dx) + Math.Abs(dy) < DragThreshold * DesktopScaling)
            return;
        if (!_isDragging)
        {
            _isDragging = true;
            Sprite.Source = SideSprite.Value;
        }
        // 侧面立绘朝左，向右拖动时镜像，让桌宠面朝移动方向。
        var facing = dx > 0 ? Mirror : Rest;
        if (!ReferenceEquals(Sprite.RenderTransform, facing))
            Sprite.RenderTransform = facing;
        Position = new PixelPoint(_pressWindowPosition.X + dx, _pressWindowPosition.Y + dy);
    }

    private void Root_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPressed)
            return;
        _isPressed = false;
        e.Pointer.Capture(null);
        if (_isDragging)
        {
            _isDragging = false;
            _anchor = GetAnchor();
            Settings.PetPositionX = _anchor.X;
            Settings.PetPositionY = _anchor.Y;
            ApplyActivity();
            Sprite.RenderTransform = Rest;
            return;
        }
        Sprite.RenderTransform = Hop;
        DispatcherTimer.RunOnce(() => Sprite.RenderTransform = Rest, TimeSpan.FromSeconds(0.25));
        if (e.InitialPressMouseButton == MouseButton.Left)
            AssistantService.RequestShowWindow();
    }

    private void MenuItemChat_OnClick(object? sender, RoutedEventArgs e) => AssistantService.RequestShowWindow();

    private void MenuItemStop_OnClick(object? sender, RoutedEventArgs e) => AssistantService.Cancel();

    private void MenuItemSettings_OnClick(object? sender, RoutedEventArgs e) =>
        App.GetService<SettingsWindowNew>().Open("assistant");

    private void MenuItemHide_OnClick(object? sender, RoutedEventArgs e) => Settings.IsPetEnabled = false;

    #endregion
}
