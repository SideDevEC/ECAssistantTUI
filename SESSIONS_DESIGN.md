# ECAssistant — Session Architecture Design (v10.20 — Revised 2026-08-13)

**Created:** 2026-08-13  
**Revised:** 2026-08-13 (full rewrite based on design discussion)  
**Status:** Design — agreed architecture, ready for implementation  
**Replaces:** Background agents (v10.19), NotificationQueue, EDispatch/ENotify tools, bulletin board

---

## Design Philosophy

A session is a session. No daemon type, no background type, no special modes. Whether a session runs once and goes idle or keeps running for hours is determined by what you ask it to do and which tools it uses. The architecture doesn't care.

**Core principles agreed in this revision:**

1. **Fully isolated sessions** — each session has its own engine, context window, tools, memory, and output buffer. No shared state between sessions.
2. **No inter-session communication** — sessions do not know about each other. If communication is needed in the future, it's a tool-level concern (e.g. an `ESessionMessage` tool), not an architecture concern.
3. **No bulletin board** — removed from the design. Sessions are independent. Coordination between sessions, if ever needed, is a tool-level feature added later.
4. **Shared model weights, separate KV caches** — one GGUF model loaded in RAM. Each session has its own `EAgentEngine` instance with its own KV cache, referencing the shared model weights. Only one inference runs at a time; other sessions wait their turn.
5. **Session is the UI gateway** — the session object has `Write()` / `WriteLine()` / `WriteRaw()` methods. The session is passed down to all components (orchestrator, engine, tools). All output goes through the session, never directly to the console.
6. **Output states, not colors** — the session emits semantic output states (Info, Success, Warning, Error, Dim, Bold, Raw, System). The UI maps states to colors. Different UIs (console, web canvas) can render differently without the session knowing.
7. **File-based output buffer** — each session's output history is a JSONL file (append-only, persistent, scrollable). The UI reads the file when switching to a session and renders the full history. Live updates are pushed to the attached UI in real-time.
8. **Prompt queue** — if a session is running, new prompts queue. After execution completes, the next queued prompt starts automatically. If queue is empty, session goes idle.
9. **Session stop is scoped** — `session.Stop()` stops only that session's current execution. Other sessions keep running. Global stop (`quit`/`exit`) stops all sessions.
10. **UI can manage prompt queue** — the UI can view the prompt queue of the active session and delete prompts before they execute.

---

## Core Concept

**Session** = own engine + own orchestrator + own context window + own tools + own memory + own output buffer + own prompt queue

- All sessions are equal — no types, no special modes
- Each session has its own `EAgentEngine` instance (own KV cache, own context window, own transcript)
- All `EAgentEngine` instances share the same loaded model weights (one GGUF in RAM)
- Only one inference runs at a time — other sessions wait their turn (serialized inference)
- A session is either: `idle` (waiting for input) or `running` (executing something)
- Sessions are fully independent — no shared state, no communication channels

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│  UI Thread (never blocks)                            │
│                                                      │
│  Session list:                                       │
│   [1] main       idle                                │
│   [2] watcher    running  (2m 15s)  queue: 1         │
│   [3] research   idle                                │
│                                                      │
│  Viewing: [1] main                                   │
│  > _                                                 │
└─────────────────────────────────────────────────────┘
         │
         ▼
┌────────────────┐  ┌────────────────┐  ┌────────────────┐
│ Session 1      │  │ Session 2      │  │ Session 3      │
│ main           │  │ watcher        │  │ research       │
│                │  │                │  │                │
│ EAgentEngine   │  │ EAgentEngine   │  │ EAgentEngine   │
│ (own KV cache) │  │ (own KV cache) │  │ (own KV cache) │
│ Orchestrator   │  │ Orchestrator   │  │ Orchestrator   │
│ ContextWindow  │  │ ContextWindow  │  │ ContextWindow  │
│ Tools (own)    │  │ Tools (own)    │  │ Tools (own)    │
│ Memory (own)   │  │ Memory (own)   │  │ Memory (own)   │
│ Output buffer  │  │ Output buffer  │  │ Output buffer  │
│   → .jsonl     │  │   → .jsonl     │  │   → .jsonl     │
│ Prompt queue   │  │ Prompt queue   │  │ Prompt queue   │
│ Runner thread  │  │ Runner thread  │  │ (idle — no     │
│ (idle)         │  │ (running)      │  │  thread)        │
└────────────────┘  └────────────────┘  └────────────────┘
         │                │                │
         └────────────────┼────────────────┘
                          ▼
              ┌───────────────────────┐
              │  Shared Model Weights │
              │  ONE GGUF in RAM     │
              │  (loaded once)        │
              │                       │
              │  Inference Scheduler  │
              │  (one at a time)      │
              └───────────────────────┘
