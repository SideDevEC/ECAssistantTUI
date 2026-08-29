# API-INDEX.md — ECAssistantTUI

Generated: 2026-08-29T22:01:49.874026+00:00
Packages: 2  |  Types: 25

---

## ECAssistantTUI (20 types, ~3205 LOC)

- 🔵 IAppController  (ECAssistantTUI)
- 🔵 IGuiConsole  (ECAssistantTUI)
- 🔵 ITerminalOutput  (ECAssistantTUI)
- 🟡 AnsiInputParser  (ECAssistantTUI)
- 🟡 AnsiInputParserTests  (ECAssistantTUI)
- 🟡 AppController : IAppController  (ECAssistantTUI)  deps: [IGuiConsole, EAgentConfig, string, string, string, ILogger, IGuiConsole, EAgentConfig, string, string, string, ILogger, List, IGuiConsole, EAgentConfig, string, string, string, ILogger, List, BackgroundProcessManager, FileWatcherService, IAiSetupResetter? setupResetter =]
- 🟡 BaseLayer  (ECAssistantTUI)
- 🟡 BaseLayerAnsiTests  (ECAssistantTUI)
- 🟡 BaseLayerBufferTests  (ECAssistantTUI)
- 🟡 ConfigLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor]
- 🟡 ConsoleTerminalOutput : ITerminalOutput  (ECAssistantTUI)
- 🟡 ConsoleUiRenderer : IOutputListener, IDisposable  (ECAssistantTUI)  deps: [SessionLayer, EColor, Func, Func]
- 🟡 ConsoleUiRendererTests  (ECAssistantTUI)
- 🟡 EGuiConsole : EGuiBase, IGuiConsole  (ECAssistantTUI)  deps: [ITerminalOutput]
- 🟡 HelpLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor, string]
- 🟡 LayerTests  (ECAssistantTUI)
- 🟡 LoadingIndicator : IDisposable  (ECAssistantTUI)  deps: [IGuiConsole, EColor]
- 🟡 SessionLayer : BaseLayer  (ECAssistantTUI)  deps: [string, string label =]
- 🟡 StartupLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor]
- 🟡 TuiSetupUi : ISetupUi  (ECAssistantTUI)  deps: [IGuiConsole]

## Tests (5 types, ~688 LOC)

- 🟡 AnsiInputParserTests  (Tests)
- 🟡 BaseLayerAnsiTests  (Tests)
- 🟡 BaseLayerBufferTests  (Tests)
- 🟡 ConsoleUiRendererTests  (Tests)
- 🟡 LayerTests  (Tests)
