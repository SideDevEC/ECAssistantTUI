using ECAssistant.Engine;

namespace ECAssistant.Tests.Engine;

public class SubAgentTaskTests
{
    [Fact]
    public void DefaultValues_AllPropertiesHaveCorrectDefaults()
    {
        var task = new SubAgentTask();
        Assert.Equal("", task.Description);
        Assert.Equal("", task.Prompt);
        Assert.Equal("", task.WorkingDir);
        Assert.Empty(task.AllowedTools);
        Assert.Equal(16384u, task.ContextSize);
        Assert.Equal(5, task.MaxTurns);
        Assert.Equal(120, task.TimeoutSeconds);
        Assert.Equal(20, task.MaxToolCalls);
        Assert.Equal(50 * 1024 * 1024, task.MaxDiskBytes);
        Assert.Equal(1, task.MaxRetries);
        Assert.Equal(1000, task.RetryDelayMs);
    }

    [Fact]
    public void Description_SetValue_ReturnsSameValue()
    {
        var task = new SubAgentTask { Description = "Write tests" };
        Assert.Equal("Write tests", task.Description);
    }

    [Fact]
    public void Prompt_SetValue_ReturnsSameValue()
    {
        var task = new SubAgentTask { Prompt = "Do something useful" };
        Assert.Equal("Do something useful", task.Prompt);
    }

    [Fact]
    public void WorkingDir_SetValue_ReturnsSameValue()
    {
        var task = new SubAgentTask { WorkingDir = "/tmp/work" };
        Assert.Equal("/tmp/work", task.WorkingDir);
    }

    [Fact]
    public void AllowedTools_SetValues_ReturnsSameValues()
    {
        var tools = new List<string> { "file_reader", "shell", "git" };
        var task = new SubAgentTask { AllowedTools = tools };
        Assert.Equal(3, task.AllowedTools.Count);
        Assert.Contains("file_reader", task.AllowedTools);
        Assert.Contains("shell", task.AllowedTools);
        Assert.Contains("git", task.AllowedTools);
    }

    [Fact]
    public void ContextSize_SetValue_ReturnsSameValue()
    {
        var task = new SubAgentTask { ContextSize = 32768 };
        Assert.Equal(32768u, task.ContextSize);
    }

    [Fact]
    public void MaxTurns_SetValue_ReturnsSameValue()
    {
        var task = new SubAgentTask { MaxTurns = 10 };
        Assert.Equal(10, task.MaxTurns);
    }

    [Fact]
    public void TimeoutSeconds_SetValue_ReturnsSameValue()
    {
        var task = new SubAgentTask { TimeoutSeconds = 300 };
        Assert.Equal(300, task.TimeoutSeconds);
    }

    [Fact]
    public void MaxToolCalls_SetValue_ReturnsSameValue()
    {
        var task = new SubAgentTask { MaxToolCalls = 50 };
        Assert.Equal(50, task.MaxToolCalls);
    }

    [Fact]
    public void MaxDiskBytes_SetValue_ReturnsSameValue()
    {
        var task = new SubAgentTask { MaxDiskBytes = 1024 * 1024 };
        Assert.Equal(1024 * 1024, task.MaxDiskBytes);
    }

    [Fact]
    public void MaxRetries_SetValue_ReturnsSameValue()
    {
        var task = new SubAgentTask { MaxRetries = 3 };
        Assert.Equal(3, task.MaxRetries);
    }

    [Fact]
    public void RetryDelayMs_SetValue_ReturnsSameValue()
    {
        var task = new SubAgentTask { RetryDelayMs = 5000 };
        Assert.Equal(5000, task.RetryDelayMs);
    }
}