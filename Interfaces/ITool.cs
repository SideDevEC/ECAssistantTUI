using System.Threading;
using System.Threading.Tasks;

namespace ECAssistant.Interfaces;

/// <summary>
/// Unified tool interface.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    Task<string> ExecuteAsync(string input, CancellationToken ct = default);
    ToolPolicy GetPolicy();
}