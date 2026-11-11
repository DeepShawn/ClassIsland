using System;
using System.Collections.Generic;
using System.Linq;
using ClassIsland.Core.Models.Components;

namespace ClassIsland.Helpers;

/// <summary>
/// 精简模式：只保留课表、天气与 AI 助手相关功能。其余功能的代码仍然保留，只是从界面中隐藏，关闭精简模式即可恢复。
/// </summary>
public static class LiteModeHelper
{
    /// <summary>
    /// 精简模式下隐藏的设置页面 ID。
    /// </summary>
    public static IReadOnlySet<string> HiddenSettingsPageIds { get; } = new HashSet<string>
    {
        "advanced",
        "automation",
        "components",
        "classisland.plugins",
        "classisland.themes",
        "update",
        "refreshing",
        "privacy",
        "storage",
        "management",
        "management.credentials",
        "management.policy",
    };

    /// <summary>
    /// 精简模式下组件库中仍可添加的组件：课程表、天气简报和分割线。
    /// </summary>
    public static IReadOnlySet<Guid> AllowedComponentIds { get; } = new HashSet<Guid>
    {
        new("1DB2017D-E374-4BC6-9D57-0B4ADF03A6B8"),
        new("CA495086-E297-4BEB-9603-C5C1C1A8551E"),
        new("AB0F26D5-9DF6-4575-B844-73B04D0907C1"),
    };

    public static IReadOnlyList<ComponentGroup> FilterComponentGroups(IReadOnlyList<ComponentGroup> groups,
        bool isLiteModeEnabled)
    {
        if (!isLiteModeEnabled)
            return groups;
        return groups
            .Select(g => g with { Components = g.Components.Where(c => AllowedComponentIds.Contains(c.Guid)).ToList() })
            .Where(g => g.Components.Count > 0)
            .ToList();
    }
}
