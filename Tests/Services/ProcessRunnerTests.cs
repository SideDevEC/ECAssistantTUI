using ECAssistant.Services;
using ECAssistant.Interfaces;

namespace ECAssistant.Tests.Services;

public class ProcessRunnerTests
{
    [Fact]
    public void Constructor_Default_CreatesInstance()
    {
        var runner = new ProcessRunner();
        Assert.NotNull(runner);
    }

    [Fact]
    public async Task ExecuteAsync_SimpleEchoCommand_ReturnsZeroExitCode()
    {
        var runner = new ProcessRunner();
        var result = await runner.ExecuteAsync("echo hello");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StdOut);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidCommand_ReturnsNonZeroExitCode()
    {
        var runner = new ProcessRunner();
        var result = await runner.ExecuteAsync("nonexistentcommand12345");
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_CommandWithStderr_CapturesStderr()
    {
        var runner = new ProcessRunner();
        var result = await runner.ExecuteAsync("echo errormsg >&2");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("errormsg", result.StdErr);
    }

    [Fact]
    public async Task ExecuteAsync_WithWorkDir_RunsInCorrectDirectory()
    {
        var runner = new ProcessRunner();
        var tempDir = Path.GetTempPath();
        var result = await runner.ExecuteAsync("pwd", tempDir);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(tempDir.TrimEnd('/'), result.StdOut.Trim());
    }

    [Fact]
    public async Task ExecuteAsync_CommandOutput_ReturnsStdOutContent()
    {
        var runner = new ProcessRunner();
        var result = await runner.ExecuteAsync("printf 'line1\\nline2'");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("line1", result.StdOut);
        Assert.Contains("line2", result.StdOut);
    }

    [Fact]
    public async Task ExecuteAsync_MultiLineCommand_ExecutesSuccessfully()
    {
        var runner = new ProcessRunner();
        var result = await runner.ExecuteAsync("x=1 && echo $x");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("1", result.StdOut);
    }

    [Fact]
    public async Task ExecuteAsync_CommandReturningNonZero_PropagatesExitCode()
    {
        var runner = new ProcessRunner();
        var result = await runner.ExecuteAsync("exit 42");
        Assert.Equal(42, result.ExitCode);
    }
}