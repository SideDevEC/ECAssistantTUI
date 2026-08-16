# ECAssistant App — Architecture

**Updated:** 2026-08-16 (v10.25)
**Build:** 0 errors, 0 warnings
**Tests:** 50/50 passing (App only)

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
      50 tests (EGuiConsole, HelpLayer, SessionLayer, ConsoleUiRenderer)
```

## What App Contains

- **Program.cs** — entry point, wires Core to the console UI
- **UI/EGuiConsole.cs** — full-screen alternate-buffer TUI (the screenbuffer engine)
- **UI/IGuiLayer.cs** — layer interface for overlay screens
- **UI/HelpLayer.cs** — help overlay
- **UI/SessionLayer.cs** — base session layer
- **UI/LoadingIndicator.cs** — animated loading dots
- **UI/ConsoleUiRenderer.cs** — bridges Core's IOutputListener to EGuiConsole

## What App Does NOT Contain

- No LLamaSharp references
- No engine/session/tool logic (all in Core)
- No system prompts or config files (embedded in Core DLL)
- No inference logic

## Dependency Flow

```
Program.cs (App — Main → binder)
  │
  ├── creates EGuiConsole (concrete TUI)
  │     └── Alternate screen buffer, dirty-flag repaint system
  │     └── Layer stack: SessionLayer (base) → HelpLayer
  │     └── Extends EGuiBase (from Core)
  │
  ├── AgentConfigBuilder.Create() → EAgentConfig  (Core)
  │
  ├── SessionManager(config, modelPath, workingDir, logger)  (Core)
  │
  ├── SessionBuilder → BuildAsync(session)  (Core)
  │
  ├── ConsoleUiRenderer(Gui, color)  (App)
  │     └── Implements IOutputListener (Core interface)
  │     └── session.AddListener(uiRenderer)
  │
  └── RunAgentLoop → session.Prompt(input)  (Core)
```

## EGuiConsole Screenbuffer (v10.25: Delta Rendering)

### Layout
```
┌─────────────────────────────────┐  row 0
│ Output region (scrolls)          │  → _outputLines[] + _scrollOffset
├─────────────────────────────────┤  row (height-2)
│ Status bar                       │
├─────────────────────────────────┤  row (height-1)
│ > user input here                │
└─────────────────────────────────┘
```

### Dirty-Flag Repaint System
- `_fullRepaint` — clear screen + redraw everything (resize, ClearCanvas)
- `_outputDirty` — output region changed (new output line)
- `_statusDirty` — status bar changed (SetStatusBar)
- `_inputDirty` — input line changed (keystroke, backspace)

### Delta Rendering (v10.25)
- `_screenRows[]` cache tracks what's currently on screen
- `PaintOutputRegion()` only clears + writes rows where `newRow != oldRow`
- Common case (new line at bottom): 1 row write instead of ~20
- Full repaint invalidates cache via `Array.Fill(null)`

### Repaint Triggers
| Trigger | Dirty Flags | Cost |
|---------|------------|------|
| New output line | _outputDirty | 1 row (delta) |
| Keystroke | _inputDirty | 1 row |
| Scroll | _outputDirty + _statusDirty | Full region |
| Resize | _fullRepaint | Full screen |
| Status bar change | _statusDirty | 1 row |

## Build Order

```
1. Build ECAssistantCore.sln → produces ECAssistant.Core.dll
2. Build ECAssistant.sln → references the DLL via HintPath
```

App csproj references Core DLL:
```xml
<Reference Include="ECAssistant.Core">
  <HintPath>..\ECAssistantCore\bin\Debug\net8.0\ECAssistant.Core.dll</HintPath>
  <Private>true</Private>
</Reference>
```

## Key Constraints
- `Console.Write/WriteLine` ONLY in `EGuiConsole`
- `EColor` used ONLY by `ConsoleUiRenderer`, `LoadingIndicator`, `Program.cs`
- App has zero LLamaSharp dependencies
- App has zero external file dependencies (no appsettings.json, no system prompts)
- Program.cs is the binder — creates UI, creates Core session, wires them together