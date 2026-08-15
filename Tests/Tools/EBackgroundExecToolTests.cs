using ECAssistant.Config;
using ECAssistant.Interfaces;
using ECAssistant.Services;
using ECAssistant.Tools.Background;

namespace ECAssistant.Tests.Tools;

public class EBackgroundExecToolTests : IDisposable
{
    private readonly BackgroundProcessManager _mgr = new();
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly Mock<IFileSystem> _fileSystem = new();
    private readonly EAgentConfig _config = new();

    private EBackgroundExecTool CreateTool()
    {
        return new EBackgroundExecTool(
            _mgr, _processRunner.Object, _fileSystem.Object,
            _config);
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


    

    // ── ExecuteAsync — start action ──

    [Fact]
    public async Task ExecuteAsync_StartWithCommand_ReturnsStartedMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "start", ["command"] = "echo hello" });

        Assert.Contains("bg-1", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("echo hello", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("Background process started", result.Succeeded ? result.Output : result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_StartWithoutCommand_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "start" });

        Assert.Contains("Missing 'command'", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_StartWithEmptyCommand_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "start", ["command"] = "" });

        Assert.Contains("Missing 'command'", result.Error);
    }

    // ── ExecuteAsync — status action ──

    [Fact]
    public async Task ExecuteAsync_StatusNoProcesses_ReturnsNoProcessesMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "status" });

        Assert.Contains("No background processes", result.Succeeded ? result.Output : result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_StatusWithProcesses_ReturnsProcessList()
    {
        // Start a process first
        await _mgr.StartAsync("echo hello", Path.GetTempPath());
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "status" });

        Assert.Contains("bg-1", result.Succeeded ? result.Output : result.Error);
        Assert.Contains("1", result.Succeeded ? result.Output : result.Error);
    }

    // ── ExecuteAsync — output action ──

    [Fact]
    public async Task ExecuteAsync_OutputWithId_ReturnsOutput()
    {
        var id = await _mgr.StartAsync("echo test_output", Path.GetTempPath());
        // Wait for process to complete
        await Task.Delay(2000);
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "output", ["id"] = id });

        Assert.Contains(id, result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_OutputWithoutId_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "output" });

        Assert.Contains("Missing 'id'", result.Error);
    }

    // ── ExecuteAsync — kill action ──

    [Fact]
    public async Task ExecuteAsync_KillWithInvalidId_ReturnsErrorMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "kill", ["id"] = "bg-99" });

        Assert.Contains("Failed to kill", result.Error);
        Assert.Contains("bg-99", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_KillWithoutId_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "kill" });

        Assert.Contains("Missing 'id'", result.Error);
    }

    // ── ExecuteAsync — unknown action ──

    [Fact]
    public async Task ExecuteAsync_UnknownAction_ReturnsErrorMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "foobar" });

        Assert.Contains("Unknown action", result.Error);
        Assert.Contains("foobar", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyAction_ReturnsErrorMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "" });

        Assert.Contains("Unknown action", result.Error);
    }

    // ── ExecuteAsync — empty/null input ──

    [Fact]
    public async Task ExecuteAsync_EmptyInput_ReturnsUnknownAction()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>());

        Assert.Contains("Unknown action", result.Error);
    }

    // ── ExecuteAsync — cancellation ──

    [Fact]
    public async Task ExecuteAsync_StartCancelled_ReturnsCancelledMessage()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "start", ["command"] = "build" }, cts.Token);

        Assert.Contains("CANCELLED", result.Error);
    }

    // ── Fallback parsing (non-JSON) ──

    [Fact]
    public async Task ExecuteAsync_NonJsonInput_ParsesKeyValuePairFormat()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "status" });

        Assert.Contains("No background processes", result.Succeeded ? result.Output : result.Error);
    }
}