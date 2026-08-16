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
  ├── Screen dimensions (shared, layers read them)
  └── Active layer pointer (who gets output + keys)

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

SessionLayer : BaseLayer  (one per session)
  ├── Own output buffer — keeps accumulating in background even when not active
  ├── Live stream polling (stream buffer getter)
  ├── Silent mode (typed but not echoed during generation)
  ├── HandleKey(): full key processing (text, backspace, enter, scroll, mouse, ESC)
  └── OnActivate(): full repaint from own buffer

HelpLayer : BaseLayer
  ├── Own help content buffer
  ├── Simple render (centered text, no input field)
  └── Enter to pop, all other keys consumed
```

## Key Design Decisions

### 1. EGuiConsole keeps the lock and dimensions
- `_writeLock` stays in `EGuiConsole` — layers use `console.Lock`
- Screen dimensions (`_screenWidth`, `_screenHeight`, `_outputRegionEnd`, etc.) stay in `EGuiConsole` — layers read them via properties
- This avoids duplicating terminal detection logic in each layer

### 2. BaseLayer owns its state but borrows the console
- `BaseLayer` receives an `EGuiConsole` reference
- Uses `console.Lock` for thread safety
- Uses `console.Width`, `console.Height`, `console.OutputRegionEnd` for layout
- Calls `Console.Write()` and `Console.Out.Flush()` for raw output (inside lock)

### 3. One SessionLayer per session — buffers run in background
- Each `AgentSession` gets its own `SessionLayer` instance
- The `SessionLayer` owns its `_outputLines` buffer
- Output from the session always goes to its `SessionLayer`, even when not active
- When GUI switches sessions, it just points to the new active `SessionLayer` and calls `Repaint()`
- No history copying — the layer already has everything
- Stream on/off stays on the `SessionLayer` — the poll timer lives there

### 4. Output routing
- `EGuiConsole.WriteLineColored()` → routes to active layer's `AddOutputLine()` + `Repaint()`
- If a non-active `SessionLayer` receives output (background session), it adds to its buffer but does NOT repaint — only the active layer repaints
- `EGuiConsole` implements `EGuiBase` by delegating to active layer

### 5. Input routing
- `EGuiConsole.PromptRaw()` → delegates to active layer's `HandleInput()`
- `BaseLayer.HandleInput()` runs the key loop (read key, process, repaint)
- Each layer controls its own input behavior

### 6. Mouse wheel
- Mouse escape sequence parsing in a shared helper
- Parsed scroll events forwarded to active layer's `ScrollUp/ScrollDown`

### 7. Overlay layers (Help)
- `HelpLayer` is pushed on top of the active `SessionLayer`
- While `HelpLayer` is active, keys go to `HelpLayer`, output goes to `HelpLayer` (if any)
- The underlying `SessionLayer` keeps its buffer intact — no interference
- When `HelpLayer` is popped, `SessionLayer.OnActivate()` repaints from its own buffer

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
| `UI/SessionLayer.cs` | Extends `BaseLayer`, one instance per session, silent mode + live stream |
| `UI/HelpLayer.cs` | Extends `BaseLayer`, simple render + Enter to pop |
| `UI/ConsoleUiRenderer.cs` | Routes to the session's `SessionLayer` directly (not via EGuiConsole) |
| `Program.cs` | Minor: creates `SessionLayer` per session, attaches to `EGuiConsole` |

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
- All state fields: `_outputLines`, `_scrollOffset`, `_screenRows`, dirty flags, `_inputBuffer`, `_statusBar`, `_liveStreamText`, etc.
- All methods: `Repaint()`, `PaintOutputRegion()`, `PaintStatusBar()`, `PaintInputLine()`, `ScrollUp/Down/ToBottom`, `AddOutputLine()`, `UpdateLiveStreamLine()`, `ClearLiveStreamLine()`
- Abstract: `HandleKey(EGuiConsole, ConsoleKeyInfo)` — each layer implements key handling
- Virtual: `OnActivate(EGuiConsole)`, `OnDeactivate(EGuiConsole)`, `OnResize(EGuiConsole)`
- Uses `EGuiConsole` reference for: lock, dimensions, raw `Console.Write`
- `RepaintIfActive()` — only repaints if this layer is the active layer

### Step 3: Convert SessionLayer
`SessionLayer : BaseLayer`:
- Constructor takes session key + stream buffer getter
- `HandleKey()`: full key processing (text, backspace, enter, scroll, mouse wheel, ESC, tab)
- `OnActivate()`: full repaint from own buffer
- Live stream: `UpdateLiveStreamLine()` + `ClearLiveStreamLine()` + poll timer (owned by layer)
- Silent mode: `_silentInput`, `_silentInputCheck`
- `AddOutputLine()`: always adds to buffer; only repaints if active
- One instance per session — buffer accumulates in background

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
- `_activeLayer` pointer (BaseLayer)
- `_layerStack` for overlay layers (HelpLayer etc.)
- `WriteLine/WriteLineColored/BlankLine/WriteRaw` → delegate to active layer
- `PromptRaw/PromptColored` → delegate to active layer's `HandleInput()`
- Resize watcher → notify active layer
- `PushLayer/PopLayer` — overlay layers on top of active base layer
- `ClearCanvas` → delegate to active layer
- `SetActiveLayer(BaseLayer)` — switch which SessionLayer is active
- `EGuiBase` API implementation (InfoColored, WarningColored, etc. → delegate)

### Step 6: Update ConsoleUiRenderer + Program.cs
- `ConsoleUiRenderer`: holds a reference to the `SessionLayer` (not just `EGuiBase`)
  - `OnOutput()` → `sessionLayer.AddOutputLine()` + `sessionLayer.RepaintIfActive()`
  - `OnStreamStart()` → `sessionLayer.StartStreamPolling()`
  - `OnStreamStop()` → `sessionLayer.StopStreamPolling()`
- `Program.cs`: create `SessionLayer` per session, call `gui.SetActiveLayer(sessionLayer)`
  - Session switch: `gui.SetActiveLayer(newSessionLayer)` — layer repaints from its own buffer
  - Remove manual history copying (`RenderHistory`)

### Step 7: Test + verify
- Build clean
- Run app: session output, input, scroll, mouse wheel
- Open help: help renders, Enter returns, session view intact
- Stream: live tokens appear during generation
- Switch sessions: active layer changes, repaints from own buffer — no history copy
- Background session output: accumulates in its layer buffer, visible on switch
- Resize: active layer repaints

---

## SessionLayer ↔ Session Mapping

```
Program.cs creates:
  SessionManager → AgentSession("main")
  SessionLayer("main", session.GetStreamBuffer) → attached to EGuiConsole
  ConsoleUiRenderer(sessionLayer) → session.AddListener(renderer)

  SessionManager → AgentSession("debug")
  SessionLayer("debug", session.GetStreamBuffer) → NOT attached (inactive)
  ConsoleUiRenderer(sessionLayer) → session.AddListener(renderer)

