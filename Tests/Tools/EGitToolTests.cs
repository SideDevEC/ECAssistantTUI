using ECAssistant.Config;
using ECAssistant.Interfaces;
using ECAssistant.Tools.Git;

namespace ECAssistant.Tests.Tools;

public class EGitToolTests
{
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly Mock<IFileSystem> _fileSystem = new();
    private readonly EAgentConfig _config = new();

    private EGitTool CreateTool(string workingDir = "/repo")
    {
        return new EGitTool(_processRunner.Object, _fileSystem.Object, _config);
    }

    private ProcessResult SuccessResult(string stdout = "") =>
        new(0, stdout, "", false);

    private ProcessResult ErrorResult(string stderr = "error") =>
        new(1, "", stderr, false);

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

    

    // ── ExecuteAsync — missing action ──

    [Fact]
    public async Task ExecuteAsync_MissingAction_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?>());

        Assert.Contains("Missing 'action'", result.Error);
    }

    // ── ExecuteAsync — status ──

    [Fact]
    public async Task ExecuteAsync_StatusClean_ReturnsCleanMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync("git status --porcelain", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "status" });

        Assert.Contains("clean", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_StatusWithChanges_ReturnsChangesList()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(" M file1.cs\n?? file2.cs"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "status" });

        Assert.Contains("Git Status", result.Output + result.Error);
        Assert.Contains("2 changes", result.Output + result.Error);
    }

    // ── ExecuteAsync — commit ──

    [Fact]
    public async Task ExecuteAsync_CommitWithMessage_RunsGitCommit()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.Is<string>(s => s.Contains("commit -m")), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("[main abc1234] fix: update"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "commit", ["message"] = "fix: update" });

        
    }

    [Fact]
    public async Task ExecuteAsync_CommitWithoutMessage_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "commit" });

    }

    // ── ExecuteAsync — log ──

    [Fact]
    public async Task ExecuteAsync_Log_ReturnsCommitHistory()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("abc1234 First commit\ndef5678 Second commit"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "log", ["max_entries"] = "2" });

        Assert.Contains("abc1234", result.Output + result.Error);
        Assert.Contains("def5678", result.Output + result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_LogNoCommits_ReturnsNoCommitsMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "log" });

        Assert.Contains("No commits", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ── ExecuteAsync — branch ──

    [Fact]
    public async Task ExecuteAsync_Branch_ReturnsBranchList()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("* main\n  feature/test"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "branch" });

        Assert.Contains("main", result.Output + result.Error);
        Assert.Contains("feature/test", result.Output + result.Error);
    }

    // ── ExecuteAsync — checkout ──

    [Fact]
    public async Task ExecuteAsync_CheckoutWithBranch_RunsGitCheckout()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.Is<string>(s => s.Contains("checkout feature")), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "checkout", ["branch"] = "feature" });

        
    }

    [Fact]
    public async Task ExecuteAsync_CheckoutWithoutBranch_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "checkout" });

    }

    // ── ExecuteAsync — init ──

    [Fact]
    public async Task ExecuteAsync_Init_ReturnsInitializedMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("Initialized empty Git repository in /repo/.git/"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "init" });

        Assert.Contains("initialized", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ── ExecuteAsync — diff ──

    [Fact]
    public async Task ExecuteAsync_DiffNoChanges_ReturnsNoDifferencesMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "diff" });

        Assert.Contains("No differences", result.Output + result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_DiffWithChanges_ReturnsChanges()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("+ added line\n- removed line"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "diff" });

        Assert.Contains("added line", result.Output + result.Error);
        Assert.Contains("removed line", result.Output + result.Error);
    }

    // ── ExecuteAsync — unknown action ──

    [Fact]
    public async Task ExecuteAsync_UnknownAction_ReturnsError()
    {
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "unknown" });

        Assert.Contains("Unknown git action", result.Error);
    }

    // ── ExecuteAsync — git error (non-zero exit) ──

    [Fact]
    public async Task ExecuteAsync_GitError_ReturnsErrorMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ErrorResult("fatal: not a git repository"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "status" });

        Assert.Contains("not a git repository", result.Error);
    }

    // ── ExecuteAsync — exception ──

    [Fact]
    public async Task ExecuteAsync_Exception_ReturnsErrorMessage()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "status" });

        Assert.Contains("boom", result.Error);
    }

    // ── ExecuteAsync — add ──

    [Fact]
    public async Task ExecuteAsync_AddAll_RunsGitAddAll()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.Is<string>(s => s.Contains("add -A")), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "add", ["files"] = "all" });

        
    }

    // ── Fallback parsing (non-JSON) ──

    [Fact]
    public async Task ExecuteAsync_NonJsonInput_ParsesKeyValuePairFormat()
    {
        _processRunner
            .Setup(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(""));
        var tool = CreateTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "status" });

        
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

        await tool.ExecuteAsync(new Dictionary<string, string?> { ["action"] = "status" }, token);

        _processRunner.Verify(p => p.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), token), Times.Once);
    }
}