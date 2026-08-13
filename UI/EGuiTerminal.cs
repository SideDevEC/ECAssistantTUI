using Terminal.Gui;
using EColor = ECAssistant.EColor;

namespace ECAssistant.UI;

/// <summary>
/// Terminal.Gui-based UI — replaces EGuiConsole.
///
/// Layout:
/// ┌─────────────────────────────────────┐
/// │                                     │
/// │  Output View (scrollable)            │  ← all session output, token stream, tool results
/// │                                     │
/// │                                     │
/// ├─────────────────────────────────────┤
///  > user input                          │  ← input field (always free, never blocks)
/// ├─────────────────────────────────────┤
///  Session: main | Idle | Ctrl+Q: Quit   │  ← status bar
/// └─────────────────────────────────────┘
///
/// The input field is always available — the user can type while the
/// model is generating tokens. The runner thread writes to the output
/// view via Application.MainLoop.Invoke (thread-safe).
/// </summary>
public sealed class EGuiTerminal : EGuiBase
{
    private Window? _appWin;
    private TextView? _outputView;
    private TextField? _inputField;
    private StatusBar? _statusBar;
    private StatusItem? _sessionItem;
    private StatusItem? _stateItem;

    // Thread-safe output accumulation
    private readonly object _outputLock = new();
    private readonly System.Text.StringBuilder _outputBuffer = new();

    // Throttled UI update — accumulate text, flush to MainLoop every 50ms
    private System.Text.StringBuilder _pendingText = new();
    private DateTime _lastUiFlush = DateTime.MinValue;
    private const int UiFlushIntervalMs = 50;
    private Timer? _flushTimer;
    private bool _flushScheduled = false;

    // Callback for when user submits input
    public Func<string, Task>? OnInputSubmitted { get; set; }

    /// <summary>Initialize the terminal GUI. Must be called before any other method.</summary>
    public void Init(string title = "ECAssistant")
    {
        Application.Init();

        // ── Main window ──
        _appWin = new Window(title)
        {
            X = 0,
            Y = 0,
            Width = Terminal.Gui.Dim.Fill(),
            Height = Terminal.Gui.Dim.Fill(),
        };

        // ── Output view (scrollable, read-only) ──
        _outputView = new TextView()
        {
            X = 0,
            Y = 0,
            Width = Terminal.Gui.Dim.Fill(),
            Height = Terminal.Gui.Dim.Fill() - 2, // leave room for input + separator
            ReadOnly = true,
            Multiline = true,
            WordWrap = true,
        };
        _appWin.Add(_outputView);

        // ── Separator line ──
        var separator = new FrameView("")
        {
            X = 0,
            Y = Pos.Bottom(_outputView),
            Width = Terminal.Gui.Dim.Fill(),
            Height = 1,
            Border = new Border() { BorderStyle = BorderStyle.Single },
        };
        _appWin.Add(separator);

        // ── Input field (bottom, always available) ──
        var promptLabel = new Label("> ")
        {
            X = 0,
            Y = Pos.Bottom(separator),
        };
        _inputField = new TextField("")
        {
            X = Pos.Right(promptLabel),
            Y = Pos.Bottom(separator),
            Width = Terminal.Gui.Dim.Fill(),
        };

        _inputField.KeyPress += (args) =>
        {
            if (args.KeyEvent.Key == Key.Enter)
            {
                var input = _inputField.Text.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(input))
                {
                    _inputField.Text = "";
                    // Submit input — call handler
                    _ = Task.Run(async () =>
                    {
                        if (OnInputSubmitted != null)
                            await OnInputSubmitted(input);
                    });
                }
                args.Handled = true;
            }
        };

        _appWin.Add(promptLabel);
        _appWin.Add(_inputField);

        Application.Top.Add(_appWin);

        // ── Status bar ──
        _sessionItem = new StatusItem(Key.CtrlMask | Key.S, "Session: main", null);
        _stateItem = new StatusItem(Key.Null, "Idle", null);
        _statusBar = new StatusBar(new[] { _sessionItem, _stateItem, new StatusItem(Key.CtrlMask | Key.Q, "~Ctrl+Q~ Quit", null) });
        Application.Top.Add(_statusBar);

