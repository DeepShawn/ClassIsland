using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClassIsland.Services.Assistant;

/// <summary>
/// 内置 API 后端使用的联网搜索与网页读取工具。DSH 后端自带 web_search / web_fetch，不使用这里的实现。
/// </summary>
public static partial class WebTools
{
    private const int MaxPageChars = 8000;

    private static readonly HttpClient HttpClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        // Bing 对没有浏览器 UA 的请求会返回精简页面，结构不同，无法解析。
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.6");
        return client;
    }

    /// <summary>
    /// 使用必应搜索，返回前若干条结果的标题、链接与摘要。
    /// </summary>
    public static async Task<string> SearchAsync(string query, int count, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "搜索词为空。";
        count = Math.Clamp(count, 1, 10);
        var url = "https://cn.bing.com/search?q=" + Uri.EscapeDataString(query);
        var html = await HttpClient.GetStringAsync(url, cancellationToken);

        var sb = new StringBuilder();
        var index = 0;
        foreach (Match block in AlgoBlockRegex().Matches(html))
        {
            var link = LinkRegex().Match(block.Value);
            if (!link.Success)
                continue;
            var href = WebUtility.HtmlDecode(link.Groups["href"].Value);
            var title = CleanText(link.Groups["title"].Value);
            var snippetMatch = SnippetRegex().Match(block.Value);
            var snippet = snippetMatch.Success ? CleanText(snippetMatch.Groups["text"].Value) : "";
            index++;
            sb.AppendLine($"{index}. {title}");
            sb.AppendLine($"   {href}");
            if (!string.IsNullOrWhiteSpace(snippet))
                sb.AppendLine($"   {snippet}");
            if (index >= count)
                break;
        }

        return index == 0
            ? $"没有解析到搜索结果，可以改用 web_fetch 打开 {url} 查看。"
            : sb.ToString();
    }

    /// <summary>
    /// 读取网页并转换为纯文本。
    /// </summary>
    public static async Task<string> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "只支持 http/https 网址。";
        using var response = await HttpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return $"请求失败：HTTP {(int)response.StatusCode}";
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.StartsWith("text/") && !mediaType.Contains("json") && !mediaType.Contains("xml"))
            return $"该网址不是文本内容（{mediaType}），无法读取。";
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var text = mediaType.Contains("html") ? HtmlToText(content) : content;
        return text.Length > MaxPageChars ? text[..MaxPageChars] + "\n……（内容过长，已截断）" : text;
    }

    private static string HtmlToText(string html)
    {
        var title = TitleRegex().Match(html);
        html = DropBlocksRegex().Replace(html, " ");
        html = BlockTagRegex().Replace(html, "\n");
        var text = WebUtility.HtmlDecode(TagRegex().Replace(html, " "));
        var lines = text.Split('\n')
            .Select(l => SpacesRegex().Replace(l, " ").Trim())
            .Where(l => l.Length > 0);
        var body = string.Join('\n', lines);
        return title.Success ? $"标题：{CleanText(title.Groups[1].Value)}\n\n{body}" : body;
    }

    private static string CleanText(string html) =>
        SpacesRegex().Replace(WebUtility.HtmlDecode(TagRegex().Replace(html, "")), " ").Trim();

    [GeneratedRegex("""<li class="b_algo".*?</li>""", RegexOptions.Singleline)]
    private static partial Regex AlgoBlockRegex();

    [GeneratedRegex("""<h2[^>]*>\s*<a[^>]*href="(?<href>[^"]+)"[^>]*>(?<title>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex LinkRegex();

    [GeneratedRegex("""<p[^>]*>(?<text>.*?)</p>""", RegexOptions.Singleline)]
    private static partial Regex SnippetRegex();

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("<(script|style|noscript|svg|head)[^>]*>.*?</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DropBlocksRegex();

    [GeneratedRegex("<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("[ \\t\\r\\u00a0]+")]
    private static partial Regex SpacesRegex();
}
