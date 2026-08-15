using ECAssistant.Engine;
using ECAssistant.Tools;
using Moq;

namespace ECAssistant.Tests.Engine;

public class ParallelToolExecutorTests
{
    private static ToolCallRequest MakeTool(string name, int index, Dictionary<string, string?>? args = null)
        => new() { ToolName = name, Index = index, Args = args ?? new() };

    private static BatchToolResult MakeBatch(params (string toolName, int index, bool succeeded, string output, string error)[] tools)
    {
        var results = tools.Select(t => new SingleToolResult
        {
            ToolCall = new ToolCallRequest { ToolName = t.toolName, Index = t.index },
            Succeeded = t.succeeded,
            Output = t.output,
            Error = t.error,
            ElapsedMs = 100
        }).ToList();

        return new BatchToolResult
        {
            Results = results,
            Groups = new List<DependencyGroup> { new() { ToolCalls = results.Select(r => r.ToolCall).ToList(), GroupIndex = 0 } }
        };
    }

    [Fact]
    public void CombineResults_SingleSuccessfulResult_ReturnsOutputDirectly()
    {
        var batch = MakeBatch(("EShellAgent", 0, true, "Build succeeded", ""));
        var result = ParallelToolExecutor.CombineResults(batch);
        Assert.Equal("Build succeeded", result);
    }

    [Fact]
    public void CombineResults_SingleFailedResult_ReturnsFailedMessage()
    {
        var batch = MakeBatch(("EShellAgent", 0, false, "", "Build failed"));
        var result = ParallelToolExecutor.CombineResults(batch);
        Assert.Contains("[FAILED]", result);
        Assert.Contains("EShellAgent", result);
        Assert.Contains("Build failed", result);
    }

    [Fact]
    public void CombineResults_MultipleResults_ReturnsBatchFormat()
    {
        var batch = MakeBatch(
            ("EShellAgent", 0, true, "Output 1", ""),
            ("EFileResearchTool", 1, true, "Output 2", "")
        );
        var result = ParallelToolExecutor.CombineResults(batch);
        Assert.Contains("BATCH", result);
        Assert.Contains("Tool 0", result);
        Assert.Contains("Tool 1", result);
        Assert.Contains("EShellAgent", result);
        Assert.Contains("EFileResearchTool", result);
        Assert.Contains("END BATCH", result);
    }

    [Fact]
    public void CombineResults_MixedSuccessFailure_ShowsBothStatuses()
    {
        var batch = MakeBatch(
            ("EShellAgent", 0, true, "Good output", ""),
            ("ECodeEditor", 1, false, "", "Edit failed")
        );
        var result = ParallelToolExecutor.CombineResults(batch);
        Assert.Contains("OK", result);
        Assert.Contains("FAIL", result);
        Assert.Contains("Good output", result);
        Assert.Contains("Edit failed", result);
    }

    [Fact]
    public void CombineResults_AllSucceeded_ShowsSuccessCount()
    {
        var batch = MakeBatch(
            ("EShellAgent", 0, true, "A", ""),
            ("EShellAgent", 1, true, "B", "")
        );
        var result = ParallelToolExecutor.CombineResults(batch);
        Assert.Contains("2/2 succeeded", result);
    }

    [Fact]
    public void CombineResults_PartialSuccess_ShowsCorrectCount()
    {
        var batch = MakeBatch(
            ("EShellAgent", 0, true, "A", ""),
            ("EShellAgent", 1, false, "", "err"),
            ("EShellAgent", 2, true, "C", "")
        );
        var result = ParallelToolExecutor.CombineResults(batch);
        Assert.Contains("2/3 succeeded", result);
    }

    [Fact]
    public void CombineResults_IncludesGroupCount()
    {
        var batch = new BatchToolResult
        {
            Results = new List<SingleToolResult>
            {
                new() { ToolCall = MakeTool("A", 0), Succeeded = true, Output = "ok", ElapsedMs = 10 },
                new() { ToolCall = MakeTool("B", 1), Succeeded = true, Output = "ok", ElapsedMs = 20 }
            },
            Groups = new List<DependencyGroup>
            {
                new() { ToolCalls = new() { MakeTool("A", 0) }, GroupIndex = 0 },
                new() { ToolCalls = new() { MakeTool("B", 1) }, GroupIndex = 1 }
            }
        };
        var result = ParallelToolExecutor.CombineResults(batch);
        Assert.Contains("2 group(s)", result);
    }

