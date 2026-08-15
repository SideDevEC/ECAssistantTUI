using ECAssistant.Engine;
using ECAssistant.Interfaces;
using Moq;

namespace ECAssistant.Tests.Engine;

public class SelfCorrectionManagerTests
{
    private readonly Mock<ILogger> _loggerMock = new();
    private readonly string _tempDir;

    public SelfCorrectionManagerTests()
    {
        _loggerMock.SetupGet(x => x.IsDebugEnabled).Returns(false);
        _tempDir = Path.Combine(Path.GetTempPath(), $"selfcorrect_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    private SelfCorrectionManager CreateManager()
        => new(_tempDir, _loggerMock.Object);

    [Fact]
    public void RecordFailure_FirstFailure_ReturnsIsolatedPattern()
    {
        var manager = CreateManager();
        var analysis = manager.RecordFailure("EShellAgent", "Error 1");
        Assert.Equal(FailurePattern.Isolated, analysis.Pattern);
        Assert.False(analysis.ShouldEscalate);
    }

    [Fact]
    public void RecordFailure_SameErrorThreeTimes_ReturnsRepeatedError()
    {
        var manager = CreateManager();
        manager.RecordFailure("EShellAgent", "Same error");
        manager.RecordFailure("EShellAgent", "Same error");
        var analysis = manager.RecordFailure("EShellAgent", "Same error");
        Assert.Equal(FailurePattern.RepeatedError, analysis.Pattern);
        Assert.True(analysis.ShouldEscalate);
    }

    [Fact]
    public void RecordFailure_SameToolThreeTimes_ReturnsToolLoop()
    {
        var manager = CreateManager();
        manager.RecordFailure("EShellAgent", "Error A");
        manager.RecordFailure("EShellAgent", "Error B");
        var analysis = manager.RecordFailure("EShellAgent", "Error C");
        Assert.Equal(FailurePattern.ToolLoop, analysis.Pattern);
        Assert.True(analysis.ShouldEscalate);
    }

    [Fact]
    public void RecordFailure_AlternatingPattern_ReturnsAlternating()
    {
        var manager = CreateManager();
        // Use different tools to avoid ToolLoop detection (3+ same tool)
        manager.RecordFailure("EShellAgent", "Error A");
        manager.RecordFailure("ECodeEditor", "Error B");
        manager.RecordFailure("EShellAgent", "Error A");
        var analysis = manager.RecordFailure("ECodeEditor", "Error B");
        Assert.Equal(FailurePattern.Alternating, analysis.Pattern);
        Assert.True(analysis.ShouldEscalate);
    }

    [Fact]
    public void RecordFailure_DifferentErrors_ReturnsIsolated()
    {
        var manager = CreateManager();
        manager.RecordFailure("EShellAgent", "Error 1");
        manager.RecordFailure("ECodeEditor", "Error 2");
        var analysis = manager.RecordFailure("EFileResearchTool", "Error 3");
        Assert.Equal(FailurePattern.Isolated, analysis.Pattern);
        Assert.False(analysis.ShouldEscalate);
    }

    [Fact]
    public void RecordFailure_WithCommand_StoresCommand()
    {
        var manager = CreateManager();
        manager.RecordFailure("EShellAgent", "Build failed", "dotnet build");
        Assert.Equal(1, manager.FailureCount);
    }

    [Fact]
    public void RecordFailure_NullCommand_StoresEmptyCommand()
    {
        var manager = CreateManager();
        manager.RecordFailure("EShellAgent", "Error", null);
        Assert.Equal(1, manager.FailureCount);
    }

    [Fact]
    public void AnalyseFailures_NoFailures_ReturnsNonePattern()
    {
        var manager = CreateManager();
        var analysis = manager.AnalyseFailures();
        Assert.Equal(FailurePattern.None, analysis.Pattern);
    }

    [Fact]
    public void FailureCount_NoFailures_ReturnsZero()
    {
        var manager = CreateManager();
        Assert.Equal(0, manager.FailureCount);
    }

    [Fact]
    public void FailureCount_AfterFailures_ReturnsCorrectCount()
    {
        var manager = CreateManager();
        manager.RecordFailure("EShellAgent", "Error 1");
        manager.RecordFailure("ECodeEditor", "Error 2");
        Assert.Equal(2, manager.FailureCount);
    }

    [Fact]
    public void ClearHistory_RemovesAllFailures()
    {
        var manager = CreateManager();
        manager.RecordFailure("EShellAgent", "Error 1");
        manager.ClearHistory();
        Assert.Equal(0, manager.FailureCount);
    }

    [Fact]
    public void ClearHistory_NoFailures_NoOp()
    {
        var manager = CreateManager();
        manager.ClearHistory();
        Assert.Equal(0, manager.FailureCount);
    }

    [Fact]
    public void GetFailureSummary_NoFailures_ReturnsEmptyString()
    {
        var manager = CreateManager();
        Assert.Equal("", manager.GetFailureSummary());
    }

    [Fact]
    public void GetFailureSummary_WithFailures_ReturnsSummary()
    {
        var manager = CreateManager();
        manager.RecordFailure("EShellAgent", "Build failed");
        var summary = manager.GetFailureSummary();
        Assert.Contains("FAILURE HISTORY", summary);
        Assert.Contains("EShellAgent", summary);
        Assert.Contains("Build failed", summary);
    }

    [Fact]
    public async Task SnapshotFileAsync_NonExistentFile_ReturnsNull()
    {
        var manager = CreateManager();
        var result = await manager.SnapshotFileAsync("/nonexistent/file.txt");
        Assert.Null(result);
    }

    [Fact]
    public async Task SnapshotFileAsync_ExistingFile_ReturnsSnapshotId()
    {
        var manager = CreateManager();
        var testFile = Path.Combine(_tempDir, "test.txt");
        await File.WriteAllTextAsync(testFile, "original content");
        var result = await manager.SnapshotFileAsync(testFile);
        Assert.NotNull(result);
        Assert.Contains("snap_", result);
    }

    [Fact]
    public async Task SnapshotFileAsync_ThenRollback_RestoresContent()
    {
        var manager = CreateManager();
        var testFile = Path.Combine(_tempDir, "rollback_test.txt");
        await File.WriteAllTextAsync(testFile, "original content");
        await manager.SnapshotFileAsync(testFile);
        await File.WriteAllTextAsync(testFile, "modified content");

        var rolledBack = await manager.RollbackAsync(testFile);
        Assert.True(rolledBack);
        var content = await File.ReadAllTextAsync(testFile);
        Assert.Equal("original content", content);
    }

    [Fact]
    public async Task RollbackAsync_NoSnapshot_ReturnsFalse()
    {
        var manager = CreateManager();
        var result = await manager.RollbackAsync("/no/snapshot/here.txt");
        Assert.False(result);
    }

    [Fact]
    public void RecordFailure_MoreThanMaxHistory_TrimsHistory()
    {
        var manager = CreateManager();
        // _maxHistory is 20, add more than 20
        for (int i = 0; i < 25; i++)
        {
            manager.RecordFailure("EShellAgent", $"Error {i}");
        }
        // Should be trimmed to 20
        Assert.Equal(20, manager.FailureCount);
    }

    [Fact]
    public void RecordFailure_RepeatedErrorRecommendation_ContainsEscalateTag()
    {
        var manager = CreateManager();
        manager.RecordFailure("EShellAgent", "Same error");
        manager.RecordFailure("EShellAgent", "Same error");
        var analysis = manager.RecordFailure("EShellAgent", "Same error");
        Assert.Contains("[ESCALATE]", analysis.Recommendation);
    }

    [Fact]
    public void RecordFailure_ToolLoopRecommendation_ContainsEscalateTag()
    {
        var manager = CreateManager();
        manager.RecordFailure("EShellAgent", "Err A");
        manager.RecordFailure("EShellAgent", "Err B");
        var analysis = manager.RecordFailure("EShellAgent", "Err C");
        Assert.Contains("[ESCALATE]", analysis.Recommendation);
    }

    [Fact]
    public void RecordFailure_AlternatingRecommendation_ContainsEscalateTag()
    {
        var manager = CreateManager();
        manager.RecordFailure("EShellAgent", "Error A");
        manager.RecordFailure("EShellAgent", "Error B");
        manager.RecordFailure("EShellAgent", "Error A");
        var analysis = manager.RecordFailure("EShellAgent", "Error B");
        Assert.Contains("[ESCALATE]", analysis.Recommendation);
    }
}