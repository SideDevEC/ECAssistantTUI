using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ECAssistant.Interfaces;

/// <summary>
/// Abstracts LLM inference from concrete implementation.
/// </summary>
public interface IInferenceEngine
{
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
    Task<string> GenerateAsync(string prompt, GenerationParams parameters, CancellationToken ct = default);
    void Dispose();
}