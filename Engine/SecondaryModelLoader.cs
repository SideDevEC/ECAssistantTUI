using LLama;
using LLama.Common;
using LLama.Sampling;
using ECAssistant.Services;

namespace ECAssistant.Engine;

/// <summary>
/// Secondary Model Loader — loads a smaller GGUF model for lightweight tasks
/// like summarization, memory queries, and context compaction.
/// 
/// This keeps the main model free for complex reasoning while a small model
/// handles routine background work (summarization, etc).
/// 
/// Usage:
///   var secondary = SecondaryModelLoader.Load("Qwen3-1.7B-Q4_K_M.gguf", contextSize: 4096);
///   var summary = secondary.Generate("Summarize: ...", maxTokens: 200);
/// </summary>
public class SecondaryModelLoader : IDisposable
{
    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private InteractiveExecutor? _executor;
    private InferenceParams? _inferenceParams;
    private bool _loaded = false;

    public string ModelPath { get; private set; } = "";
    public uint ContextSize { get; private set; } = 4096;
    public bool IsLoaded => _loaded;

    /// <summary>Load a secondary model from disk.</summary>
    public static SecondaryModelLoader? Load(string modelPath, uint contextSize = 4096, int gpuLayers = 0)
    {
        var loader = new SecondaryModelLoader();

        if (!File.Exists(modelPath))
        {
            ECAssistant.Services.Logger.Warn("SecondaryModel", $"Model not found: {modelPath}");
            return null;
        }

        try
        {
            var parameters = new ModelParams(modelPath)
            {
                GpuLayerCount = gpuLayers,
                ContextSize = contextSize,
            };

            loader._weights = LLamaWeights.LoadFromFile(parameters);
            loader._context = loader._weights.CreateContext(parameters);
            loader._executor = new InteractiveExecutor(loader._context, new Microsoft.Extensions.Logging.Abstractions.NullLogger<InteractiveExecutor>());
            loader._inferenceParams = new InferenceParams
            {
                MaxTokens = 512,
                OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.3f,
                    TopP = 0.9f,
                }
            };
            loader._loaded = true;
            loader.ModelPath = modelPath;
            loader.ContextSize = contextSize;
            ECAssistant.Services.Logger.Info("SecondaryModel", $"Loaded: {modelPath} | Context: {contextSize} | GPU: {gpuLayers}");
        }
        catch (Exception ex)
        {
            ECAssistant.Services.Logger.Error("SecondaryModel", $"Failed to load: {ex.Message}");
            return null;
        }

        return loader;
    }

    /// <summary>Generate text with the secondary model (non-streaming, simple).</summary>
    public async Task<string> GenerateAsync(string prompt, int maxTokens = 512)
    {
        if (!_loaded || _executor == null || _inferenceParams == null)
            return "(Secondary model not loaded)";

        var inferenceParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = _inferenceParams.SamplingPipeline,
        };

        var sb = new System.Text.StringBuilder();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var token in _executor.InferAsync(prompt, inferenceParams, cts.Token))
                sb.Append(token);
        }
        catch (OperationCanceledException)
        {
            // Timeout — return what we have
        }

        return sb.ToString().Trim();
    }

    /// <summary>Summarize text using the secondary model.</summary>
    public async Task<string> SummarizeAsync(string text, int maxTokens = 200)
    {
        var prompt = $"Summarize the following concisely. Keep key facts and decisions only:\n\n{text}\n\nSummary:";
        return await GenerateAsync(prompt, maxTokens);
    }

    public void Dispose()
    {
        try { _context?.Dispose(); } catch { }
        ECAssistant.Services.Logger.Info("SecondaryModel", "Disposed.");
    }
}