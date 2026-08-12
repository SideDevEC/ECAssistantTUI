using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Web;

namespace ECAssistant.Tools.Web;

/// <summary>
/// Web Search Tool — lets the LLM search the web using DuckDuckGo Instant Answer API.
/// No API key needed, no authentication, no rate limits for reasonable use.
/// 
/// Usage:
///   <toolcall>EWebSearch<query>your search query</query></toolcall>
///   <toolcall>EWebSearch<query>how to parse JSON in C#</query><max_results>5</max_results></toolcall>
/// </summary>
public class EWebSearchTool : EToolBase
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public override string Name => "EWebSearch";

    public override string Description =>
        "Search the web using DuckDuckGo. Returns search results with titles, URLs, and snippets. " +
        "Use for: finding documentation, looking up APIs, getting code examples, researching topics. " +
        "No authentication needed.";

    public override string UsageExample =>
        "EWebSearch(query=\"how to parse JSON in C#\")";

    public override string GetToolRules() =>
        "<query>=search terms. <max_results>?(default 5). Use for external info.";


    public override string GetToolExample() =>
        "<toolcall>EWebSearch<query>how to parse JSON in C#</query></toolcall>\n" +
        "<toolcall>EWebSearch<query>dotnet build error CS0006</query><max_results>3</max_results></toolcall>";

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var query = arguments.GetValueOrDefault("query");
        if (string.IsNullOrWhiteSpace(query))
            return EToolResult.Failure(Name, "Missing 'query' argument.");

        var maxResults = 5;
        if (arguments.TryGetValue("max_results", out var maxStr) && int.TryParse(maxStr, out var m))
            maxResults = Math.Clamp(m, 1, 10);

        try
        {
            // ── Strategy 1: DuckDuckGo Instant Answer API ──
            var ddgUrl = $"https://api.duckduckgo.com/?q={HttpUtility.UrlEncode(query)}&format=json&no_html=1&skip_disambig=1";
                        // v10.9.3: Cancellation support
            if (cancellationToken.IsCancellationRequested)
                return EToolResult.Failure(Name, "[CANCELLED] Web search was cancelled by user.");
            var ddgResponse = await _httpClient.GetStringAsync(ddgUrl, cancellationToken);
            var ddgJson = JsonDocument.Parse(ddgResponse);

            var sb = new StringBuilder();
            sb.AppendLine($"Search results for: \"{query}\"\n");

            var hasResults = false;

            // Abstract (main answer)
            var abstractText = ddgJson.RootElement.GetProperty("AbstractText").GetString();
            var abstractSource = ddgJson.RootElement.GetProperty("AbstractSource").GetString();
            var abstractUrl = ddgJson.RootElement.GetProperty("AbstractURL").GetString();

            if (!string.IsNullOrWhiteSpace(abstractText))
            {
                sb.AppendLine($"📖 {abstractSource ?? "DuckDuckGo"}:");
                sb.AppendLine(abstractText);
                if (!string.IsNullOrWhiteSpace(abstractUrl))
                    sb.AppendLine($"Source: {abstractUrl}");
                sb.AppendLine();
                hasResults = true;
            }

            // Related topics
            if (ddgJson.RootElement.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
            {
                var count = 0;
                foreach (var topic in topics.EnumerateArray())
                {
                    if (count >= maxResults) break;

                    if (topic.TryGetProperty("Text", out var textProp) && topic.TryGetProperty("FirstURL", out var urlProp))
                    {
                        var text = textProp.GetString();
                        var url = urlProp.GetString();
                        if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(url))
                        {
                            sb.AppendLine($"🔗 {text}");
                            sb.AppendLine($"   URL: {url}");
                            sb.AppendLine();
                            count++;
                            hasResults = true;
                        }
                    }
                }
            }

            // ── Strategy 2: If DDG has no results, try DuckDuckGo HTML lite ──
            if (!hasResults)
            {
                // Try the lite HTML endpoint as fallback
                var liteUrl = $"https://lite.duckduckgo.com/lite/?q={HttpUtility.UrlEncode(query)}";
                var liteResponse = await _httpClient.GetStringAsync(liteUrl, cancellationToken);

                // Parse simple HTML links from lite response
                var linkMatches = System.Text.RegularExpressions.Regex.Matches(
                    liteResponse,
                    @"<a[^>]*href=""(https?://[^""]+)""[^>]*class=""result-link""[^>]*>([^<]+)</a>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                var count = 0;
                foreach (System.Text.RegularExpressions.Match match in linkMatches)
                {
                    if (count >= maxResults) break;
                    var url = match.Groups[1].Value;
                    var title = match.Groups[2].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(title) && !url.Contains("duckduckgo.com"))
                    {
                        sb.AppendLine($"🔗 {title}");
                        sb.AppendLine($"   URL: {url}");
                        sb.AppendLine();
                        count++;
                        hasResults = true;
                    }
                }
            }

            if (!hasResults)
            {
                sb.AppendLine("(No results found. Try a different query or more specific terms.)");
            }

            return EToolResult.Success(Name, sb.ToString(), new Dictionary<string, string>
            {
                ["query"] = query,
                ["has_results"] = hasResults.ToString()
            });
        }
        catch (HttpRequestException ex)
        {
            return EToolResult.Failure(Name, $"Web request failed: {ex.Message}. The agent may not have internet access.");
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Search error: {ex.Message}");
        }
    }
}