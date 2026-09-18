# ECAssistant.TUI

**Terminal UI library** for the [ECAssistant](https://github.com/SideDevEC/ECAssistantLLM) local AI agent — streaming chat, session tabs, tool-call rendering, and setup wizards, all in a plain terminal. macOS, Linux, Windows.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

## Features

- **Streaming chat rendering** — token-by-token output with thinking/reasoning blocks
- **Session tabs** — multiple concurrent conversations, independent scrollback
- **Tool-call visualization** — tool invocations, approvals, and results rendered inline
- **Setup wizards** — first-run model install and provider configuration flows
- **Cross-platform** — raw ANSI/VT handling; no mouse sequences, terminal-native scrolling
- **Async-safe** — all rendering marshaled to the UI thread; no deadlocks on streaming

## Installation

```bash
dotnet add package ECAssistant.TUI
```

Packages are served from [GitHub Packages](https://github.com/SideDevEC?tab=packages) — see [ECAssistantCore](https://github.com/SideDevEC/ECAssistantCore) for the one-time source setup.

## Related repos

| Repo | What it is |
|---|---|
| [ECAssistantLLM](https://github.com/SideDevEC/ECAssistantLLM) | OpenAI-compatible local LLM server |
| [ECAssistantCore](https://github.com/SideDevEC/ECAssistantCore) | Agent engine, tools, memory, wizard |
| [ECAssistantConsole](https://github.com/SideDevEC/ECAssistantConsole) | Console application using this library |

## License

[MIT](LICENSE) — © 2026 SideDevEC
