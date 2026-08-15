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

    public SubAgentIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ECAInteg_SubAgent_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _gui = new EGuiTestHarness();
        Program.Gui = _gui;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>Create a MockEngine-based orchestrator for sub-agent tests.</summary>
    private (MockEngine engine, AgentOrchestrator orchestrator) CreateEngineWithOrchestrator(int maxTurns = 5)
    {
        var logger = new Mock<ILogger>();
        var engine = new MockEngine(_tempDir);
        var policy = new ECAssistant.Tools.ToolPolicy();
        var orchestrator = new AgentOrchestrator(engine, sessionOutput: null, maxTurns: maxTurns, maxFailures: 3, toolPolicy: policy, logger: logger.Object);
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

    [Fact]
    public async Task SubAgentToolCall_ThroughOrchestrator_ReturnsResult()
    {
        var (engine, orchestrator) = CreateEngineWithOrchestrator(maxTurns: 5);

        // Initialize sub-agents
        orchestrator.InitializeSubAgents(_tempDir);

        // The sub-agent will try to run with a real engine (not mock), which will fail
        // because there's no real model. But the ESubAgent tool should still be dispatched
        // and return a result (failure due to no model).
        engine.AddResponse(
            "<lm><thinking>Spawn a sub-agent</thinking>" +
            "<toolcall>ESubAgent<task>Do something simple</task></toolcall></lm>");
        engine.AddResponse("<lm><thinking>Sub-agent completed</thinking><output>Sub-agent task handled</output></lm>");

        var result = await orchestrator.ExecuteMultiStep("Run a sub-agent task");

        // The orchestrator should complete — the sub-agent may fail but the orchestrator continues
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
        orchestrator.InitializeSubAgents(_tempDir);

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
        orchestrator.InitializeSubAgents(_tempDir);

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
        var mockColor = new Mock<IColorFormatter>();
        var mockLogger = new Mock<ILogger>();

        mockConfig.Setup(c => c.GetValue(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, _) => _tempDir);

        // ColorFormatter mock
        mockColor.Setup(c => c.Format(It.IsAny<string>(), It.IsAny<string>())).Returns<string, string>((_, text) => text);
        mockColor.SetupGet(c => c.Red).Returns("");
        mockColor.SetupGet(c => c.Green).Returns("");
        mockColor.SetupGet(c => c.Blue).Returns("");
        mockColor.SetupGet(c => c.Yellow).Returns("");
        mockColor.SetupGet(c => c.Cyan).Returns("");
        mockColor.SetupGet(c => c.White).Returns("");
        mockColor.SetupGet(c => c.Reset).Returns("");
        mockColor.SetupGet(c => c.Bold).Returns("");
        mockColor.SetupGet(c => c.Dim).Returns("");
        mockColor.SetupGet(c => c.Black).Returns("");
        mockColor.SetupGet(c => c.Magenta).Returns("");

        var engine = new MockEngine(_tempDir);
        var policy = new ECAssistant.Tools.ToolPolicy();
        var orchestrator = new AgentOrchestrator(engine, sessionOutput: null, maxTurns: maxTurns, maxFailures: 3, toolPolicy: policy, logger: mockLogger.Object);
        return (engine, orchestrator);
    }
}