namespace ECAssistant.UI;

/// <summary>
/// Abstract base for ALL console / I/O interaction points.
/// Every user-facing output and input flows through these methods,
/// so swapping the concrete implementation (console, GUI, web, etc.) is trivial —
/// one line change in Program.cs.
/// </summary>
public abstract class EGuiBase
{
    // ─── Write / Print Methods ───────────────────────

    /// <summary>Write a plain text line to the user (no coloring).</summary>
    public abstract void WriteLine(string text);

    /// <summary>Write a colored/text-blocked line. Text + ANSI wrapper are pre-assembled.</summary>
    public abstract void WriteLineColored(string coloredText);

    // v10.12.20: Centralized truncation — one place, used everywhere.
    // Truncates text to maxChars and appends [...] if truncated. Console display only.

    /// <summary>Write a colored line, truncating to maxChars with [...] indicator.</summary>
    public virtual void WriteLineColored(string coloredText, int maxChars)
    {
        if (string.IsNullOrEmpty(coloredText)) { WriteLineColored(""); return; }
        if (coloredText.Length <= maxChars) { WriteLineColored(coloredText); return; }
        WriteLineColored(coloredText.Substring(0, maxChars) + " [...]");
    }

    /// <summary>Write a plain line, truncating to maxChars with [...] indicator.</summary>
    public virtual void WriteLine(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) { WriteLine(""); return; }
        if (text.Length <= maxChars) { WriteLine(text); return; }
        WriteLine(text.Substring(0, maxChars) + " [...]");
    }

    /// <summary>Truncate text to maxChars and append [...] if truncated. Returns the truncated string.</summary>
   // Stateless utility — no mutable state
    public static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text ?? "";
        return text.Substring(0, maxChars) + " [...]";
    }

    /// <summary>Print inline text with no trailing newline (raw, unstyled).</summary>
    public abstract void WriteRaw(string text);

    /// <summary>Print a blank line separator.</summary>
    public abstract void BlankLine();

    // ─── User Input Methods ──────────────────────────

    /// <summary>Prompt the user with colored label, then read a line of input.</summary>
    public abstract string? PromptColored(string labelAndText);

    /// <summary>Prompt with raw text and read a line of input (label is plain).</summary>
    public abstract string? PromptRaw(string label);

    // ─── Status / Info Methods ───────────────────────

    /// <summary>Write an error/notice-line (caller assembles the ANSI text, caller keeps control over color).</summary>
    public abstract void InfoColored(string coloredText);

    /// <summary>Log a warning to the user.</summary>
    public abstract void WarningColored(string coloredText);

    // ─── Internal Diagnostics ────────────────────────

    /// <summary>Internal debug / diagnostic logging. Never shown to end user in normal runs.</summary>
    public virtual void LogInternal(string text)
    {
        // Default: no-op. UI implementations override to route through their output system.
        // This prevents direct Console.WriteLine calls in the abstract base.
    }

    // ─── Low-level raw access (for streaming inference, etc.) ──

    /// <summary>Direct raw write — caller must include their own ANSI escapes or newlines.</summary>
    public abstract void WriteRawDirect(string text);

    // ─── Input Events (non-blocking) ────────────────

    /// <summary>
    /// Check if the user pressed ESC (non-blocking). Returns true if ESC was pressed.
    /// Used by the engine during token streaming to allow mid-stream stop.
    /// UI implementations should check for ESC key without blocking.
    /// Default: returns false (no console available).
    /// </summary>
    public virtual bool IsEscapePressed() => false;
}
