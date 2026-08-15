using ECAssistant.Interfaces;
using ECAssistant.Tools.Shell;

namespace ECAssistant.Tests.Tools;

public class EShellAgentTests
{
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly Mock<IConfigProvider> _configProvider = new();
    private readonly Mock<IColorFormatter> _colorFormatter = new();

    private EShellAgent CreateTool(string workingDir = "/tmp")
    {
        return new EShellAgent(_processRunner.Object, _configProvider.Object, _colorFormatter.Object, workingDir);
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

    // ── GetPolicy ──

    [Fact]
    public void GetPolicy_ReturnsApprovedPolicy()
    {
        var tool = CreateTool();
        var policy = tool.GetPolicy();

        Assert.Equal("EShellAgent", policy.ToolName);
        Assert.Equal("Approved", policy.Level);
    }

    // ── ExecuteAsync — success cases ──

    [Fact]
    public async Task ExecuteAsync_EmptyInput_ReturnsMissingCommandMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("");

        Assert.Contains("Missing command", result);
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceInput_ReturnsMissingCommandMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("   ");

        Assert.Contains("Missing command", result);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessNoStdout_ReturnsNoOutputMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("echo");

        Assert.Contains("[Shell Success]", result);
        Assert.Contains("no output", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessWithStdout_ReturnsOutput()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("hello world"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("echo hello");

        Assert.Contains("[Shell Success]", result);
        Assert.Contains("hello world", result);
    }

    // ── ExecuteAsync — warning (exit 0 with stderr) ──

    [Fact]
    public async Task ExecuteAsync_ExitZeroWithStderr_ReturnsWarning()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "output", "warning text", false));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("some-cmd");

        Assert.Contains("[Shell Warning]", result);
        Assert.Contains("output", result);
        Assert.Contains("warning text", result);
    }

    [Fact]
    public async Task ExecuteAsync_ExitZeroWithStderrNoStdout_ReturnsWarningWithStderrOnly()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", "err msg", false));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("cmd");

        Assert.Contains("[Shell Warning]", result);
        Assert.Contains("err msg", result);
    }

    // ── ExecuteAsync — error cases ──

    [Fact]
    public async Task ExecuteAsync_NonZeroExit_ReturnsError()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorResult(1, "command not found"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("bad-cmd");

        Assert.Contains("[Shell Error (Exit 1)]", result);
        Assert.Contains("command not found", result);
        Assert.Contains("bad-cmd", result);
    }

    [Fact]
    public async Task ExecuteAsync_Exception_ReturnsExecutionFailedMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("cmd");

        Assert.Contains("Execution failed", result);
        Assert.Contains("InvalidOperationException", result);
        Assert.Contains("boom", result);
    }

    // ── XML escaping ──

    [Fact]
    public async Task ExecuteAsync_OutputWithAngleBrackets_PreservedInOutput()
    {
        // EscapeXml replaces < with < and > with > (literal same chars), so angle brackets are preserved
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("<script>alert('xss')</script>"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("cmd");

        Assert.Contains("<script>alert('xss')</script>", result);
    }

    [Fact]
    public async Task ExecuteAsync_StderrWithAngleBrackets_PreservedInOutput()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorResult(1, "<error>"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("cmd");

        Assert.Contains("<error>", result);
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

        await tool.ExecuteAsync("cmd", token);

        _processRunner.Verify(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), token), Times.Once);
    }

    // ── Null input ──

    [Fact]
    public async Task ExecuteAsync_NullInput_ReturnsMissingCommandMessage()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(null!);

        Assert.Contains("Missing command", result);
    }
}