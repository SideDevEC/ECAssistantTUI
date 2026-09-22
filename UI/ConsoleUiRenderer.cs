using ECAssistant.Core;
using ECAssistant.Core.Session;
using ECAssistant.TUI.UI;

namespace ECAssistant.TUI.Session;

/// <summary>
/// Bridges Core's IOutputListener to a SessionLayer's buffer.
///
/// v11.0: Writes to the SessionLayer's buffer, NOT to EGuiConsole directly.
/// The layer owns the output buffer. When the layer is active, its buffer
/// is what gets rendered. When it's not active, the buffer keeps filling
/// silently — so when the user switches back, all output is there.
///
/// Stream polling (80ms) updates the layer's LiveStreamLine.
/// </summary>
public class ConsoleUiRenderer : IOutputListener, IDisposable
{
    private readonly SessionLayer _layer;
    private readonly EColor _color;
    private readonly Func<string>? _streamBufferGetter;
    private Timer? _streamPollTimer;
    private volatile string _lastStreamSnapshot = ""; // L7: volatile — read/written across timer/UI threads; benign races (worst case: a stale compare skips one 80ms tick)
    private readonly Func<string, bool>? _approvalPrompt;
    private readonly Func<string, ECAssistant.Core.Session.ApprovalScope>? _approvalPromptScoped;
    private readonly Func<string, System.Collections.Generic.IReadOnlyList<string>, int?>? _choicePrompt;
    private readonly Action<string?>? _statusCallback;

    public ConsoleUiRenderer(SessionLayer layer, EColor color, Func<string>? streamBufferGetter = null, Func<string, bool>? approvalPrompt = null,
        Func<string, ECAssistant.Core.Session.ApprovalScope>? approvalPromptScoped = null, Func<string, System.Collections.Generic.IReadOnlyList<string>, int?>? choicePrompt = null, Action<string?>? statusCallback = null)
    {
        _layer = layer;
        _color = color;
        _streamBufferGetter = streamBufferGetter;
        _approvalPrompt = approvalPrompt;
        _approvalPromptScoped = approvalPromptScoped;
        _choicePrompt = choicePrompt;
        _statusCallback = statusCallback;
    }

    /// <summary>Map output states to ANSI color codes.</summary>
    private string StateToColor(OutputState state) => state switch
    {
        OutputState.Info    => _color.Cyan,
        OutputState.Success => _color.Green,
        OutputState.Warning => _color.Yellow,
        OutputState.Error   => _color.Red,
        OutputState.Dim     => _color.Dim,
        OutputState.Bold    => _color.Bold,
        OutputState.Raw     => _color.Reset,
        OutputState.System  => _color.Magenta,
        _ => _color.Reset
    };

    /// <summary>Map output states to a tag prefix for display.</summary>
    private string? StateToTag(OutputState state) => state switch
    {
        OutputState.Success => "OK",
        OutputState.Warning => "WARN",
        OutputState.Error   => "ERR",
        OutputState.System  => "SYS",
        _ => null
    };

    // ── IOutputListener implementation ──

    public void OnOutput(string text, OutputState state)
    {
        _statusCallback?.Invoke(null); // real output arriving — spinner goes away
        if (string.IsNullOrEmpty(text))
        {
            _layer.AddOutputLine("");
            return;
        }

        var color = StateToColor(state);
        var tag = StateToTag(state);
        if (tag != null)
            _layer.AddOutputLine($"{color}[{tag}] {text}{_color.Reset}");
        else
            _layer.AddOutputLine($"{color}{text}{_color.Reset}");
    }

    public void OnStreamStart()
    {
        _statusCallback?.Invoke(null); // streamed tokens take over from the spinner
        _streamPollTimer?.Dispose();
        _lastStreamSnapshot = "";

        if (_streamBufferGetter == null) return;

        // Start polling the stream buffer every 80ms
        _streamPollTimer = new Timer(_ =>
        {
            try
            {
                var current = _streamBufferGetter();
                if (current == _lastStreamSnapshot) return;
                _lastStreamSnapshot = current;

                // Write the live stream content to the layer's buffer
                _layer.UpdateLiveStreamLine(current);
            }
            catch (Exception pollEx)
            {
                // M5: log once per failure cycle instead of silently swallowing.
                // The stream buffer getter should never throw, but if it does
                // (e.g. session disposed mid-poll) we want a trace in the log.
                System.Diagnostics.Debug.WriteLine($"[StreamPoll] {pollEx.GetType().Name}: {pollEx.Message}");
            }
        }, null, 80, 80);
    }

    public void OnStreamStop()
    {
        _streamPollTimer?.Dispose();
        _streamPollTimer = null;
        _layer.ClearLiveStreamLine();
    }

    public bool OnRequestApproval(string message)
    {
        return OnRequestApprovalScoped(message) == ECAssistant.Core.Session.ApprovalScope.AllowOnce;
    }

    /// <summary>
    /// v14.10.2: scoped approval — the wired prompt callback decides whether the
    /// user chose allow-once, allow-for-session, or deny. Falls back to the legacy
    /// bool callback (AllowOnce/Deny) when no scoped callback is wired.
    /// </summary>
    public ECAssistant.Core.Session.ApprovalScope OnRequestApprovalScoped(string message)
    {
        if (_approvalPromptScoped != null)
            return _approvalPromptScoped(message);
        if (_approvalPrompt != null)
            return _approvalPrompt(message) ? ECAssistant.Core.Session.ApprovalScope.AllowOnce : ECAssistant.Core.Session.ApprovalScope.Deny;
        // No approval callback — auto-deny
        return ECAssistant.Core.Session.ApprovalScope.Deny;
    }

    /// <summary>v14.9: interactive checkpoint — delegate to the choice callback when
    /// wired; without one, return null so the orchestrator proceeds autonomously.</summary>
    public void OnStatus(string? status)
    {
        _statusCallback?.Invoke(status);
    }

    public int? OnRequestChoice(string prompt, System.Collections.Generic.IReadOnlyList<string> options)
    {
        if (_choicePrompt != null)
            return _choicePrompt(prompt, options);
        return null;
    }

    /// <summary>Render output history when switching to a session.</summary>
    public void RenderHistory(List<OutputEntry> entries)
    {
        foreach (var entry in entries)
        {
            switch (entry.Type)
            {
                case "stream":
                    if (!string.IsNullOrEmpty(entry.Text))
                    {
                        var c = StateToColor(entry.State);
                        _layer.AddOutputLine(c + entry.Text + _color.Reset);
                    }
                    break;

                case "line":
                    if (string.IsNullOrEmpty(entry.Text))
                        _layer.AddOutputLine("");
                    else
                    {
                        var c = StateToColor(entry.State);
                        var tag = StateToTag(entry.State);
                        if (tag != null)
                            _layer.AddOutputLine($"{c}[{tag}] {entry.Text}{_color.Reset}");
                        else
                            _layer.AddOutputLine($"{c}{entry.Text}{_color.Reset}");
                    }
                    break;
            }
        }
    }

    public void Dispose()
    {
        _streamPollTimer?.Dispose();
    }
}