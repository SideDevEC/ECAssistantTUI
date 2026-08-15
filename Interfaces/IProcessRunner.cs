using System.Threading;
using System.Threading.Tasks;

namespace ECAssistant.Interfaces;

/// <summary>
/// Abstract process execution.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> ExecuteAsync(string command, string? workDir = null, CancellationToken ct = default);
}