```

---

## Session as UI Gateway

The session object is the **sole interface** for all output from its components. The orchestrator, engine, tools — everything gets a reference to the session and calls `session.Write()`, `session.WriteLine()`, `session.WriteRaw()`.

**The session does NOT extend `EGuiBase`.** The session has its own write methods. The `EGuiBase` abstraction stays for the console/canvas UI layer — the UI uses `EGuiBase` to render, but the session does not inherit from it.

```
Session (has Write, WriteLine, WriteRaw methods)
  ├── passed to → Orchestrator
  ├── passed to → EAgentEngine
  ├── passed to → every Tool
  └── all components call session.Write/WriteLine/WriteRaw
```

### Output States

The session emits semantic **output states**, not colors. The UI maps states to visual rendering.

| State | Meaning | Console color (example) |
|-------|---------|------------------------|
| `Info` | Informational message | Cyan |
| `Success` | Operation succeeded | Green |
| `Warning` | Caution / non-critical | Yellow |
| `Error` | Failure / critical | Red |
| `Dim` | De-emphasized text | Dark gray |
| `Bold` | Emphasized text | White bold |
| `Raw` | Plain text / token stream | No color |
| `System` | System-level message | Magenta |

Different UIs can map these states to different visual styles (ANSI colors, CSS classes, etc.) without the session knowing anything about rendering.

---

## File-Based Output Buffer

Each session has a JSONL file for its output history:

**Location:** `~/ECAssistant/.sessions/<session-key>/ui_output.jsonl`

**Format:** JSON Lines — one JSON object per line, append-only, never cleared on session switch.

### JSONL Entry Types

```jsonl
{"type":"stream","text":"<lm><thinking>Let me analyze...","state":"raw","ts":"2026-08-13T19:01:28Z"}
{"type":"line","text":"[Tool] EShellAgent: OK (45ms)","state":"success","ts":"2026-08-13T19:01:29Z"}
{"type":"line","text":"","state":"info","ts":"2026-08-13T19:01:30Z"}
{"type":"line","text":"[Orchestrator] LLM gave direct answer.","state":"info","ts":"2026-08-13T19:01:32Z"}
```

| Field | Description |
|-------|-------------|
| `type` | `"stream"` (accumulated token output) or `"line"` (a discrete line) |
| `text` | The output text content |
| `state` | Output state (Info, Success, Warning, Error, Dim, Bold, Raw, System) |
| `ts` | ISO-8601 timestamp |

### Auto-Flushing StringBuilder Buffer

The session has an internal `StringBuilder` (`_streamBuffer`) and a `_currentState` tracker. The buffer self-manages based on state changes and `WriteLine` calls:

```
Session internal state:
  _streamBuffer = StringBuilder    // accumulates WriteRaw/Write tokens
  _currentState = null              // tracks current output state
  _outputFile = StreamWriter       // open append handle to .jsonl file

WriteRaw(token):
  → _streamBuffer.Append(token)
  (no file I/O, no flush — just accumulate)

Write(text, state):
  if state != _currentState:
    → flush _streamBuffer as {"type":"stream","text":"...","state":_currentState}
    → _currentState = state
  → _streamBuffer.Append(text)
  (if buffer was empty and state same, just append — no flush)

WriteLine(text, state):
  → if _streamBuffer not empty:
      flush as {"type":"stream","text":"...","state":_currentState}
  → write {"type":"line","text":text,"state":state}
  → _currentState = state
  (if text is empty → {"type":"line","text":"","state":state} = linebreak)

Flush():
  if _streamBuffer not empty:
    → write {"type":"stream","text":"...","state":_currentState} to file
    → _streamBuffer.Clear()
