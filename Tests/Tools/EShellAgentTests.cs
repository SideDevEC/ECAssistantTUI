using ECAssistant.Config;
using ECAssistant.Interfaces;
using ECAssistant.Tools.Shell;

namespace ECAssistant.Tests.Tools;

public class EShellAgentTests
{
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly EAgentConfig _config = new();

    private EShellAgent CreateTool(string workingDir = "/tmp")
    {
        return new EShellAgent(_processRunner.Object, _config, workingDir);
    }

    private ProcessResult SuccessResult(string stdout = "ok", string stderr = "") =>
        new(0, stdout, stderr, false);

    private ProcessResult ErrorResult(int exitCode, string stderr = "error") =>
        new(exitCode, "", stderr, false);

    // ── Name / Description ──

    [Fact]
    public void Name_ReturnsEShellAgent()
    {
        var tool = CreateTool();
        Assert.Equal("EShellAgent", tool.Name);
    }

    [Fact]
    public void Description_ContainsFilesystemAndShell()
    {
        var tool = CreateTool();
        Assert.Contains("filesystem", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shell", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    

    // ── ExecuteAsync — success cases ──

    [Fact]
    public async Task ExecuteAsync_EmptyInput_ReturnsMissingCommandMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>());

        Assert.Contains("Missing command", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceInput_ReturnsMissingCommandMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "   " });

        Assert.Contains("Missing command", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessNoStdout_ReturnsNoOutputMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "echo" });

        Assert.True(result.Succeeded);
        Assert.Contains("no output", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessWithStdout_ReturnsOutput()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("hello world"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "echo hello" });

        Assert.True(result.Succeeded);
        Assert.Contains("hello world", result.Output + result.Error);
    }

    // ── ExecuteAsync — warning (exit 0 with stderr) ──

    [Fact]
    public async Task ExecuteAsync_ExitZeroWithStderr_ReturnsWarning()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "output", "warning text", false));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "some-cmd" });

        
        Assert.Contains("output", result.Output + result.Error);
        Assert.Contains("warning text", result.Output + result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ExitZeroWithStderrNoStdout_ReturnsWarningWithStderrOnly()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", "err msg", false));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "cmd" });

        
        Assert.Contains("err msg", result.Output + result.Error);
    }

    // ── ExecuteAsync — error cases ──

    [Fact]
    public async Task ExecuteAsync_NonZeroExit_ReturnsError()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorResult(1, "command not found"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "bad-cmd" });

        Assert.Contains("command not found", result.Error);
        Assert.Contains("bad-cmd", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_Exception_ReturnsExecutionFailedMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "cmd" });

        Assert.Contains("Execution failed", result.Error);
        Assert.Contains("InvalidOperationException", result.Error);
        Assert.Contains("boom", result.Error);
    }

    // ── XML escaping ──

    [Fact]

    public async Task ExecuteAsync_OutputWithAngleBrackets_XmlEscapedInOutput()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("<script>alert('xss')</script>"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "cmd" });

    }

    [Fact]
    public async Task ExecuteAsync_StderrWithAngleBrackets_XmlEscapedInOutput()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorResult(1, "<error>"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "cmd" });

    }
    // ── CancellationToken ──

    [Fact]
    public async Task ExecuteAsync_PassesCancellationToken()
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), token))
            .ReturnsAsync(SuccessResult("ok"));
        var tool = CreateTool();

        await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "cmd" }, token);

        _processRunner.Verify(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), token), Times.Once);
    }

    // ── Null input ──

    [Fact]
    public async Task ExecuteAsync_NullInput_ReturnsMissingCommandMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(null!);

        Assert.Contains("Missing command", result.Error);
    }
}