        // Ctrl+Q to quit
        Application.Top.KeyPress += (args) =>
        {
            if (args.KeyEvent.Key == (Key.CtrlMask | Key.Q))
            {
                Application.RequestStop();
                args.Handled = true;
            }
        };
    }

    /// <summary>Run the terminal GUI event loop. Blocks until Application.RequestStop().</summary>
    public void Run()
    {
        Application.Run();
        Application.Shutdown();
    }

    /// <summary>Set focus to the input field.</summary>
    public void FocusInput()
    {
        Application.MainLoop?.Invoke(() => _inputField?.SetFocus());
    }

    // ── Thread-safe output writing ──

    /// <summary>Append text to the output view (thread-safe, throttled).
    /// Accumulates text in _pendingText and schedules a MainLoop flush every 50ms.
    /// This prevents flooding the event loop with thousands of per-token invokes.
    /// </summary>
    private void AppendOutput(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_outputLock)
        {
            _pendingText.Append(text);
            ScheduleFlush();
        }
    }

    /// <summary>Schedule a throttled flush to the MainLoop. Only one flush is
    /// scheduled at a time; subsequent calls just add to _pendingText.</summary>
    private void ScheduleFlush()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;

        _flushTimer = new Timer(_ =>
        {
            Application.MainLoop?.Invoke(() =>
            {
                string textToWrite;
                lock (_outputLock)
                {
                    textToWrite = _pendingText.ToString();
                    _pendingText.Clear();
                    _flushScheduled = false;
                }

                if (_outputView == null || string.IsNullOrEmpty(textToWrite)) return;

                // Append text to TextView — ustring doesn't support + operator with string
                var currentText = _outputView.Text.ToString() ?? "";
                var combined = currentText + textToWrite;
                _outputView.Text = combined;

                // Auto-scroll to bottom — scroll to the last line
                // TextView uses row-based scrolling, not character offset
                var lines = combined.Split('\n');
                if (lines.Length > 0)
                {
                    _outputView.CursorPosition = new Point(0, Math.Max(0, lines.Length - 1));
                    _outputView.ScrollTo(lines.Length - 1);
                }
            });
        }, null, UiFlushIntervalMs, Timeout.Infinite);
    }

    // ── EGuiBase implementation ──

    public override void WriteLine(string text)
    {
        if (Application.MainLoop == null) { Console.WriteLine(text); return; }
        AppendOutput(text + "\n");
    }

    public override void WriteLineColored(string coloredText)
    {
        // Strip ANSI colors for terminal GUI — Terminal.Gui has its own color system
        var plain = StripAnsi(coloredText);
        WriteLine(plain);
    }

    public override void WriteRaw(string text)
    {
        if (Application.MainLoop == null) { Console.Write(text); return; }
        AppendOutput(text);
    }

    public override void BlankLine()
    {
        WriteLine("");
    }

    // ── User Input ──
    // In Terminal.Gui, input is handled by the TextField's KeyPress event.
    // PromptRaw/PromptColored are fallbacks for non-GUI contexts.

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

    // ── Status / Info ──

    public override void InfoColored(string coloredText)
        => WriteLineColored(coloredText);

    public override void WarningColored(string coloredText)
        => WriteLineColored(coloredText);

    public override void WriteRawDirect(string text)
    {
        if (Application.MainLoop == null) { Console.Write(text); return; }
        AppendOutput(text);
    }

    public override void LogInternal(string text)
    {
        WriteLineColored(text);
    }

    // ── Status bar updates ──

    public void SetSessionName(string name)
    {
        Application.MainLoop?.Invoke(() =>
        {
            if (_sessionItem != null)
                _sessionItem.Title = $"Session: {name}";
            _statusBar?.SetNeedsDisplay();
        });
    }

    public void SetState(string state)
    {
        Application.MainLoop?.Invoke(() =>
        {
            if (_stateItem != null)
                _stateItem.Title = state;
            _statusBar?.SetNeedsDisplay();
        });
    }

    public void SetLoading(bool loading, string? label = null)
    {
        Application.MainLoop?.Invoke(() =>
        {
            if (loading && _stateItem != null)
                _stateItem.Title = label ?? "Loading...";
            _statusBar?.SetNeedsDisplay();
        });
    }

    // ── ANSI stripping ──

    private static string StripAnsi(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = new System.Text.StringBuilder(text.Length);
        var inEscape = false;
        foreach (char c in text)
        {
            if (c == '\x1b') { inEscape = true; continue; }
            if (inEscape)
            {
                if (c == 'm' || c == 'H' || c == 'K' || c == 'J' || c == 'A' || c == 'B' || c == 'C' || c == 'D')
                    inEscape = false;
                continue;
            }
            result.Append(c);
        }
        return result.ToString();
    }
}