using ECAssistant;
using ECAssistant.Engine;
using ECAssistant.Interfaces;
using ECAssistant.Orchestration;
using ECAssistant.Services;
using ECAssistant.Testing;
using ECAssistant.Tools;
using ECAssistant.UI;
using ToolPolicy = ECAssistant.Tools.ToolPolicy;

namespace ECAssistant.Tests.Integration;

/// <summary>
/// Integration tests for sub-agent spawning through the orchestrator.
/// Tests ESubAgent tool registration and SubAgentManager integration.
/// </summary>
[Collection("ProgramGuiCollection")]
public class SubAgentIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EGuiTestHarness _gui;
    private readonly List<MockEngine> _engines = new();
    private readonly List<AgentOrchestrator> _orchestrators = new();

    public SubAgentIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAInteg_SubAgent_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _gui = new EGuiTestHarness();
        TestRunner.TestGui = _gui;
    }

    public void Dispose()
    {
        foreach (var orch in _orchestrators)
            try { orch.DisposeAsync().AsTask().Wait(1000); } catch { }
        foreach (var eng in _engines)
            try { eng.DisposeAsync().AsTask().Wait(1000); } catch { }
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>Create a MockEngine-based orchestrator for sub-agent tests.</summary>
    private (MockEngine engine, AgentOrchestrator orchestrator) CreateEngineWithOrchestrator(int maxTurns = 5)
    {
        var logger = new Mock<ILogger>();
        var engine = new MockEngine(_tempDir);
        _engines.Add(engine);
        var policy = new ECAssistant.Tools.ToolPolicy();
        var orchestrator = new AgentOrchestrator(engine, sessionOutput: new TestSessionOutput(_gui), maxTurns: maxTurns, maxFailures: 3, toolPolicy: policy, logger: logger.Object);
        _orchestrators.Add(orchestrator);
        return (engine, orchestrator);
    }

    // ── InitializeSubAgents registers ESubAgent tool ──

    [Fact]
    public void InitializeSubAgents_WithMockEngine_ESubAgentToolRegistered()
    {
        var (engine, orchestrator) = CreateEngineWithOrchestrator();

        // Before init, ESubAgent is not registered
        Assert.DoesNotContain(engine.Tools, t => t.Name == "ESubAgent");

        orchestrator.InitializeSubAgents(_tempDir);

        // After init, ESubAgent should be registered
        Assert.Contains(engine.Tools, t => t.Name == "ESubAgent");
    }

    [Fact]
    public void InitializeSubAgents_WithMockEngine_SubAgentManagerNotNull()
    {
        var (engine, orchestrator) = CreateEngineWithOrchestrator();

        Assert.Null(orchestrator.SubAgentManager);

        orchestrator.InitializeSubAgents(_tempDir);

        Assert.NotNull(orchestrator.SubAgentManager);
    }

    [Fact]
    public void InitializeSubAgents_WithMockEngine_PolicyAllowsESubAgent()
    {
        var (engine, orchestrator) = CreateEngineWithMockedTools();

        orchestrator.InitializeSubAgents(_tempDir);

        // ESubAgent should be allowed (not blocked)
        Assert.True(orchestrator.Policy.IsAllowed("ESubAgent"));
    }

    // ── ESubAgent tool call through orchestrator ──
    // NOTE: We do NOT call InitializeSubAgents here because that creates a real
    // SubAgentManager which loads real config and would spawn a real EAgentEngine
    // with a 5GB GGUF model. Instead, we register a mock ESubAgent tool that
    // returns a canned result, testing only the orchestrator dispatch path.

    [Fact]
    public async Task SubAgentToolCall_ThroughOrchestrator_ReturnsResult()
    {
        var (engine, orchestrator) = CreateEngineWithOrchestrator(maxTurns: 5);

        // Register a mock ESubAgent tool instead of InitializeSubAgents
        engine.RegisterTool(new MockSubAgentTool());
        orchestrator.Policy.SetPermission("ESubAgent", ECAssistant.Tools.ToolPermissionLevel.Allowed, "Test");

        engine.AddResponse(
            "<lm><thinking>Spawn a sub-agent</thinking>" +
            "<toolcall>ESubAgent<task>Do something simple</task></toolcall></lm>");
        engine.AddResponse("<lm><thinking>Sub-agent completed</thinking><output>Sub-agent task handled</output></lm>");

        var result = await orchestrator.ExecuteMultiStep("Run a sub-agent task");

        // The orchestrator should complete — the mock sub-agent returns a canned result
        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        Assert.Equal("Sub-agent task handled", result.FinalOutput);
        // At least one tool result should be recorded
        Assert.NotEmpty(engine.ToolResults);
    }

    // ── Sub-agent with missing task argument ──

    [Fact]
    public async Task SubAgentToolCall_MissingTaskArgument_ReturnsError()
    {
        var (engine, orchestrator) = CreateEngineWithMockedTools(maxTurns: 5);
        // Register mock ESubAgent instead of InitializeSubAgents to avoid loading real model
        engine.RegisterTool(new MockSubAgentTool());
        orchestrator.Policy.SetPermission("ESubAgent", ECAssistant.Tools.ToolPermissionLevel.Allowed, "Test");

        // Call ESubAgent without task argument
        engine.AddResponse("<lm><thinking>Spawn sub-agent without task</thinking><toolcall>ESubAgent</toolcall></lm>");
        engine.AddResponse("<lm><thinking>Error received</thinking><output>Missing task argument</output></lm>");

        var result = await orchestrator.ExecuteMultiStep("Spawn sub-agent without task");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        // The tool result should contain error info about missing task
        Assert.Contains(engine.ToolResults, t => t.output.Contains("Missing task", StringComparison.OrdinalIgnoreCase)
                                              || t.output.Contains("FAILED", StringComparison.OrdinalIgnoreCase));
    }

    // ── Sub-agent manager configuration defaults ──

    [Fact]
    public void SubAgentManager_Defaults_AreSetFromConfig()
    {
        var (engine, orchestrator) = CreateEngineWithOrchestrator();
        orchestrator.InitializeSubAgents(_tempDir);

        var manager = orchestrator.SubAgentManager!;
        Assert.True(manager.DefaultMaxTurns > 0);
        Assert.True(manager.DefaultTimeoutSeconds > 0);
        Assert.True(manager.MaxConcurrent > 0);
    }

    // ── Sub-agent manager cancel all ──

    [Fact]
    public void SubAgentManager_CancelAll_WhenNoActiveAgents_DoesNotThrow()
    {
        var (engine, orchestrator) = CreateEngineWithOrchestrator();
        orchestrator.InitializeSubAgents(_tempDir);

        var manager = orchestrator.SubAgentManager!;

        // Should be a no-op when no agents are active
        manager.CancelAll();
        Assert.Empty(manager.ActiveAgents);
    }

    // ── Sub-agent manager get active status ──

    [Fact]
    public void SubAgentManager_GetActiveStatus_WhenNoAgents_ReturnsEmptyList()
    {
        var (engine, orchestrator) = CreateEngineWithOrchestrator();
        orchestrator.InitializeSubAgents(_tempDir);

        var manager = orchestrator.SubAgentManager!;
        var status = manager.GetActiveStatus();

        Assert.Empty(status);
    }

    // ── Multiple ESubAgent calls in one response (parallel) ──

    [Fact]
    public async Task MultipleSubAgentCalls_BothDispatched_OrchestratorContinues()
    {
        var (engine, orchestrator) = CreateEngineWithOrchestrator(maxTurns: 5);
        // Register mock ESubAgent instead of InitializeSubAgents to avoid loading real model
        engine.RegisterTool(new MockSubAgentTool());
        orchestrator.Policy.SetPermission("ESubAgent", ECAssistant.Tools.ToolPermissionLevel.Allowed, "Test");

        engine.AddResponse(
            "<lm><thinking>Spawn two sub-agents</thinking>" +
            "<toolcall>ESubAgent<task>Task A</task></toolcall>" +
            "<toolcall>ESubAgent<task>Task B</task></toolcall></lm>");
        engine.AddResponse("<lm><thinking>Both sub-agents done</thinking><output>Both tasks completed</output></lm>");

        var result = await orchestrator.ExecuteMultiStep("Run two sub-agents");

        Assert.Equal(OrchestratorStatus.GoalAchieved, result.Status);
        Assert.Equal("Both tasks completed", result.FinalOutput);
    }

    // ── Helper: create engine with mocked tool deps ──
    private (MockEngine engine, AgentOrchestrator orchestrator) CreateEngineWithMockedTools(int maxTurns = 5)
    {
        var mockConfig = new Mock<IConfigProvider>();
        var mockLogger = new Mock<ILogger>();

        mockConfig.Setup(c => c.GetValue(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, _) => _tempDir);

        var engine = new MockEngine(_tempDir);
        _engines.Add(engine);
        var policy = new ECAssistant.Tools.ToolPolicy();
        var orchestrator = new AgentOrchestrator(engine, sessionOutput: new TestSessionOutput(_gui), maxTurns: maxTurns, maxFailures: 3, toolPolicy: policy, logger: mockLogger.Object);
        _orchestrators.Add(orchestrator);
        return (engine, orchestrator);
    }
}

/// <summary>
/// Mock ESubAgent tool for testing — returns a canned result without
/// spawning a real EAgentEngine that would load a 5GB GGUF model.
/// </summary>
public class MockSubAgentTool : ECAssistant.Tools.EToolBase
{
    public override string Name => "ESubAgent";
    public override string Description => "Mock sub-agent tool for testing";
    public override string UsageExample => "ESubAgent(task=\"test\")";
    public override string GetToolRules() => "<task>=description (required)";
    public override string GetToolExample() => "<toolcall>ESubAgent<task>test</task></toolcall>";

    public override async Task<ECAssistant.Tools.EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        var taskDesc = arguments.GetValueOrDefault("task")?.Trim();
        if (string.IsNullOrEmpty(taskDesc))
            return new ECAssistant.Tools.EToolResult { ToolName = Name, Succeeded = false, Error = "Missing task argument." };

        return new ECAssistant.Tools.EToolResult
        {
            ToolName = Name,
            Succeeded = true,
            Output = $"Mock sub-agent completed task: {taskDesc}"
        };
    }
}