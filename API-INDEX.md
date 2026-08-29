# API-INDEX.md — ECAssistantTUI

Generated: 2026-08-29T08:19:28.915120+00:00
Packages: 2  |  Types: 22

---

## ECAssistantTUI (18 types, ~3392 LOC)

- 🔵 IAppController  (ECAssistantTUI)
- 🔵 IGuiConsole  (ECAssistantTUI)
- 🔵 ITerminalOutput  (ECAssistantTUI)
- 🟡 AppController : IAppController  (ECAssistantTUI)  deps: [IGuiConsole, EAgentConfig, string, string, string, ILogger, IGuiConsole, EAgentConfig, string, string, string, ILogger, List, IGuiConsole, EAgentConfig, string, string, string, ILogger, List, BackgroundProcessManager, FileWatcherService]
- 🟡 BaseLayer  (ECAssistantTUI)
- 🟡 BaseLayerAnsiTests  (ECAssistantTUI)
- 🟡 BaseLayerBufferTests  (ECAssistantTUI)
- 🟡 ConfigLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor]
- 🟡 ConsoleTerminalOutput : ITerminalOutput  (ECAssistantTUI)
- 🟡 ConsoleUiRenderer : IOutputListener, IDisposable  (ECAssistantTUI)  deps: [SessionLayer, EColor, Func, Func]
- 🟡 ConsoleUiRendererTests  (ECAssistantTUI)
- 🟡 EGuiConsole : EGuiBase, IGuiConsole  (ECAssistantTUI)  deps: [ITerminalOutput]
- 🟡 FirstRunWizard  (ECAssistantTUI)  deps: [IGuiConsole, EColor, ModelCatalogDocument, ModelInstallerService, FirstRunStatus, RemoteProviderSetupWriter? remoteWriter =, VectorMemorySetupWriter? vectorMemoryWriter =, EmbeddingSetupWriter? embeddingWriter =]
- 🟡 HelpLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor, string]
- 🟡 LayerTests  (ECAssistantTUI)
- 🟡 LoadingIndicator : IDisposable  (ECAssistantTUI)  deps: [IGuiConsole, EColor]
- 🟡 SessionLayer : BaseLayer  (ECAssistantTUI)  deps: [string, string label =]
- 🟡 StartupLayer : BaseLayer  (ECAssistantTUI)  deps: [EColor]

## Tests (4 types, ~574 LOC)

- 🟡 BaseLayerAnsiTests  (Tests)
- 🟡 BaseLayerBufferTests  (Tests)
- 🟡 ConsoleUiRendererTests  (Tests)
- 🟡 LayerTests  (Tests)
