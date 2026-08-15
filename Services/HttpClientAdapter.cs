using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ECAssistant.Interfaces;

namespace ECAssistant.Services;

/// <summary>
/// Concrete HTTP client implementation.
/// </summary>
public class HttpClientAdapter : IHttpClient, IDisposable
{
    private readonly HttpClient _client;

    public HttpClientAdapter()
    {
        _client = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<string> GetAsync(string url, CancellationToken ct = default)
    {
        return await _client.GetStringAsync(url, ct);
    }

    public async Task<string> PostAsync(string url, string content, CancellationToken ct = default)
    {
        var response = await _client.PostAsync(url, new StringContent(content), ct);
        return await response.Content.ReadAsStringAsync();
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}