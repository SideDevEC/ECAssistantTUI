using System.Text;
using ECAssistant.UI;

namespace ECAssistant.Testing;

/// <summary>
/// Non-interactive test harness for EGuiBase.
/// Captures all output into a StringBuilder log and provides scripted input.
/// This is how tests "pretend to be the user" — no console interaction needed.
/// </summary>
public sealed class EGuiTestHarness : EGuiBase
{
    private readonly StringBuilder _log = new();
    private readonly Queue<string?> _inputQueue = new();
    private readonly object _lock = new();

    /// <summary>All captured output (thread-safe).</summary>
    public string CapturedOutput
    {
        get { lock (_lock) return _log.ToString(); }
    }

    /// <summary>Lines captured so far.</summary>
    public int LineCount
    {
        get { lock (_lock) return _log.ToString().Count(c => c == '\n'); }
    }

    /// <summary>Queue a scripted user response for the next PromptRaw call.</summary>
    public void QueueInput(string? input) => _inputQueue.Enqueue(input);

    /// <summary>Queue multiple scripted inputs.</summary>
    public void QueueInputs(params string?[] inputs)
    {
        foreach (var input in inputs)
            _inputQueue.Enqueue(input);
    }

    /// <summary>Clear captured output (for between tests).</summary>
    public void ClearOutput() { lock (_lock) _log.Clear(); }

    // ── EGuiBase implementation ───────────────────

    public override void WriteLine(string text)
    {
        lock (_lock) _log.AppendLine(text);
    }

    public override void WriteLineColored(string coloredText)
    {
        lock (_lock) _log.AppendLine(coloredText);
    }

    public override void WriteRaw(string text)
    {
        lock (_lock) _log.Append(text);
    }

    public override void BlankLine()
    {
        lock (_lock) _log.AppendLine();
    }

    public override string? PromptColored(string labelAndText)
    {
        lock (_lock) _log.AppendLine($"[PROMPT] {labelAndText}");
        return _inputQueue.Count > 0 ? _inputQueue.Dequeue() : null;
    }

    public override string? PromptRaw(string label)
    {
        lock (_lock) _log.AppendLine($"[PROMPT] {label}");
        return _inputQueue.Count > 0 ? _inputQueue.Dequeue() : null;
    }

    public override void InfoColored(string coloredText)
    {
        lock (_lock) _log.AppendLine(coloredText);
    }

    public override void WarningColored(string coloredText)
    {
        lock (_lock) _log.AppendLine(coloredText);
    }

    public override void WriteRawDirect(string text)
    {
        lock (_lock) _log.Append(text);
    }

    public override void ClearCanvas()
    {
        lock (_lock) _log.AppendLine("[CLEAR]");
    }

    // ── Helpers ──────────────────────────────────

    /// <summary>Check if captured output contains a string (case-insensitive).</summary>
    public bool OutputContains(string text)
    {
        lock (_lock) return _log.ToString().Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Get last N lines of captured output.</summary>
    public string GetLastLines(int count)
    {
        lock (_lock)
        {
            var lines = _log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return string.Join("\n", lines.TakeLast(count));
        }
    }

    /// <summary>Dump full captured output to a file (for debugging).</summary>
    public void DumpToFile(string path)
    {
        lock (_lock) File.WriteAllText(path, _log.ToString());
    }
}