Switching to "debug":
  gui.SetActiveLayer(debugSessionLayer)  → debugSessionLayer.OnActivate() → Repaint()
  No history copying needed — debugSessionLayer already has all output in _outputLines
```

## API Stability

`EGuiBase` (Core) stays unchanged.
`EGuiConsole` public surface stays the same from `Program.cs`'s perspective:
- `InitConsole()`, `ShutdownConsole()`
- `WriteLineColored()`, `WriteLine()`, `BlankLine()`, `WriteRaw()` → delegate to active layer
- `PromptRaw()`, `PromptColored()` → delegate to active layer
- `PushLayer()`, `PopLayer()`, `ClearCanvas()`
- `SetHandlers()`, `SetSilentInputCheck()`, `SetSilentInputInitial()`
- `SetActiveLayer(BaseLayer)` — NEW: switch active session layer
- `TriggerFullRepaint()`, `SetStatusBar()`
- `UpdateLiveStreamLine()`, `ClearLiveStreamLine()` → delegate to active layer

## Risk Assessment

- **Low risk:** `EGuiBase` API unchanged, `Program.cs` changes minimal
- **Medium risk:** Mouse wheel parsing moves to shared helper
- **Medium risk:** Live stream polling timer ownership moves to SessionLayer
- **Low risk:** Session switch becomes trivial (no history copy)
- **Mitigation:** One step at a time, build + test after each step