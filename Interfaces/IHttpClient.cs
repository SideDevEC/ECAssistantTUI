using System.Threading;
using System.Threading.Tasks;

namespace ECAssistant.Interfaces;

/// <summary>
/// HTTP client abstraction.
/// </summary>
public interface IHttpClient
{
    Task<string> GetAsync(string url, CancellationToken ct = default);
    Task<string> PostAsync(string url, string content, CancellationToken ct = default);
}