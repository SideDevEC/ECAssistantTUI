using ECAssistant.Interfaces;
using ECAssistant.Services;
using ECAssistant.Tools.Background;

namespace ECAssistant.Tests.Tools;

public class EBackgroundExecToolTests : IDisposable
{
    private readonly BackgroundProcessManager _mgr = new();
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly Mock<IFileSystem> _fileSystem = new();
    private readonly Mock<IConfigProvider> _configProvider = new();

    private EBackgroundExecTool CreateTool()
    {
        _configProvider.Setup(c => c.GetValue("background.workingDir", It.IsAny<string>()))
                       .Returns(Path.GetTempPath());
        return new EBackgroundExecTool(
            _mgr, _processRunner.Object, _fileSystem.Object,
            _configProvider.Object);
    }

    public void Dispose() => _mgr.Dispose();

    // ── Name / Description ──

    [Fact]
    public void Name_ReturnsEBackgroundExec()
    {
        var tool = CreateTool();
        Assert.Equal("EBackgroundExec", tool.Name);
    }

    [Fact]
    public void Description_ContainsBackgroundProcess()
    {
        var tool = CreateTool();
        Assert.Contains("background", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    // ── GetPolicy ──

    [Fact]
    public void GetPolicy_ReturnsAllowedPolicy()
    {
        var tool = CreateTool();
        var policy = tool.GetPolicy();

        Assert.Equal("EBackgroundExec", policy.ToolName);
        Assert.Equal("Allowed", policy.Level);
    }

    // ── ExecuteAsync — start action ──

    [Fact]
    public async Task ExecuteAsync_StartWithCommand_ReturnsStartedMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"start","command":"echo hello"}""");

        Assert.Contains("bg-1", result);
        Assert.Contains("echo hello", result);
        Assert.Contains("Background process started", result);
    }

    [Fact]
    public async Task ExecuteAsync_StartWithoutCommand_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"start"}""");

        Assert.Contains("Missing 'command'", result);
    }

    [Fact]
    public async Task ExecuteAsync_StartWithEmptyCommand_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"start","command":""}""");

        Assert.Contains("Missing 'command'", result);
    }

    // ── ExecuteAsync — status action ──

    [Fact]
    public async Task ExecuteAsync_StatusNoProcesses_ReturnsNoProcessesMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"status"}""");

        Assert.Contains("No background processes", result);
    }

    [Fact]
    public async Task ExecuteAsync_StatusWithProcesses_ReturnsProcessList()
    {
        // Start a process first
        await _mgr.StartAsync("echo hello", Path.GetTempPath());
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"status"}""");

        Assert.Contains("bg-1", result);
        Assert.Contains("1", result);
    }

    // ── ExecuteAsync — output action ──

    [Fact]
    public async Task ExecuteAsync_OutputWithId_ReturnsOutput()
    {
        var id = await _mgr.StartAsync("echo test_output", Path.GetTempPath());
        // Wait for process to complete
        await Task.Delay(2000);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync($"{{\"action\":\"output\",\"id\":\"{id}\"}}");

        Assert.Contains(id, result);
    }

    [Fact]
    public async Task ExecuteAsync_OutputWithoutId_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"output"}""");

        Assert.Contains("Missing 'id'", result);
    }

    // ── ExecuteAsync — kill action ──

    [Fact]
    public async Task ExecuteAsync_KillWithInvalidId_ReturnsErrorMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"kill","id":"bg-99"}""");

        Assert.Contains("Failed to kill", result);
        Assert.Contains("bg-99", result);
    }

    [Fact]
    public async Task ExecuteAsync_KillWithoutId_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"kill"}""");

        Assert.Contains("Missing 'id'", result);
    }

    // ── ExecuteAsync — unknown action ──

    [Fact]
    public async Task ExecuteAsync_UnknownAction_ReturnsErrorMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"foobar"}""");

        Assert.Contains("Unknown action", result);
        Assert.Contains("foobar", result);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyAction_ReturnsErrorMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":""}""");

        Assert.Contains("Unknown action", result);
    }

    // ── ExecuteAsync — empty/null input ──

    [Fact]
    public async Task ExecuteAsync_EmptyInput_ReturnsUnknownAction()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("");

        Assert.Contains("Unknown action", result);
    }

    // ── ExecuteAsync — cancellation ──

    [Fact]
    public async Task ExecuteAsync_StartCancelled_ReturnsCancelledMessage()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"start","command":"build"}""", cts.Token);

        Assert.Contains("CANCELLED", result);
    }

    // ── Fallback parsing (non-JSON) ──

    [Fact]
    public async Task ExecuteAsync_NonJsonInput_ParsesKeyValuePairFormat()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("action=status");

        Assert.Contains("No background processes", result);
    }
}