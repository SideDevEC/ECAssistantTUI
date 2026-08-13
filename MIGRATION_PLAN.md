# Migration Plan: EShellAgent + Cross-Platform Support

## Phase 1: Rename EPowerShellAgent → EShellAgent

### Step 1.1: Rename files and folders
- [ ] `Tools/EPowerShell/EPowerShellAgent.cs` → `Tools/EShell/EShellAgent.cs`
- [ ] `Tools/EPowerShell/` folder → `Tools/EShell/`
- [ ] Update namespace: `ECAssistant.Tools.PowerShell` → `ECAssistant.Tools.Shell`

### Step 1.2: Rename class and tool name
- [ ] Class: `EPowerShellAgent` → `EShellAgent`
- [ ] Tool name string: `"EPowerShellAgent"` → `"EShellAgent"`
- [ ] Constructor: `EPowerShellAgent(string workingDirectory)` → `EShellAgent(string workingDirectory)`
- [ ] All `override string Name => "EPowerShellAgent"` → `"EShellAgent"`

### Step 1.3: Update all references in code
- [ ] `Tools/ToolPolicy.cs`: `_permissions["EPowerShellAgent"]` → `_permissions["EShellAgent"]`
- [ ] `Program.cs`: Wherever `EPowerShellAgent` is instantiated/registered
- [ ] `Orchestrator.cs`: Any direct references (if any)
- [ ] `Engine/EAgentEngine.cs`: Tool registration if referenced by name

### Step 1.4: Update SystemPrompt.md
- [ ] All `EPowerShellAgent` references → `EShellAgent`
- [ ] Keep PowerShell examples as-is (Windows syntax — will split in Phase 3)

### Step 1.5: Update comments and docstrings
- [ ] `"PowerShell Agent Tool"` → `"Shell Agent Tool"`
- [ ] `"Can read/write/copy/move/delete files"` description stays
- [ ] Update `ToSystemPromptBlock()` — keep PowerShell examples for now
- [ ] `RunPowerShellAsync` method name → `RunShellAsync` (internal)

### Step 1.6: Build and verify
- [ ] `dotnet build` — 0 errors
- [ ] Run on Windows — tool still executes PowerShell correctly
- [ ] Verify LLM calls `EShellAgent` with same syntax

---

## Phase 2: OS-Aware Shell Execution

### Step 2.1: Detect OS in EShellAgent
- [ ] Add `private static readonly bool IsWindows = OperatingSystem.IsWindows();`
- [ ] Add `private static readonly bool IsMacOS = OperatingSystem.IsMacOS();`

### Step 2.2: Branch execution logic
- [ ] Windows path: Keep current `powershell.exe` + `.ps1` temp script (unchanged)
- [ ] Mac path: Use `zsh -c` with temp `.sh` script or inline command
- [ ] Mac script wrapper: `#!/bin/zsh\nset -e\n{command}\n` (equivalent of $ErrorActionPreference)

### Step 2.3: Update RunShellAsync (renamed from RunPowerShellAsync)
- [ ] Branch on OS for `FileName`: `powershell.exe` vs `/bin/zsh`
- [ ] Branch on OS for `Arguments`: `-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{temp}"` vs `-c "{command}"` or `-l "{temp}.sh"`
- [ ] Branch on OS for temp script extension: `.ps1` vs `.sh`
- [ ] Branch on OS for error handling wrapper in script content

### Step 2.4: Update ToSystemPromptBlock() with OS-aware examples
- [ ] Windows examples (current):
  - `Get-Content`, `Set-Content`, `New-Item`, `Copy-Item`, `Get-ChildItem`, `Select-String`
- [ ] Mac examples (zsh):
  - `cat`, `echo "..." >`, `touch`, `cp`, `ls`, `grep`
  - `mkdir -p`, `rm`, `mv`, `head -n`, `tail -n`, `wc -l`
- [ ] Description: "Full filesystem and shell command execution" (drop "PowerShell")

### Step 2.5: Build and verify
- [ ] `dotnet build` — 0 errors
- [ ] Run on Mac — `EShellAgent` executes zsh commands
- [ ] Run on Windows — `EShellAgent` still executes PowerShell (regression check)

---

## Phase 3: Dual System Prompts

