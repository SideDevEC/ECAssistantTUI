using ECAssistant.TUI.Input;
using Xunit;

namespace ECAssistant.TUI.Tests;

/// <summary>
/// Tests for in-line cursor editing keys (left/right/delete) and bracketed paste
/// (CSI 200~/201~) added to the ANSI input parser (2026-09-21, Emre request:
/// left/right cursor movement + clipboard paste in the TUI).
/// </summary>
public class AnsiInputParserCursorPasteTests
{
    private const char Esc = '\x1b';

    private static (AnsiInputEvent last, AnsiInputParser p) Feed(AnsiInputParser p, string s)
    {
        var last = AnsiInputEvent.None;
        foreach (var c in s) last = p.Feed(c);
        return (last, p);
    }

    private static AnsiInputEvent Run(AnsiInputParser p, string s) => Feed(p, s).last;

    // ── Cursor arrows ──

    [Fact]
    public void Csi_ArrowLeft_Detected()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.ArrowLeft, Run(p, "[D"));
    }

    [Fact]
    public void Csi_ArrowRight_Detected()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.ArrowRight, Run(p, "[C"));
    }

    [Fact]
    public void Ss3_ArrowLeft_And_Right_Detected()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.ArrowLeft, Run(p, "OD"));

        var p2 = new AnsiInputParser();
        p2.FeedEsc();
        Assert.Equal(AnsiInputEvent.ArrowRight, Run(p2, "OC"));
    }

    // ── Delete key ──

    [Fact]
    public void Csi_Delete_3Tilde_Detected()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.Delete, Run(p, "[3~"));
    }

    // ── Bracketed paste ──

    [Fact]
    public void BracketedPaste_StartEnd_Detected()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.BracketPasteStart, Run(p, "[200~"));
        Assert.True(p.IsMidSequence); // inside paste window
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.BracketPasteEnd, Run(p, "[201~"));
        Assert.False(p.IsMidSequence);
    }

    [Fact]
    public void PasteContent_DeliveredAsPasteChars()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.BracketPasteStart, Run(p, "[200~"));

        var (ev, _) = Feed(p, "he");
        // Feed returns the last event; collect all PasteChar events + chars.
        var chars = new List<char>();
        var p2 = new AnsiInputParser();
        p2.FeedEsc();
        Assert.Equal(AnsiInputEvent.BracketPasteStart, Run(p2, "[200~"));
        foreach (var c in "he")
        {
            var e = p2.Feed(c);
            if (e == AnsiInputEvent.PasteChar) chars.Add(p2.LastChar);
        }
        Assert.Equal("he", new string(chars.ToArray()));

        p2.FeedEsc();
        Assert.Equal(AnsiInputEvent.BracketPasteEnd, Run(p2, "[201~"));
    }

    [Fact]
    public void PasteContent_WithNewlines_DeliveredAsChars_NotEscape()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.BracketPasteStart, Run(p, "[200~"));

        foreach (var c in "line1\r\nline2")
        {
            var e = p.Feed(c);
            if (c == '\r' || c == '\n')
                Assert.Equal(AnsiInputEvent.PasteChar, e); // \r\n inside paste is content, NOT Escape/Enter
            Assert.NotEqual(AnsiInputEvent.Escape, e);
        }
    }

    [Fact]
    public void Reset_TerminatesPasteWindow()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.BracketPasteStart, Run(p, "[200~"));
        Assert.True(p.IsMidSequence);

        p.Reset();
        Assert.False(p.IsMidSequence);
        // After reset, plain chars are no longer paste content (caller-side flag governs).
        Assert.Equal(AnsiInputEvent.None, p.Feed('x'));
    }

    [Fact]
    public void SequenceSplit_AcrossFeeds_LeftArrow_StillDetected()
    {
        var p = new AnsiInputParser();
        p.FeedEsc();
        Assert.Equal(AnsiInputEvent.None, p.Feed('['));
        Assert.Equal(AnsiInputEvent.ArrowLeft, p.Feed('D'));
    }
}