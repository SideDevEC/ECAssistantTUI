using ECAssistant.UI;

namespace ECAssistant.Tests.UI;

/// <summary>
/// Tests for BaseLayer subclasses (SessionLayer, HelpLayer).
/// No more layer stack — layers are independent, controller swaps them.
/// </summary>
public class LayerTests
{
    // ── SessionLayer ──

    [Fact]
    public void SessionLayer_Name_IncludesSessionKey()
    {
        var layer = new SessionLayer("main");
        Assert.Equal("session:main", layer.Name);
    }

    [Fact]
    public void SessionLayer_GetInputPrompt_ReturnsGreaterThan()
    {
        var layer = new SessionLayer("main");
        Assert.Equal("> ", layer.GetInputPrompt());
    }

    [Fact]
    public void SessionLayer_SessionKey_StoredCorrectly()
    {
        var layer = new SessionLayer("my-session");
        Assert.Equal("my-session", layer.SessionKey);
    }

    [Fact]
    public void SessionLayer_Label_DefaultsToEmpty()
    {
        var layer = new SessionLayer("main");
        Assert.Equal("", layer.Label);
    }

    [Fact]
    public void SessionLayer_Label_CanBeSet()
    {
        var layer = new SessionLayer("main") { Label = "My Label" };
        Assert.Equal("My Label", layer.Label);
    }

    // ── HelpLayer ──

    [Fact]
    public void HelpLayer_Name_IsHelp()
    {
        var layer = new HelpLayer(new EColor(), Array.Empty<string>());
        Assert.Equal("help", layer.Name);
    }

    [Fact]
    public void HelpLayer_GetInputPrompt_ReturnsGreaterThan()
    {
        var layer = new HelpLayer(new EColor(), Array.Empty<string>());
        Assert.Equal("> ", layer.GetInputPrompt());
    }

    [Fact]
    public void HelpLayer_BuildContent_PopulatesBuffer()
    {
        var layer = new HelpLayer(new EColor(), new[] { "Line 1", "Line 2" });
        layer.BuildContent();
        // Should have: title + hint + blank + Line 1 + Line 2
        Assert.True(layer._outputLines.Count >= 5);
    }

    [Fact]
    public void HelpLayer_BuildContent_FirstLine_IsTitle()
    {
        var layer = new HelpLayer(new EColor(), Array.Empty<string>());
        layer.BuildContent();
        Assert.Contains("ECAssistant", layer._outputLines[0]);
    }

    [Fact]
    public void HelpLayer_BuildContent_SecondLine_HasReturnHint()
    {
        var layer = new HelpLayer(new EColor(), Array.Empty<string>());
        layer.BuildContent();
        Assert.Contains("Enter", layer._outputLines[1]);
    }

    // ── StartupLayer ──
    
    [Fact]
    public void StartupLayer_Name_IsStartup()
    {
        var layer = new StartupLayer(new EColor());
        Assert.Equal("startup", layer.Name);
    }
    
    [Fact]
    public void StartupLayer_GetInputPrompt_ReturnsGreaterThan()
    {
        var layer = new StartupLayer(new EColor());
        Assert.Equal("> ", layer.GetInputPrompt());
    }
    
    [Fact]
    public void StartupLayer_UpdateStatus_RebuildsHomeScreen()
    {
        var layer = new StartupLayer(new EColor());
        layer.UpdateStatus("v11.0", "/path/to/model.gguf", "/path/to/secondary.gguf", true, "/working/dir", "/config/path", 3, "main");
        Assert.True(layer._outputLines.Count > 0);
        Assert.Contains(layer._outputLines, l => l.Contains("ECAssistant"));
        Assert.Contains(layer._outputLines, l => l.Contains("v11.0"));
        Assert.Contains(layer._outputLines, l => l.Contains("model.gguf"));
        Assert.Contains(layer._outputLines, l => l.Contains("secondary.gguf"));
    }
    
    [Fact]
    public void StartupLayer_UpdateStatus_SecondaryDisabled_ShowsDisabled()
    {
        var layer = new StartupLayer(new EColor());
        layer.UpdateStatus("v11.0", "/path/to/model.gguf", "", false, "/working/dir", "/config/path", 0, "none");
        Assert.Contains(layer._outputLines, l => l.Contains("disabled"));
    }
    
