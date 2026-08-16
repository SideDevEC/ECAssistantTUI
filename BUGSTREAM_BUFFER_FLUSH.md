# Bug: Stream Buffer Never Flushed to Listeners

**Status:** Not yet fixed — lower priority than the SessionLayer input freeze
**Found:** 2026-08-16

## Problem

In `AgentSession.WriteLine()`, the stream buffer flush is guarded by `if (_streaming)`:

```csharp
public void WriteLine(string text, OutputState state = OutputState.Info)
{
    lock (_bufferLock)
    {
        if (_streaming)          // ← FALSE if StopStream() was already called
        {
            StopStream();
            FlushStreamBuffer(); // ← NEVER reached
        }
        // ... writes entry, notifies listeners
    }
}
```

The engine calls `StopStream()` before `WriteLine()`:

```csharp
_out?.StopStream();                          // sets _streaming = false
_out?.WriteLine($"── End Token Stream ──");  // _streaming already false → no flush
```

Result: streamed LLM tokens are trapped in `_streamBuffer` and never sent to listeners.

## Fix

Move `FlushStreamBuffer()` outside the `if (_streaming)` block — always flush:

```csharp
if (_streaming)
    StopStream();
FlushStreamBuffer();  // always flush, even if StopStream was already called
```

## Location

- `AgentSession.cs` in ECAssistantCore — `WriteLine` method
- Also check `FlushStreamBuffer()` to make sure it handles empty buffer gracefully