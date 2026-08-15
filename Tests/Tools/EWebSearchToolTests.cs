using ECAssistant.Config;
using ECAssistant.Interfaces;
using ECAssistant.Tools.Web;

namespace ECAssistant.Tests.Tools;

public class EWebSearchToolTests
{
    private readonly Mock<IHttpClient> _httpClient = new();
    private readonly EAgentConfig _config = new();

    private EWebSearchTool CreateTool()
    {
        return new EWebSearchTool(_httpClient.Object, _config);
    }


    [Fact]
    public void Name_ReturnsEWebSearch()
    {
        var tool = CreateTool();
        Assert.Equal("EWebSearch", tool.Name);
    }

    [Fact]
    public void Description_ContainsSearch()
    {
        var tool = CreateTool();
        Assert.Contains("search", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    

    // ── Constructor null checks ──

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EWebSearchTool(null!, _config));
    }

    [Fact]
    public void Constructor_NullConfigProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EWebSearchTool(_httpClient.Object, null!));
    }

    [Fact]
    public void Constructor_NullColorFormatter_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EWebSearchTool(_httpClient.Object, null!));
    }

    // ── ExecuteAsync — missing query ──

    [Fact]
    public async Task ExecuteAsync_MissingQuery_ReturnsFailed()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>());

        Assert.Contains("[FAILED]", result.Error);
        Assert.Contains("Missing 'query'", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceQuery_ReturnsFailed()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "   " });

        Assert.Contains("[FAILED]", result.Error);
        Assert.Contains("Missing 'query'", result.Error);
    }

    // ── ExecuteAsync — success with results ──

    [Fact]
    public async Task ExecuteAsync_WithAbstract_ReturnsInstantAnswer()
    {
        var json = """{"AbstractText":"Test answer","AbstractURL":"https://example.com","RelatedTopics":[]}""";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(json);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("[SUCCESS]", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("Test answer", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("https://example.com", result.Succeeded ? result.Output : result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_WithRelatedTopics_ReturnsResults()
    {
        var json = """{"AbstractText":"","RelatedTopics":[{"Text":"Result 1","FirstURL":"https://r1.com"},{"Text":"Result 2","FirstURL":"https://r2.com"}]}""";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(json);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("[SUCCESS]", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("Result 1", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("Result 2", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("https://r1.com", result.Succeeded ? result.Output : result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_NoResults_ReturnsNoResultsMessage()
    {
        var json = """{"AbstractText":"","RelatedTopics":[]}""";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(json);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("[SUCCESS]", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("No results found", result.Succeeded ? result.Output : result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_MaxResults_LimitsOutput()
    {
        var topics = new List<string>();
        for (int i = 1; i <= 10; i++)
            topics.Add($"{{\"Text\":\"Result {i}\",\"FirstURL\":\"https://r{i}.com\"}}");
        var json = $"{{\"AbstractText\":\"\",\"RelatedTopics\":[{string.Join(",", topics)}]}}";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(json);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test", ["max_results"] = "2" });

        Assert.Contains("Result 1", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("Result 2", result.Succeeded ? result.Output : result.Error);
        Assert.DoesNotContain("Result 3", result.Succeeded ? result.Output : result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_NestedTopics_ReturnsNestedResults()
    {
        var json = """{"AbstractText":"","RelatedTopics":[{"Text":"Top level","FirstURL":"https://top.com","Topics":[{"Text":"Nested result","FirstURL":"https://nested.com"}]}]}""";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(json);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("Top level", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("Nested result", result.Succeeded ? result.Output : result.Error);
    }

    // ── ExecuteAsync — error handling ──

    [Fact]
    public async Task ExecuteAsync_HttpException_ReturnsFailed()
    {
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new HttpRequestException("Network error"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("[FAILED]", result.Error);
        Assert.Contains("Network error", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJson_ReturnsFailed()
    {
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync("not json");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" });

        Assert.Contains("[FAILED]", result.Error);
    }

    // ── ExecuteAsync — URL encoding ──

    [Fact]
    public async Task ExecuteAsync_EncodesQueryInUrl()
    {
        _httpClient.Setup(h => h.GetAsync(It.Is<string>(u => u.Contains("hello+world") || u.Contains("hello%20world")), It.IsAny<CancellationToken>()))
                   .ReturnsAsync("""{"AbstractText":"","RelatedTopics":[]}""");
        var tool = CreateTool();

        await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "hello world" });

        _httpClient.Verify(h => h.GetAsync(It.Is<string>(u => u.Contains("hello") && (u.Contains("+") || u.Contains("%20"))), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── ExecuteAsync — CancellationToken ──

    [Fact]
    public async Task ExecuteAsync_PassesCancellationToken()
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), token))
                   .ReturnsAsync("""{"AbstractText":"","RelatedTopics":[]}""");
        var tool = CreateTool();

        await tool.ExecuteAsync(new Dictionary<string, string?> { ["query"] = "test" }, token);

        _httpClient.Verify(h => h.GetAsync(It.IsAny<string>(), token), Times.Once);
    }
}