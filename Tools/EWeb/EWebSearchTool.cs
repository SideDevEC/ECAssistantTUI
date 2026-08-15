using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Web;

/// <summary>
/// Web Search Tool — lets the LLM search the web using DuckDuckGo Instant Answer API.
/// No API key needed, no authentication, no rate limits for reasonable use.
/// </summary>
public class EWebSearchTool : ITool
{
    private readonly IHttpClient _httpClient;
    private readonly IConfigProvider _configProvider;
    private readonly IColorFormatter _color;

    public EWebSearchTool(IHttpClient httpClient, IConfigProvider configProvider, IColorFormatter color)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _color = color ?? throw new ArgumentNullException(nameof(color));
    }

    public string Name => "EWebSearch";

    public string Description =>
        "Search the web using DuckDuckGo. Returns search results with titles, URLs, and snippets. " +
        "Use for: finding documentation, looking up APIs, getting code examples, researching topics. " +
        "No authentication needed.";

    public ECAssistant.Interfaces.ToolPolicy GetPolicy() => ECAssistant.Interfaces.ToolPolicy.Approved(Name);

    public async Task<string> ExecuteAsync(string input, CancellationToken ct = default)
    {
        var args = ParseInput(input);
        var query = args.GetValueOrDefault("query", "");
        var maxResults = int.TryParse(args.GetValueOrDefault("max_results", "5"), out var mr) ? mr : 5;

        if (string.IsNullOrWhiteSpace(query))
            return $"[FAILED] {Name}: Missing 'query' argument.";

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var apiUrl = $"https://api.duckduckgo.com/?q={encodedQuery}&format=json&no_html=1";

            var json = await _httpClient.GetAsync(apiUrl, ct);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var results = new StringBuilder();
            results.AppendLine($"Search results for: {query}");
            results.AppendLine(new string('-', 50));

            var abstractText = root.TryGetProperty("AbstractText", out var absText) ? absText.GetString() ?? "" : "";
            var abstractUrl = root.TryGetProperty("AbstractURL", out var absUrl) ? absUrl.GetString() ?? "" : "";
            var relatedTopics = root.TryGetProperty("RelatedTopics", out var related) ? related : default;

            if (!string.IsNullOrEmpty(abstractText))
            {
                results.AppendLine($"Instant Answer: {abstractText}");
                if (!string.IsNullOrEmpty(abstractUrl))
                    results.AppendLine($"Source: {abstractUrl}");
                results.AppendLine();
            }

            var count = 0;
            if (related.ValueKind == JsonValueKind.Array)
            {
                foreach (var topic in related.EnumerateArray())
                {
                    if (count >= maxResults)
                        break;

                    var text = topic.TryGetProperty("Text", out var t) ? t.GetString() ?? "" : "";
                    var firstUrl = topic.TryGetProperty("FirstURL", out var u) ? u.GetString() ?? "" : "";
                    var icon = topic.TryGetProperty("Icon", out var iconEl) ? iconEl.TryGetProperty("URL", out var iconUrl) ? iconUrl.GetString() ?? "" : "" : "";

                    if (!string.IsNullOrEmpty(text))
                    {
                        results.AppendLine($"• {text}");
                        if (!string.IsNullOrEmpty(firstUrl))
                            results.AppendLine($"  URL: {firstUrl}");
                        results.AppendLine();
                        count++;
                    }

                    // Check for nested topics
                    if (topic.TryGetProperty("Topics", out var nested) && nested.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var nestedTopic in nested.EnumerateArray())
                        {
                            if (count >= maxResults)
                                break;

                            var nText = nestedTopic.TryGetProperty("Text", out var nt) ? nt.GetString() ?? "" : "";
                            var nUrl = nestedTopic.TryGetProperty("FirstURL", out var nu) ? nu.GetString() ?? "" : "";

                            if (!string.IsNullOrEmpty(nText))
                            {
                                results.AppendLine($"• {nText}");
                                if (!string.IsNullOrEmpty(nUrl))
                                    results.AppendLine($"  URL: {nUrl}");
                                results.AppendLine();
                                count++;
                            }
                        }
                    }
                }
            }

            if (count == 0)
            {
                results.AppendLine("No results found.");
            }

            return $"[SUCCESS] {Name}: Found {count} results for '{query}'\n\n{results.ToString().Trim()}";
        }
        catch (Exception ex)
        {
            return $"[FAILED] {Name}: Search error: {ex.Message}";
        }
    }

    private Dictionary<string, string> ParseInput(string input)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(input))
            return result;

        var matches = System.Text.RegularExpressions.Regex.Matches(input, @"(\w+)\s*=\s*""([^""]*)""|(\w+)\s*=\s*(\S+)");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
            var value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value;
            result[key] = value;
        }

        return result;
    }
}