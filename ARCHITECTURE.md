# ECAssistant App — Architecture

**Updated:** 2026-08-16 (v11.0)
**Status:** Design document — refactor in progress

## Overview

ECAssistant is a full-screen terminal UI (TUI) for a local, offline AI coding assistant. The app is a thin frontend that wires into `ECAssistant.Core.dll` (built from the separate `ECAssistantCore` repo).

The architecture follows a strict three-layer separation:

1. **Controller** — application logic, command parsing, session/layer lifecycle
2. **EGuiConsole** — terminal rendering engine, input reading, delta rendering
3. **BaseLayer** — per-screen state: output buffers, scroll position, status bar

Nobody knows about things above them. Dependencies flow strictly top-down.

## Project Structure

```
ECAssistant.sln / ECAssistant.slnx
├── ECAssistant.csproj              ← Console exe (App)
│     OutputType=Exe, AssemblyName=e-assistant
│     References: ECAssistant.Core.dll (from ECAssistantCore repo)
│     No LLamaSharp packages — Core ships everything
│     No content files — system prompts + config embedded in Core DLL
│
└── Tests/ECAssistant.Tests.csproj  ← UI tests only
      ProjectReference → ECAssistant.csproj
      Reference → ECAssistant.Core.dll
```

## Files (v11.0 target)

```
Program.cs                          ← Minimal entry point — creates Controller, calls Run()
UI/
├── EGuiConsole.cs                  ← Terminal engine (render + input + delta)
├── IGuiLayer.cs                    ← (removed — replaced by BaseLayer)
├── BaseLayer.cs                    ← Abstract layer: output buffer, scroll, status, repaint
├── SessionLayer.cs                 ← One per session, owns output buffer, fills continuously
├── HelpLayer.cs                    ← Static help content layer
├── LoadingIndicator.cs             ← Animated loading dots (status bar)
└── ConsoleUiRenderer.cs            ← Bridges Core IOutputListener → SessionLayer buffer
Controller/
└── AppController.cs                ← Application logic, command parsing, layer lifecycle
```

## Dependency Flow

```
Program.cs
  │
  │ creates Controller(config, modelPath, workingDir, logger, ...)
  │ calls controller.Run()
  │ does nothing else
  ▼
AppController
  ├── creates → EGuiConsole (owns it)
  ├── creates → Dictionary<LayerKey, BaseLayer> (owns all layers)
  ├── holds  → ActiveLayer reference
  ├── holds  → SessionManager (Core)
  ├── holds  → config, logger, bgMgr (injected from Program)
  │
  │ EGuiConsole calls back (callbacks only):
  │   controller.OnPrompt(string input)       ← user hit Enter
  │   controller.OnEscapePressed()            ← user hit ESC
  │
  │ EGuiConsole talks directly to ActiveLayer:
  │   layer.HandleScroll(lines)               ← scroll keys / mouse wheel
  │   layer.OnResize()                        ← terminal resize
  │   layer.GetVisibleRows()                  ← for rendering
  │   layer.GetStatusBar()                    ← for rendering
  │   layer.GetInputPrompt()                  ← for input line prefix
  │
  ▼
EGuiConsole
  ├── owns: terminal (alt buffer, cursor, ANSI, dimensions)
  ├── owns: input buffer (accumulates keystrokes, renders input line)
  ├── owns: delta rendering (_screenRows cache, dirty flags)
  ├── owns: raw key reading (Console.ReadKey)
  ├── holds: ActiveLayer (set by controller via SetActiveLayer)
  ├── holds: Controller reference (for OnPrompt / OnEscapePressed callbacks)
  │
  │ Input loop:
  │   printable/backspace/tab → update input buffer → repaint input line
  │   Enter                   → controller.OnPrompt(input) → clear input buffer
  │   ESC                     → controller.OnEscapePressed()
  │   scroll keys / mouse     → activeLayer.HandleScroll(...)
  │   resize                  → activeLayer.OnResize()
  │
  ▼
BaseLayer (abstract)
  ├── owns: OutputLines[] buffer (with ANSI codes)
  ├── owns: ScrollOffset
  ├── owns: StatusBar string
  ├── owns: LiveStreamLine (for streaming token display)
  ├── owns: IsDirty flag
  ├── holds: EGuiConsole reference (set via BindToConsole, cleared via UnbindFromConsole)
  │
  ├── GetVisibleRows() → List<string>     ← rows to render, after scroll + wrap
  ├── GetStatusBar() → string             ← status bar content
  ├── HandleScroll(int lines)             ← scroll up/down, updates offset
  ├── ScrollToBottom()                    ← reset scroll to newest
  ├── OnResize()                          ← recalculate visible region
  ├── BindToConsole(EGuiConsole)          ← layer gets console reference for repaint
  ├── UnbindFromConsole()                 ← clear console reference
  ├── RequestRepaint()                    ← calls console.Repaint() if bound and active
  │
  ├── SessionLayer : BaseLayer
  │     ├── holds: Core AgentSession reference
  │     ├── holds: ConsoleUiRenderer (writes to THIS layer's buffer, not to console)
  │     ├── buffer fills continuously — even when not the active layer
  │     ├── stream updates write to LiveStreamLine, triggers RequestRepaint()
  │     └── GetInputPrompt() → "> " (standard session prompt)
  │
  └── HelpLayer : BaseLayer
        ├── static content (help text lines)
        ├── GetInputPrompt() → "" (no input prompt in help)
        └── all keys handled by controller (Enter/ESC → switch back)
```

