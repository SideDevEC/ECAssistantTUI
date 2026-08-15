using System.Threading;
using System.Threading.Tasks;
using ECAssistant.Interfaces;

namespace ECAssistant.Tools;

/// <summary>
/// Adapter that wraps an ITool implementation and exposes it as EToolBase,
/// so the engine and orchestrator can work with both old and new tools uniformly.
/// </summary>
public sealed class ToolAdapter : EToolBase
{
    private readonly ITool _inner;

    public ToolAdapter(ITool inner)
    {
        _inner = inner;
    }

    /// <summary>The wrapped ITool instance.</summary>
    public ITool Inner => _inner;

    public override string Name => _inner.Name;

    public override string Description => _inner.Description;

    public override string UsageExample => string.Empty;

    /// <summary>
    /// Bridge: converts the dictionary args to the XML tag string format expected by ITool,
    /// then wraps the string result in an EToolResult.
    /// Tools parse input like: &lt;command&gt;echo hello&lt;/command&gt;&lt;file&gt;test.cs&lt;/file&gt;
    /// </summary>
    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var input = ArgumentsToXml(arguments);
        var result = await _inner.ExecuteAsync(input, cancellationToken);

        if (result.StartsWith("[FAILED]") || result.StartsWith("[Shell Error") || result.StartsWith("[") && result.Contains("] Error"))
            return EToolResult.Failure(_inner.Name, result);

        return EToolResult.Success(_inner.Name, result);
    }

    public override string GetExtendedSystemPrompt() => _inner.Description;

    /// <summary>
    /// Convert dictionary arguments to XML tag format for ITool consumption.
    /// E.g., {"command": "date"} → "&lt;command&gt;date&lt;/command&gt;"
    /// </summary>
    private string ArgumentsToXml(Dictionary<string, string?> args)
    {
        if (args == null || args.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var kv in args)
        {
            if (string.IsNullOrEmpty(kv.Value))
                sb.Append($"<{kv.Key}>");
            else
                sb.Append($"<{kv.Key}>{kv.Value}</{kv.Key}>");
        }
        return sb.ToString();
    }
}