using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ECAssistant.Config;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools.Web;

/// <summary>
/// Web Search Tool — lets the LLM search the web using DuckDuckGo Instant Answer API.
/// No API key needed, no authentication, no rate limits for reasonable use.
/// </summary>
public class EWebSearchTool : EToolBase
{
    private readonly IHttpClient _httpClient;
    private readonly JsonElement? _toolConfig;

    public override string Name => "EWebSearch";

    public override string Description =>
        "Search the web using DuckDuckGo. Returns search results with titles, URLs, and snippets. " +
        "Use for: finding documentation, looking up APIs, getting code examples, researching topics. " +
        "No authentication needed.";

    public override string UsageExample => "<toolcall>EWebSearch<query>dotnet 8 async streams</query></toolcall>";

    public override bool IsEnabled { get; protected set; } = true;

    public EWebSearchTool(IHttpClient httpClient, EAgentConfig config)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        config.Tools.TryGetValue(Name, out var tc);
        _toolConfig = tc.ValueKind == JsonValueKind.Undefined ? null : tc;
        IsEnabled = ReadCfg(_toolConfig, "enabled", true);
    }

    public override object GetConfigSection() => new { enabled = true };

    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var query = arguments.GetValueOrDefault("query")?.Trim() ?? "";
        var maxResults = int.TryParse(arguments.GetValueOrDefault("max_results"), out var mr) ? mr : 5;

        if (string.IsNullOrWhiteSpace(query))
            return EToolResult.Failure(Name, "Missing 'query' argument.");

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var apiUrl = $"https://api.duckduckgo.com/?q={encodedQuery}&format=json&no_html=1";

            var json = await _httpClient.GetAsync(apiUrl, cancellationToken);
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

                    if (!string.IsNullOrEmpty(text))
                    {
                        results.AppendLine($"• {text}");
                        if (!string.IsNullOrEmpty(firstUrl))
                            results.AppendLine($"  URL: {firstUrl}");
                        results.AppendLine();
                        count++;
                    }

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
                results.AppendLine("No results found.");

            return EToolResult.Success(Name, $"Found {count} results for '{query}'\n\n{results.ToString().Trim()}");
        }
        catch (Exception ex)
        {
            return EToolResult.Failure(Name, $"Search error: {ex.Message}");
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
}