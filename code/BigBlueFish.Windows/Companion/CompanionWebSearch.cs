using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;

// Keyless web lookup for the local chat. The search page is parsed into short
// title/snippet pairs so a small model can quote facts instead of guessing.
internal sealed class CompanionWebSearch : IDisposable
{
    private const int MaxResults = 4;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly Regex Block = new(@"<li class=""b_algo""[\s\S]*?(?=<li class=""b_algo""|</ol>)", RegexOptions.IgnoreCase);
    private static readonly Regex Heading = new(@"<h2[^>]*>[\s\S]*?<a[^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
    private static readonly Regex Anchor = new(@"<a[^>]*href=""(https?://[^""]+)""", RegexOptions.IgnoreCase);
    private static readonly Regex Snippet = new(@"<p[^>]*>([\s\S]*?)</p>", RegexOptions.IgnoreCase);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[ \t\u00a0]+", RegexOptions.Compiled);

    private readonly HttpClient web = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly Dictionary<string, (DateTimeOffset At, string Text)> cache = new();
    private readonly object sync = new();

    internal CompanionWebSearch()
    {
        web.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        web.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
    }

    public void Dispose() => web.Dispose();

    internal async Task<string> Search(string query, CancellationToken token = default)
    {
        query = query.Trim();
        if (query.Length == 0) return "";
        lock (sync)
            if (cache.TryGetValue(query, out var hit) && DateTimeOffset.UtcNow - hit.At <= Lifetime && hit.Text.Length > 0)
                return hit.Text;
        var url = "https://cn.bing.com/search?q=" + Uri.EscapeDataString(query) + "&setlang=zh-CN&count=8";
        using var response = await web.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        var text = Parse(await response.Content.ReadAsStringAsync(token));
        if (text.Length == 0) return "";
        lock (sync) cache[query] = (DateTimeOffset.UtcNow, text);
        return text;
    }

    internal static string Parse(string html)
    {
        var results = new List<string>();
        foreach (Match block in Block.Matches(html))
        {
            var chunk = block.Value;
            var title = Clean(Heading.Match(chunk) is { Success: true } heading ? heading.Groups[1].Value : "");
            if (title.Length == 0) continue;
            var snippet = Clean(Snippet.Match(chunk) is { Success: true } paragraph ? paragraph.Groups[1].Value : "");
            var site = "";
            if (Anchor.Match(chunk) is { Success: true } anchor && Uri.TryCreate(anchor.Groups[1].Value, UriKind.Absolute, out var link))
                site = link.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
            if (snippet.Length > 220) snippet = snippet[..220] + "…";
            results.Add((results.Count + 1) + ". " + title + (site.Length > 0 ? "（" + site + "）" : "")
                + (snippet.Length > 0 ? "：" + snippet : ""));
            if (results.Count >= MaxResults) break;
        }
        return string.Join("\n", results);
    }

    private static string Clean(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = Tags.Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        text = Spaces.Replace(text, " ").Trim();
        return text;
    }

    internal static string QueryFrom(string prompt)
    {
        var text = prompt.Trim();
        foreach (var prefix in new[] { "搜索：", "搜索:", "搜：", "搜:", "查一下：", "查一下:", "查：" })
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                return text[prefix.Length..].Trim();
        return text;
    }
}
