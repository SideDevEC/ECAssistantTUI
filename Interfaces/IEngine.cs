using System.Threading;
using System.Threading.Tasks;

namespace ECAssistant.Interfaces;

/// <summary>
/// Engine interface for testing/abstraction.
/// </summary>
public interface IEngine
{
    Task StartAsync(string userMessage);
    Task RunAsync(CancellationToken ct = default);
    void Dispose();
}