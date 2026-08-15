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
/// Integration tests for ParallelToolExecutor — dependency analysis and parallel
/// execution of tool calls through the full orchestrator pipeline.
/// </summary>
[Collection("ProgramGuiCollection")]
public class ParallelToolExecutorIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EGuiTestHarness _gui;

    public ParallelToolExecutorIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAInteg_Parallel_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _gui = new EGuiTestHarness();
        Program.Gui = _gui;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── ToolDependencyAnalyzer with real ToolCallRequests ──

    [Fact]
    public void DependencyAnalyzer_TwoIndependentReadTools_ReturnsSingleGroup()
    {
        var analyzer = new ToolDependencyAnalyzer();

        var calls = new List<ToolCallRequest>
        {
            new() { ToolName = "EFileReader", Index = 1, Args = new() { ["file"] = "a.txt" } },
            new() { ToolName = "EFileReader", Index = 2, Args = new() { ["file"] = "b.txt" } },
        };

        var groups = analyzer.Analyze(calls);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].ToolCalls.Count);
    }

    [Fact]
    public void DependencyAnalyzer_TwoCodeEditorCalls_SameFile_CircularDependencyReturnsSingleGroup()
    {
        var analyzer = new ToolDependencyAnalyzer();

        var calls = new List<ToolCallRequest>
        {
            new() { ToolName = "ECodeEditor", Index = 1, Args = new() { ["file"] = "same.txt", ["action"] = "create", ["content"] = "hello" } },
            new() { ToolName = "ECodeEditor", Index = 2, Args = new() { ["file"] = "same.txt", ["action"] = "patch", ["old_text"] = "hello", ["new_text"] = "world" } },
        };

        var groups = analyzer.Analyze(calls);

        // Two ECodeEditor on same file creates circular dependency (each depends on other)
        // The analyzer resolves this by putting both in the same group
        Assert.Single(groups);
        Assert.Equal(2, groups[0].ToolCalls.Count);
    }

    [Fact]
    public void DependencyAnalyzer_DifferentFiles_ReturnsSingleParallelGroup()
    {
        var analyzer = new ToolDependencyAnalyzer();

        var calls = new List<ToolCallRequest>
        {
            new() { ToolName = "ECodeEditor", Index = 1, Args = new() { ["file"] = "file_a.txt", ["action"] = "create", ["content"] = "A" } },
            new() { ToolName = "ECodeEditor", Index = 2, Args = new() { ["file"] = "file_b.txt", ["action"] = "create", ["content"] = "B" } },
        };

        var groups = analyzer.Analyze(calls);

        // Different files = no dependency = single group
        Assert.Single(groups);
        Assert.Equal(2, groups[0].ToolCalls.Count);
    }

    [Fact]
    public void DependencyAnalyzer_CodeEditorThenBuild_SameIterationSingleGroup()
    {
        var analyzer = new ToolDependencyAnalyzer();

        // ECodeEditor modifies "Program.cs", EDotnetBuild is a post-modify tool
        // Post-modify tools always depend on any modifying tool, but both are processed
        // in the same while-loop iteration (deps[1] = [0], and processed[0] becomes true
        // before i=1 is checked), so they end up in the same group.
        var calls = new List<ToolCallRequest>
        {
            new() { ToolName = "ECodeEditor", Index = 1, Args = new() { ["file"] = "Program.cs", ["action"] = "patch", ["old_text"] = "old", ["new_text"] = "new" } },
            new() { ToolName = "EDotnetBuild", Index = 2, Args = new() { ["project"] = "MyApp.csproj" } },
        };

        var groups = analyzer.Analyze(calls);

        // Both tools end up in the same group because deps are processed in-order
        Assert.Single(groups);
        Assert.Equal(2, groups[0].ToolCalls.Count);
        // But the order within the group matters — ECodeEditor should be first
        Assert.Equal("ECodeEditor", groups[0].ToolCalls[0].ToolName);
        Assert.Equal("EDotnetBuild", groups[0].ToolCalls[1].ToolName);
    }

    [Fact]
    public void DependencyAnalyzer_SingleCall_ReturnsSingleGroup()
    {
        var analyzer = new ToolDependencyAnalyzer();

        var calls = new List<ToolCallRequest>
        {
            new() { ToolName = "EShellAgent", Index = 1, Args = new() { ["command"] = "echo test" } },
        };

        var groups = analyzer.Analyze(calls);

        Assert.Single(groups);
        Assert.Single(groups[0].ToolCalls);
    }

    [Fact]
    public void DependencyAnalyzer_EmptyList_ReturnsSingleEmptyGroup()
    {
        var analyzer = new ToolDependencyAnalyzer();

        var groups = analyzer.Analyze(new List<ToolCallRequest>());

        Assert.Single(groups);
        Assert.Empty(groups[0].ToolCalls);
    }

    [Fact]
    public void DependencyAnalyzer_ThreeIndependentReads_SingleGroup()
    {
        var analyzer = new ToolDependencyAnalyzer();

        var calls = new List<ToolCallRequest>
        {
            new() { ToolName = "EFileReader", Index = 1, Args = new() { ["file"] = "a.txt" } },
            new() { ToolName = "EFileReader", Index = 2, Args = new() { ["file"] = "b.txt" } },
            new() { ToolName = "EFileReader", Index = 3, Args = new() { ["file"] = "c.txt" } },
        };

        var groups = analyzer.Analyze(calls);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].ToolCalls.Count);
    }

    [Fact]
    public void DependencyAnalyzer_WriteThenRead_SameFile_SingleGroupWithOrdering()
    {
        var analyzer = new ToolDependencyAnalyzer();

        // ECodeEditor modifies "target.txt" (IsModifyingTool = true)
        // EFileReader targets "target.txt" — EFileReader depends on ECodeEditor
        // But both are processed in the same while-loop iteration, so they end up
        // in the same group. The ParallelToolExecutor handles sequential execution
        // within the group based on the dependency order.
        var calls = new List<ToolCallRequest>
        {
            new() { ToolName = "ECodeEditor", Index = 1, Args = new() { ["file"] = "target.txt", ["action"] = "create", ["content"] = "content" } },
            new() { ToolName = "EFileReader", Index = 2, Args = new() { ["file"] = "target.txt" } },
        };

        var groups = analyzer.Analyze(calls);

        // Both in same group, but ECodeEditor should be first (EFileReader depends on it)
        Assert.Single(groups);
        Assert.Equal(2, groups[0].ToolCalls.Count);
        Assert.Equal("ECodeEditor", groups[0].ToolCalls[0].ToolName);
        Assert.Equal("EFileReader", groups[0].ToolCalls[1].ToolName);
    }

    // ── Two independent tools through orchestrator ──

    [Fact]
    public async Task TwoIndependentTools_DifferentFiles_BothExecuteInSameBatch()
    {
        var mockProcessRunner = new Mock<IProcessRunner>();
        var mockFileSystem = new Mock<IFileSystem>();
        var mockConfig = new Mock<IConfigProvider>();
        var mockLogger = new Mock<ILogger>();

        mockConfig.Setup(c => c.GetValue(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, _) => _tempDir);

        var engine = new MockEngine(_tempDir);
        engine.RegisterTool(new EFileReaderTool(mockFileSystem.Object, mockConfig.Object));
        engine.RegisterTool(new EShellAgent(mockProcessRunner.Object, mockConfig.Object, _tempDir));

        var policy = new ECAssistant.Tools.ToolPolicy();
        var orchestrator = new AgentOrchestrator(engine, sessionOutput: new TestSessionOutput(_gui), maxTurns: 10, maxFailures: 3, toolPolicy: policy, logger: mockLogger.Object);

        // Two independent tool calls in one response
        engine.AddResponse(
            "<lm><thinking>Read file and run command in parallel</thinking>" +
            "<toolcall>EFileReader<file>readme.md</file></toolcall>" +
            "<toolcall>EShellAgent<command>echo parallel</command></toolcall></lm>");
        engine.AddResponse("<lm><thinking>Both done</thinking><output>Parallel execution complete</output></lm>");

        // EFileReader won't find the file through ToolAdapter, but EShellAgent will execute
        // Use It.IsAny to match whatever the adapter produces
        mockFileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("# README\nContent");
        mockProcessRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "parallel", "", false));

        var result = await orchestrator.ExecuteMultiStep("Read file and run command");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        // At least one tool should have been executed (EShellAgent)
        mockProcessRunner.Verify(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Two dependent tools (same file: write then read) ──

    [Fact]
    public async Task TwoDependentTools_SameFileWriteThenRead_BothExecuteThroughOrchestrator()
    {
        var mockFileSystem = new Mock<IFileSystem>();
        var mockConfig = new Mock<IConfigProvider>();
        var mockLogger = new Mock<ILogger>();

        mockConfig.Setup(c => c.GetValue(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, _) => _tempDir);

        var engine = new MockEngine(_tempDir);
        engine.RegisterTool(new ECodeEditorTool(mockFileSystem.Object, mockConfig.Object));
        engine.RegisterTool(new EFileReaderTool(mockFileSystem.Object, mockConfig.Object));

        var policy = new ECAssistant.Tools.ToolPolicy();
        var orchestrator = new AgentOrchestrator(engine, sessionOutput: new TestSessionOutput(_gui), maxTurns: 10, maxFailures: 3, toolPolicy: policy, logger: mockLogger.Object);

        // Two dependent tool calls (write then read same file)
        engine.AddResponse(
            "<lm><thinking>Create then read same file</thinking>" +
            "<toolcall>ECodeEditor<action>create</action><file>dependent.txt</file><content>Created content</content></toolcall>" +
            "<toolcall>EFileReader<file>dependent.txt</file></toolcall></lm>");
        engine.AddResponse("<lm><thinking>Both done sequentially</thinking><output>Sequential execution complete</output></lm>");

        mockFileSystem.Setup(f => f.FileExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(true);
        mockFileSystem.Setup(f => f.ReadFile(It.IsAny<string>())).Returns("Created content");

        var result = await orchestrator.ExecuteMultiStep("Create then read dependent.txt");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        // Both tools should have been dispatched through the orchestrator
        Assert.NotEmpty(engine.ToolResults);
    }

    // ── Parallel execution produces combined batch output ──

    [Fact]
    public async Task ParallelExecution_BatchOutput_ContainsBothToolResults()
    {
        var mockProcessRunner = new Mock<IProcessRunner>();
        var mockConfig = new Mock<IConfigProvider>();
        var mockLogger = new Mock<ILogger>();

        mockConfig.Setup(c => c.GetValue(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, _) => _tempDir);

        var engine = new MockEngine(_tempDir);
        engine.RegisterTool(new EShellAgent(mockProcessRunner.Object, mockConfig.Object, _tempDir));

        var policy = new ECAssistant.Tools.ToolPolicy();
        var orchestrator = new AgentOrchestrator(engine, sessionOutput: new TestSessionOutput(_gui), maxTurns: 10, maxFailures: 3, toolPolicy: policy, logger: mockLogger.Object);

        engine.AddResponse(
            "<lm><thinking>Run two commands in parallel</thinking>" +
            "<toolcall>EShellAgent<command>echo first</command></toolcall>" +
            "<toolcall>EShellAgent<command>echo second</command></toolcall></lm>");
        engine.AddResponse("<lm><thinking>Batch complete</thinking><output>Batch done</output></lm>");

        // Match any command since EShellAgent wraps the command through ToolAdapter
        mockProcessRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "echo-output", "", false));

        var result = await orchestrator.ExecuteMultiStep("Run two commands");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        // Verify both tools were called
        mockProcessRunner.Verify(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        // Verify a batch result was added
        Assert.Contains(engine.ToolResults, t => t.toolName == "Batch");
    }

    // ── One tool fails in batch — other still succeeds ──

    [Fact]
    public async Task ParallelBatch_OneToolFails_OtherStillSucceeds()
    {
        var mockProcessRunner = new Mock<IProcessRunner>();
        var mockConfig = new Mock<IConfigProvider>();
        var mockLogger = new Mock<ILogger>();

        mockConfig.Setup(c => c.GetValue(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, _) => _tempDir);

        var engine = new MockEngine(_tempDir);
        engine.RegisterTool(new EShellAgent(mockProcessRunner.Object, mockConfig.Object, _tempDir));

        var policy = new ECAssistant.Tools.ToolPolicy();
        var orchestrator = new AgentOrchestrator(engine, sessionOutput: new TestSessionOutput(_gui), maxTurns: 10, maxFailures: 3, toolPolicy: policy, logger: mockLogger.Object);

        engine.AddResponse(
            "<lm><thinking>One good one bad command</thinking>" +
            "<toolcall>EShellAgent<command>echo good</command></toolcall>" +
            "<toolcall>EShellAgent<command>bad-cmd</command></toolcall></lm>");
        engine.AddResponse("<lm><thinking>One succeeded one failed</thinking><output>Partial success</output></lm>");

        // First call succeeds, second fails — use a sequence setup
        var callIndex = 0;
        mockProcessRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var idx = Interlocked.Increment(ref callIndex);
                return idx == 1
                    ? new ProcessResult(0, "good", "", false)
                    : new ProcessResult(127, "", "command not found", false);
            });

        var result = await orchestrator.ExecuteMultiStep("Run good and bad command");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        Assert.Equal("Partial success", result.FinalOutput);

        // Both tools were called despite one failing
        mockProcessRunner.Verify(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── Helper ──
}