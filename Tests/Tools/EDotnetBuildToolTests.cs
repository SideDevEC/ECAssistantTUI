using ECAssistant.Config;
using ECAssistant.Interfaces;
using ECAssistant.Tools.Build;

namespace ECAssistant.Tests.Tools;

public class EDotnetBuildToolTests
{
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly EAgentConfig _config = new();

    private EDotnetBuildTool CreateTool()
    {
        return new EDotnetBuildTool(_processRunner.Object, _config);
    }

    private ProcessResult SuccessResult(string stdout = "Build succeeded.") =>
        new(0, stdout, "", false);

    private ProcessResult FailureResult(string stdout = "", string stderr = "") =>
        new(1, stdout, stderr, false);

    [Fact]
    public void Name_ReturnsDotnetBuild()
    {
        var tool = CreateTool();
        Assert.Equal("DotnetBuild", tool.Name);
    }

    [Fact]
    public void Description_ContainsDotnet()
    {
        var tool = CreateTool();
        Assert.Contains("dotnet", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    

    [Fact]
    public async Task ExecuteAsync_BuildSucceeded_ReturnsSuccessMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("Build succeeded.\n0 Errors\n0 Warnings"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "build" });

        Assert.True(result.Succeeded);
        Assert.Contains("0 warning(s)", result.Output + result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyInput_DefaultsToBuild()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync("dotnet build", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>());

        _processRunner.Verify(p => p.ExecuteAsync("dotnet build", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithProjectPath_IncludesPathInCommand()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync("dotnet build /path/to/proj.csproj", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult());
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "build", ["projectPath"] = "/path/to/proj.csproj" });

        Assert.True(result.Succeeded);
        _processRunner.Verify(p => p.ExecuteAsync("dotnet build /path/to/proj.csproj", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_RestoreAction_RunsDotnetRestore()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync("dotnet restore", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("Restore completed."));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "restore" });

        Assert.True(result.Succeeded);
        _processRunner.Verify(p => p.ExecuteAsync("dotnet restore", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_BuildFailed_ReturnsFailedMessage()
    {
        var stdout = "Program.cs(10,5): error CS1002: ; expected";
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailureResult(stdout, ""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "build" });

        Assert.Contains("[Build Failed", result.Error);
        Assert.Contains("CS1002", result.Error);
        Assert.Contains("Program.cs", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_BuildWithWarnings_ReturnsWarningsSection()
    {
        var stdout = "Program.cs(5,1): warning CS0219: The variable 'x' is assigned but its value is never used";
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, stdout, "", false));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "build" });

        Assert.Contains("warning", result.Output + result.Error);
        Assert.Contains("CS0219", result.Output + result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleErrors_AllListed()
    {
        var stdout = """
            File1.cs(1,1): error CS1001: error one
            File2.cs(2,2): error CS1002: error two
            """;
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailureResult(stdout, ""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "build" });

        Assert.Contains("CS1001", result.Error);
        Assert.Contains("CS1002", result.Error);
        Assert.Contains("error one", result.Error);
        Assert.Contains("error two", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_TimedOut_ReturnsTimeoutMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(-1, "", "", true));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "build" });

    }

    [Fact]
    public async Task ExecuteAsync_PassesCancellationToken()
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), token))
            .ReturnsAsync(SuccessResult());
        var tool = CreateTool();

        await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "build" }, token);

        _processRunner.Verify(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), token), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessWithOutput_ShowsLastLines()
    {
        var stdout = "line1\nline2\nline3\nline4\nline5";
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(stdout));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["command"] = "build" });

        Assert.Contains("line5", result.Output + result.Error);
    }
}