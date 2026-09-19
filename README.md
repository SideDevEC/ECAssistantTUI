# ECAssistant.TUI

> **The terminal UI layer of ECAssistant.** Streaming chat, session tabs, tool-call rendering, and setup wizards — a reusable library for building assist-first AI experiences in a plain terminal. macOS, Linux, Windows.

[![NuGet](https://img.shields.io/nuget/v/ECAssistant.TUI)](https://www.nuget.org/packages/ECAssistant.TUI)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

ECAssistant exists to **assist** people — and the TUI is how that assistance looks and feels: every token streamed live, every tool call rendered inline with its approval and result, nothing hidden behind a spinner. If you're building your own host on [ECAssistantCore](https://github.com/SideDevEC/ECAssistantCore), this library gives you that experience for free.

Part of [ECAssistant](https://github.com/SideDevEC/ECAssistant) — your models, your keys, your machine. MIT.

## Features

- **Streaming chat rendering** — token-by-token output with thinking/reasoning blocks
- **Session tabs** — multiple concurrent conversations, independent scrollback
- **Tool-call visualization** — tool invocations, approvals, and results rendered inline
- **Setup wizards** — first-run model install and provider configuration flows
- **Cross-platform** — raw ANSI/VT handling; no mouse sequences, terminal-native scrolling
- **Async-safe** — all rendering marshaled to the UI thread; no deadlocks on streaming

## Install

Public on nuget.org — no token, no auth:

```bash
dotnet add package ECAssistant.TUI
```

## See it in action

[ECAssistantConsole](https://github.com/SideDevEC/ECAssistantConsole) is the reference host built on this library — run it and you're looking at the TUI doing its thing.

## The ecosystem

| Repo | What it is |
|---|---|
| [ECAssistant](https://github.com/SideDevEC/ECAssistant) | Start here — overview & docs |
| [ECAssistantCore](https://github.com/SideDevEC/ECAssistantCore) | The embeddable agent library |
| [ECAssistantLLM](https://github.com/SideDevEC/ECAssistantLLM) | OpenAI-compatible local LLM server |
| [ECAssistantConsole](https://github.com/SideDevEC/ECAssistantConsole) | Reference host / end-user CLI |

## License

[MIT](LICENSE) — © 2026 SideDevEC
