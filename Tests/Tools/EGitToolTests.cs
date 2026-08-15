using ECAssistant.Interfaces;
using ECAssistant.Tools.Git;

namespace ECAssistant.Tests.Tools;

public class EGitToolTests
{
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly Mock<IFileSystem> _fileSystem = new();
    private readonly Mock<IConfigProvider> _configProvider = new();

    private EGitTool CreateTool(string workingDir = "/repo")
    {
        _configProvider.Setup(c => c.GetValue("git.workingDir", It.IsAny<string>()))
                       .Returns(workingDir);
        return new EGitTool(_processRunner.Object, _fileSystem.Object, _configProvider.Object);
    }

    private ProcessResult SuccessResult(string stdout = "") =>
        new(0, stdout, "", false);

    private ProcessResult ErrorResult(string stderr = "error") =>
        new(1, "", stderr, false);

    // ── Name / Description / GetPolicy ──

    [Fact]
    public void Name_ReturnsEGitTool()
    {
        var tool = CreateTool();
        Assert.Equal("EGitTool", tool.Name);
    }

    [Fact]
    public void Description_ContainsGit()
    {
        var tool = CreateTool();
        Assert.Contains("git", tool.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetPolicy_ReturnsAllowedPolicy()
    {
        var tool = CreateTool();
        var policy = tool.GetPolicy();

        Assert.Equal("EGitTool", policy.ToolName);
        Assert.Equal("Allowed", policy.Level);
    }

    // ── ExecuteAsync — missing action ──

    [Fact]
    public async Task ExecuteAsync_MissingAction_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("");

        Assert.Contains("Missing 'action'", result);
    }

    // ── ExecuteAsync — status ──

    [Fact]
    public async Task ExecuteAsync_StatusClean_ReturnsCleanMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync("git status --porcelain", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"status"}""");

        Assert.Contains("clean", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_StatusWithChanges_ReturnsChangesList()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(" M file1.cs\n?? file2.cs"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"status"}""");

        Assert.Contains("Git Status", result);
        Assert.Contains("2 changes", result);
    }

    // ── ExecuteAsync — commit ──

    [Fact]
    public async Task ExecuteAsync_CommitWithMessage_RunsGitCommit()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.Is<string>(s => s.Contains("commit -m")), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("[main abc1234] fix: update"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"commit","message":"fix: update"}""");

        Assert.Contains("[EGitTool]", result);
    }

    [Fact]
    public async Task ExecuteAsync_CommitWithoutMessage_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"commit"}""");

        Assert.Contains("ERROR", result);
    }

    // ── ExecuteAsync — log ──

    [Fact]
    public async Task ExecuteAsync_Log_ReturnsCommitHistory()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("abc1234 First commit\ndef5678 Second commit"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"log","max_entries":"2"}""");

        Assert.Contains("abc1234", result);
        Assert.Contains("def5678", result);
    }

    [Fact]
    public async Task ExecuteAsync_LogNoCommits_ReturnsNoCommitsMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"log"}""");

        Assert.Contains("No commits", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── ExecuteAsync — branch ──

    [Fact]
    public async Task ExecuteAsync_Branch_ReturnsBranchList()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("* main\n  feature/test"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"branch"}""");

        Assert.Contains("main", result);
        Assert.Contains("feature/test", result);
    }

    // ── ExecuteAsync — checkout ──

    [Fact]
    public async Task ExecuteAsync_CheckoutWithBranch_RunsGitCheckout()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.Is<string>(s => s.Contains("checkout feature")), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"checkout","branch":"feature"}""");

        Assert.Contains("[EGitTool]", result);
    }

    [Fact]
    public async Task ExecuteAsync_CheckoutWithoutBranch_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"checkout"}""");

        Assert.Contains("ERROR", result);
    }

    // ── ExecuteAsync — init ──

    [Fact]
    public async Task ExecuteAsync_Init_ReturnsInitializedMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("Initialized empty Git repository in /repo/.git/"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"init"}""");

        Assert.Contains("initialized", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── ExecuteAsync — diff ──

    [Fact]
    public async Task ExecuteAsync_DiffNoChanges_ReturnsNoDifferencesMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"diff"}""");

        Assert.Contains("No differences", result);
    }

    [Fact]
    public async Task ExecuteAsync_DiffWithChanges_ReturnsChanges()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("+ added line\n- removed line"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"diff"}""");

        Assert.Contains("added line", result);
        Assert.Contains("removed line", result);
    }

    // ── ExecuteAsync — unknown action ──

    [Fact]
    public async Task ExecuteAsync_UnknownAction_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"unknown"}""");

        Assert.Contains("Unknown git action", result);
    }

    // ── ExecuteAsync — git error (non-zero exit) ──

    [Fact]
    public async Task ExecuteAsync_GitError_ReturnsErrorMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorResult("fatal: not a git repository"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"status"}""");

        Assert.Contains("ERROR", result);
        Assert.Contains("not a git repository", result);
    }

    // ── ExecuteAsync — exception ──

    [Fact]
    public async Task ExecuteAsync_Exception_ReturnsErrorMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"status"}""");

        Assert.Contains("ERROR", result);
        Assert.Contains("boom", result);
    }

    // ── ExecuteAsync — add ──

    [Fact]
    public async Task ExecuteAsync_AddAll_RunsGitAddAll()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.Is<string>(s => s.Contains("add -A")), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("""{"action":"add","files":"all"}""");

        Assert.Contains("[EGitTool]", result);
    }

    // ── Fallback parsing (non-JSON) ──

    [Fact]
    public async Task ExecuteAsync_NonJsonInput_ParsesKeyValuePairFormat()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("action=status");

        Assert.Contains("[EGitTool]", result);
    }

    // ── CancellationToken ──

    [Fact]
    public async Task ExecuteAsync_PassesCancellationToken()
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), token))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        await tool.ExecuteAsync("""{"action":"status"}""", token);

        _processRunner.Verify(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), token), Times.Once);
    }
}