### Step 3.1: Create SystemPrompt.Windows.md
- [ ] Copy current `SystemPrompt.md` → `SystemPrompt.Windows.md`
- [ ] Replace all `EPowerShellAgent` → `EShellAgent`
- [ ] Keep all PowerShell cmdlet examples
- [ ] Keep Windows-specific file paths (`C:\`, backslashes) in examples

### Step 3.2: Create SystemPrompt.Mac.md
- [ ] Copy `SystemPrompt.Windows.md` → `SystemPrompt.Mac.md`
- [ ] Replace PowerShell cmdlets with zsh equivalents in ALL examples:
  - `Get-Content File.cs` → `cat File.cs`
  - `Set-Content -Path notes.txt -Value 'Hello'` → `echo 'Hello' > notes.txt`
  - `New-Item -ItemType File -Name test.txt` → `touch test.txt`
  - `Copy-Item A.cs B.cs` → `cp A.cs B.cs`
  - `Get-ChildItem -Filter *.cs` → `ls *.cs`
  - `Select-String -Pattern "TODO" -Path *.cs` → `grep "TODO" *.cs`
  - `Remove-Item file.txt` → `rm file.txt`
  - `New-Item -ItemType Directory -Name folder` → `mkdir folder`
- [ ] Replace Windows paths with Unix paths in examples (`/Users/...` style)
- [ ] Keep ALL format rules, tag structure, thinking/toolcall/output patterns IDENTICAL

### Step 3.3: Update SystemPrompt loading in EAgentEngine.cs
- [ ] Current: loads `SystemPrompt.md` from working directory
- [ ] New logic:
  ```csharp
  var promptFile = OperatingSystem.IsMacOS() ? "SystemPrompt.Mac.md"
                 : OperatingSystem.IsWindows() ? "SystemPrompt.Windows.md"
                 : "SystemPrompt.md";  // fallback
  ```
- [ ] Keep `SystemPrompt.md` as fallback (copy of Windows version or generic)
- [ ] Log which prompt was loaded

### Step 3.4: Update content includes in .csproj
- [ ] Add `SystemPrompt.Windows.md` and `SystemPrompt.Mac.md` as content
- [ ] Keep `SystemPrompt.md` as fallback
- [ ] All with `CopyToOutputDirectory = PreserveNewest`

### Step 3.5: Build and verify
- [ ] `dotnet build` — 0 errors
- [ ] On Mac: verify `SystemPrompt.Mac.md` is loaded
- [ ] On Windows: verify `SystemPrompt.Windows.md` is loaded
- [ ] LLM uses correct shell syntax for the OS

---

## Phase 4: .csproj Cross-Platform

### Step 4.1: Change TargetFramework
- [ ] `net8.0-windows` → `net8.0`
- [ ] Verify LLamaSharp packages support `net8.0` (they do — CPU backend is cross-platform)

### Step 4.2: Conditional backend packages
- [ ] Windows: `LLamaSharp.Backend.Cuda12` (GPU) + `LLamaSharp.Backend.Cpu` (fallback)
- [ ] Mac: `LLamaSharp.Backend.Vulkan` (Metal) + `LLamaSharp.Backend.Cpu` (fallback)
- [ ] Linux: `LLamaSharp.Backend.Cuda12` + `LLamaSharp.Backend.Cpu`
- [ ] Use MSBuild conditionals:
  ```xml
  <PackageReference Include="LLamaSharp.Backend.Cuda12" Condition="'$(OS)' == 'Windows'" />
  <PackageReference Include="LLamaSharp.Backend.Vulkan" Condition="'$(RuntimeIdentifier)' contains 'osx'" />
  ```

### Step 4.3: Build and verify
- [ ] `dotnet build` on Mac — 0 errors
- [ ] `dotnet build` on Windows — 0 errors (regression)
- [ ] Model loads correctly on Mac (CPU backend at minimum)

---

## Phase 5: Testing on Mac

### Step 5.1: Basic smoke test
- [ ] Start app on Mac with a small model
- [ ] Ask "what day is today" — verify full flow (toolcall → result → output)
- [ ] Verify token stream shows `<lm>` and `</lm>`

### Step 5.2: File creation test
- [ ] Ask "create 3 files: a.txt, b.txt, c.txt"
- [ ] Verify LLM uses zsh syntax (`touch a.txt; touch b.txt; touch c.txt`)
- [ ] Verify all 3 files created
- [ ] Verify sub-task tracking completes all steps

### Step 5.3: File creation + content test
- [ ] Ask "create 3 files and add today's date to one of them"
- [ ] Verify LLM batches in one toolcall
- [ ] Verify all sub-tasks advance to completed
- [ ] Verify LLM gives correct final output

### Step 5.4: Format retry test
- [ ] Trigger a format retry (model outputs without tags)
- [ ] Verify KV cache rewind works
- [ ] Verify retry succeeds

### Step 5.5: ESC stop test
- [ ] Press ESC during generation
- [ ] Verify cache rebuild works
- [ ] Verify next command starts clean

---

## Execution Order

```
Phase 1 (rename) → Phase 2 (OS-aware) → Phase 3 (dual prompts) → Phase 4 (csproj) → Phase 5 (test)
```

Each phase is independently committable. Phase 1 can be tested on Windows. Phase 4 enables Mac builds. Phases 2+3 together enable Mac execution.

## Risk Notes

- LLamaSharp Vulkan backend on Mac (Metal) may need testing — CPU backend is safe fallback
- zsh error handling differs from PowerShell ($ErrorActionPreference vs set -e)
- Mac temp dir is `/tmp/` (same as Linux) — `Path.GetTempPath()` handles this
- `Process.Start` on Mac needs `/bin/zsh` full path (no PATH lookup without shell)
- `Console.KeyAvailable` works on Mac terminal but may behave differently in some IDE terminals