# Tests.API.md

Types: 4  |  LOC: 574  |  ~167 tokens

---

### Class: BaseLayerAnsiTests
> Unit tests for BaseLayer static methods (ANSI helpers).
Cross-package deps: ECAssistant.Core, ECAssistant.TUI.UI

### Class: BaseLayerBufferTests
> Tests for BaseLayer output buffer management (AddOutputLine, _outputLines).
Cross-package deps: ECAssistant.Core, ECAssistant.TUI.UI

### Class: ConsoleUiRendererTests
> Tests for ConsoleUiRenderer — verifies it writes to SessionLayer's buffer
Cross-package deps: ECAssistant.Core, ECAssistant.Core.Session, ECAssistant.TUI.Session, ECAssistant.TUI.UI

### Class: LayerTests
> Tests for BaseLayer subclasses (SessionLayer, HelpLayer).
Cross-package deps: ECAssistant.Core, ECAssistant.TUI.UI
