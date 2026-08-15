using System;
using System.Linq;
using LLama;

namespace ECAssistant.Engine;

/// <summary>
/// Token counting using the real LLamaSharp tokenizer for accurate estimates.
/// Falls back to char-based estimation if context is not yet loaded.
/// </summary>
public class TokenCounter
{
    private LLamaContext? _context;

    public TokenCounter(LLamaContext? context = null)
    {
        _context = context;
    }

    public void Initialize(LLamaContext context) => _context = context;

    public int Count(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        if (_context != null)
        {
            try
            {
                var tokens = _context.Tokenize(text).ToList();
                return tokens.Count;
            }
            catch { }
        }

        var chars = text.Length;
        var tokenBudget = (int)(chars / 3.8f);

        var score = 0f;
        var i = 0;
        while (i < chars)
        {
            var c = text[i];
            if (c == ' ' || c == '\n' || c == '\t') { score += 0.5f; }
            else if (char.IsPunctuation(c)) { score += 1.2f; }
            else if (char.IsUpper(c)) { score += 0.8f; }
            i++;
        }

        tokenBudget = (int)(chars / (4.0f - score / chars));
        return Math.Max(1, tokenBudget);
    }

    public int EstimateUpper(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        if (_context != null)
        {
            try
            {
                var tokens = _context.Tokenize(text).ToList();
                return tokens.Count + 16;
            }
            catch { }
        }

        return (int)(text.Length / 3.5f);
    }
}