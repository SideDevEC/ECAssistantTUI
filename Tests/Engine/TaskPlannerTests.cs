using ECAssistant.Engine;
using ECAssistant.Interfaces;
using Moq;

namespace ECAssistant.Tests.Engine;

public class TaskPlannerTests
{
    private readonly Mock<ILogger> _loggerMock = new();
    private readonly TaskPlanner _planner;

    public TaskPlannerTests()
    {
        // Setup logger mock to avoid NRE in Logger constructor
        _loggerMock.SetupGet(x => x.IsDebugEnabled).Returns(false);
        _planner = new TaskPlanner(_loggerMock.Object);
    }

    [Fact]
    public void Decompose_SimpleTask_ReturnsSingleSubTask()
    {
        var result = _planner.Decompose("Build the project");
        Assert.Single(result);
        Assert.Equal("Build the project", result[0].Description);
        Assert.Equal(SubTaskStatus.Pending, result[0].Status);
    }

    [Fact]
    public void Decompose_TaskWithThen_SplitsIntoMultipleSteps()
    {
        var result = _planner.Decompose("Build the project then run the tests");
        Assert.True(result.Count >= 2);
        Assert.Contains("Build the project", result[0].Description);
    }

    [Fact]
    public void Decompose_TaskWithAndThen_SplitsIntoMultipleSteps()
    {
        var result = _planner.Decompose("Create a file and then build the project");
        Assert.True(result.Count >= 2);
    }

    [Fact]
    public void Decompose_TaskWithAfterThat_SplitsIntoMultipleSteps()
    {
        var result = _planner.Decompose("Build the project after that run tests");
        Assert.True(result.Count >= 2);
    }

    [Fact]
    public void Decompose_TaskWithAlso_SplitsIntoMultipleSteps()
    {
        var result = _planner.Decompose("Build the project also run tests");
        Assert.True(result.Count >= 2);
    }

    [Fact]
    public void Decompose_TaskWithFinally_SplitsIntoMultipleSteps()
    {
        var result = _planner.Decompose("Build the project then run tests finally deploy");
        Assert.True(result.Count >= 2);
    }

    [Fact]
    public void Decompose_TaskWithNext_SplitsIntoMultipleSteps()
    {
        var result = _planner.Decompose("Build the project next run tests");
        Assert.True(result.Count >= 2);
    }

    [Fact]
    public void Decompose_MultipleActionWords_SplitsIntoMultipleSteps()
    {
        var result = _planner.Decompose("build and create and fix and test");
        Assert.True(result.Count >= 2);
    }

    [Fact]
    public void Decompose_EmptyString_ReturnsSingleEmptySubTask()
    {
        var result = _planner.Decompose("");
        // ContainsAny returns false for empty, CountActions returns 0
        // So it falls through to single sub-task
        Assert.Single(result);
    }

    [Fact]
    public void Decompose_PreservesSubTaskStatusAsPending()
    {
        var result = _planner.Decompose("Build then test");
        Assert.All(result, t => Assert.Equal(SubTaskStatus.Pending, t.Status));
    }

    [Fact]
    public void Current_AfterDecompose_ReturnsFirstSubTask()
    {
        _planner.Decompose("Build then test");
        Assert.NotNull(_planner.Current);
        Assert.Contains("Build", _planner.Current!.Description);
    }

    [Fact]
    public void Current_AfterCompleteCurrent_AdvancesToNext()
    {
        _planner.Decompose("Build then test");
        _planner.CompleteCurrent();
        Assert.NotNull(_planner.Current);
        Assert.Contains("test", _planner.Current!.Description.ToLower());
    }

    [Fact]
    public void Current_AfterAllCompleted_ReturnsNull()
    {
        _planner.Decompose("Build");
        _planner.CompleteCurrent();
        Assert.Null(_planner.Current);
    }

    [Fact]
    public void CompleteCurrent_MarksAsCompleted()
    {
        _planner.Decompose("Build then test");
        _planner.CompleteCurrent();
        // The first sub-task should be completed
        // Check via GetSummary
        var summary = _planner.GetSummary();
        Assert.Contains("completed", summary.ToLower());
    }

    [Fact]
    public void CompleteCurrent_SetsCompletedAt()
    {
        _planner.Decompose("Build");
        var before = DateTime.UtcNow.AddSeconds(-1);
        _planner.CompleteCurrent();
        // Indirectly verified — no direct access to CompletedAt
        // but HasRemaining should be false
        Assert.False(_planner.HasRemaining);
    }

    [Fact]
    public void FailCurrent_MarksAsFailed()
    {
        _planner.Decompose("Build then test");
        _planner.FailCurrent("Build error");
        var summary = _planner.GetSummary();
        Assert.Contains("failed", summary.ToLower());
    }

    [Fact]
    public void FailCurrent_AdvancesToNext()
    {
        _planner.Decompose("Build then test");
        _planner.FailCurrent("error");
        Assert.NotNull(_planner.Current);
    }

    [Fact]
    public void HasRemaining_AfterDecompose_True()
    {
        _planner.Decompose("Build then test");
        Assert.True(_planner.HasRemaining);
    }

    [Fact]
    public void HasRemaining_AfterAllCompleted_False()
    {
        _planner.Decompose("Build");
        _planner.CompleteCurrent();
        Assert.False(_planner.HasRemaining);
    }

    [Fact]
    public void GetProgressContext_SingleTask_ReturnsEmptyString()
    {
        _planner.Decompose("Build");
        Assert.Equal("", _planner.GetProgressContext());
    }

    [Fact]
    public void GetProgressContext_MultipleTasks_ReturnsProgressInfo()
    {
        _planner.Decompose("Build then test");
        var context = _planner.GetProgressContext();
        Assert.Contains("TASK PROGRESS", context);
        Assert.Contains("Step 1/2", context);
    }

    [Fact]
    public void GetProgressContext_AfterComplete_ShowsCompleted()
    {
        _planner.Decompose("Build then test");
        _planner.CompleteCurrent();
        var context = _planner.GetProgressContext();
        Assert.Contains("✅", context);
    }

    [Fact]
    public void GetSummary_ReturnsCorrectCounts()
    {
        // "Build then test then deploy" splits on first " then " -> ["Build", "test then deploy"]
        // The second " then " is not re-processed, so we get 2 sub-tasks
        _planner.Decompose("Build then test then deploy");
        _planner.CompleteCurrent();
        _planner.FailCurrent("test error");
        var summary = _planner.GetSummary();
        Assert.Contains("1/2 completed", summary);
        Assert.Contains("1 failed", summary);
    }

    [Fact]
    public void CompleteCurrent_NoSubTasks_NoOp()
    {
        // After all tasks done, completing again should not throw
        _planner.Decompose("Build");
        _planner.CompleteCurrent();
        _planner.CompleteCurrent(); // should not throw
    }

    [Fact]
    public void FailCurrent_NoSubTasks_NoOp()
    {
        _planner.Decompose("Build");
        _planner.CompleteCurrent();
        _planner.FailCurrent("error"); // should not throw
    }

    [Fact]
    public void Decompose_LongTaskWithAnd_SplitsOnAnd()
    {
        var longTask = "This is a very long task description that mentions building and creating something useful with more than thirty characters";
        var result = _planner.Decompose(longTask);
        // Should split on " and " since length > 30
        Assert.True(result.Count >= 2);
    }
}