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

namespace ClassIsland.Views.SettingPages;

/// <summary>
/// AI 助手设置页面。
/// </summary>
[SettingsPageInfo("assistant", "AI 助手", "\ue276", "\ue275", SettingsPageCategory.Internal)]
public partial class AssistantSettingsPage : SettingsPageBase
{
    private static readonly (string BaseUrl, string Model)[] Presets =
    [
        ("https://api.deepseek.com/v1", "deepseek-chat"),
        ("https://api.openai.com/v1", "gpt-4o-mini"),
        ("https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        ("https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        ("https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        ("https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct"),
        ("http://localhost:11434/v1", "qwen2.5:7b"),
    ];

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
    }

    private void ComboBoxPreset_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: >= 0 } box || box.SelectedIndex >= Presets.Length)
            return;
        var preset = Presets[box.SelectedIndex];
        Settings.ApiBaseUrl = preset.BaseUrl;
        Settings.Model = preset.Model;
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
