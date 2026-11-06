using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassIsland.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services;

/// <summary>
/// 背景图片库中的一张图片。
/// </summary>
public partial class BackgroundImageItem : ObservableObject
{
    public required string Path { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty] private Bitmap? _thumbnail;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 背景图片服务：管理背景图片库（导入、删除、选择），并生成主界面使用的背景画刷。
/// </summary>
public partial class BackgroundImageService : ObservableObject
{
    /// <summary>支持导入的图片扩展名。</summary>
    public static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif"];

    /// <summary>加载背景图片时的最大宽度，避免超大图片占用过多内存。</summary>
    private const int MaxDecodeWidth = 2560;

    private const int ThumbnailWidth = 200;

    private SettingsService SettingsService { get; }
    private ILogger<BackgroundImageService> Logger { get; }

    private Models.Settings Settings => SettingsService.Settings;

    /// <summary>背景图片库目录。</summary>
    public static string LibraryFolderPath => Path.Combine(CommonDirectories.AppRootFolderPath, "Backgrounds");

    /// <summary>背景图片库。</summary>
    public ObservableCollection<BackgroundImageItem> Library { get; } = [];

    /// <summary>当前背景图片。</summary>
    [ObservableProperty] private Bitmap? _bitmap;

    /// <summary>当前背景画刷。</summary>
    [ObservableProperty] private IBrush? _brush;

    /// <summary>当前背景图片的填充方式（用于 Image 控件）。</summary>
    [ObservableProperty] private Stretch _imageStretch = Stretch.UniformToFill;

    /// <summary>背景图片是否生效（已启用且成功加载）。</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>背景图片是否应用到 AI 助手窗口。</summary>
    public bool IsActiveForAssistant => IsActive && Settings.IsBackgroundImageAppliedToAssistant;

    private Models.Settings? _attachedSettings;
    private bool _libraryLoaded;

