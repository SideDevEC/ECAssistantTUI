# Layer Architecture Refactor Plan

**Date:** 2026-08-16
**Goal:** Move all rendering, output buffer, scroll, and input logic from `EGuiConsole` into a `BaseLayer` class hierarchy. `EGuiConsole` becomes a thin layer manager.

---

## Current Problem

`EGuiConsole` owns everything: output buffer, screen model, rendering, input handling, scroll, live stream, layer stack. Layers (`HelpLayer`, `SessionLayer`) are thin shells with no state. When a layer is active, `EGuiConsole`'s rendering state interferes — output lines bleed through, scroll status mixes up, live stream poll timer writes to the wrong buffer.

## Target Architecture

```
EGuiConsole (thin manager)
  ├── Terminal entry/exit (ANSI, mouse, cursor)
  ├── Resize watcher (timer → notify active layer)
  ├── Layer stack (push/pop, route keys + output to active layer)
  ├── _writeLock (shared, passed to layers)
  └── Screen dimensions (shared, layers read them)

BaseLayer (abstract)
  ├── Output buffer: List<string> _outputLines
  ├── Scroll: _scrollOffset, ScrollUp/Down/ToBottom
  ├── Screen rows cache: string?[] _screenRows (delta rendering)
  ├── Dirty flags: _outputDirty, _statusDirty, _inputDirty, _fullRepaint
  ├── Input buffer: StringBuilder _inputBuffer
  ├── Status bar: _statusBar
  ├── Rendering: Repaint(), PaintOutputRegion(), PaintStatusBar(), PaintInputLine()
  ├── Input handling: HandleKey(key) → processes text, backspace, enter, scroll, mouse
  ├── ANSI helpers: StripAnsi, TruncateAnsi, WrapLine (static, shared)
  └── Live stream: _liveStreamText, _liveStreamLineIndex, UpdateLiveStreamLine()

SessionLayer : BaseLayer
  ├── All session output + input (current EGuiConsole behavior)
  ├── Live stream polling (stream buffer getter)
  └── Silent mode (typed but not echoed during generation)

HelpLayer : BaseLayer
  ├── Own help content buffer
  ├── Simple render (centered text, no input field)
  └── Enter to pop, all other keys consumed
```

## Key Design Decisions

### 1. EGuiConsole keeps the lock and dimensions
- `_writeLock` stays in `EGuiConsole` — layers receive it or use a delegate
- Screen dimensions (`_screenWidth`, `_screenHeight`, `_outputRegionEnd`, etc.) stay in `EGuiConsole` — layers read them via properties
- This avoids duplicating terminal detection logic in each layer

### 2. BaseLayer owns its state but borrows the console
- `BaseLayer` receives an `EGuiConsole` reference (like current `IGuiLayer.OnActivate(EGuiConsole)`)
- Uses `console.Lock` for thread safety
- Uses `console.Width`, `console.Height`, `console.OutputRegionEnd` for layout
- Calls `console.Write()` and `console.Flush()` for raw output

### 3. Output routing
- `EGuiConsole.WriteLineColored()` → routes to active layer's `AddOutputLine()` + `Repaint()`
- If no layer active → route to a default `SessionLayer`
- `EGuiConsole` implements `EGuiBase` (abstract API) by delegating to active layer

### 4. Input routing
- `EGuiConsole.ReadInputLine()` → delegates to active layer's `HandleInput()`
- `BaseLayer.HandleInput()` runs the key loop (read key, process, repaint)
- Each layer controls its own input behavior

### 5. Mouse wheel
- Mouse escape sequence parsing stays in `EGuiConsole.ReadInputLine()` (or a shared helper)
- Parsed scroll events are forwarded to active layer's `ScrollUp/ScrollDown`

---

## File Changes

### New Files

| File | Description |
|------|-------------|
| `UI/BaseLayer.cs` | Abstract base class with all rendering, buffer, scroll, input logic |
| `UI/AnsiHelpers.cs` | Static helpers: `StripAnsi`, `TruncateAnsi`, `WrapLine` (moved from EGuiConsole) |

### Modified Files

| File | Changes |
|------|---------|
| `UI/EGuiConsole.cs` | Strip down to: terminal init/shutdown, dimensions, lock, layer stack, output routing. ~150 lines from ~1000. |
| `UI/IGuiLayer.cs` | Replace with `abstract class BaseLayer` (or keep interface + add BaseLayer) |
| `UI/SessionLayer.cs` | Extends `BaseLayer`, adds silent mode + live stream |
| `UI/HelpLayer.cs` | Extends `BaseLayer`, simple render + Enter to pop |
| `UI/ConsoleUiRenderer.cs` | Minor: calls `console.GetActiveLayer()` for stream updates |
| `Program.cs` | No changes (EGuiBase API stays the same) |