## Component Responsibilities

### Program.cs

Minimal entry point. Only responsibility:

1. Build config from `AgentConfigBuilder` + command-line args
2. Create `AppController` with config, model path, working dir, logger
3. Call `controller.Run()`
4. Return exit code

Program.cs does NOT know about EGuiConsole, layers, sessions, or UI rendering.

### AppController

The binder and application logic hub.

**Construction:**
- Creates `EGuiConsole` instance
- Loads Core session manager, discovers sessions
- Creates first `SessionLayer` for the "main" session
- Binds console ↔ layer, sets active layer

**Run loop:**
- Calls `console.InitConsole()` → enters alternate buffer
- Calls `console.InputLoop()` → blocks here forever; EGuiConsole reads keys
- On quit: calls `console.ShutdownConsole()`

**Callbacks from EGuiConsole:**
- `OnPrompt(string input)` — user hit Enter:
  - `/help` → create/switch to HelpLayer
  - `/quit`, `/exit` → stop all sessions, end loop
  - `/clear` → active session layer clears its buffer
  - `/session <n>` → switch to SessionLayer #n
  - `/session-new <name>` → create new session + new SessionLayer
  - `/tools`, `/context-status`, etc. → query Core, write result to active layer buffer
  - everything else → forward as prompt to active Core session
- `OnEscapePressed()` — user hit ESC:
  - Active layer is SessionLayer → stop running session
  - Active layer is HelpLayer → switch back to prior active layer

**Layer lifecycle:**
```
SwitchLayer(LayerKey key):
  1. oldLayer = ActiveLayer
  2. oldLayer.UnbindFromConsole()
  3. console.SetActiveLayer(null)
  4. newLayer = layers[key]
  5. newLayer.BindToConsole(console)
  6. console.SetActiveLayer(newLayer)
  7. console.Repaint()
  8. ActiveLayer = newLayer
```

### EGuiConsole

Pure terminal engine. No application logic. No output storage. No command parsing.