    [Fact]
    public void StartupLayer_UpdateStatus_ShowsSessionSwitchCommand()
    {
        var layer = new StartupLayer(new EColor());
        layer.UpdateStatus("v11.0", "/path/to/model.gguf", "", false, "/working/dir", "/config/path", 2, "main");
        Assert.Contains(layer._outputLines, l => l.Contains("/session"));
    }
    
    // ── BaseLayer ProcessInput ──
    
    [Fact]
    public void BaseLayer_ProcessInput_Clear_ClearsBuffer()
    {
        var layer = new SessionLayer("test");
        layer.AddOutputLine("content");
        Assert.Single(layer._outputLines);
        bool handled = layer.ProcessInput("/clear");
        Assert.True(handled);
        Assert.Empty(layer._outputLines);
    }
    
    [Fact]
    public void BaseLayer_ProcessInput_UnknownCommand_ReturnsFalse()
    {
        // Use HelpLayer which doesn't have a default catch-all like SessionLayer
        var layer = new HelpLayer(new EColor(), Array.Empty<string>());
        bool handled = layer.ProcessInput("/totally-made-up-command");
        Assert.False(handled);
    }
    
    [Fact]
    public void SessionLayer_ProcessInput_NonCommand_ForwardsToSession()
    {
        var layer = new SessionLayer("test");
        // Without a CoreSession, it still returns true (handled)
        bool handled = layer.ProcessInput("hello world");
        Assert.True(handled);
    }
    
    // ── BaseLayer scroll ──

    [Fact]
    public void ScrollUp_IncreasesOffset()
    {
        var layer = new SessionLayer("test");
        for (int i = 0; i < 30; i++)
            layer.AddOutputLine($"line {i}");
        Assert.Equal(0, layer.ScrollOffset);
        layer.ScrollUp(5);
        Assert.Equal(5, layer.ScrollOffset);
    }

    [Fact]
    public void ScrollDown_DecreasesOffset()
    {
        var layer = new SessionLayer("test");
        for (int i = 0; i < 50; i++)
            layer.AddOutputLine($"line {i}");
        layer.ScrollUp(10);
        Assert.Equal(10, layer.ScrollOffset);
        layer.ScrollDown(3);
        Assert.Equal(7, layer.ScrollOffset);
    }

    [Fact]
    public void ScrollToBottom_ResetsOffset()
    {
        var layer = new SessionLayer("test");
        for (int i = 0; i < 30; i++)
            layer.AddOutputLine($"line {i}");
        layer.ScrollUp(10);
        layer.ScrollToBottom();
        Assert.Equal(0, layer.ScrollOffset);
    }

    [Fact]
    public void ScrollUp_BeyondMax_ClampsToMax()
    {
        var layer = new SessionLayer("test");
        for (int i = 0; i < 10; i++)
            layer.AddOutputLine($"line {i}");
        // With default dimensions (80x24, region=21), max scroll = max(0, 10-21) = 0
        // So scrolling up should have no effect
        layer.ScrollUp(100);
        Assert.Equal(0, layer.ScrollOffset);
    }

    [Fact]
    public void ScrollDown_BelowZero_ClampsToZero()
    {
        var layer = new SessionLayer("test");
        layer.AddOutputLine("line 1");
        layer.ScrollDown(100);
        Assert.Equal(0, layer.ScrollOffset);
    }

    // ── BaseLayer live stream ──

    [Fact]
    public void UpdateLiveStreamLine_CreatesNewLine()
    {
        var layer = new SessionLayer("test");
        layer.AddOutputLine("existing content");
        int countBefore = layer._outputLines.Count;
        layer.UpdateLiveStreamLine("streaming text");
        Assert.Equal(countBefore + 1, layer._outputLines.Count);
    }

    [Fact]
    public void UpdateLiveStreamLine_UpdatesExistingLine()
    {
        var layer = new SessionLayer("test");
        layer.UpdateLiveStreamLine("first chunk");
        int index = layer._outputLines.Count - 1;
        layer.UpdateLiveStreamLine("first chunk second chunk");
        Assert.Equal("first chunk second chunk", layer._outputLines[index]);
    }

    [Fact]
    public void ClearLiveStreamLine_RemovesTheLiveLine()
    {
        var layer = new SessionLayer("test");
        layer.AddOutputLine("content");
        layer.UpdateLiveStreamLine("streaming");
        int countWithStream = layer._outputLines.Count;
        layer.ClearLiveStreamLine();
        Assert.Equal(countWithStream - 1, layer._outputLines.Count);
    }
}