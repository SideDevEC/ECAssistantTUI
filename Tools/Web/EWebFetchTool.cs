using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Session;

namespace ECAssistant.Tools.Web;

/// <summary>
/// EWebFetch — fetch a URL's content and convert HTML to plain text.
///
/// Simple HTTP GET with HTML-to-text conversion. No JavaScript execution.
/// For JS-heavy sites, the LLM should use EShellAgent with a headless browser.
///
/// Args:
/// - url (required): URL to fetch
/// - maxchars (optional): max chars to return (default 6000)
/// - selector (optional): CSS-like selector hint (not fully implemented — just looks for tag)
/// </summary>
public class EWebFetchTool : EToolBase
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public override string Name => "EWebFetch";

    public override string Description =>
        "Fetch a web page URL and return its content as plain text. " +
        "Handles HTTP/HTTPS, strips HTML tags, extracts readable text. " +
        "No JavaScript execution — for static pages only.";

    public override string UsageExample =>
        "<toolcall>EWebFetch<url>https://example.com/docs</url></toolcall>\n" +
        "<toolcall>EWebFetch<url>https://example.com/docs</url><maxchars>3000</maxchars></toolcall>";

    public override string GetToolRules() =>
        "Rules:\n" +
        "1. url (required): full URL including https://\n" +
        "2. maxchars (optional): max chars to return (default 6000, max 20000)\n" +
        "3. Use for documentation pages, API references, articles — NOT for JS-heavy SPAs\n" +
        "4. For search, use EWebSearch first, then EWebFetch to get page content";

    public override string GetToolExample() =>
        "Examples:\n" +
        "Fetch a doc page: <toolcall>EWebFetch<url>https://learn.microsoft.com/dotnet</url></toolcall>\n" +
        "Fetch with limit: <toolcall>EWebFetch<url>https://example.com/long-page</url><maxchars>3000</maxchars></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var url = arguments.GetValueOrDefault("url")?.Trim();
        if (string.IsNullOrEmpty(url))
            return EToolResult.Failure(Name, "Missing required argument: url");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return EToolResult.Failure(Name, $"Invalid URL: {url}");

        var maxChars = 6000;
        if (arguments.TryGetValue("maxchars", out var mcStr) && int.TryParse(mcStr, out var mc))
            maxChars = Math.Max(500, Math.Min(20000, mc));

        try
        {
            // Add user-agent header — some sites block default HttpClient UA
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (compatible; ECAssistant/1.0)");
            request.Headers.Add("Accept", "text/html, application/json, text/plain, */*");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            string text;

            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                // JSON — return as-is (pretty-printed)
                text = content;
            }
            else if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || content.Contains("<html", StringComparison.OrdinalIgnoreCase))
            {
                // HTML — convert to plain text
                text = HtmlToText(content);
            }
            else
            {
                // Plain text or unknown — return as-is
                text = content;
            }

            // Truncate to maxChars
            if (text.Length > maxChars)
            {
                text = text.Substring(0, maxChars) + $"\n\n[Truncated at {maxChars} chars. Total: {text.Length} chars. URL: {url}]";
            }

            var header = $"URL: {url}\nStatus: {(int)response.StatusCode} {response.StatusCode}\nContent-Type: {contentType}\nLength: {text.Length} chars\n\n";
            return EToolResult.Success(Name, header + text);
        }
        catch (TaskCanceledException)
        {
            return EToolResult.Failure(Name, $"Request timed out (15s): {url}");
        }
        catch (HttpRequestException ex)
        {
            return EToolResult.Failure(Name, $"HTTP error: {ex.Message} (URL: {url})");
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Error fetching URL: {ex.Message}");
        }
    }

    /// <summary>Convert HTML to plain text — strips tags, preserves structure.</summary>
    private static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        // Remove script and style sections entirely
        html = Regex.Replace(html, @"<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<nav[^>]*>.*?</nav>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<footer[^>]*>.*?</footer>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<header[^>]*>.*?</header>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Convert common block elements to newlines
        html = Regex.Replace(html, @"</(p|div|section|article|h[1-6]|li|tr|td|th|blockquote|pre)>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<br\s*>", "\n", RegexOptions.IgnoreCase);

        // Convert links: keep text, note href
        html = Regex.Replace(html, @"<a[^>]*href=[""']([^""']+)[""'][^>]*>(.*?)</a>", "$2 ($1)", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Strip all remaining HTML tags
        html = Regex.Replace(html, @"<[^>]+>", "", RegexOptions.Singleline);

        // Decode common HTML entities
        html = html.Replace("&nbsp;", " ")
                   .Replace("&amp;", "&")
                   .Replace("&lt;", "<")
                   .Replace("&gt;", ">")
                   .Replace("&quot;", "\"")
                   .Replace("&#39;", "'")
                   .Replace("&apos;", "'")
                   .Replace("&hellip;", "...")
                   .Replace("&mdash;", "—")
                   .Replace("&ndash;", "–");

        // Clean up whitespace
        var lines = html.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l));

        // Collapse multiple blank lines to single
        var result = new StringBuilder();
        string? prevLine = null;
        foreach (var line in lines)
        {
            if (prevLine == null || line != prevLine || !string.IsNullOrWhiteSpace(line))
                result.AppendLine(line);
            prevLine = line;
        }

        return result.ToString().Trim();
    }
}