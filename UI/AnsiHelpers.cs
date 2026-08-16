using System.Text;

namespace ECAssistant.UI;

/// <summary>
/// Static ANSI escape sequence helpers.
/// Used by BaseLayer and any layer that needs to measure or truncate colored text.
/// Stateless utility — no mutable state.
/// </summary>
internal static class AnsiHelpers
{
    /// <summary>Remove ANSI escape sequences from a string to get visible length.</summary>
    public static string StripAnsi(string text)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\x1b')
            {
                i++;
                // Skip CSI sequence: \x1b[ params intermediates final
                if (i < text.Length && text[i] == '[')
                {
                    i++; // skip '['
                    // Skip parameter bytes 0x30-0x3F (digits, ;, :, etc.)
                    while (i < text.Length && text[i] >= 0x30 && text[i] <= 0x3F) i++;
                    // Skip intermediate bytes 0x20-0x2F
                    while (i < text.Length && text[i] >= 0x20 && text[i] <= 0x2F) i++;
                    // Skip final byte 0x40-0x7E
                    if (i < text.Length && text[i] >= 0x40 && text[i] <= 0x7E) i++;
                }
                // Skip OSC sequence: \x1b] ... BEL or ST
                else if (i < text.Length && text[i] == ']')
                {
                    i++; // skip ']'
                    while (i < text.Length && text[i] != '\x07' && text[i] != '\x1b') i++;
                    if (i < text.Length && text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\') i += 2;
                    else if (i < text.Length) i++; // skip BEL
                }
                // Other escape sequences: skip next char
                else if (i < text.Length)
                {
                    i++; // skip single char after ESC
                }
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Truncate a string with ANSI codes to a max visible width.</summary>
    public static string TruncateAnsi(string text, int maxWidth)
    {
        var visible = StripAnsi(text);
        if (visible.Length <= maxWidth) return text;
        // Find the cut point in the original string
        int visibleCount = 0;
        int cutIdx = 0;
        bool inEscape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\x1b') { inEscape = true; continue; }
            if (inEscape)
            {
                if (c >= 0x40 && c <= 0x7E) inEscape = false;
                continue;
            }
            visibleCount++;
            if (visibleCount > maxWidth)
            {
                cutIdx = i;
                break;
            }
        }
        return text.Substring(0, cutIdx) + "\x1b[0m [...]\x1b[0m";
    }

    /// <summary>Wrap a line with ANSI codes into multiple rows, each ≤ maxCols visible width.</summary>
    public static List<string> WrapLine(string text, int maxCols)
    {
        var result = new List<string>();
        var visible = StripAnsi(text);
        if (visible.Length <= maxCols)
        {
            result.Add(text);
            return result;
        }

        // Simple wrapping: split visible text into chunks
        int idx = 0;
        while (idx < visible.Length)
        {
            int len = Math.Min(maxCols, visible.Length - idx);
            string chunk = visible.Substring(idx, len);
            result.Add(chunk);
            idx += len;
        }
        return result;
    }
}