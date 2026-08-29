using ECAssistant.TUI.Input;
using Xunit;

namespace ECAssistant.TUI.Tests;

/// <summary>
/// Regression tests for the ANSI input parser — covers the mouse-scroll garbage bug:
/// sequences must never leak into the prompt as typed text, even when split across
/// arbitrary feed boundaries (the original bug: fast scroll split ESC[M mid-sequence).
/// </summary>
public class AnsiInputParserTests
{
    private const char Esc = '\x1b';

    private static AnsiInputEvent FeedString(AnsiInputParser p, string s)
    {
        var last = AnsiInputEvent.None;
        foreach (var c in s) last = p.Feed(c);
        return last;
    }

    // ── X10 mouse (ESC[M + button + 2 coord bytes) ──

    [Fact]
    public void X10_WheelUp_Detected()
    {
        var p = new AnsiInputParser();
        Assert.Equal(AnsiInputEvent.None, p.FeedEsc());
        Assert.Equal(AnsiInputEvent.WheelUp, FeedString(p, "[M`!!")); // button 0x60=64(wheel up)
    }

    [Fact]
    public void X10_WheelDown_Detected()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.WheelDown, FeedString(p, "[Ma!!")); // 0x61=65(wheel down)
    }

    [Fact]
    public void X10_CharByChar_NeverLeaks()
    {
        // the original bug: fast scroll split the sequence — bytes landed in the prompt
        var p = new AnsiInputParser();
        var events = new List<AnsiInputEvent>();
        foreach (var c in $"{Esc}[M`!!{Esc}[Ma!!{Esc}[M`!!")
        {
            if (c == Esc) events.Add(p.FeedEsc());
            else events.Add(p.Feed(c));
        }
        Assert.Contains(AnsiInputEvent.WheelUp, events);
        Assert.Contains(AnsiInputEvent.WheelDown, events);
        Assert.Equal(3, events.Count(e => e == AnsiInputEvent.WheelUp || e == AnsiInputEvent.WheelDown));
        Assert.All(events, e => Assert.NotEqual(AnsiInputEvent.Escape, e));
        Assert.False(p.IsMidSequence);
    }

    // ── SGR mouse (ESC[< button;x;y M) ──

    [Fact]
    public void Sgr_WheelUp_And_Down_Detected()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.WheelUp, FeedString(p, "[<64;10;5M"));
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.WheelDown, FeedString(p, "[<65;10;5M"));
    }

    // ── Arrows ──

    [Fact]
    public void CsiArrows_Mapped()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.None, p.Feed('['));
        Assert.Equal(AnsiInputEvent.ArrowUp, p.Feed('A'));
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.ArrowDown, FeedString(p, "[B"));
    }

    [Fact]
    public void Ss3Arrows_Mapped()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.None, p.Feed('O'));
        Assert.Equal(AnsiInputEvent.ArrowUp, p.Feed('A'));
    }

    // ── Lone ESC / timeouts ──

    [Fact]
    public void EscapeOnTimeout_OnlyForBareEsc_AndCsiStart()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.True(p.EscapeOnTimeout); // lone ESC = real Escape keypress

        p.FeedEsc(); p.Feed('[');
        Assert.True(p.EscapeOnTimeout); // ESC[ + nothing = also Escape (legacy semantics)

        p.Reset(); p.FeedEsc(); p.Feed('['); p.Feed('M'); p.Feed('`');
        Assert.False(p.EscapeOnTimeout); // truncated X10 = drop silently, NOT Escape
    }

    // ── Unknown CSI drained, nothing leaks ──

    [Fact]
    public void UnknownCsi_Drained_NoLeak()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        // e.g. cursor-position report ESC[12;34R
        Assert.Equal(AnsiInputEvent.None, FeedString(p, "[12;34R"));
        Assert.False(p.IsMidSequence);
    }

    [Fact]
    public void Reset_MidSequence_DiscardsCleanly()
    {
        var p = new AnsiInputParser();
        p.FeedEsc(); p.Feed('['); p.Feed('M'); p.Feed('`'); // mid-X10
        p.Reset();
        Assert.False(p.IsMidSequence);
        // after reset, ground-level feeding is inert — caller handles chars as keys
        Assert.Equal(AnsiInputEvent.None, p.Feed('h'));
        Assert.Equal(AnsiInputEvent.None, p.Feed('e'));
    }

    // ── Mixed traffic (wheel bursts interleaved with unknown sequences) ──

    [Fact]
    public void MixedBurst_NoLeaks_NoFalseEvents()
    {
        var p = new AnsiInputParser();
        var events = new List<AnsiInputEvent>();
        var stream = $"{Esc}[M`!!{Esc}[?25h{Esc}[<65;3;4M{Esc}[12;1R{Esc}[Ma!!}}";
        foreach (var c in stream)
        {
            if (c == Esc) events.Add(p.FeedEsc());
            else events.Add(p.Feed(c));
        }
        Assert.Equal(3, events.Count(e => e == AnsiInputEvent.WheelUp || e == AnsiInputEvent.WheelDown));
        Assert.DoesNotContain(AnsiInputEvent.Escape, events);
        Assert.False(p.IsMidSequence);
    }
}
