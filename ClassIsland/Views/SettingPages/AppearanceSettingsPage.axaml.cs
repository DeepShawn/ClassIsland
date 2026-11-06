using ClassIsland.Core.Abstractions.Controls;
using System;
using System.Windows;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Attributes;
using ClassIsland.Services;
using ClassIsland.ViewModels.SettingsPages;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Shared;
using System.Linq;
using Avalonia.Platform.Storage;
using ClassIsland.Core.Helpers.UI;
using ClassIsland.Platforms.Abstraction;

namespace ClassIsland.Views.SettingPages;

/// <summary>
/// AppearanceSettingsPage.xaml 的交互逻辑
/// </summary>
[Group("classisland.mainwindow")]
[SettingsPageInfo("appearance", "外观", "\ue51e", "\ue51d", SettingsPageCategory.Internal)]
public partial class AppearanceSettingsPage : SettingsPageBase
{
    public AppearanceSettingsViewModel ViewModel { get; } = IAppHost.GetService<AppearanceSettingsViewModel>();

    public BackgroundImageService BackgroundImageService { get; } = IAppHost.GetService<BackgroundImageService>();

    public AppearanceSettingsPage()
    {
        InitializeComponent();
        DataContext = this;
    }

    private async void ButtonUpdateWallpaper_OnClick(object sender, SelectionChangedEventArgs e)
    {
        // if (SettingsService.Settings.ColorSource is 1 or 3)
        //     await WallpaperPickingService.GetWallpaperAsync();
    }

    private void ButtonPreviewWallpaper_OnClick(object sender, RoutedEventArgs e)
    {
        // var w = App.GetService<WallpaperPreviewWindow>();
        // w.Owner = Window.GetWindow(this);
        // w.ShowDialog();
    }

    private async void ButtonBrowseWindows_OnClick(object sender, RoutedEventArgs e)
    {
        // var w = new WindowsPicker(SettingsService.Settings.WallpaperClassName)
        // {
        //     Owner = Window.GetWindow(this),
        // };
        // var r = w.ShowDialog();
        // SettingsService.Settings.WallpaperClassName = w.SelectedResult ?? "";
        // if (r == true)
        // {
        //     await WallpaperPickingService.GetWallpaperAsync();
        // }
        // GC.Collect();
    }

    #region 背景图片

    private async void ExpanderBackgroundImage_OnLoaded(object? sender, RoutedEventArgs e)
    {
        await BackgroundImageService.LoadLibraryAsync();
    }

    private async void ButtonImportBackgroundImage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
            return;
        var paths = await PlatformServices.FilePickerService.OpenFilesPickerAsync(new FilePickerOpenOptions
        {
            Title = "选择背景图片",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("图片")
                {
                    Patterns = BackgroundImageService.SupportedExtensions.Select(x => "*" + x).ToList()
                }
            ]
        }, topLevel);
        if (paths.Count == 0)
            return;
        try
        {
            var items = await BackgroundImageService.ImportAsync(paths);
            this.ShowSuccessToast($"已导入 {items.Count} 张背景图片");
        }
        catch (Exception ex)
        {
            this.ShowErrorToast("导入背景图片失败：" + ex.Message);
        }
    }

    private void ButtonBackgroundImageItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BackgroundImageItem item })
            BackgroundImageService.Apply(item);
    }

    private void MenuItemApplyBackgroundImage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BackgroundImageItem item })
            BackgroundImageService.Apply(item);
    }

    private async void MenuItemDeleteBackgroundImage_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: BackgroundImageItem item })
            return;
        var confirmed = await ContentDialogHelper.ShowConfirmationDialog("删除背景图片",
            $"确定要从背景图片库中删除“{item.FileName}”吗？此操作无法撤销。", root: TopLevel.GetTopLevel(this), positiveText: "删除");
        if (confirmed)
            BackgroundImageService.Delete(item);
    }

    private void ButtonOpenBackgroundFolder_OnClick(object? sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(BackgroundImageService.LibraryFolderPath);
        IAppHost.GetService<ClassIsland.Services.Assistant.DesktopOperationService>()
            .Open(System.IO.Path.GetFullPath(BackgroundImageService.LibraryFolderPath));
    }

    #endregion
}
