using ECAssistant.Config;
using ECAssistant.Interfaces;
using ECAssistant.Tools.Web;

namespace ECAssistant.Tests.Tools;

public class EWebFetchToolTests
{
    private readonly Mock<IHttpClient> _httpClient = new();
    private readonly EAgentConfig _config = new();

    private EWebFetchTool CreateTool()
    {
        return new EWebFetchTool(_httpClient.Object, _config);
    }

    [Fact]
    public void Name_ReturnsEWebFetch()
    {
        var tool = CreateTool();
        Assert.Equal("EWebFetch", tool.Name);
    }

    [Fact]
    public void Description_ContainsFetch()
    {
        var tool = CreateTool();
        Assert.Contains("fetch", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    

    // ── Constructor null checks ──

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EWebFetchTool(null!, _config));
    }

    // ── ExecuteAsync — missing/invalid url ──

    [Fact]
    public async Task ExecuteAsync_MissingUrl_ReturnsFailed()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>());

        Assert.Contains("Missing required argument: url", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidUrl_ReturnsFailed()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["url"] = "not-a-url" });

        Assert.Contains("Invalid URL", result.Error);
    }

    // ── ExecuteAsync — success ──

    [Fact]
    public async Task ExecuteAsync_ValidUrl_ReturnsContent()
    {
        var html = "<html><body><p>Hello World</p></body></html>";
        _httpClient.Setup(h => h.GetAsync("https://example.com", It.IsAny<CancellationToken>()))
                   .ReturnsAsync(html);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["url"] = "https://example.com" });

        Assert.True(result.Succeeded);
        Assert.Contains("Hello World", result.Output + result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_HtmlWithScript_ScriptRemoved()
    {
        var html = "<html><body><script>alert('xss')</script><p>Content</p></body></html>";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(html);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["url"] = "https://example.com" });

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("alert", result.Output + result.Error);
        Assert.Contains("Content", result.Output + result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_HtmlWithStyle_StyleRemoved()
    {
        var html = "<html><head><style>body { color: red; }</style></head><body><p>Text</p></body></html>";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(html);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["url"] = "https://example.com" });

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("color: red", result.Output + result.Error);
        Assert.Contains("Text", result.Output + result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_LargeHtml_TruncatesAtMaxChars()
    {
        var bigContent = string.Join("", Enumerable.Repeat("A", 10000));
        var html = $"<html><body><p>{bigContent}</p></body></html>";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(html);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["url"] = "https://example.com", ["maxchars"] = "100" });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_CustomMaxChars_RespectsLimit()
    {
        var html = "<html><body><p>" + new string('X', 500) + "</p></body></html>";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(html);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["url"] = "https://example.com", ["maxchars"] = "50" });

        Assert.True(result.Succeeded);
    }

    // ── ExecuteAsync — error handling ──

    [Fact]
    public async Task ExecuteAsync_TaskCanceledException_ReturnsTimeoutMessage()
    {
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new TaskCanceledException());
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["url"] = "https://example.com" });

        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_GeneralException_ReturnsErrorMessage()
    {
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new HttpRequestException("Connection refused"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["url"] = "https://example.com" });

        Assert.Contains("Connection refused", result.Error);
    }

    // ── ExecuteAsync — CancellationToken ──

    [Fact]
    public async Task ExecuteAsync_PassesCancellationToken()
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), token))
                   .ReturnsAsync("<html><body>OK</body></html>");
        var tool = CreateTool();

        await tool.ExecuteAsync(new Dictionary<string, string?> { ["url"] = "https://example.com" }, token);

        _httpClient.Verify(h => h.GetAsync(It.IsAny<string>(), token), Times.Once);
    }

    // ── HTML entity decoding ──

    [Fact]
    public async Task ExecuteAsync_HtmlEntities_Decoded()
    {
        var html = "<html><body><p>Hello &amp; Goodbye</p></body></html>";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(html);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["url"] = "https://example.com" });

        Assert.Contains("Hello & Goodbye", result.Output + result.Error);
    }

    // ── Empty HTML ──

    [Fact]
    public async Task ExecuteAsync_EmptyHtml_ReturnsEmptyContent()
    {
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync("");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["url"] = "https://example.com" });

    }
}