# ECAssistant TUI — Summary

**Updated:** 2026-08-16 (v11.1)
**Build:** 0 errors, 0 warnings
**Tests:** 66/66 passing
**Repo:** https://github.com/SideDevEC/ECAssistantTUI.git
**Core repo:** https://github.com/SideDevEC/ECAssistantCore.git
**Namespace:** `ECAssistant.TUI.*`

## What It Is

The TUI library for ECAssistant — a full-screen terminal UI (alternate buffer, like nano/vim) that wires into the ECAssistant.Core engine. Built as a **class library** (not an executable) so it can be referenced by any .NET 8 app, including the standalone `ECAssistantConsole` launcher or future GUI hosts (e.g., ECSQL's Avalonia terminal pane).

## Project Structure

```
ECAssistantTUI/
├── ECAssistant.TUI.csproj         ← Class library, AssemblyName=ECAssistant.TUI
├── Controller/
│   └── AppController.cs            ← Application logic + layer lifecycle
├── UI/
│   ├── IGuiConsole.cs              ← Interface for terminal injection (v11.1)
│   ├── GuiConsole.cs              ← Terminal engine (render + input + delta)
│   ├── BaseLayer.cs                ← Abstract layer: buffer, scroll, ANSI helpers
│   ├── SessionLayer.cs             ← One per session, owns output buffer
│   ├── StartupLayer.cs             ← Home screen, always present
│   ├── HelpLayer.cs                ← Help overlay
│   ├── ConfigLayer.cs              ← Config inspection overlay
│   ├── LoadingIndicator.cs         ← Animated loading dots
│   └── ConsoleUiRenderer.cs        ← Bridges Core output → SessionLayer buffer
├── Tests/
│   └── ECAssistant.TUI.Tests.csproj ← 66 tests
├── ARCHITECTURE.md
├── SUMMARY.md
└── lib/ECAssistant.Core.dll        ← Built from ECAssistantCore repo
```

## Dependencies

- **ECAssistant.Core.dll** — the engine (from ECAssistantCore repo)
- **LLamaSharp** 0.27.0 — runtime dependency of Core
- **Standard .NET 8** — nothing else
- No external files (system prompts and config embedded in Core DLL)

## Build Order

1. Build `ECAssistantCore.sln` → produces `ECAssistant.Core.dll`
2. Copy DLL to `ECAssistantTUI/lib/`
3. Build `ECAssistant.TUI.csproj`

## v11.1 Changes (2026-08-16)

- **Library project** — `OutputType` changed from `Exe` to `Library`
- **IGuiConsole interface** — enables hosting TUI in external apps
- **GuiConsole implements IGuiConsole**
- **AppController** — accepts `IGuiConsole` via constructor (injectable)
- **AppController** — constructor overload with `List<EToolBase>` for external tools
- **AppController** — passes external tools to `SessionBuilder.BuildAsync`
- **BaseLayer** — uses `IGuiConsole` instead of `GuiConsole`
- **LoadingIndicator** — uses `IGuiConsole` instead of `GuiBase`
- **Namespace** — `ECAssistant.*` → `ECAssistant.TUI.*` (UI, Controller, Session)
- **Program.cs removed** — moved to ECAssistantConsole project
- **Tests renamed** — `ECAssistant.Tests` → `ECAssistant.TUI.Tests`

## v11.0 Architecture (2026-08-16)

- **AppController** — application logic, command parsing, session/layer lifecycle
- **GuiConsole** — pure terminal engine (render + input + delta rendering)
- **BaseLayer** — per-screen state: output buffers, scroll position, status bar
- Each session gets its own SessionLayer with its own buffer
- ConsoleUiRenderer writes to SessionLayer buffer, not GuiConsole
- Layer-level command parsing: BaseLayer handles /clear, SessionLayer handles session commands