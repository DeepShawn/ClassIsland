using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassIsland.Models.Assistant;
using ClassIsland.Services;
using ClassIsland.Services.Assistant;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;

namespace ClassIsland.Views.SettingPages;

/// <summary>
/// AI 助手设置页面。
/// </summary>
[SettingsPageInfo("assistant", "AI 助手", "\ue276", "\ue275", SettingsPageCategory.Internal)]
public partial class AssistantSettingsPage : SettingsPageBase
{
    /// <summary>可选的连接模板。</summary>
    public IReadOnlyList<AiProviderTemplate> Templates => AiProviderTemplate.All;

    public SettingsService SettingsService { get; }
    public AssistantService AssistantService { get; }
    public VoiceAssistantService VoiceAssistantService { get; }

    public AssistantSettings Settings => SettingsService.Settings.AssistantSettings;

    public List<string> Cultures { get; } = ["zh-CN", "zh-TW", "zh-HK", "en-US", "en-GB", "ja-JP"];

    public static readonly StyledProperty<string> TestResultProperty =
        AvaloniaProperty.Register<AssistantSettingsPage, string>(nameof(TestResult), "");

    public string TestResult
    {
        get => GetValue(TestResultProperty);
        set => SetValue(TestResultProperty, value);
    }

    public static readonly StyledProperty<bool> IsTestingProperty =
        AvaloniaProperty.Register<AssistantSettingsPage, bool>(nameof(IsTesting));

    public bool IsTesting
    {
        get => GetValue(IsTestingProperty);
        set => SetValue(IsTestingProperty, value);
    }

    public AssistantSettingsPage(SettingsService settingsService, AssistantService assistantService,
        VoiceAssistantService voiceAssistantService)
    {
        SettingsService = settingsService;
        AssistantService = assistantService;
        VoiceAssistantService = voiceAssistantService;
        InitializeComponent();
        DataContext = this;
        ComboBoxTemplate.SelectedItem = Settings.Template;
    }

    private void ComboBoxTemplate_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ComboBoxTemplate.SelectedItem is not AiProviderTemplate template ||
            template.Id == Settings.ProviderTemplateId)
            return;
        // 从内置模板切换到自定义时，以当前模板的值作为起点，方便修改
        if (template.IsCustom)
        {
            if (string.IsNullOrWhiteSpace(Settings.ApiBaseUrl))
                Settings.ApiBaseUrl = Settings.EffectiveApiBaseUrl;
            if (string.IsNullOrWhiteSpace(Settings.Model))
                Settings.Model = Settings.EffectiveModel;
        }
        Settings.ProviderTemplateId = template.Id;
        TestResult = "";
    }

    private void ButtonDshLogin_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            AssistantService.OpenDshWeb();
            TestResult = "已启动 DSH 网页版，请在浏览器中登录 DeepSeek 账号。";
        }
        catch (Exception ex)
        {
            TestResult = "✗ 无法启动 DSH：" + ex.Message;
        }
    }

    private async void ButtonDshReconnect_OnClick(object? sender, RoutedEventArgs e)
    {
        await AssistantService.ResetDshAsync();
        ButtonTest_OnClick(sender, e);
    }

    private void ButtonResetPetPosition_OnClick(object? sender, RoutedEventArgs e)
    {
        Settings.PetPositionX = -1;
        Settings.PetPositionY = -1;
    }

    private void ButtonGetApiKey_OnClick(object? sender, RoutedEventArgs e)
    {
        var url = Settings.Template.ApiKeyUrl;
        if (!string.IsNullOrWhiteSpace(url))
            IAppHost.GetService<IUriNavigationService>().Navigate(new Uri(url));
    }

    private async void ButtonTest_OnClick(object? sender, RoutedEventArgs e)
    {
        IsTesting = true;
        TestResult = "";
        try
        {
            var reply = await AssistantService.TestConnectionAsync();
            TestResult = "✓ " + (string.IsNullOrWhiteSpace(reply) ? "连接成功（空回复）" : reply.Trim());
        }
        catch (Exception ex)
        {
            TestResult = "✗ " + ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void ButtonOpenAssistant_OnClick(object? sender, RoutedEventArgs e)
    {
        App.GetService<AssistantWindow>().Open();
    }
}
