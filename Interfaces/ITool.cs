using System.Threading;
using System.Threading.Tasks;

namespace ECAssistant.Interfaces;

/// <summary>
/// Unified tool interface.
/// Every tool must implement this.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }

    /// <summary>
    /// Whether this tool is enabled. Read from config.Tools[Name].enabled.
    /// If false, the engine skips registration and the LLM never sees this tool.
    /// </summary>
    bool IsEnabled { get; }

    Task<string> ExecuteAsync(string input, CancellationToken ct = default);
    ToolPolicy GetPolicy();

    /// <summary>
    /// v10.24: Returns the default config section for this tool.
    /// Called when the tool is registered and its config section is not yet in appsettings.json.
    /// Must include at minimum: { enabled = true }.
    /// </summary>
    object GetConfigSection() => new { enabled = true };
}