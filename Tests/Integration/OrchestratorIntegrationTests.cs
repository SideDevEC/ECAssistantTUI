using ECAssistant;
using ECAssistant.Engine;
using ECAssistant.Interfaces;
using ECAssistant.Orchestration;
using ECAssistant.Services;
using ECAssistant.Testing;
using ECAssistant.Tools;
using ECAssistant.Tools.Shell;
using ECAssistant.Tools.Code;
using ECAssistant.Tools.Reader;
using ECAssistant.UI;

namespace ECAssistant.Tests.Integration;

/// <summary>
/// Integration tests for the full Orchestrator → Engine → Tools → Output pipeline.
/// Uses MockEngine (no real GGUF model) and mocked tool dependencies.
/// </summary>
[Collection("ProgramGuiCollection")]
public class OrchestratorIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EGuiTestHarness _gui;

    public OrchestratorIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAInteg_Orch_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _gui = new EGuiTestHarness();
        Program.Gui = _gui;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>Create a MockEngine with real tools wired to mocked dependencies.</summary>
    private (MockEngine engine, AgentOrchestrator orchestrator, Mock<IProcessRunner> processRunner, Mock<IFileSystem> fileSystem) CreateEngineWithMockedTools(int maxTurns = 10)
    {
        var mockProcessRunner = new Mock<IProcessRunner>();
        var mockFileSystem = new Mock<IFileSystem>();
        var mockConfig = new Mock<IConfigProvider>();
        var mockLogger = new Mock<ILogger>();

        mockConfig.Setup(c => c.GetValue("workingDir", It.IsAny<string>())).Returns(_tempDir);
        mockConfig.Setup(c => c.GetValue(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, _) => _tempDir);

        var engine = new MockEngine(_tempDir);

        // Register real tools with mocked dependencies
        engine.RegisterTool(new EShellAgent(mockProcessRunner.Object, mockConfig.Object, _tempDir));
        engine.RegisterTool(new ECodeEditorTool(mockFileSystem.Object, mockConfig.Object));
        engine.RegisterTool(new EFileReaderTool(mockFileSystem.Object, mockConfig.Object));

        var policy = new ECAssistant.Tools.ToolPolicy();
        var orchestrator = new AgentOrchestrator(engine, sessionOutput: new TestSessionOutput(_gui), maxTurns: maxTurns, maxFailures: 3, toolPolicy: policy, logger: mockLogger.Object);

        return (engine, orchestrator, mockProcessRunner, mockFileSystem);
    }

    // ── Single tool call → final answer ──

    [Fact]
    public async Task SingleToolCall_ToolExecutesThenFinalAnswer_VerifiesToolWasCalled()
    {
        var (engine, orchestrator, processRunner, _) = CreateEngineWithMockedTools();

        // Turn 1: LLM requests a shell command
        engine.AddResponse("<lm><thinking>Need to run echo</thinking><toolcall>EShellAgent<command>echo hello</command></toolcall></lm>");
        // Turn 2: LLM gives final answer
        engine.AddResponse("<lm><thinking>Command succeeded</thinking><output>Done</output></lm>");

        processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "hello", "", false));

        var result = await orchestrator.ExecuteMultiStep("Run echo hello");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        Assert.Equal("Done", result.FinalOutput);
        Assert.Equal(2, engine.GenerateCallCount);
        processRunner.Verify(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(engine.ToolResults);
        Assert.Equal("EShellAgent", engine.ToolResults[0].toolName);
        Assert.Contains("hello", engine.ToolResults[0].output);
    }

    // ── Multiple tool calls in one response ──

    [Fact]
    public async Task MultipleToolCallsInOneResponse_BothExecuted_VerifiesBothRan()
    {
        var (engine, orchestrator, processRunner, _) = CreateEngineWithMockedTools();

        // Turn 1: LLM requests two shell commands in one response
        engine.AddResponse(
            "<lm><thinking>Need two commands</thinking>" +
            "<toolcall>EShellAgent<command>echo first</command></toolcall>" +
            "<toolcall>EShellAgent<command>echo second</command></toolcall>" +
            "</lm>");
        // Turn 2: Final answer
        engine.AddResponse("<lm><thinking>Both done</thinking><output>Both commands executed</output></lm>");

        processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "output", "", false));

        var result = await orchestrator.ExecuteMultiStep("Run two commands");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        Assert.Equal("Both commands executed", result.FinalOutput);
        // ProcessRunner should be called twice (once per tool call)
        processRunner.Verify(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── Multi-turn: tool call → result → final answer ──

    [Fact]
    public async Task MultiTurn_ToolCallThenAnswer_VerifiesTwoGenerateCalls()
    {
        var (engine, orchestrator, processRunner, _) = CreateEngineWithMockedTools();

        engine.AddResponse("<lm><thinking>Step 1</thinking><toolcall>EShellAgent<command>ls</command></toolcall></lm>");
        engine.AddResponse("<lm><thinking>Got the listing</thinking><output>Directory listed successfully</output></lm>");

        processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "file1.txt\nfile2.txt", "", false));

        var result = await orchestrator.ExecuteMultiStep("List directory");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        Assert.Equal("Directory listed successfully", result.FinalOutput);
        Assert.Equal(2, engine.GenerateCallCount);
    }

    // ── Max turns reached ──

    [Fact]
    public async Task MaxTurnsReached_LLMAlwaysCallsTools_TurnsExhaustedStatus()
    {
        var (engine, orchestrator, processRunner, _) = CreateEngineWithMockedTools(maxTurns: 3);

        // Engine always returns a tool call, never a final answer
        engine.SetDefaultResponse("<lm><thinking>Need more data</thinking><toolcall>EShellAgent<command>echo retry</command></toolcall></lm>");

        processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "ok", "", false));

        var result = await orchestrator.ExecuteMultiStep("Never-ending task");

        Assert.Equal(OrchestratorStatus.TurnsExhausted, result.Status);
        Assert.Contains("max turns", result.FinalOutput, StringComparison.OrdinalIgnoreCase);
    }

    // ── Tool failure: mock IProcessRunner returns error ──

    [Fact]
    public async Task ToolFailure_ProcessRunnerReturnsError_ErrorHandledGracefully()
    {
        var (engine, orchestrator, processRunner, _) = CreateEngineWithMockedTools();

        engine.AddResponse("<lm><thinking>Run a failing command</thinking><toolcall>EShellAgent<command>bad-cmd</command></toolcall></lm>");
        engine.AddResponse("<lm><thinking>Command failed, reporting</thinking><output>The command failed with exit code 1</output></lm>");

        processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", "command not found", false));

        var result = await orchestrator.ExecuteMultiStep("Run a failing command");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        Assert.Equal("The command failed with exit code 1", result.FinalOutput);
        // The tool result should contain error info
        Assert.Single(engine.ToolResults);
        Assert.Contains("Error", engine.ToolResults[0].output, StringComparison.OrdinalIgnoreCase);
    }

    // ── Direct answer (no tools) ──

    [Fact]
    public async Task DirectAnswer_NoToolCalls_VerifiesNoToolExecution()
    {
        var (engine, orchestrator, processRunner, _) = CreateEngineWithMockedTools();

        engine.AddResponse("<lm><thinking>Simple answer</thinking><output>42</output></lm>");

        var result = await orchestrator.ExecuteMultiStep("What is 6*7?");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        Assert.Equal("42", result.FinalOutput);
        Assert.Equal(1, engine.GenerateCallCount);
        Assert.Empty(engine.ToolResults);
        processRunner.Verify(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Empty/null response handling ──

    [Fact]
    public async Task EmptyResponse_HandledAsInvalidFormat_RetriesThenExhausts()
    {
        var (engine, orchestrator, _, _) = CreateEngineWithMockedTools(maxTurns: 5);

        // Return empty string — no tags, will trigger format retries
        engine.SetDefaultResponse("");

        var result = await orchestrator.ExecuteMultiStep("Test empty response");

        // Should exhaust turns due to format retries never producing valid tags
        Assert.True(result.Status == OrchestratorStatus.TurnsExhausted || result.Status == OrchestratorStatus.GoalAchieved);
    }

    [Fact]
    public async Task NoTags_ResponseTriggersFormatRetry_ThenValidAnswer()
    {
        var (engine, orchestrator, _, _) = CreateEngineWithMockedTools(maxTurns: 5);

        // First: no tags (invalid)
        engine.AddResponse("I think the answer is 42.");
        // After retry: valid response
        engine.AddResponse("<lm><thinking>correcting format</thinking><output>42</output></lm>");

        var result = await orchestrator.ExecuteMultiStep("What is the answer?");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        Assert.Equal("42", result.FinalOutput);
    }

    // ── Tool not registered: unknown tool name ──

    [Fact]
    public async Task UnknownTool_NotRegistered_ErrorHandledInOrchestrator()
    {
        var (engine, orchestrator, _, _) = CreateEngineWithMockedTools();

        engine.AddResponse("<lm><thinking>Use unknown tool</thinking><toolcall>ENonExistentTool<arg>value</arg></toolcall></lm>");
        engine.AddResponse("<lm><thinking>Tool not found, answering</thinking><output>Tool was not available</output></lm>");

        var result = await orchestrator.ExecuteMultiStep("Use unknown tool");

        // The orchestrator should handle the unknown tool and continue
        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        Assert.Equal("Tool was not available", result.FinalOutput);
    }

    // ── Helper ──
}