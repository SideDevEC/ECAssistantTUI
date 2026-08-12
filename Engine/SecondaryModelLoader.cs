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
    private ModelParams? _modelParams;
    // v10.7.1: StatelessExecutor — no KV cache between calls.
    // Each GenerateAsync call gets a fresh context. This allows dynamic
    // prompting for decomposition, summarization, etc. without state leakage.
    private StatelessExecutor? _executor;
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
            loader._modelParams = parameters;
            // v10.7.1: StatelessExecutor — fresh context per call, no KV cache
            loader._executor = new StatelessExecutor(loader._weights, parameters, new Microsoft.Extensions.Logging.Abstractions.NullLogger<StatelessExecutor>());
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

    /// <summary>
    /// Decompose a user request into sub-tasks using the secondary model.
    /// Returns a list of step descriptions.
    /// Falls back to null if decomposition fails (caller should use keyword fallback).
    /// </summary>
    public async Task<List<string>?> DecomposeTaskAsync(string userRequest)
    {
        if (!_loaded)
            return null;

        var prompt = @"Break the user request into steps. Output ONLY what the user asked for.

STRICT RULES:
- One step per line, numbered: 1. 2. 3.
- ONLY include actions the user EXPLICITLY asked for
- Do NOT add setup, cleanup, verification, or ""helpful"" extra steps
- Do NOT create projects, files, configs, or directories unless asked
- Do NOT include thinking, reasoning, or explanation
- If the user asked for N things, output exactly N steps (or fewer if one action covers multiple)
- Keep each step under 15 words
- If it is a single action, output one line

Examples:
User: read Program.cs then fix line 42 then rebuild
1. Read Program.cs
2. Fix the bug at line 42
3. Rebuild the project

User: what day is today
1. Get the current date

User: calculate 4+2, write it to a file, then copy the file to C:\temp
1. Calculate 4+2
2. Write the result to a file
3. Copy the file to a new location

User: " + userRequest + "\n";

        var result = await GenerateAsync(prompt, maxTokens: 256);
        
        if (string.IsNullOrWhiteSpace(result))
            return null;

        // Parse numbered lines: "1. ...", "2. ...", etc.
        var steps = new List<string>();
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Match "N. ..." pattern
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\d+\.\s*(.+)$");
            if (match.Success)
            {
                var step = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(step))
                    steps.Add(step);
            }
        }

        // Also strip any non-step content (model might add explanation)
        // If we found no numbered steps, return null (use keyword fallback)
        if (steps.Count == 0)
        {
            Logger.Warn("SecondaryModel", "Decomposition produced no numbered steps — falling back to keywords.");
            return null;
        }

        Logger.Info("SecondaryModel", $"Decomposed into {steps.Count} steps: {string.Join(" | ", steps.Select(s => s.Substring(0, Math.Min(s.Length, 50))))}");
        return steps;
    }

    public void Dispose()
    {
        try { _weights?.Dispose(); } catch { }
        ECAssistant.Services.Logger.Info("SecondaryModel", "Disposed.");
    }
}