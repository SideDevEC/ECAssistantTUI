using ECAssistant;
using ECAssistant.Tools;

namespace ECAssistant.Tests.Tools;

public class EToolBaseTests
{
    // ── EToolResult.Success ──

    [Fact]
    public void Success_SetsPropertiesCorrectly()
    {
        var result = EToolResult.Success("MyTool", "done");

        Assert.Equal("MyTool", result.ToolName);
        Assert.True(result.Succeeded);
        Assert.Equal("done", result.Output);
        Assert.Equal("", result.Error);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public void Success_WithMetadata_StoresMetadata()
    {
        var meta = new Dictionary<string, string> { ["key"] = "val" };

        var result = EToolResult.Success("Tool", "ok", meta);

        Assert.NotNull(result.Metadata);
        Assert.Equal("val", result.Metadata!["key"]);
    }

    [Fact]
    public void Success_WithEmptyOutput_Succeeds()
    {
        var result = EToolResult.Success("Tool", "");

        Assert.True(result.Succeeded);
        Assert.Equal("", result.Output);
    }

    // ── EToolResult.Failure ──

    [Fact]
    public void Failure_SetsPropertiesCorrectly()
    {
        var result = EToolResult.Failure("MyTool", "something broke");

        Assert.Equal("MyTool", result.ToolName);
        Assert.False(result.Succeeded);
        Assert.Equal("something broke", result.Error);
        Assert.Equal("", result.Output);
        Assert.Null(result.Metadata);
    }

    [Fact]
    public void Failure_WithMetadata_StoresMetadata()
    {
        var meta = new Dictionary<string, string> { ["code"] = "500" };

        var result = EToolResult.Failure("Tool", "err", meta);

        Assert.NotNull(result.Metadata);
        Assert.Equal("500", result.Metadata!["code"]);
    }

    // ── EToolResult.ToString ──

    [Fact]
    public void ToString_Succeeded_ReturnsSuccessFormat()
    {
        var result = EToolResult.Success("MyTool", "all good");

        var s = result.ToString();

        Assert.Equal("[SUCCESS] MyTool: all good", s);
    }

    [Fact]
    public void ToString_Failed_ReturnsFailedFormat()
    {
        var result = EToolResult.Failure("MyTool", "bad");

        var s = result.ToString();

        Assert.Equal("[FAILED] MyTool: bad", s);
    }

    // ── ToSystemPromptBlock ──

    [Fact]
    public void ToSystemPromptBlock_IncludesNameAndDescription()
    {
        var tool = new TestTool("TestTool", "A test tool");

        var block = tool.ToSystemPromptBlock();

        Assert.Contains("## TestTool", block);
        Assert.Contains("A test tool", block);
    }

    [Fact]
    public void ToSystemPromptBlock_NoRulesOrExamples_OnlyNameAndDescription()
    {
        var tool = new TestTool("T", "D");

        var block = tool.ToSystemPromptBlock();

        Assert.Contains("## T", block);
        Assert.Contains("D", block);
        Assert.DoesNotContain("Examples:", block);
    }

    [Fact]
    public void ToSystemPromptBlock_WithRules_IncludesRules()
    {
        var tool = new TestTool("T", "D", rules: "Be careful.");

        var block = tool.ToSystemPromptBlock();

        Assert.Contains("Be careful.", block);
    }

    [Fact]
    public void ToSystemPromptBlock_WithExample_IncludesExamplesSection()
    {
        var tool = new TestTool("T", "D", example: "<toolcall>T()</toolcall>");

        var block = tool.ToSystemPromptBlock();

        Assert.Contains("Examples:", block);
        Assert.Contains("<toolcall>T()</toolcall>", block);
    }

    [Fact]
    public void ToSystemPromptBlock_WithRulesAndExamples_RulesBeforeExamples()
    {
        var tool = new TestTool("T", "D", rules: "RULE_MARKER", example: "EXAMPLE_MARKER");

        var block = tool.ToSystemPromptBlock();

        var rulesIdx = block.IndexOf("RULE_MARKER");
        var exampleIdx = block.IndexOf("EXAMPLE_MARKER");
        Assert.True(rulesIdx < exampleIdx);
    }

    // ── Default virtual members ──

    [Fact]
    public void GetExtendedSystemPrompt_Default_ReturnsEmpty()
    {
        var tool = new TestTool("T", "D");

        Assert.Equal(string.Empty, tool.GetExtendedSystemPrompt());
    }

    [Fact]
    public void GetToolExample_Default_ReturnsEmpty()
    {
        var tool = new TestTool("T", "D");

        Assert.Equal(string.Empty, tool.GetToolExample());
    }

    [Fact]
    public void GetToolRules_Default_ReturnsEmpty()
    {
        var tool = new TestTool("T", "D");

        Assert.Equal(string.Empty, tool.GetToolRules());
    }

    /// <summary>Concrete subclass for testing EToolBase abstract members.</summary>
    private class TestTool : EToolBase
    {
        private readonly string _name;
        private readonly string _desc;
        private readonly string _rules;
        private readonly string _example;

        public TestTool(string name, string desc, string rules = "", string example = "")
        {
            _name = name;
            _desc = desc;
            _rules = rules;
            _example = example;
        }

        public override string Name => _name;
        public override string Description => _desc;
        public override string UsageExample => string.Empty;
        public override string GetToolRules() => _rules;
        public override string GetToolExample() => _example;

        public override Task<EToolResult> ExecuteAsync(Dictionary<string, string?> arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(EToolResult.Success(_name, "ok"));
    }
}