### Files Not Changed

| File | Reason |
|------|--------|
| `UI/LoadingIndicator.cs` | Uses EGuiBase API, no changes needed |
| `UI/EColor.cs` | Color constants, no changes |
| `EGuiBase.cs` (Core) | Abstract API unchanged |

---

## Migration Steps (ordered)

### Step 1: Extract AnsiHelpers
Move `StripAnsi`, `TruncateAnsi`, `WrapLine` from `EGuiConsole` to static `AnsiHelpers` class. Update references. Build + verify.

### Step 2: Create BaseLayer
Create `BaseLayer` abstract class with:
- All state fields currently in `EGuiConsole`: `_outputLines`, `_scrollOffset`, `_screenRows`, dirty flags, `_inputBuffer`, `_statusBar`, `_liveStreamText`, etc.
- All methods: `Repaint()`, `PaintOutputRegion()`, `PaintStatusBar()`, `PaintInputLine()`, `ScrollUp/Down/ToBottom`, `AddOutputLine()`, `UpdateLiveStreamLine()`, `ClearLiveStreamLine()`
- Abstract: `HandleKey(EGuiConsole, ConsoleKeyInfo)` — each layer implements its key handling
- Virtual: `OnActivate(EGuiConsole)`, `OnDeactivate(EGuiConsole)`, `OnResize(EGuiConsole)`
- Uses `EGuiConsole` reference for: lock, dimensions, raw `Console.Write`

### Step 3: Convert SessionLayer
`SessionLayer : BaseLayer`:
- `HandleKey()`: full key processing (text, backspace, enter, scroll, mouse wheel, ESC, tab)
- `OnActivate()`: full repaint
- Live stream: `UpdateLiveStreamLine()` + `ClearLiveStreamLine()` (moved from EGuiConsole)
- Silent mode: `_silentInput`, `_silentInputCheck`

### Step 4: Convert HelpLayer
`HelpLayer : BaseLayer`:
- Override `OnActivate()`: paint help content (own buffer, no input line)
- Override `HandleKey()`: Enter = pop, all others consumed
- Override `Repaint()`: simple centered render (no output region, no input line)
- No scroll, no input buffer, no status bar — minimal

### Step 5: Strip down EGuiConsole
Remove from `EGuiConsole`:
- All output buffer fields and methods
- All rendering methods (Repaint, Paint*)
- All scroll methods
- All input handling (ReadInputLine, ReadInputLineFallback)
- All live stream methods
- All ANSI helpers (moved to AnsiHelpers)

Keep in `EGuiConsole`:
- `_writeLock` (expose as `internal object Lock`)
- Terminal init/shutdown (ANSI, mouse, cursor)
- Screen dimensions (expose as properties)
- `_layerStack` with `BaseLayer` instead of `IGuiLayer`
- `WriteLine/WriteLineColored/BlankLine/WriteRaw` → delegate to active layer
- `PromptRaw/PromptColored` → delegate to active layer's `HandleInput()`
- Resize watcher → notify active layer
- `PushLayer/PopLayer`
- `ClearCanvas` → delegate to active layer
- `EGuiBase` API implementation (InfoColored, WarningColored, etc. → delegate)

### Step 6: Update ConsoleUiRenderer
- `OnStreamStart/OnStreamStop`: get active session layer from `EGuiConsole`
- `UpdateLiveStreamLine` calls go to the active `SessionLayer`, not `EGuiConsole`

### Step 7: Test + verify
- Build clean
- Run app: session output, input, scroll, mouse wheel
- Open help: help renders, Enter returns, session view intact
- Stream: live tokens appear during generation
- Switch sessions: history renders correctly
- Resize: active layer repaints

---

## API Stability

`EGuiBase` (Core) stays unchanged — `Program.cs` doesn't need changes.
The `EGuiConsole` public surface stays the same from `Program.cs`'s perspective:
- `InitConsole()`, `ShutdownConsole()`
- `WriteLineColored()`, `WriteLine()`, `BlankLine()`, `WriteRaw()`
- `PromptRaw()`, `PromptColored()`
- `PushLayer()`, `PopLayer()`, `ClearCanvas()`
- `SetHandlers()`, `SetSilentInputCheck()`, `SetSilentInputInitial()`
- `TriggerFullRepaint()`, `SetStatusBar()`
- `UpdateLiveStreamLine()`, `ClearLiveStreamLine()`
- `PaintLayerScreen()` — may be removed if HelpLayer renders itself

## Risk Assessment

- **Low risk:** `Program.cs` API unchanged, tests for UI should still pass
- **Medium risk:** Mouse wheel parsing moves between layers — need to test on both platforms
- **Medium risk:** Live stream polling timer ownership moves to SessionLayer
- **Mitigation:** One step at a time, build + test after each step