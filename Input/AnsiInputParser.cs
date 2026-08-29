namespace ECAssistant.TUI.Input;

/// <summary>Terminal input events decoded from ANSI escape sequences.</summary>
public enum AnsiInputEvent
{
    /// <summary>Character consumed as part of a sequence — no user-visible action.</summary>
    None,
    /// <summary>Lone ESC (no sequence followed).</summary>
    Escape,
    WheelUp,
    WheelDown,
    ArrowUp,
    ArrowDown
}

/// <summary>
/// Pure streaming parser for the ANSI escape sequences the TUI cares about:
/// X10 mouse (ESC[M + 3 bytes), SGR mouse (ESC[&lt; b;x;y M|m), CSI arrows (ESC[A/B),
/// SS3 arrows (ESC O A/B) and lone ESC. Handles sequences split across arbitrary
/// Feed() boundaries, drains unknown CSI sequences, and never leaks sequence bytes
/// as user input. Fully unit-testable — no Console dependency.
/// </summary>
public sealed class AnsiInputParser
{
    private enum State { Ground, AfterEsc, AfterCsiStart, InX10, InSgr, InSs3 }

    private State _state = State.Ground;
    private readonly System.Text.StringBuilder _sgr = new();
    private int _x10BytesLeft;
    private int _x10Button;

    /// <summary>True while a sequence is incomplete — a timeout should Reset() rather than treat input as keys.</summary>
    public bool IsMidSequence => _state != State.Ground;

    /// <summary>True when only ESC has been seen — a timeout here means the user really pressed ESC.</summary>
    public bool EscapeOnTimeout => _state is State.AfterEsc or State.AfterCsiStart;

    /// <summary>Feed the ESC character (the input loop's ESC keypress).</summary>
    public AnsiInputEvent FeedEsc()
    {
        _state = State.AfterEsc;
        return AnsiInputEvent.None;
    }

    /// <summary>Feed one character following the ESC. Returns the decoded event when a sequence completes.</summary>
    public AnsiInputEvent Feed(char c)
    {
        switch (_state)
        {
            case State.Ground:
                return AnsiInputEvent.None; // normal characters are handled by the caller, not the parser

            case State.AfterEsc:
                _state = c switch
                {
                    '[' => State.AfterCsiStart,
                    'O' => State.InSs3,
                    _ => State.Ground
                };
                if (_state == State.Ground)
                {
                    // Unknown byte after ESC: treat as Escape and drop the byte (legacy semantics).
                    return AnsiInputEvent.Escape;
                }
                return AnsiInputEvent.None;

            case State.AfterCsiStart:
                if (c == 'M') { _state = State.InX10; _x10BytesLeft = 3; return AnsiInputEvent.None; }
                if (c == '<') { _state = State.InSgr; _sgr.Clear(); return AnsiInputEvent.None; }
                if (c == 'A') { _state = State.Ground; return AnsiInputEvent.ArrowUp; }
                if (c == 'B') { _state = State.Ground; return AnsiInputEvent.ArrowDown; }
                _state = State.InSgr; // unknown CSI — drain to final byte
                _sgr.Clear();
                _sgr.Append(c);
                return DrainCheck(c);

            case State.InX10:
                _x10BytesLeft--;
                if (_x10BytesLeft == 2) { _x10Button = c - 32; return AnsiInputEvent.None; }
                if (_x10BytesLeft > 0) return AnsiInputEvent.None; // coords — ignored
                _state = State.Ground;
                return _x10Button switch
                {
                    64 => AnsiInputEvent.WheelUp,
                    65 => AnsiInputEvent.WheelDown,
                    _ => AnsiInputEvent.None
                };

            case State.InSgr:
                // Final byte: 'M'/'m' terminate SGR mouse; any other @-~ byte terminates an unknown CSI.
                var isSgrTerminator = c == 'M' || c == 'm';
                var isCsiFinal = c >= '@' && c <= '~';
                if (!isSgrTerminator && !isCsiFinal)
                {
                    _sgr.Append(c);
                    return AnsiInputEvent.None;
                }
                _state = State.Ground;
                var parts = _sgr.ToString().Split(';');
                if (isSgrTerminator && parts.Length >= 1 && int.TryParse(parts[0], out var button))
                {
                    if (button == 64) return AnsiInputEvent.WheelUp;
                    if (button == 65) return AnsiInputEvent.WheelDown;
                }
                return AnsiInputEvent.None;

            case State.InSs3:
                _state = State.Ground;
                return c switch
                {
                    'A' => AnsiInputEvent.ArrowUp,
                    'B' => AnsiInputEvent.ArrowDown,
                    _ => AnsiInputEvent.None
                };

            default:
                _state = State.Ground;
                return AnsiInputEvent.None;
        }
    }

    /// <summary>For unknown CSI sequences: the final byte (in @-~) ends the sequence.</summary>
    private AnsiInputEvent DrainCheck(char c)
    {
        if (_state == State.InSgr && c >= '@' && c <= '~' && c != '[')
        {
            _state = State.Ground;
            return AnsiInputEvent.None;
        }
        return AnsiInputEvent.None;
    }

    /// <summary>Discard a partially received sequence (input timeout mid-sequence).</summary>
    public void Reset()
    {
        _state = State.Ground;
        _sgr.Clear();
        _x10BytesLeft = 0;
    }
}
