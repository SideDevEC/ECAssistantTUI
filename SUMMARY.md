# ECAssistant App — Summary

**Updated:** 2026-08-16 (v10.25)
**Build:** 0 errors, 0 warnings
**Tests:** 50/50 passing (App only)
**Repo:** https://github.com/LLamaDudeX/ECAssistant.git
**Core repo:** https://github.com/LLamaDudeX/ECAssistantCore.git

## What It Is

The console application frontend for ECAssistant — a local, offline AI coding assistant. Built in C# .NET 8. Provides a full-screen terminal UI (alternate buffer, like nano/vim) that wires into the ECAssistant.Core engine DLL.

## Project Structure

```
ECAssistant.sln
├── ECAssistant.csproj              ← Console exe (references Core.dll)
├── Tests/ECAssistant.Tests.csproj  ← 50 UI tests
├── ARCHITECTURE.md                 ← this app's architecture
├── SUMMARY.md                      ← this file
├── Program.cs                      ← entry point + command loop
├── UI/
│   ├── EGuiConsole.cs              ← full-screen TUI with delta rendering
│   ├── IGuiLayer.cs                ← layer interface for overlays
│   ├── HelpLayer.cs                ← help screen overlay
│   ├── SessionLayer.cs             ← base session layer
│   ├── LoadingIndicator.cs         ← animated loading dots
│   └── ConsoleUiRenderer.cs        ← bridges Core output → EGuiConsole
└── Tests/
    ├── UI/                         ← EGuiConsole, buffer, ANSI, layer tests
    └── Session/                    ← ConsoleUiRenderer tests
```

## Dependencies

- **ECAssistant.Core.dll** — the engine (from ECAssistantCore repo)
- **Standard .NET 8** — nothing else
- No LLamaSharp packages
- No external files (system prompts and config embedded in Core DLL)

## Build Order

1. Build `ECAssistantCore.sln` first → produces `ECAssistant.Core.dll`
2. Build `ECAssistant.sln` → picks up the DLL via `HintPath`

## Key Stats
- **Project type:** Console exe (net8.0)
- **.cs files:** 7 (Program.cs + 6 UI files)
- **Test files:** 5
- **Tests:** 50 passing
- **Dependencies:** ECAssistant.Core.dll only

## Recent Changes (2026-08-16)
- **v10.25: Repo split + zero dependencies**
  - Core extracted to separate repo (`ECAssistantCore`)
  - App references Core.dll only — no LLamaSharp, no content files
  - 857 Core tests moved to Core repo, 50 UI tests remain
  - Delta rendering in EGuiConsole (screenbuffer cache diffing)
- **v10.24.2: All tests passing** — 907/907 (before split)
- **Previous:** EGuiConsole screenbuffer rewrite, layer system, session UI