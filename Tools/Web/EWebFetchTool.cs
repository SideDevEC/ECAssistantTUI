using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Web;

/// <summary>
/// EWebFetch — fetch a URL's content and convert HTML to plain text.
/// Simple HTTP GET with HTML-to-text conversion. No JavaScript execution.
/// </summary>
public class EWebFetchTool : ITool
{
    private readonly IHttpClient _httpClient;
    private readonly IConfigProvider _configProvider;

    public EWebFetchTool(IHttpClient httpClient, IConfigProvider configProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public string Name => "EWebFetch";

    public string Description =>
        "Fetch a web page URL and return its content as plain text. " +
        "Handles HTTP/HTTPS, strips HTML tags, extracts readable text. " +
        "Use for: documentation, articles, API reference pages, plain text content.";

    public ECAssistant.Interfaces.ToolPolicy GetPolicy() => ECAssistant.Interfaces.ToolPolicy.Approved(Name);

    public async Task<string> ExecuteAsync(string input, CancellationToken ct = default)
    {
        var args = ParseInput(input);
        var url = args.TryGetValue("url", out var urlVal) ? urlVal : "";
        var maxChars = int.TryParse(args.GetValueOrDefault("maxchars", "6000"), out var mc) ? mc : 6000;

        if (string.IsNullOrWhiteSpace(url))
            return $"[FAILED] {Name}: Missing required argument: url";

        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            return $"[FAILED] {Name}: Invalid URL: {url}";

        try
        {
            var html = await _httpClient.GetAsync(url, ct);
            var text = HtmlToText(html);

            if (text.Length > maxChars)
                text = text.Substring(0, maxChars) + "\n\n... [truncated]";

            return $"[SUCCESS] {Name}: Fetched {url} ({text.Length} chars)\n\n{text}";
        }
        catch (TaskCanceledException)
        {
            return $"[FAILED] {Name}: Request timed out (15s): {url}";
        }
        catch (Exception ex)
        {
            return $"[FAILED] {Name}: Error fetching URL: {ex.Message}";
        }
    }

    private Dictionary<string, string> ParseInput(string input)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(input))
            return result;

        // XML tag format from ToolAdapter: <url>...</url>
        var xmlMatches = Regex.Matches(input, @"<(\w+)>(.*?)</\1>");
        foreach (Match m in xmlMatches)
            result[m.Groups[1].Value] = m.Groups[2].Value;

        if (result.Count > 0) return result;

        // Try key=value or key="value" format
        var matches = Regex.Matches(input, @"(\w+)\s*=\s*""([^""]*)""|(\w+)\s*=\s*(\S+)");
        foreach (Match match in matches)
        {
            var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
            var value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value;
            result[key] = value;
        }

        return result;
    }

    private string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        // Remove script and style content
        var text = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Remove HTML tags
        text = Regex.Replace(text, "<[^>]+>", " ");

        // Decode HTML entities
        text = WebUtility.HtmlDecode(text);

        // Clean up whitespace
        text = Regex.Replace(text, @"\s+", " ");

        // Preserve line breaks for block elements
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