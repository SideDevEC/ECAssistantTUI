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
    /// Bridge: converts the dictionary args to the string input format expected by ITool,
    /// then wraps the string result in an EToolResult.
    /// </summary>
    public override async Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        var input = ArgumentsToString(arguments);
        var result = await _inner.ExecuteAsync(input, cancellationToken);

        if (result.StartsWith("[FAILED]"))
            return EToolResult.Failure(_inner.Name, result);

        return EToolResult.Success(_inner.Name, result);
    }

    public override string GetExtendedSystemPrompt() => _inner.Description;

    /// <summary>Convert dictionary arguments to a key=value string for ITool consumption.</summary>
    private string ArgumentsToString(Dictionary<string, string?> args)
    {
        if (args == null || args.Count == 0)
            return string.Empty;

        var parts = new System.Text.StringBuilder();
        foreach (var kv in args)
        {
            if (string.IsNullOrEmpty(kv.Value))
                parts.Append($"{kv.Key} ");
            else
                parts.Append($"{kv.Key}=\"{kv.Value}\" ");
        }
        return parts.ToString().Trim();
    }
}