using System;
using System.Linq;
using LLama;
using LLama.Native;

namespace ECAssistant.Engine;

/// <summary>
/// Token counting using the real LLamaSharp tokenizer for accurate estimates.
/// Uses LLamaContext.Tokenize() which returns an IEnumerable of LLamaToken.
/// Falls back to char-based estimation if context is not yet loaded.
/// </summary>
public static class TokenCounter
{
    private static LLamaContext? _context;

       /// <summary>Initialize tokenizer from context (call once after loading model).</summary>
    public static void Initialize(LLamaContext context)
           => _context = context;

      /// <summary>Count tokens using real tokenizer if available, otherwise estimate.</summary>
    public static int Count(string? text)
       {
        if (string.IsNullOrEmpty(text)) return 0;

         // Try real tokenizer first
         if (_context != null)
           try
             {
               var tokens = _context.Tokenize(text).ToList();
                return tokens.Count;
              }
          catch { }

          // Fallback: char-based estimation (~4 chars/token baseline)
         var chars = text.Length;
       var tokenBudget = (int)(chars / 3.8f);

           // Adjust for known patterns
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

      /// <summary>Get a conservative upper-bound estimate.</summary>
     public static int EstimateUpper(string? text)
        {
           if (string.IsNullOrEmpty(text)) return 0;

         if (_context != null)
             try
               {
                var tokens = _context.Tokenize(text).ToList();
                  return tokens.Count + 16; // +16 for safety margin
                   }
          catch { }

         return (int)(text.Length / 3.5f); // generous upper bound
        }
}
