namespace ECAssistant;

/// <summary>
/// String utility — truncation and text helpers.
/// Stateless, no dependencies.
/// </summary>
public static class StringUtil
{
    /// <summary>Truncate text to maxChars and append [...] if truncated.</summary>
    public static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text ?? "";
        return text.Substring(0, maxChars) + " [...]";
    }
}