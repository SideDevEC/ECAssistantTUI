using static ECAssistant.EColor;

namespace ECAssistant.UI;

/// <summary>
/// Console-based implementation of EGuiBase.
/// Wraps the existing EColor ANSI helpers — one place to change coloring strategy.
///
/// v10.21: PromptRaw/PromptColored use Console.ReadKey-based input loop instead of
/// Console.ReadLine, which would block console output from the session's token stream.
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    public override void WriteLine(string text)
         => Console.WriteLine(text);

    public override void WriteLineColored(string coloredText)
         => Console.WriteLine(coloredText);

    public override void WriteRaw(string text)
    {
         Console.Write(text);
    }

    public override void BlankLine()
         => Console.WriteLine();

    // ── User Input ────────────────────────────────
    // v10.21: Use ReadKey-based input loop instead of Console.ReadLine().
    // Console.ReadLine() blocks the console sync lock on macOS, preventing
    // Console.Write from the background runner thread from rendering.
    // ReadKey(true) reads one key at a time without locking the output stream.

    public override string? PromptColored(string labelAndText)
     {
        Console.Write(labelAndText);
        return ReadLineKeyBased();
     }

    public override string? PromptRaw(string label)
      {
          Console.Write(label);
          return ReadLineKeyBased();
       }

    /// <summary>
    /// Read a line using Console.ReadKey(true) — character by character.
    /// This doesn't hold the console sync lock, so the background runner
    /// thread can write tokens to Console.Out simultaneously.
    /// </summary>
    private static string? ReadLineKeyBased()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            try
            {
                if (!Console.KeyAvailable)
                {
                    // No key pressed — yield to let other threads run
                    Thread.Sleep(10);
                    continue;
                }
            }
            catch (InvalidOperationException)
            {
                // No real console (redirected/piped) — fall back to ReadLine
                return Console.ReadLine();
            }

            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine(); // echo the newline
                return sb.ToString();
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    Console.Write("\b \b"); // erase last char
                }
            }
            else if (key.KeyChar != '\0')
            {
                sb.Append(key.KeyChar);
                Console.Write(key.KeyChar); // echo
            }
        }
    }

        // ── Status / Info (caller pre-styles) ─────────

    public override void InfoColored(string coloredText)
         => Console.WriteLine(coloredText);

    public override void WarningColored(string coloredText)
         => Console.WriteLine(coloredText);

    // ── Low-level raw (for inference streaming etc.) ─

    public override void WriteRawDirect(string text)
        => Console.Write(text);
}