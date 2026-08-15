using ECAssistant.Engine;
using ECAssistant.Services;

namespace ECAssistant.Tests.Integration;

/// <summary>
/// Integration tests for ContextWindow with a real TokenCounter —
/// exercises the full message tracking, budget enforcement, and summarization pipeline.
/// </summary>
public class ContextWindowIntegrationTests
{
    private readonly TokenCounter _tokenCounter = new(null);

    private ContextWindow CreateWindow(uint maxTokens = 1000)
        => new(maxTokens, _tokenCounter);

    [Fact]
    public void AddMessagesUntilOverBudget_AutoSummarizeTriggers()
    {
        // Use a very small budget to force summarization
        var window = new ContextWindow(50, _tokenCounter);

        // Add many messages to exceed budget
        for (int i = 0; i < 20; i++)
        {
            window.AddUserMessage($"This is message number {i} with some content to add tokens");
        }

        // GetWindowMessages triggers summarization when over budget
        var messages = window.GetWindowMessages();

        // After summarization, the message count should be reduced
        // (SummarizeOldest keeps ~30% of messages, minimum 5)
        Assert.True(messages.Count < 20);
    }

    [Fact]
    public void AddMessages_GetWindowMessages_TokenCountsSetCorrectly()
    {
        var window = CreateWindow(10000);

        var userTokens = window.AddUserMessage("Hello, how are you today?");
        var assistantTokens = window.AddAssistantMessage("I'm doing well, thank you for asking!");
        var toolTokens = window.AddToolOutput("Build succeeded with 0 errors", "EDotnetBuildTool");

        var messages = window.GetWindowMessages();

        Assert.Equal(3, messages.Count);
        Assert.Equal(userTokens, messages[0].EstimatedTokens);
        Assert.Equal(assistantTokens, messages[1].EstimatedTokens);
        Assert.Equal(toolTokens, messages[2].EstimatedTokens);

        // Total tokens should equal sum
        Assert.Equal(userTokens + assistantTokens + toolTokens, window.GetTotalTokens());
    }

    [Fact]
    public void AddUserMessage_AddAssistantMessage_AddToolOutput_AllTrackedCorrectly()
    {
        var window = CreateWindow(100000);

        window.AddUserMessage("What is 2+2?");
        window.AddAssistantMessage("2+2 equals 4.");
        window.AddToolOutput("Calculator returned: 4", "EShellAgent");
        window.AddUserMessage("Thanks!");
        window.AddAssistantMessage("You're welcome!");

        var messages = window.GetWindowMessages();
        Assert.Equal(5, messages.Count);

        // Verify roles in order
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal("tool_output", messages[2].Role);
        Assert.Equal("user", messages[3].Role);
        Assert.Equal("assistant", messages[4].Role);
    }

    [Fact]
    public void RemoveLastAssistantMessage_RemovedFromHistory()
    {
        var window = CreateWindow(100000);

        window.AddUserMessage("Question 1");
        window.AddAssistantMessage("Answer 1");
        window.AddUserMessage("Question 2");
        window.AddAssistantMessage("Answer 2");

        Assert.Equal(4, window.MessageCount);

        var result = window.RemoveLastAssistantMessage();
        Assert.True(result);
        Assert.Equal(3, window.MessageCount);

        var messages = window.GetWindowMessages();
        Assert.Equal("user", messages[^1].Role);
    }

    [Fact]
    public void RemoveLastAssistantMessage_NoAssistantMessage_ReturnsFalse()
    {
        var window = CreateWindow(10000);
        window.AddUserMessage("Just a user message");

        var result = window.RemoveLastAssistantMessage();
        Assert.False(result);
        Assert.Equal(1, window.MessageCount);
    }

    [Fact]
    public void Clear_AllMessagesRemoved_CountIsZero()
    {
        var window = CreateWindow(10000);

        window.AddUserMessage("Hello");
        window.AddAssistantMessage("Hi");
        window.AddToolOutput("output", "tool");
        Assert.Equal(3, window.MessageCount);

        window.Clear();
        Assert.Equal(0, window.MessageCount);
        Assert.Equal(0, window.GetTotalTokens());
    }

    [Fact]
    public void SystemMessage_ReplacedNotDuplicated_WhenAddedTwice()
    {
        var window = CreateWindow(10000);

        window.AddSystemMessage("You are a helpful assistant.");
        Assert.Equal(1, window.MessageCount);

        window.AddSystemMessage("You are a code reviewer.");
        Assert.Equal(1, window.MessageCount); // Still 1, replaced

        var messages = window.GetWindowMessages();
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("You are a code reviewer.", messages[0].Content);
    }

    [Fact]
    public void SystemMessage_AddedAfterUserMessage_InsertsAtBeginning()
    {
        var window = CreateWindow(10000);

        window.AddUserMessage("Hello");
        window.AddSystemMessage("System prompt");

        var messages = window.GetWindowMessages();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
    }

    [Fact]
    public void GetTotalTokens_ReflectsAddedMessages()
    {
        var window = CreateWindow(100000);
        Assert.Equal(0, window.GetTotalTokens());

        var t1 = window.AddUserMessage("First message");
        Assert.Equal(t1, window.GetTotalTokens());

        var t2 = window.AddAssistantMessage("Second message");
        Assert.Equal(t1 + t2, window.GetTotalTokens());

        var t3 = window.AddToolOutput("Third message", "tool");
        Assert.Equal(t1 + t2 + t3, window.GetTotalTokens());
    }

    [Fact]
    public void IsWithinBudget_WithLargeBudget_ReturnsTrue()
    {
        var window = CreateWindow(100000);
        window.AddUserMessage("Hello world");
        window.AddAssistantMessage("Hi there!");
        Assert.True(window.IsWithinBudget());
    }

    [Fact]
    public void IsWithinBudget_WithTinyBudget_ReturnsFalse()
    {
        var window = CreateWindow(1);
        window.AddUserMessage("This message exceeds the tiny budget");
        Assert.False(window.IsWithinBudget());
    }

    [Fact]
    public void FullConversationFlow_UserAssistantToolSystem_TracksAllCorrectly()
    {
        var window = CreateWindow(50000);

        // System message
        window.AddSystemMessage("You are a coding assistant.");

        // User asks a question
        window.AddUserMessage("Build the project and tell me the result.");

        // Assistant decides to use a tool
        window.AddAssistantMessage("I'll build the project for you.");

        // Tool output
        window.AddToolOutput("Build succeeded. 0 errors, 0 warnings.", "EDotnetBuildTool");

        // Assistant responds with result
        window.AddAssistantMessage("The build succeeded with no errors or warnings.");

        // User thanks
        window.AddUserMessage("Thanks!");

        var messages = window.GetWindowMessages();
        Assert.Equal(6, messages.Count);

        // Verify full conversation order
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
        Assert.Equal("assistant", messages[2].Role);
        Assert.Equal("tool_output", messages[3].Role);
        Assert.Equal("assistant", messages[4].Role);
        Assert.Equal("user", messages[5].Role);

        // All should have positive token counts (except system which may have some too)
        foreach (var msg in messages)
            Assert.True(msg.EstimatedTokens > 0);

        // Total should be within budget
        Assert.True(window.IsWithinBudget());
    }
}