```

**Why this works:**
- Token streaming during generation → tokens accumulate in memory, no file I/O
- State change (e.g. Raw → Success) → forces flush, one `stream` entry written to file
- `WriteLine()` → forces flush first (if buffer not empty), then writes the line entry
- Empty `WriteLine("")` → flush + linebreak entry
- File stays manageable: one entry per generation stream, one entry per status line
- Full history preserved — switching sessions and back renders everything

### File Lifecycle

- Created when session is created
- Append-only for the session's lifetime
- Never cleared on UI switch (only cleared when session is closed/deleted)
- Survives restart — can resume viewing a session's history
- UI can paginate/scroll through the file

---

## UI Attachment & Rendering

### UI Registration

The UI attaches to one session at a time as the "active renderer":

```
session.AttachUi(IUiRenderer renderer)   // register for live updates
session.DetachUi()                        // unregister
```

`IUiRenderer` is an interface the console/canvas UI implements:
```csharp
interface IUiRenderer
{
    void OnOutput(OutputEntry entry);    // called by session on flush/write
    void OnQueueChanged(List<string> queue);  // called when prompt queue changes
    void OnStateChanged(SessionState state);  // called when session goes idle/running
}
```

### Session Write Flow (always two steps)

```
Session.Write/WriteLine/WriteRaw:
  1. Buffer logic (accumulate or flush — see above)
  2. On flush or line write → append to .jsonl file (always)
  3. If UI attached → notify IUiRenderer with the entry (real-time push)
```

The session ALWAYS writes to the file. If a UI is attached, it ALSO gets a live notification. The file is the source of truth.

### UI Selects a Session (`session <n>`)

```
1. Detach from current session (if any)
2. Read the session's .jsonl file → parse all entries → render to console/canvas
   (full scrollable history, with correct state→color mapping)
3. Attach to the new session (register IUiRenderer)
4. From now on, new entries are pushed live to the console/canvas
```

### UI Switches Away

```
1. Detach from session (stop live notifications)
2. Buffer file keeps accumulating — nothing lost
3. Attach to the newly selected session (repeat above)
```

### UI Rendering

The UI has a state→color mapping:

```
Info    → Cyan
Success → Green
Warning → Yellow
Error   → Red
Dim     → Dark gray
Bold    → White bold
Raw     → No color
System  → Magenta
```

When reading the JSONL file or receiving a live push:
- Parse `type` and `state`
- Map `state` to color (ANSI for console, CSS for canvas)
- `stream` entries → render as text blocks (inline, no forced line break unless contained)
- `line` entries → render as a line (with line break)

Different UIs (console, web canvas, future terminal) can map states to different visual styles.

---

## Prompt Queue & Execution Flow

### Session States

```
Idle    — no execution running, waiting for input
Running — orchestrator is executing, prompt queue may have items
```

### Prompt Flow

```
UI calls session.Prompt(input):
  if session is Idle:
    → start execution in own thread immediately
    → session state = Running
  if session is Running:
    → add input to PromptQueue
    → session keeps running current execution

Execution completes:
  → check PromptQueue:
    if queue has items:
      → dequeue next prompt
      → start execution (stays Running)
    if queue is empty:
      → session state = Idle
```

```
Session lifecycle:

  Idle ──Prompt()──▶ Running (own thread)
                        │
                        ├─ Prompt() ──▶ PromptQueue (keeps running)
                        │
                        └─ execution done ──▶ queue empty?
                                                ├─ yes → Idle
                                                └─ no  → dequeue & run next
```

### Runner Thread

Each session runs execution in its own thread (background `Task`):

```
Session Runner (per session):
  loop:
    dequeue prompt from prompt queue (blocks if empty)
    set state = Running
    run orchestrator.ExecuteMultiStep(prompt)
    stream output via session.Write/WriteLine/WriteRaw
    on completion or stop:
      if queue not empty → dequeue next, continue loop
      if queue empty → set state = Idle, exit loop (thread ends)
    on next Prompt() while idle → new thread starts
```

The UI thread never blocks on session execution. It only reads user input and renders output.

### Inference Serialization

Only one inference runs at a time (shared model, one GPU/CPU). When multiple sessions need inference:

- Sessions submit inference requests to a shared scheduler
- Scheduler runs one request at a time
- Other sessions' runner threads block on the scheduler until their turn
- From the user's perspective this feels concurrent — generations are fast on an 8B model

This does NOT require KV cache save/restore. Each session has its own `EAgentEngine` with its own KV cache. The scheduler just ensures only one `GenerateAsync` call runs at a time (e.g. via a `SemaphoreSlim(1,1)`).

---

## Session Stop

### Per-Session Stop

`session.Stop()` — stops only that session's current execution:

- Cancels the session's execution token (like ESC does now, but scoped)
- Orchestrator bails out, cleans up, session goes to Idle
- Prompt queue is preserved (stop = stop current execution, not clear queue)
- Other sessions keep running unaffected

```
Session.Stop():
  → cancel execution token
  → orchestrator detects cancellation, returns
  → session state = Idle
  → prompt queue intact (next prompt can start if queue not empty)
