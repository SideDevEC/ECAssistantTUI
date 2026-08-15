using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;
using ECAssistant.Interfaces;

namespace ECAssistant.Services;

/// <summary>
/// Concrete LLamaSharp inference engine.
/// Uses StatelessExecutor for stateless text generation.
/// </summary>
public class LlamaInferenceEngine : IInferenceEngine
{
    private readonly LLamaWeights _weights;
    private readonly ModelParams _modelParams;

    public LlamaInferenceEngine(LLamaWeights weights, ModelParams modelParams)
    {
        _weights = weights ?? throw new ArgumentNullException(nameof(weights));
        _modelParams = modelParams ?? throw new ArgumentNullException(nameof(modelParams));
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        var parameters = new GenerationParams(512, 0.8f, 0.95f, 40, 1.1f);
        return await GenerateAsync(prompt, parameters, ct);
    }

    public async Task<string> GenerateAsync(string prompt, GenerationParams parameters, CancellationToken ct = default)
    {
        var inferenceParams = new InferenceParams
        {
            MaxTokens = parameters.MaxTokens,
            AntiPrompts = new[] { "User:", "### User" },
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = parameters.Temperature,
                TopP = parameters.TopP,
                TopK = parameters.TopK,
                RepeatPenalty = parameters.RepeatPenalty,
            },
        };

        var executor = new StatelessExecutor(_weights, _modelParams, Microsoft.Extensions.Logging.Abstractions.NullLogger<StatelessExecutor>.Instance);
        var sb = new StringBuilder();

        await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))
            sb.Append(token);

        return sb.ToString();
    }

    public void Dispose()
    {
        _weights?.Dispose();
    }
}