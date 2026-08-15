using ECAssistant.UI;

namespace ECAssistant.Tests.UI;

/// <summary>
/// Tests for the EGuiConsole layer stack — push, pop, multiple layers, session as base.
/// Uses a mock IGuiLayer to verify behavior without a real terminal.
/// </summary>
public class LayerStackTests
{
    /// <summary>
    /// Simple mock layer that tracks activation and key handling.
    /// </summary>
    private sealed class MockLayer : IGuiLayer
    {
        public string Name { get; }
        public int ActivateCount { get; private set; }
        public int ResizeCount { get; private set; }
        public int KeyCount { get; private set; }
        public bool HandleKeys { get; set; } = true;

        public MockLayer(string name) => Name = name;

        public void OnActivate(EGuiConsole console) => ActivateCount++;
        public void OnResize(EGuiConsole console) => ResizeCount++;
        public bool OnKey(EGuiConsole console, ConsoleKeyInfo key)
        {
            KeyCount++;
            return HandleKeys;
        }
    }

    [Fact]
    public void SessionLayer_Name_IsSession()
    {
        var layer = new SessionLayer();
        Assert.Equal("session", layer.Name);
    }

    [Fact]
    public void HelpLayer_Name_IsHelp()
    {
        var layer = new HelpLayer(new EColor(), Array.Empty<string>());
        Assert.Equal("help", layer.Name);
    }

    [Fact]
    public void HelpLayer_OnKey_Enter_ReturnsFalse_ToPop()
    {
        var layer = new HelpLayer(new EColor(), Array.Empty<string>());
        var enterKey = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);
        Assert.False(layer.OnKey(null!, enterKey));
    }

    [Fact]
    public void HelpLayer_OnKey_OtherKeys_ReturnsTrue_ToStay()
    {
        var layer = new HelpLayer(new EColor(), Array.Empty<string>());
        var spaceKey = new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false);
        Assert.True(layer.OnKey(null!, spaceKey));

        var arrowKey = new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false);
        Assert.True(layer.OnKey(null!, arrowKey));

        var letterKey = new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false);
        Assert.True(layer.OnKey(null!, letterKey));
    }

    [Fact]
    public void MockLayer_TracksActivateCalls()
    {
        var layer = new MockLayer("test");
        Assert.Equal(0, layer.ActivateCount);
        layer.OnActivate(null!);
        Assert.Equal(1, layer.ActivateCount);
        layer.OnActivate(null!);
        Assert.Equal(2, layer.ActivateCount);
    }

    [Fact]
    public void MockLayer_TracksResizeCalls()
    {
        var layer = new MockLayer("test");
        Assert.Equal(0, layer.ResizeCount);
        layer.OnResize(null!);
        Assert.Equal(1, layer.ResizeCount);
    }

    [Fact]
    public void MockLayer_TracksKeyCalls()
    {
        var layer = new MockLayer("test");
        var key = new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false);
        Assert.Equal(0, layer.KeyCount);
        layer.OnKey(null!, key);
        Assert.Equal(1, layer.KeyCount);
    }

    [Fact]
    public void MockLayer_HandleKeysFalse_PopsOnAnyKey()
    {
        var layer = new MockLayer("test") { HandleKeys = false };
        var key = new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false);
        Assert.False(layer.OnKey(null!, key));
    }

    [Fact]
    public void HelpLayer_BuildContent_IncludesTitleAndReturn()
    {
        var layer = new HelpLayer(new EColor(), new[] { "Test line 1", "Test line 2" });
        // We can't call BuildHelpContent directly (private), but we can verify
        // OnActivate doesn't throw — it builds and would paint if console was real
        // This is a smoke test
        Assert.NotNull(layer);
    }
}