```

### Global Stop

`quit` / `exit` — stops all sessions gracefully:

```
SessionManager.StopAll():
  → for each session: session.Stop()
  → wait for all runner threads to complete
  → save all transcripts
  → clean up resources
  → exit
```

---

## UI Prompt Queue Management

When viewing a session, the UI shows the prompt queue:

```
Session: [2] research — Running (2m 15s)
Queue: 1 prompt pending
  1. "Also check the API rate limits"
> _
```

The UI can:
- **View** the queue of the active session
- **Delete** a queued prompt before it executes (remove by index)
- **Clear** all queued prompts

Session API:
```csharp
session.GetQueue()                 // returns List<string> of queued prompts
session.RemoveFromQueue(int index)  // remove a queued prompt by index
session.ClearQueue()                // clear all queued prompts
```

When the queue changes, the session notifies the attached UI via `IUiRenderer.OnQueueChanged()`.

---

## Thread Separation

**Current (v10.19.2):** Everything on one thread. `Console.ReadLine()` blocks the entire app.

**New design:**

- **UI thread** — reads user input, renders output, never blocks on inference
- **Session runner thread** — per-session background task running the orchestrator loop
- **Inference scheduler** — `SemaphoreSlim(1,1)` ensuring one inference at a time on the shared model

```
UI Thread:
  loop:
    render active session's live output (from IUiRenderer notifications)
    read user input (non-blocking or async with cancellation)
    if command → execute (sessions, stop, help, queue, etc.)
    if prompt → call activeSession.Prompt(input)

Session Runner (per session, background task):
  loop:
    dequeue prompt (or exit if idle and queue empty)
    set state = Running, notify UI
    run orchestrator.ExecuteMultiStep(prompt)
    output flows through session.Write/WriteLine/WriteRaw → file + UI
    on completion or stop:
      if queue not empty → dequeue next, continue
      if queue empty → set state = Idle, notify UI, exit thread

Inference Scheduler:
  SemaphoreSlim(1,1)
  each GenerateAsync call acquires, runs, releases
  sessions wait their turn — serialized inference
```

---

## UI Commands

| Command | Description |
|---------|-------------|
| `<type request>` | Send prompt to active session (queues if busy) |
| `sessions` | List all sessions with status + queue count |
| `session <n>` | Switch to session n (render full history + live updates) |
| `session-new <name>` | Create a new session and switch to it |
| `session-stop <n>` | Stop session n's current execution (queue preserved) |
| `session-close <n>` | Close and delete session n (cleanup files) |
| `session-peek <n>` | Quick glance at session n's last few output lines |
| `session-queue` | Show active session's prompt queue |
| `session-queue-remove <i>` | Remove prompt i from active session's queue |
| `session-queue-clear` | Clear active session's prompt queue |
| `stop` | Stop active session's current execution |
| `quit` / `exit` | Stop all sessions, save transcripts, exit |

### Status Display

Each session shows:
- **ID** — number or name
- **Status** — `idle` or `running` (with elapsed time if running)
- **Queue** — number of queued prompts (if > 0)
- **Last activity** — brief description (from last prompt)

```
[1] main       idle
[2] watcher    running  (2m 15s)  queue: 1
[3] research   idle
```

---

## Session File Structure

Each session has its own directory:

```
~/ECAssistant/.sessions/
├── main/
│   ├── ui_output.jsonl      ← output buffer (JSONL, append-only)
│   └── transcript.json      ← conversation transcript (crash recovery)
├── watcher/
│   ├── ui_output.jsonl
│   └── transcript.json
└── research/
    ├── ui_output.jsonl
    └── transcript.json
