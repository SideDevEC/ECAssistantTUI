# Tests.API.md

Types: 5  |  LOC: 690  |  ~208 tokens

---

### Class: AnsiInputParserTests
> Regression tests for the ANSI input parser — covers the mouse-scroll garbage bug:
Cross-package deps: ECAssistant.TUI.Input, Xunit

### Class: BaseLayerAnsiTests
> Unit tests for BaseLayer static methods (ANSI helpers).
Cross-package deps: ECAssistant.Core, ECAssistant.TUI.UI

### Class: BaseLayerBufferTests
> Tests for BaseLayer output buffer management (AddOutputLine, OutputLines).
Cross-package deps: ECAssistant.Core, ECAssistant.TUI.UI

### Class: ConsoleUiRendererTests
> Tests for ConsoleUiRenderer — verifies it writes to SessionLayer's buffer
Cross-package deps: ECAssistant.Core, ECAssistant.Core.Session, ECAssistant.TUI.Session, ECAssistant.TUI.UI

### Class: LayerTests
> Tests for BaseLayer subclasses (SessionLayer, HelpLayer).
Cross-package deps: ECAssistant.Core, ECAssistant.TUI.UI
