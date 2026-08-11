using static ECAssistant.EColor;

namespace ECAssistant.UI;

/// <summary>
/// Console-based implementation of EGuiBase.
/// Wraps the existing EColor ANSI helpers — one place to change coloring strategy.
/// </summary>
public sealed class EGuiConsole : EGuiBase
{
    public override void WriteLine(string text)
         => Console.WriteLine(text);

    public override void WriteLineColored(string coloredText)
         => Console.WriteLine(coloredText);

    public override void WriteRaw(string text)
         => Console.Write(text);

    public override void BlankLine()
         => Console.WriteLine();

    // ── User Input ────────────────────────────────

    public override string? PromptColored(string labelAndText)
     {
        Console.Write(labelAndText);
        return Console.ReadLine();
     }

    public override string? PromptRaw(string label)
      {
          Console.Write(label);
          return Console.ReadLine();
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
