using System.Collections.Generic;
using System.Linq;

namespace ClassIsland.Models.Assistant;

/// <summary>
/// AI 接口连接模板。选择模板后自动填充接口地址与模型，只需填写 API Key 即可连接。
/// </summary>
/// <param name="Id">模板 ID</param>
/// <param name="Name">显示名称</param>
/// <param name="BaseUrl">OpenAI 兼容接口地址</param>
/// <param name="Model">默认模型</param>
/// <param name="ApiKeyUrl">获取 API Key 的网址</param>
/// <param name="RequiresApiKey">是否需要 API Key</param>
public record AiProviderTemplate(
    string Id,
    string Name,
    string BaseUrl,
    string Model,
    string ApiKeyUrl,
    bool RequiresApiKey = true)
{
    /// <summary>自定义模板 ID。</summary>
    public const string CustomId = "custom";

    /// <summary>默认模板 ID。</summary>
    public const string DefaultId = "deepseek";

    /// <summary>是否为自定义模板（需要手动填写地址和模型）。</summary>
    public bool IsCustom => Id == CustomId;

    /// <summary>内置模板列表。</summary>
    public static IReadOnlyList<AiProviderTemplate> All { get; } =
    [
        new("deepseek", "DeepSeek（推荐）", "https://api.deepseek.com/v1", "deepseek-chat",
            "https://platform.deepseek.com/api_keys"),
        new("qwen", "通义千问（阿里云百炼）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus",
            "https://bailian.console.aliyun.com/?apiKey=1"),
        new("moonshot", "Kimi（月之暗面）", "https://api.moonshot.cn/v1", "moonshot-v1-8k",
            "https://platform.moonshot.cn/console/api-keys"),
        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash",
            "https://open.bigmodel.cn/usercenter/apikeys"),
        new("siliconflow", "硅基流动", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct",
            "https://cloud.siliconflow.cn/account/ak"),
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini",
            "https://platform.openai.com/api-keys"),
        new("ollama", "Ollama（本地，无需 Key）", "http://localhost:11434/v1", "qwen2.5:7b",
            "https://ollama.com/download", RequiresApiKey: false),
        new(CustomId, "自定义", "", "", ""),
    ];

    /// <summary>按 ID 查找模板，找不到时返回默认模板。</summary>
    public static AiProviderTemplate Get(string? id) =>
        All.FirstOrDefault(x => x.Id == id) ?? All.First(x => x.Id == DefaultId);
}