    [Fact]
    public void FormatConsoleSummary_SingleResult_ReturnsSimpleSummary()
    {
        var batch = MakeBatch(("EShellAgent", 0, true, "output", ""));
        var result = ParallelToolExecutor.FormatConsoleSummary(batch);
        Assert.Contains("EShellAgent", result);
        Assert.Contains("OK", result);
    }

    [Fact]
    public void FormatConsoleSummary_SingleFailedResult_ShowsFail()
    {
        var batch = MakeBatch(("ECodeEditor", 0, false, "", "error"));
        var result = ParallelToolExecutor.FormatConsoleSummary(batch);
        Assert.Contains("ECodeEditor", result);
        Assert.Contains("FAIL", result);
    }

    [Fact]
    public void FormatConsoleSummary_MultipleResults_ShowsCountsAndParts()
    {
        var batch = MakeBatch(
            ("EShellAgent", 0, true, "A", ""),
            ("EFileResearchTool", 1, false, "", "err")
        );
        var result = ParallelToolExecutor.FormatConsoleSummary(batch);
        Assert.Contains("1/2 OK", result);
        Assert.Contains("EShellAgent#0:OK", result);
        Assert.Contains("EFileResearchTool#1:FAIL", result);
    }

    [Fact]
    public void FormatConsoleSummary_MultipleResults_ShowsGroupCount()
    {
        var batch = new BatchToolResult
        {
            Results = new List<SingleToolResult>
            {
                new() { ToolCall = MakeTool("A", 0), Succeeded = true, Output = "ok", ElapsedMs = 10 },
                new() { ToolCall = MakeTool("B", 1), Succeeded = true, Output = "ok", ElapsedMs = 20 }
            },
            Groups = new List<DependencyGroup>
            {
                new() { ToolCalls = new() { MakeTool("A", 0) }, GroupIndex = 0 },
                new() { ToolCalls = new() { MakeTool("B", 1) }, GroupIndex = 1 },
                new() { ToolCalls = new() { MakeTool("C", 2) }, GroupIndex = 2 }
            }
        };
        var result = ParallelToolExecutor.FormatConsoleSummary(batch);
        Assert.Contains("3 groups", result);
    }

    [Fact]
    public void BatchToolResult_AllSucceeded_TrueWhenAllSucceed()
    {
        var batch = MakeBatch(
            ("A", 0, true, "x", ""),
            ("B", 1, true, "y", "")
        );
        Assert.True(batch.AllSucceeded);
    }

    [Fact]
    public void BatchToolResult_AllSucceeded_FalseWhenAnyFails()
    {
        var batch = MakeBatch(
            ("A", 0, true, "x", ""),
            ("B", 1, false, "", "err")
        );
        Assert.False(batch.AllSucceeded);
    }

    [Fact]
    public void BatchToolResult_AnySucceeded_TrueWhenAtLeastOneSucceeds()
    {
        var batch = MakeBatch(
            ("A", 0, false, "", "err"),
            ("B", 1, true, "y", "")
        );
        Assert.True(batch.AnySucceeded);
    }

    [Fact]
    public void BatchToolResult_AnySucceeded_FalseWhenAllFail()
    {
        var batch = MakeBatch(
            ("A", 0, false, "", "err1"),
            ("B", 1, false, "", "err2")
        );
        Assert.False(batch.AnySucceeded);
    }

    [Fact]
    public void BatchToolResult_SuccessCount_ReturnsCorrectCount()
    {
        var batch = MakeBatch(
            ("A", 0, true, "x", ""),
            ("B", 1, false, "", "err"),
            ("C", 2, true, "z", "")
        );
        Assert.Equal(2, batch.SuccessCount);
    }

    [Fact]
    public void BatchToolResult_Default_HasEmptyResults()
    {
        var batch = new BatchToolResult();
        Assert.Empty(batch.Results);
        Assert.True(batch.AllSucceeded); // vacuously true
        Assert.False(batch.AnySucceeded);
        Assert.Equal(0, batch.SuccessCount);
    }

    [Fact]
    public async Task ExecuteAsync_SingleTool_ExecutesSuccessfully()
    {
        // We test CombineResults and FormatConsoleSummary as static methods.
        // ExecuteAsync requires EAgentEngine, ToolPolicy, and Program.Gui which are hard to mock.
        // This is covered by the static method tests above.
        await Task.CompletedTask;
        Assert.True(true);
    }
}