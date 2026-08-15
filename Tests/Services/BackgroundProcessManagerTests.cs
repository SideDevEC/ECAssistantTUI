using ECAssistant.Services;

namespace ECAssistant.Tests.Services;

public class BackgroundProcessManagerTests : IDisposable
{
    private readonly BackgroundProcessManager _manager;
    private readonly string _tempWorkDir;

    public BackgroundProcessManagerTests()
    {
        _manager = new BackgroundProcessManager();
        _tempWorkDir = Path.Combine(Path.GetTempPath(), "ECAssistantTests_BgMgr_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempWorkDir);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try { Directory.Delete(_tempWorkDir, true); } catch { }
    }

    [Fact]
    public void Constructor_Default_CreatesInstance()
    {
        Assert.NotNull(_manager);
    }

    [Fact]
    public async Task StartAsync_ValidCommand_ReturnsProcessId()
    {
        var id = await _manager.StartAsync("echo hello", _tempWorkDir);
        Assert.NotNull(id);
        Assert.StartsWith("bg-", id);
    }

    [Fact]
    public async Task StartAsync_MultipleCommands_ReturnsUniqueIds()
    {
        var id1 = await _manager.StartAsync("echo first", _tempWorkDir);
        var id2 = await _manager.StartAsync("echo second", _tempWorkDir);
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public async Task StartAsync_CreatesTempDirectory()
    {
        var tempDir = Path.Combine(_tempWorkDir, ".tmp");
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        await _manager.StartAsync("echo test", _tempWorkDir);
        Assert.True(Directory.Exists(tempDir));
    }

    [Fact]
    public async Task GetStatus_UnknownId_ReturnsNotFound()
    {
        var status = _manager.GetStatus("nonexistent-id");
        Assert.Equal(BgStatus.NotFound, status);
    }

    [Fact]
    public async Task GetStatus_RunningProcess_ReturnsRunning()
    {
        var id = await _manager.StartAsync("sleep 5", _tempWorkDir);
        var status = _manager.GetStatus(id);
        Assert.True(status == BgStatus.Running || status == BgStatus.Completed);
    }

    [Fact]
    public async Task GetStatus_CompletedProcess_ReturnsCompleted()
    {
        var id = await _manager.StartAsync("echo done", _tempWorkDir);
        // Wait for completion
        await Task.Delay(2000);
        var status = _manager.GetStatus(id);
        Assert.Equal(BgStatus.Completed, status);
    }

    [Fact]
    public async Task GetStatus_FailedProcess_ReturnsFailed()
    {
        var id = await _manager.StartAsync("exit 1", _tempWorkDir);
        await Task.Delay(2000);
        var status = _manager.GetStatus(id);
        Assert.Equal(BgStatus.Failed, status);
    }

    [Fact]
    public async Task GetOutput_UnknownId_ReturnsNotFoundMessage()
    {
        var output = _manager.GetOutput("nonexistent-id");
        Assert.Contains("not found", output);
    }

    [Fact]
    public async Task GetOutput_CompletedProcess_ReturnsStdOut()
    {
        var id = await _manager.StartAsync("echo hello_output", _tempWorkDir);
        await Task.Delay(2000);
        var output = _manager.GetOutput(id);
        Assert.Contains("hello_output", output);
    }

    [Fact]
    public async Task GetInfo_UnknownId_ReturnsNotFoundStatus()
    {
        var info = _manager.GetInfo("nonexistent-id");
        Assert.Equal(BgStatus.NotFound, info.Status);
    }

    [Fact]
    public async Task GetInfo_KnownId_ReturnsCorrectInfo()
    {
        var id = await _manager.StartAsync("echo test_info", _tempWorkDir);
        var info = _manager.GetInfo(id);
        Assert.Equal(id, info.Id);
        Assert.Equal("echo test_info", info.Command);
        Assert.True(info.ElapsedSeconds >= 0);
    }

    [Fact]
    public async Task Kill_UnknownId_ReturnsFalse()
    {
        Assert.False(_manager.Kill("nonexistent-id"));
    }

    [Fact]
    public async Task Kill_RunningProcess_ReturnsTrue()
    {
        var id = await _manager.StartAsync("sleep 30", _tempWorkDir);
        await Task.Delay(500);
        var killed = _manager.Kill(id);
        Assert.True(killed);
    }

    [Fact]
    public async Task Kill_AlreadyFinishedProcess_ReturnsFalse()
    {
        var id = await _manager.StartAsync("echo quick", _tempWorkDir);
        await Task.Delay(2000);
        Assert.False(_manager.Kill(id));
    }

    [Fact]
    public async Task List_NoProcesses_ReturnsEmptyList()
    {
        var list = _manager.List();
        Assert.Empty(list);
    }

    [Fact]
    public async Task List_WithProcesses_ReturnsAllProcesses()
    {
        await _manager.StartAsync("echo first", _tempWorkDir);
        await _manager.StartAsync("echo second", _tempWorkDir);
        var list = _manager.List();
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task CleanupFinished_RemovesCompletedProcesses()
    {
        var id = await _manager.StartAsync("echo done", _tempWorkDir);
        await Task.Delay(2000);
        _manager.CleanupFinished();
        Assert.Empty(_manager.List());
    }

    [Fact]
    public async Task CleanupFinished_KeepsRunningProcesses()
    {
        var id = await _manager.StartAsync("sleep 10", _tempWorkDir);
        _manager.CleanupFinished();
        Assert.Single(_manager.List());
    }

    [Fact]
    public async Task Dispose_KillsAllProcesses()
    {
        var id = await _manager.StartAsync("sleep 30", _tempWorkDir);
        _manager.Dispose();
        // After dispose, list should be empty
        Assert.Empty(_manager.List());
    }
}