using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ECAssistant.Config;
using ECAssistant.Interfaces;
using System.Text.Json;

namespace ECAssistant.Tools.Web;

/// <summary>
/// EWebFetch — fetch a URL's content and convert HTML to plain text.
/// Simple HTTP GET with HTML-to-text conversion. No JavaScript execution.
/// </summary>
public class EWebFetchTool : EToolBase
{
    private readonly IHttpClient _httpClient;
    private readonly JsonElement? _toolConfig;

    public override string Name => "EWebFetch";

    public override string Description =>
        "Fetch a web page URL and return its content as plain text. " +
        "Handles HTTP/HTTPS, strips HTML tags, extracts readable text. " +
        "Use for: documentation, articles, API reference pages, plain text content.";

    public override string UsageExample => "<toolcall>EWebFetch<url>https://example.com</url></toolcall>";

    public override bool IsEnabled { get; protected set; } = true;

    public EWebFetchTool(IHttpClient httpClient, EAgentConfig config)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        config.Tools.TryGetValue(Name, out var tc);
        _toolConfig = tc.ValueKind == JsonValueKind.Undefined ? null : tc;
        IsEnabled = ReadCfg(_toolConfig, "enabled", true);
    }

    public override object GetConfigSection() => new { enabled = true };

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var url = arguments.GetValueOrDefault("url")?.Trim() ?? "";
        var maxChars = int.TryParse(arguments.GetValueOrDefault("maxchars"), out var mc) ? mc : 6000;

        if (string.IsNullOrWhiteSpace(url))
            return EToolResult.Failure(Name, "Missing required argument: url");

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            return EToolResult.Failure(Name, $"Invalid URL: {url}");

        try
        {
            var html = await _httpClient.GetAsync(url, cancellationToken);
            var text = HtmlToText(html);

            if (text.Length > maxChars)
                text = text.Substring(0, maxChars) + "\n\n... [truncated]";

            return EToolResult.Success(Name, $"Fetched {url} ({text.Length} chars)\n\n{text}");
        }
        catch (TaskCanceledException)
        {
            return EToolResult.Failure(Name, $"Request timed out: {url}");
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Error fetching URL: {ex.Message}");
        }
    }

    private static T ReadCfg<T>(JsonElement? section, string key, T defaultValue)
    {
        if (section.HasValue && section.Value.ValueKind == JsonValueKind.Object)
        {
            if (section.Value.TryGetProperty(key, out var prop))
            {
                try { return prop.Deserialize<T>() ?? defaultValue; } catch { return defaultValue; }
            }
        }
        return defaultValue;
    }

    private string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var text = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ");
        text = text.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n");
        text = text.Replace("</p>", "\n\n").Replace("</div>", "\n");
        text = text.Replace("<p>", "").Replace("<div>", "");

        var lines = text.Split('\n');
        var result = new StringBuilder();
        string prevLine = "";

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                if (prevLine != trimmed)
                    result.AppendLine(trimmed);
                prevLine = trimmed;
            }
        }

        return result.ToString().Trim();
    }
}