**Owns:**
- Terminal state (alternate buffer, cursor position, ANSI support detection)
- Input buffer (StringBuilder — what the user is currently typing)
- Delta rendering cache (`_screenRows[]` — what's currently on screen)
- Screen dimensions (width, height, region boundaries)
- Resize watcher (200ms timer polling `Console.WindowWidth/Height`)
- ActiveLayer reference (who to ask for visible rows)
- Controller reference (for OnPrompt / OnEscapePressed callbacks)

**Does NOT own:**
- Output line buffers (those live on layers)
- Scroll position (lives on layers)
- Status bar content (lives on layers)
- Any application state

**Layout:**
```
┌─────────────────────────────────┐  row 0
│ Output region (from ActiveLayer) │  → layer.GetVisibleRows()
├─────────────────────────────────┤  row (height-2)
│ Status bar (from ActiveLayer)    │  → layer.GetStatusBar()
├─────────────────────────────────┤  row (height-1)
│ > user input (from input buffer) │  → EGuiConsole owns this
└─────────────────────────────────┘
```

**Delta rendering:**
- `_screenRows[]` cache tracks what's currently on screen
- `Repaint()` asks ActiveLayer for visible rows, diffs against cache
- Only writes rows that changed (common case: 1 row for new output line)
- Full repaint on resize / layer switch / clear (invalidates cache)

**Input loop:**
```
while running:
  key = Console.ReadKey(true)

  if printable char or backspace or tab:
    → update input buffer locally
    → repaint input line only
    → continue (no controller involvement)

  if Enter:
    → input = inputBuffer.ToString()
    → clear input buffer
    → repaint input line
    → controller.OnPrompt(input)
    → continue

  if ESC:
    → controller.OnEscapePressed()
    → continue

  if scroll keys (PageUp/PageDown/ArrowUp/ArrowDown/Home/End):
    → activeLayer.HandleScroll(direction, amount)
    → repaint output region
    → continue

  if mouse wheel (X10 escape sequence):
    → parse button (64=up, 65=down)
    → activeLayer.HandleScroll(direction, 3)
    → repaint output region
    → continue
```

### BaseLayer (Abstract)

Per-screen state container. Each layer represents one full screen of content.

**Properties:**
- `OutputLines` — `List<string>` with ANSI codes, the layer's output history
- `ScrollOffset` — int, 0 = bottom (newest), N = scrolled up N lines
- `StatusBar` — string, content for the status bar row
- `LiveStreamText` — string?, current live stream content (or null when not streaming)
- `LiveStreamLineIndex` — int, index in OutputLines of the live stream line (-1 = none)
- `IsDirty` — bool, buffer changed since last render
- `Console` — EGuiConsole?, set by BindToConsole, cleared by UnbindFromConsole

**Methods:**
- `GetVisibleRows()` — returns wrapped, scrolled rows for the output region
- `GetStatusBar()` — returns status bar string (or empty)
- `GetInputPrompt()` — returns the prompt prefix ("> " for sessions, "" for help)
- `HandleScroll(int direction, int lines)` — updates scroll offset, triggers repaint
- `ScrollToBottom()` — resets scroll to 0
- `OnResize()` — recalculates visible region
- `AddOutputLine(string text)` — adds a line to the buffer (splits on \n, strips \r)
- `BindToConsole(EGuiConsole console)` — stores reference for repaint calls
- `UnbindFromConsole()` — clears reference
- `RequestRepaint()` — if console is bound, calls `console.Repaint()`
- `Clear()` — clears output buffer, resets scroll

**Static helpers (moved from EGuiConsole):**
- `StripAnsi(string text)` — remove ANSI escape sequences
- `TruncateAnsi(string text, int maxWidth)` — truncate with ANSI codes
- `WrapLine(string text, int maxCols)` — wrap long lines into multiple rows

### SessionLayer : BaseLayer

One instance per Core session. Owns the output buffer for that session.

**Additional properties:**
- `SessionKey` — string, the Core session key
- `CoreSession` — AgentSession reference (from Core)
- `Renderer` — ConsoleUiRenderer, writes to THIS layer's buffer

**Behavior:**
- ConsoleUiRenderer writes output to this layer's `AddOutputLine()` — NOT to EGuiConsole
- Stream polling (80ms timer) updates `LiveStreamLine` in this layer's buffer
- Buffer keeps filling even when this layer is not the active one
- When user switches back to this layer, all output is already there
- RequestRepaint() only fires if the layer is bound to the console (i.e., is active)

### HelpLayer : BaseLayer

Static content layer. Shows the help screen.

**Behavior:**
- Pre-filled with help text lines at construction
- No live updates, no streaming
- GetInputPrompt() returns "" (no input prompt on help screen)
- Controller handles ESC/Enter by switching back to the prior active layer

### ConsoleUiRenderer

Bridges Core's `IOutputListener` to a `SessionLayer`'s buffer.

**Changes from v10.25:**
- Previously wrote to `EGuiConsole` directly
- Now writes to `SessionLayer.AddOutputLine()` — the layer owns the buffer
- Stream polling timer still runs here, but updates the layer's LiveStreamLine, not console's
- Session switching: new ConsoleUiRenderer per session, each pointing at its own layer

## Screen Layout

```
┌─────────────────────────────────┐  row 0
│ Output region (scrolls)          │  → ActiveLayer.GetVisibleRows()
│ ...                              │  → EGuiConsole delta-renders against cache
│                                  │
├─────────────────────────────────┤  row (height-2)
│ Status bar                       │  → ActiveLayer.GetStatusBar()
├─────────────────────────────────┤  row (height-1)
│ > user input here                │  → EGuiConsole input buffer
└─────────────────────────────────┘
```

- Output region: rows 0 to (height-3)
- Status bar: row (height-2)
- Input line: row (height-1)
- EGuiConsole owns the input line (terminal concern)
- ActiveLayer owns the output region + status bar content

## Key Constraints

- `Console.Write/WriteLine` ONLY in `EGuiConsole`
- `EColor` used ONLY by `ConsoleUiRenderer`, `LoadingIndicator`, `Program.cs` (injected into controller)
- App has zero LLamaSharp dependencies in source code (runtime deps only)
- App has zero external file dependencies (no appsettings.json, no system prompts)
- Program.cs only interacts with AppController — nothing else
- EGuiConsole has no application logic — it renders and forwards input
- Layers have no knowledge of each other or the controller
- Controller is the only class that knows about all three (console + layers + Core)

## Build Order

```
1. Build ECAssistantCore.sln → produces ECAssistant.Core.dll
2. Build ECAssistant.sln → references the DLL via HintPath
```

App csproj references Core DLL:
```xml
<Reference Include="ECAssistant.Core">
  <HintPath>lib\ECAssistant.Core.dll</HintPath>
  <Private>true</Private>
</Reference>
```

## Test Migration (v10.25 → v11.0)

| Current Test File | New Location | Notes |
|---|---|---|
| `EGuiConsoleAnsiTests.cs` | `BaseLayerAnsiTests.cs` | StripAnsi, TruncateAnsi, WrapLine move to BaseLayer |
| `EGuiConsoleBufferTests.cs` | `BaseLayerBufferTests.cs` | AddOutputLine, multi-line, \r\n stripping move to BaseLayer |
| `LayerStackTests.cs` | `LayerTests.cs` | No stack anymore — test SessionLayer + HelpLayer directly |
| `ConsoleUiRendererTests.cs` | `ConsoleUiRendererTests.cs` | Updated to verify writes go to SessionLayer buffer, not EGuiConsole |
| (new) | `EGuiConsoleRenderingTests.cs` | Test delta rendering with mock layer |
| (new) | `AppControllerTests.cs` | Test OnPrompt command parsing, layer switching, ESC handling |