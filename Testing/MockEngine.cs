using System.Text;
using System.Text.RegularExpressions;
using ECAssistant.Engine;
using ECAssistant.Tools;
using ECAssistant.Memory;
using ECAssistant.Services;
using ECAssistant.Session;
using LLama.Common;
using LLama.Sampling;

namespace ECAssistant.Testing;

/// <summary>
/// Mock engine for model-independent testing.
///
/// Returns predefined responses instead of running actual LLM inference.
/// This lets you test orchestrator logic, tool dispatch, sub-task advancement,
/// format retry, and parallel execution WITHOUT burning model inference —
/// faster, cheaper, deterministic.
///
/// Usage:
///   var mock = new MockEngine(workingDir);
///   mock.AddResponse("&lt;lm&gt;&lt;thinking&gt;ok&lt;/thinking&gt;&lt;toolcall&gt;EShellAgent&lt;command&gt;echo test&lt;/command&gt;&lt;/toolcall&gt;&lt;/lm&gt;");
///   mock.AddResponse("&lt;lm&gt;&lt;thinking&gt;done&lt;/thinking&gt;&lt;output&gt;Task complete&lt;/output&gt;&lt;/lm&gt;");
///   var orchestrator = new AgentOrchestrator(mock, ...);
///   var result = await orchestrator.ExecuteMultiStep("test goal");
/// </summary>
public class MockEngine : EAgentEngine
{
    private readonly Queue<string> _responses = new();
    private readonly List<string> _allResponses = new();
    private string? _defaultResponse;
    private bool _cycleResponses;

    /// <summary>Called when a response is consumed (for test assertions).</summary>
    public Action<string>? OnResponseConsumed { get; set; }

    /// <summary>Called when GenerateAsync is invoked (for timing/assertions).</summary>
    public Action<string>? OnGenerateCalled { get; set; }

    /// <summary>All responses that were consumed during execution.</summary>
    public IReadOnlyList<string> ConsumedResponses => _allResponses;

    /// <summary>Number of times GenerateAsync was called.</summary>
    public int GenerateCallCount { get; private set; }

    // ── Tool results captured for test assertions ──
    public List<(string toolName, string output)> ToolResults { get; } = new();

    /// <summary>
    /// Create a mock engine that returns predefined responses.
    /// </summary>
    /// <param name="workingDir">Working directory (for file operations)</param>
    /// <param name="cycleResponses">If true, cycle through responses repeatedly. If false, use default after queue empties.</param>
    public MockEngine(string workingDir, bool cycleResponses = false)
        : base(modelPath: ActivateMockMode("/mock/model.gguf"),
               contextSize: 4096,
               gpuLayers: 0,
               threadCount: 1,
               inferenceParams: new InferenceParams { MaxTokens = 128 },
               workingDir: workingDir,
               sharedWeights: null,
               sharedModelParams: null)
    {
        _cycleResponses = cycleResponses;
    }

    /// <summary>
    /// Side-effect helper: sets the static mock-mode flag before base() constructor runs.
    /// Returns the modelPath unchanged. The EAgentEngine constructor checks _sForceMockMode
    /// and skips LLama weight loading when true.
    /// </summary>
    private static string ActivateMockMode(string modelPath)
    {
        EAgentEngine._sForceMockMode = true;
        return modelPath;
    }

    /// <summary>Add a predefined response to the queue.</summary>
    public void AddResponse(string response)
    {
        _responses.Enqueue(response);
    }

    /// <summary>Add multiple responses.</summary>
    public void AddResponses(params string[] responses)
    {
        foreach (var r in responses) _responses.Enqueue(r);
    }

    /// <summary>Set a default response for when the queue is empty.</summary>
    public void SetDefaultResponse(string response)
    {
        _defaultResponse = response;
    }

    /// <summary>Clear all queued responses.</summary>
    public void ClearResponses()
    {
        _responses.Clear();
        _allResponses.Clear();
        GenerateCallCount = 0;
    }

    // ── Override GenerateAsync to return predefined responses ──

    public override Task<string> GenerateAsync(string userPrompt)
    {
        GenerateCallCount++;
        OnGenerateCalled?.Invoke(userPrompt);

        string response;
        if (_responses.Count > 0)
        {
            response = _responses.Dequeue();
            if (_cycleResponses) _responses.Enqueue(response);
        }
        else if (_defaultResponse != null)
        {
            response = _defaultResponse;
        }
        else
        {
            // No more responses — return an output to end the loop
            response = "<lm><thinking>No more mock responses</thinking><output>Done (mock engine exhausted)</output></lm>";
        }

        _allResponses.Add(response);
        OnResponseConsumed?.Invoke(response);

        // Simulate streaming by writing the response to session output
        SessionOutput?.StartStream(OutputState.Raw);
        SessionOutput?.Write(response);
        SessionOutput?.StopStream();
        SessionOutput?.WriteLine(response, OutputState.Raw);

        return Task.FromResult(response);
    }

    // ── Override methods that touch real LLM ──

    public override Task PrefillStaticPrefix()
    {
        // No-op — mock doesn't need KV cache prefill
        return Task.CompletedTask;
    }

    public override Task ResetAndRebuildCacheAsync()
    {
        // No-op
        return Task.CompletedTask;
    }

    public override Task RebuildCacheAfterStopAsync()
    {
        return Task.CompletedTask;
    }

    public override Task RemoveLastAssistantResponseAsync()
    {
        // Remove from context window only (no KV cache to rewind)
        ContextWindow.RemoveLastAssistantMessage();
        return Task.CompletedTask;
    }

    public override void AddToolResult(string toolName, string output)
    {
        ToolResults.Add((toolName, output));
        // Add to context window for orchestrator to see
        var safeOutput = output.Replace("<", "&lt;").Replace(">", "&gt;");
        ContextWindow.AddToolOutput(safeOutput, toolName);
        Transcript.AddToolOutput(safeOutput, toolName);
    }

    /// <summary>Get a summary of what happened during execution (for test assertions).</summary>
    public string GetExecutionSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"GenerateAsync calls: {GenerateCallCount}");
        sb.AppendLine($"Responses consumed: {_allResponses.Count}");
        sb.AppendLine($"Tool results captured: {ToolResults.Count}");
        foreach (var (tool, output) in ToolResults)
            sb.AppendLine($"  {tool}: {TruncateForLog(output, 100)}");
        return sb.ToString();
    }

    private string TruncateForLog(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text.Substring(0, max) + "...";
    }
}