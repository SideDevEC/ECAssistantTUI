using LLama;
using ECAssistant.Interfaces;
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
    private string[] _antiPrompts = new[] { "User:", "Question:" };
    private bool _loaded = false;
    private readonly ILogger _logger;
    
    public string ModelPath { get; private set; } = "";
    public uint ContextSize { get; private set; } = 4096;
    public bool IsLoaded => _loaded;

    private SecondaryModelLoader(ILogger? logger = null)
    {
        _logger = logger ?? new Logger();
    }

    /// <summary>Load a secondary model from disk.</summary>
   // Stateless factory — immutable data class
    public static SecondaryModelLoader? Load(string modelPath, uint contextSize = 4096, int gpuLayers = 0,
        float temperature = 0.1f, float topP = 0.8f, int topK = 40, float repeatPenalty = 1.1f, int maxTokens = 512,
        string[]? antiPrompts = null, ILogger? logger = null)
    {
        var loader = new SecondaryModelLoader(logger);

        if (!File.Exists(modelPath))
        {
            loader._logger.Warn("SecondaryModel", $"Model not found: {modelPath}");
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
            loader._antiPrompts = antiPrompts ?? new[] { "User:", "Question:" };
            loader._inferenceParams = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = loader._antiPrompts,
                OverflowStrategy = LLama.Common.ContextOverflowStrategy.TruncateAndReprefill,
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = temperature,
                    TopP = topP,
                    TopK = topK,
                    RepeatPenalty = repeatPenalty,
                }
            };
            loader._loaded = true;
            loader.ModelPath = modelPath;
            loader.ContextSize = contextSize;
            loader._logger.Info("SecondaryModel", $"Loaded: {modelPath} | Context: {contextSize} | GPU: {gpuLayers}");
        }
        catch (Exception ex)
        {
            loader._logger.Error("SecondaryModel", $"Failed to load: {ex.Message}");
            return null;
        }

        return loader;
    }

    /// <summary>Generate text with the secondary model (non-streaming, simple).</summary>
    // v10.12.13: Default maxTokens scales with configured MaxTokens
    public async Task<string> GenerateAsync(string prompt, int? maxTokens = null)
    {
        if (!_loaded || _executor == null || _inferenceParams == null)
            return "(Secondary model not loaded)";

        // Use provided maxTokens, or fall back to configured default
        var effectiveMaxTokens = maxTokens ?? (int)_inferenceParams.MaxTokens;

        var inferenceParams = new InferenceParams
        {
            MaxTokens = effectiveMaxTokens,
            AntiPrompts = _antiPrompts,
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
    // v10.12.13: Default maxTokens = 25% of configured MaxTokens (min 100)
    public async Task<string> SummarizeAsync(string text, int? maxTokens = null)
    {
        var effectiveMax = maxTokens ?? Math.Max(100, (int)(_inferenceParams?.MaxTokens ?? 512) / 4);
        var prompt = $"Summarize the conversation below. STRICT RULES:\n- Output ONLY a concise summary of what was discussed\n- Keep facts, decisions, and tool results only\n- Do NOT add opinions, suggestions, or extra context\n- Do NOT add greetings, conclusions, or meta-commentary\n- Maximum 3 sentences\n- Plain text only, no formatting\n\nConversation:\n{text}\n\nSummary:";
        return await GenerateAsync(prompt, effectiveMax);
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

        var prompt = @"You decompose tasks. Output ONLY numbered steps. Nothing else.

Count the distinct actions the user asked for. Output exactly that many steps. Stop. Do not add any more.

FORBIDDEN:
- Extra steps not requested (verify, check, clean up, create project, setup)
- Explanations, reasoning, or text outside numbered steps
- Steps the user did not explicitly ask for

User: read Program.cs then fix line 42 then rebuild
1. Read Program.cs
2. Fix the bug at line 42
3. Rebuild the project

User: what day is today
1. Get the current date

User: calculate 4+2, write it to a file, then copy the file to a new location
1. Calculate 4+2
2. Write the result to a file
3. Copy the file to a new location

User: " + userRequest + "\n";

        var effectiveMax = Math.Max(128, (int)(_inferenceParams?.MaxTokens ?? 512) / 2);
        var result = await GenerateAsync(prompt, maxTokens: effectiveMax);
        
        // v10.7.5: Log raw decomposition output for debugging
        _logger.Info("SecondaryModel", $"Raw decomposition output:\n{result}");

        if (string.IsNullOrWhiteSpace(result))
            return null;

        // Parse numbered lines: "1. ...", "2. ...", etc.
        // v10.7.5: Stop at first non-numbered line (model added extra steps after the real ones)
        var steps = new List<string>();
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int expectedNumber = 1;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Match "N. ..." pattern with sequential numbering
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, $@"^{expectedNumber}\.\s*(.+)$");
            if (match.Success)
            {
                var step = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(step))
                    steps.Add(step);
                expectedNumber++;
            }
            else
            {
                // v10.7.5: Stop at first line that breaks the sequence
                // This catches extra steps, explanations, or commentary after the real steps
                break;
            }
        }

        // If we found no numbered steps, return null (use keyword fallback)
        if (steps.Count == 0)
        {
            _logger.Warn("SecondaryModel", "Decomposition produced no numbered steps — falling back to keywords.");
            return null;
        }

        _logger.Info("SecondaryModel", $"Decomposed into {steps.Count} steps: {string.Join(" | ", steps.Select(s => s.Substring(0, Math.Min(s.Length, 50))))}");
        

        return steps;
    }

    public void Dispose()
    {
        try { _weights?.Dispose(); } catch { }
        _logger.Info("SecondaryModel", "Disposed.");
    }
}