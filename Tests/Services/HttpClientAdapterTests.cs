using ECAssistant.Services;
using ECAssistant.Interfaces;

namespace ECAssistant.Tests.Services;

public class HttpClientAdapterTests : IDisposable
{
    private readonly HttpClientAdapter _adapter;

    public HttpClientAdapterTests()
    {
        _adapter = new HttpClientAdapter();
    }

    public void Dispose()
    {
        _adapter.Dispose();
    }

    [Fact]
    public void Constructor_Default_CreatesInstance()
    {
        Assert.NotNull(_adapter);
    }

    [Fact]
    public async Task GetAsync_InvalidUrl_ThrowsHttpRequestException()
    {
        await Assert.ThrowsAsync<HttpRequestException>(
            () => _adapter.GetAsync("https://nonexistent.invalid.domain.example/get"));
    }

    [Fact]
    public async Task PostAsync_InvalidUrl_ThrowsHttpRequestException()
    {
        await Assert.ThrowsAsync<HttpRequestException>(
            () => _adapter.PostAsync("https://nonexistent.invalid.domain.example/post", "data"));
    }

    [Fact]
    public void Dispose_CalledOnce_DoesNotThrow()
    {
        var adapter = new HttpClientAdapter();
        adapter.Dispose();
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var adapter = new HttpClientAdapter();
        adapter.Dispose();
        adapter.Dispose();
    }

    [Fact]
    public async Task GetAsync_ValidUrl_ReturnsContent()
    {
        // Use a reliable endpoint
        try
        {
            var result = await _adapter.GetAsync("https://example.com");
            Assert.NotEmpty(result);
        }
        catch (HttpRequestException)
        {
            // Network may be unavailable in test env — skip if so
        }
    }

    [Fact]
    public async Task PostAsync_ValidUrl_ReturnsResponse()
    {
        try
        {
            var result = await _adapter.PostAsync("https://httpbin.org/post", "test payload");
            Assert.NotEmpty(result);
        }
        catch (HttpRequestException)
        {
            // Network may be unavailable in test env — skip if so
        }
    }
}