```

---

## What Gets Removed

| Component | Reason |
|-----------|--------|
| `BackgroundSubAgent` class | Sessions replace this — a session IS the background worker |
| `BackgroundAgentManager` | SessionManager handles all session lifecycle |
| `NotificationQueue` | No inter-session communication — sessions are isolated |
| `ENotifyTool` | No push notifications |
| `EDispatchTool` | No background agent spawning — user creates sessions |
| 3-phase notification display | No notifications |
| `PromptWithNotifications` | UI thread separation replaces this |
| **Bulletin board** | Removed — sessions are fully isolated, no shared files |

## What Stays

| Component | Reason |
|-----------|--------|
| `SubAgentManager` (v10.18) | Short-lived task delegation within a session — still useful |
| `ESubAgentTool` | LLM can still spawn sub-agents for focused tasks within a session |
| `AgentSession` / `SessionManager` | Base infrastructure exists — rewrite for full isolation |
| All 7 original tools | Unchanged (but receive session reference for output) |
| Two-phase planning (StepMapper) | Unchanged |
| Parallel multi-tool execution | Unchanged |
| Self-correction, project context, memory | Unchanged (per-session) |
| `EGuiBase` / `EGuiConsole` | Stays for the UI layer — console rendering |
| `EColor` | Stays for console color mapping |

---

## What Needs Building

1. **Rewrite `AgentSession`** — own `EAgentEngine` instance (own KV cache), own orchestrator, own tools, own memory, own output buffer, own prompt queue, own write methods
2. **Session write methods** — `Write()`, `WriteLine()`, `WriteRaw()` with output states, auto-flushing StringBuilder buffer, JSONL file output
3. **File-based output buffer** — JSONL writer (append), JSONL reader (for UI history rendering)
4. **`IUiRenderer` interface** — UI attaches/detaches, receives live `OnOutput` / `OnQueueChanged` / `OnStateChanged` notifications
5. **UI thread separation** — break `Console.ReadLine()` blocking, async input + output rendering loop
6. **Session runner** — background `Task` per session running orchestrator loop independently
7. **Inference scheduler** — `SemaphoreSlim(1,1)` serializing `GenerateAsync` calls across sessions
8. **Prompt queue** — `Queue<string>` per session, auto-dequeue after execution, UI management (view/remove/clear)
9. **Session stop** — per-session cancellation token, `session.Stop()` cancels only that session
10. **Session commands** — `sessions`, `session <n>`, `session-new`, `session-stop`, `session-close`, `session-peek`, `session-queue`, `session-queue-remove`, `session-queue-clear`
11. **UI state→color mapping** — console renderer maps output states to ANSI colors
12. **Session directory structure** — `~/ECAssistant/.sessions/<key>/` with `ui_output.jsonl` and `transcript.json`
13. **Pass session to all components** — orchestrator, engine, tools all receive session reference for output (instead of `Program.Gui`)
14. **Global stop on exit** — `SessionManager.StopAll()` stops all sessions, saves transcripts, cleans up

---

## Migration Plan

### Phase 1: Session Rewrite
- Rewrite `AgentSession` with own `EAgentEngine`, orchestrator, tools, memory
- Implement session write methods (Write/WriteLine/WriteRaw with states)
- Implement file-based output buffer (JSONL writer + reader)
- Implement auto-flushing StringBuilder buffer
- Implement `IUiRenderer` interface
- Pass session reference to all components (replace `Program.Gui` calls)

### Phase 2: UI Thread Separation
- Break `RunAgentLoop` into async UI loop (non-blocking input + output rendering)
- Implement session runner (background Task per session)
- Implement inference scheduler (`SemaphoreSlim(1,1)`)
- Implement prompt queue (enqueue, auto-dequeue, UI management)
- Implement session stop (per-session cancellation token)

### Phase 3: Session Commands & Multi-Session
- `SessionManager` creates/manages multiple sessions
- `sessions`, `session <n>`, `session-new` commands
- `session-stop`, `session-close`, `session-peek` commands
- `session-queue`, `session-queue-remove`, `session-queue-clear` commands
- `stop` command targets active session
- `quit`/`exit` stops all sessions gracefully

### Phase 4: Cleanup
- Remove `BackgroundSubAgent`, `BackgroundAgentManager`, `NotificationQueue`
- Remove `EDispatchTool`, `ENotifyTool`
- Remove `PromptWithNotifications`, 3-phase notification code
- Remove old `AgentSession` / `SessionManager` (replaced by new implementation)
- Remove `EGuiTestHarness` session references (update for new session-based output)
- Update `ARCHITECTURE.md`, `SUMMARY.md`
- Update tests (remove bgagent tests, add session tests)

---

## Open Questions

1. **Max sessions** — hard limit? Default to 5? Configurable in `appsettings.json`?
2. **Session naming** — auto-generated ("session-2") or user-named? Both?
3. **ESC key** — still stops active session? Or only `stop` command? (Proposal: ESC = stop active session, same as `stop` command)
4. **Session resumption** — on restart, detect existing `.sessions/` dirs and offer to resume?
5. **Output file rotation** — cap at max size? Or unlimited (sessions are finite conversations)?
6. **Test harness update** — `EGuiTestHarness` needs to implement `IUiRenderer` instead of `EGuiBase` for session-based testing

---

**Status:** Design agreed and finalized 2026-08-13. Ready for implementation.