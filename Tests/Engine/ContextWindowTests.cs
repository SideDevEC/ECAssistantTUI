using ECAssistant.Engine;
using ECAssistant.Services;

namespace ECAssistant.Tests.Engine;

public class ContextWindowTests
{
    private readonly TokenCounter _tokenCounter = new(null);

    private ContextWindow CreateWindow(uint maxTokens = 1000)
        => new(maxTokens, _tokenCounter);

    [Fact]
    public void AddUserMessage_ValidContent_ReturnsTokenCount()
    {
        var window = CreateWindow();
        var tokens = window.AddUserMessage("Hello world");
        Assert.True(tokens > 0);
    }

    [Fact]
    public void AddUserMessage_IncreasesMessageCount()
    {
        var window = CreateWindow();
        window.AddUserMessage("Hello");
        Assert.Equal(1, window.MessageCount);
        window.AddUserMessage("World");
        Assert.Equal(2, window.MessageCount);
    }

    [Fact]
    public void AddAssistantMessage_ValidContent_ReturnsTokenCount()
    {
        var window = CreateWindow();
        var tokens = window.AddAssistantMessage("I can help with that");
        Assert.True(tokens > 0);
    }

    [Fact]
    public void AddAssistantMessage_IncreasesMessageCount()
    {
        var window = CreateWindow();
        window.AddAssistantMessage("Response");
        Assert.Equal(1, window.MessageCount);
    }

    [Fact]
    public void AddToolOutput_ValidContent_ReturnsTokenCount()
    {
        var window = CreateWindow();
        var tokens = window.AddToolOutput("Build succeeded");
        Assert.True(tokens > 0);
    }

    [Fact]
    public void AddToolOutput_WithToolName_IncreasesMessageCount()
    {
        var window = CreateWindow();
        window.AddToolOutput("output", "EShellAgent");
        Assert.Equal(1, window.MessageCount);
    }

    [Fact]
    public void AddToolOutput_EmptyToolName_WorksCorrectly()
    {
        var window = CreateWindow();
        var tokens = window.AddToolOutput("some output", "");
        Assert.True(tokens > 0);
        Assert.Equal(1, window.MessageCount);
    }

    [Fact]
    public void AddSystemMessage_EmptyWindow_InsertsAtBeginning()
    {
        var window = CreateWindow();
        window.AddSystemMessage("System prompt");
        Assert.Equal(1, window.MessageCount);
        var messages = window.GetWindowMessages();
        Assert.Equal("system", messages[0].Role);
    }

    [Fact]
    public void AddSystemMessage_ExistingSystemMessage_ReplacesContent()
    {
        var window = CreateWindow();
        window.AddSystemMessage("First system prompt");
        window.AddSystemMessage("Updated system prompt");
        Assert.Equal(1, window.MessageCount);
        var messages = window.GetWindowMessages();
        Assert.Equal("Updated system prompt", messages[0].Content);
    }

    [Fact]
    public void AddSystemMessage_AfterUserMessage_InsertsAtBeginning()
    {
        var window = CreateWindow();
        window.AddUserMessage("User message");
        window.AddSystemMessage("System prompt");
        var messages = window.GetWindowMessages();
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
    }

    [Fact]
    public void GetWindowMessages_WithinBudget_ReturnsAllMessages()
    {
        var window = CreateWindow(10000);
        window.AddUserMessage("Hello");
        window.AddAssistantMessage("Hi there");
        var messages = window.GetWindowMessages();
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void IsWithinBudget_WithinBudget_ReturnsTrue()
    {
        var window = CreateWindow(10000);
        window.AddUserMessage("Hello");
        Assert.True(window.IsWithinBudget());
    }

    [Fact]
    public void IsWithinBudget_OverBudget_ReturnsFalse()
    {
        var window = CreateWindow(1); // Very small budget
        window.AddUserMessage("This is a long message that exceeds the tiny budget");
        Assert.False(window.IsWithinBudget());
    }

    [Fact]
    public void IsWithinBudget_EmptyWindow_ReturnsTrue()
    {
        var window = CreateWindow(100);
        Assert.True(window.IsWithinBudget());
    }

    [Fact]
    public void GetTotalTokens_NoMessages_ReturnsZero()
    {
        var window = CreateWindow();
        Assert.Equal(0, window.GetTotalTokens());
    }

    [Fact]
    public void GetTotalTokens_WithMessages_ReturnsSumOfTokens()
    {
        var window = CreateWindow();
        var tokens1 = window.AddUserMessage("Hello");
        var tokens2 = window.AddAssistantMessage("Hi");
        Assert.Equal(tokens1 + tokens2, window.GetTotalTokens());
    }

    [Fact]
    public void Clear_RemovesAllMessages()
    {
        var window = CreateWindow();
        window.AddUserMessage("Hello");
        window.AddAssistantMessage("Hi");
        window.Clear();
        Assert.Equal(0, window.MessageCount);
        Assert.Equal(0, window.GetTotalTokens());
    }

    [Fact]
    public void Clear_EmptyWindow_NoOp()
    {
        var window = CreateWindow();
        window.Clear();
        Assert.Equal(0, window.MessageCount);
    }

    [Fact]
    public void RemoveLastAssistantMessage_WithAssistantMessage_RemovesAndReturnsTrue()
    {
        var window = CreateWindow();
        window.AddUserMessage("Hello");
        window.AddAssistantMessage("Response");
        var result = window.RemoveLastAssistantMessage();
        Assert.True(result);
        Assert.Equal(1, window.MessageCount);
    }

    [Fact]
    public void RemoveLastAssistantMessage_NoAssistantMessage_ReturnsFalse()
    {
        var window = CreateWindow();
        window.AddUserMessage("Hello");
        var result = window.RemoveLastAssistantMessage();
        Assert.False(result);
        Assert.Equal(1, window.MessageCount);
    }

    [Fact]
    public void RemoveLastAssistantMessage_EmptyWindow_ReturnsFalse()
    {
        var window = CreateWindow();
        var result = window.RemoveLastAssistantMessage();
        Assert.False(result);
    }

    [Fact]
    public void RemoveLastAssistantMessage_MultipleMessages_RemovesLastAssistant()
    {
        var window = CreateWindow();
        window.AddUserMessage("Hello");
        window.AddAssistantMessage("Response 1");
        window.AddToolOutput("Tool output");
        window.AddAssistantMessage("Response 2");
        var result = window.RemoveLastAssistantMessage();
        Assert.True(result);
        Assert.Equal(3, window.MessageCount);
        var messages = window.GetWindowMessages();
        Assert.Equal("tool_output", messages[^1].Role);
    }

    [Fact]
    public void MaxTokens_ReturnsConfiguredValue()
    {
        var window = CreateWindow(5000);
        Assert.Equal((uint)5000, window.MaxTokens);
    }

    [Fact]
    public void MessageCount_ReturnsCorrectCount()
    {
        var window = CreateWindow();
        Assert.Equal(0, window.MessageCount);
        window.AddUserMessage("a");
        Assert.Equal(1, window.MessageCount);
        window.AddUserMessage("b");
        Assert.Equal(2, window.MessageCount);
    }

    [Fact]
    public void GetWindowMessages_EmptyWindow_ReturnsEmptyList()
    {
        var window = CreateWindow();
        var messages = window.GetWindowMessages();
        Assert.Empty(messages);
    }

    [Fact]
    public void AddUserMessage_EmptyString_ReturnsZeroTokens()
    {
        var window = CreateWindow();
        var tokens = window.AddUserMessage("");
        Assert.Equal(0, tokens);
    }
}