    public BackgroundImageService(SettingsService settingsService, ILogger<BackgroundImageService> logger)
    {
        SettingsService = settingsService;
        Logger = logger;
        AttachSettings();
        SettingsService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsService.Settings))
                AttachSettings();
        };
    }

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(IsActiveForAssistant));

    private void AttachSettings()
    {
        if (_attachedSettings != null)
            _attachedSettings.PropertyChanged -= OnSettingsChanged;
        _attachedSettings = Settings;
        _attachedSettings.PropertyChanged += OnSettingsChanged;
        Reload();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Models.Settings.IsBackgroundImageEnabled):
            case nameof(Models.Settings.BackgroundImagePath):
                Reload();
                break;
            case nameof(Models.Settings.BackgroundImageStretch):
                UpdateBrush();
                break;
            case nameof(Models.Settings.IsBackgroundImageAppliedToAssistant):
                OnPropertyChanged(nameof(IsActiveForAssistant));
                break;
        }
    }

    /// <summary>
    /// 根据设置重新加载当前背景图片。
    /// </summary>
    public void Reload()
    {
        var path = Settings.BackgroundImagePath;
        var old = Bitmap;
        Bitmap = null;
        if (Settings.IsBackgroundImageEnabled && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                Bitmap = LoadBitmap(path, MaxDecodeWidth);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "无法加载背景图片 {}", path);
            }
        }
        UpdateBrush();
        if (!ReferenceEquals(old, Bitmap))
            old?.Dispose();
        foreach (var item in Library)
            item.IsSelected = PathEquals(item.Path, path);
    }

    private void UpdateBrush()
    {
        ImageStretch = Settings.BackgroundImageStretch switch
        {
            1 => Stretch.Fill,
            2 => Stretch.Uniform,
            3 => Stretch.None,
            _ => Stretch.UniformToFill
        };
        if (Bitmap == null)
        {
            Brush = null;
            IsActive = false;
            return;
        }

        var brush = new ImageBrush(Bitmap)
        {
            Stretch = ImageStretch,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
        };
        if (Settings.BackgroundImageStretch == 3)
        {
            brush.TileMode = TileMode.Tile;
            brush.AlignmentX = AlignmentX.Left;
            brush.AlignmentY = AlignmentY.Top;
            brush.DestinationRect = new RelativeRect(0, 0, Bitmap.Size.Width, Bitmap.Size.Height, RelativeUnit.Absolute);
        }
        Brush = brush;
        IsActive = true;
    }

    /// <summary>
    /// 加载背景图片库（生成缩略图）。
    /// </summary>
    public async Task LoadLibraryAsync()
    {
        if (_libraryLoaded) return;
        _libraryLoaded = true;
        Directory.CreateDirectory(LibraryFolderPath);
        var files = Directory.EnumerateFiles(LibraryFolderPath)
            .Where(IsSupported)
            .OrderBy(File.GetCreationTimeUtc)
            .ToList();
        foreach (var file in files)
            await AddToLibraryAsync(file);
    }

    /// <summary>
    /// 将图片导入到背景图片库，返回导入后的项目。
    /// </summary>
    /// <param name="sourcePaths">要导入的图片路径</param>
    /// <param name="applyLast">是否将最后一张导入的图片设为当前背景</param>
    public async Task<IReadOnlyList<BackgroundImageItem>> ImportAsync(IEnumerable<string> sourcePaths, bool applyLast = true)
    {
        await LoadLibraryAsync();
        Directory.CreateDirectory(LibraryFolderPath);
        var imported = new List<BackgroundImageItem>();
        foreach (var source in sourcePaths)
        {
            try
            {
                if (!IsSupported(source))
                    throw new NotSupportedException($"不支持的图片格式：{Path.GetExtension(source)}");
                // 验证是有效图片
                using (LoadBitmap(source, ThumbnailWidth)) { }

                var name = Path.GetFileNameWithoutExtension(source);
                var ext = Path.GetExtension(source).ToLowerInvariant();
                var target = Path.Combine(LibraryFolderPath, name + ext);
                for (var i = 1; File.Exists(target); i++)
                    target = Path.Combine(LibraryFolderPath, $"{name} ({i}){ext}");
                await Task.Run(() => File.Copy(source, target));
                Logger.LogInformation("导入背景图片 {} -> {}", source, target);
                var item = await AddToLibraryAsync(target);
                if (item != null)
                    imported.Add(item);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "无法导入背景图片 {}", source);
                throw new InvalidOperationException($"无法导入 {Path.GetFileName(source)}：{e.Message}", e);
            }
        }

        if (applyLast && imported.Count > 0)
            Apply(imported[^1]);
        return imported;
    }

    /// <summary>
    /// 将图片设为当前背景并启用背景图片。
    /// </summary>
    public void Apply(BackgroundImageItem item)
    {
        Settings.BackgroundImagePath = item.Path;
        Settings.IsBackgroundImageEnabled = true;
    }

    /// <summary>
    /// 从背景图片库中删除图片。
    /// </summary>
    public void Delete(BackgroundImageItem item)
    {
        if (PathEquals(item.Path, Settings.BackgroundImagePath))
        {
            Settings.IsBackgroundImageEnabled = false;
            Settings.BackgroundImagePath = "";
        }
        Library.Remove(item);
        item.Thumbnail?.Dispose();
        try
        {
            File.Delete(item.Path);
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "无法删除背景图片 {}", item.Path);
        }
    }

    private async Task<BackgroundImageItem?> AddToLibraryAsync(string path)
    {
        try
        {
            var thumb = await Task.Run(() => LoadBitmap(path, ThumbnailWidth));
            var item = new BackgroundImageItem
            {
                Path = path,
                Thumbnail = thumb,
                IsSelected = PathEquals(path, Settings.BackgroundImagePath)
            };
            await Dispatcher.UIThread.InvokeAsync(() => Library.Add(item));
            return item;
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "无法加载背景图片缩略图 {}", path);
            return null;
        }
    }

    private static Bitmap LoadBitmap(string path, int maxWidth)
    {
        using var stream = File.OpenRead(path);
        var full = new Bitmap(stream);
        if (full.PixelSize.Width <= maxWidth)
            return full;
        full.Dispose();
        stream.Position = 0;
        return Bitmap.DecodeToWidth(stream, maxWidth, BitmapInterpolationMode.HighQuality);
    }

    private static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static bool PathEquals(string a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
