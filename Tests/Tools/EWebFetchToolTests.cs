using ECAssistant.Interfaces;
using ECAssistant.Tools.Web;

namespace ECAssistant.Tests.Tools;

public class EWebFetchToolTests
{
    private readonly Mock<IHttpClient> _httpClient = new();
    private readonly Mock<IConfigProvider> _configProvider = new();
    private readonly Mock<IColorFormatter> _colorFormatter = new();

    private EWebFetchTool CreateTool()
    {
        return new EWebFetchTool(_httpClient.Object, _configProvider.Object, _colorFormatter.Object);
    }

    // ── Name / Description / GetPolicy ──

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

    [Fact]
    public void GetPolicy_ReturnsApprovedPolicy()
    {
        var tool = CreateTool();
        var policy = tool.GetPolicy();

        Assert.Equal("EWebFetch", policy.ToolName);
        Assert.Equal("Approved", policy.Level);
    }

    // ── Constructor null checks ──

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EWebFetchTool(null!, _configProvider.Object, _colorFormatter.Object));
    }

    // ── ExecuteAsync — missing/invalid url ──

    [Fact]
    public async Task ExecuteAsync_MissingUrl_ReturnsFailed()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("");

        Assert.Contains("[FAILED]", result);
        Assert.Contains("Missing required argument: url", result);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidUrl_ReturnsFailed()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("url=\"not-a-url\"");

        Assert.Contains("[FAILED]", result);
        Assert.Contains("Invalid URL", result);
    }

    // ── ExecuteAsync — success ──

    [Fact]
    public async Task ExecuteAsync_ValidUrl_ReturnsContent()
    {
        var html = "<html><body><p>Hello World</p></body></html>";
        _httpClient.Setup(h => h.GetAsync("https://example.com", It.IsAny<CancellationToken>()))
                   .ReturnsAsync(html);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("url=\"https://example.com\"");

        Assert.Contains("[SUCCESS]", result);
        Assert.Contains("Hello World", result);
    }

    [Fact]
    public async Task ExecuteAsync_HtmlWithScript_ScriptRemoved()
    {
        var html = "<html><body><script>alert('xss')</script><p>Content</p></body></html>";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(html);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("url=\"https://example.com\"");

        Assert.Contains("[SUCCESS]", result);
        Assert.DoesNotContain("alert", result);
        Assert.Contains("Content", result);
    }

    [Fact]
    public async Task ExecuteAsync_HtmlWithStyle_StyleRemoved()
    {
        var html = "<html><head><style>body { color: red; }</style></head><body><p>Text</p></body></html>";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(html);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("url=\"https://example.com\"");

        Assert.Contains("[SUCCESS]", result);
        Assert.DoesNotContain("color: red", result);
        Assert.Contains("Text", result);
    }

    [Fact]
    public async Task ExecuteAsync_LargeHtml_TruncatesAtMaxChars()
    {
        var bigContent = string.Join("", Enumerable.Repeat("A", 10000));
        var html = $"<html><body><p>{bigContent}</p></body></html>";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(html);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("url=\"https://example.com\" maxchars=\"100\"");

        Assert.Contains("[SUCCESS]", result);
        Assert.Contains("truncated", result);
    }

    [Fact]
    public async Task ExecuteAsync_CustomMaxChars_RespectsLimit()
    {
        var html = "<html><body><p>" + new string('X', 500) + "</p></body></html>";
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(html);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("url=\"https://example.com\" maxchars=\"50\"");

        Assert.Contains("[SUCCESS]", result);
        Assert.Contains("truncated", result);
    }

    // ── ExecuteAsync — error handling ──

    [Fact]
    public async Task ExecuteAsync_TaskCanceledException_ReturnsTimeoutMessage()
    {
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new TaskCanceledException());
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("url=\"https://example.com\"");

        Assert.Contains("[FAILED]", result);
        Assert.Contains("timed out", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_GeneralException_ReturnsErrorMessage()
    {
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new HttpRequestException("Connection refused"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("url=\"https://example.com\"");

        Assert.Contains("[FAILED]", result);
        Assert.Contains("Connection refused", result);
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

        await tool.ExecuteAsync("url=\"https://example.com\"", token);

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

        var result = await tool.ExecuteAsync("url=\"https://example.com\"");

        Assert.Contains("Hello & Goodbye", result);
    }

    // ── Empty HTML ──

    [Fact]
    public async Task ExecuteAsync_EmptyHtml_ReturnsEmptyContent()
    {
        _httpClient.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync("");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("url=\"https://example.com\"");

        Assert.Contains("[SUCCESS]", result);
    }
}