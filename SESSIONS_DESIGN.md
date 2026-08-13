# ECAssistant — Session Architecture Design (v10.20 Draft)

**Created:** 2026-08-13  
**Status:** Design draft — agreed architecture, not yet implemented  
**Replaces:** Background agents (v10.19), NotificationQueue, EDispatch/ENotify tools

---

## Design Philosophy

A session is a session. No daemon type, no background type, no special modes. Whether a session runs once and goes idle or keeps running for hours is determined by what you ask it to do and which tools it uses. The architecture doesn't care.

---

## Core Concept

**Session** = orchestrator + context window + tool access + output buffer

- All sessions are equal
- Each session has its own orchestrator, context window, and tool access
- Sessions share ONE model instance (single GGUF in RAM)
- Only one inference runs at a time — other sessions wait their turn
- A session is either: `idle` (waiting for input) or `running` (executing something)

---

## Architecture

```
┌─────────────────────────────────────────────┐
│  UI Thread (never blocks)                    │
│                                              │
│  > _                                         │
│                                              │
│  Session list:                               │
│   [1] main       idle                        │
│   [2] watcher    running  (2m 15s)           │
│   [3] research   idle                        │
│                                              │
│  Currently viewing: [1] main                 │
│  > _                                         │
└─────────────────────────────────────────────┘
         │
         ▼
┌──────────────┐  ┌──────────────┐  ┌──────────────┐
│ Session 1    │  │ Session 2    │  │ Session 3    │
│ main         │  │ watcher     │  │ research     │
│              │  │              │  │              │
│ Orchestrator │  │ Orchestrator │  │ Orchestrator │
│ Context win  │  │ Context win  │  │ Context win  │
│ Tool access  │  │ Tool access  │  │ Tool access  │
│ Output buf   │  │ Output buf   │  │ Output buf   │
│ Prompt queue │  │ Prompt queue │  │ Prompt queue │
└──────────────┘  └──────────────┘  └──────────────┘
         │                │                │
         └────────────────┼────────────────┘
                          ▼
              ┌───────────────────────┐
              │  Shared Model         │
              │  ONE GGUF in RAM     │
              │  Inference scheduler  │
              │  (one at a time)      │
              └───────────────────────┘
                          │
              ┌───────────────────────┐
              │  Bulletin Board       │
              │  ~/ECAssistant/       │
              │  bulletin.md          │
              │  (shared file)        │
              └───────────────────────┘
```

---

## Thread Separation

**Current (v10.19.2):** Everything on one thread. `Console.ReadLine()` blocks the entire app.

**Proposed:**

- **UI thread** — reads user input, renders output, never blocks on inference
- **Session runner** — background task per active session, runs orchestrator loop
- **Inference scheduler** — queues inference requests, runs one at a time on the shared model

```
UI Thread:
  loop:
    check all sessions for new output → render active session's output
    read user input (non-blocking or async with cancellation)
    if command → execute (sessions, stop, help, etc.)
    if prompt → enqueue to active session's prompt queue

Session Runner (per session, background task):
  loop:
    dequeue prompt from prompt queue
    run orchestrator.ExecuteMultiStep(prompt)
    stream output to output buffer
    on completion → set status to idle
    on stop → cancel execution, set status to idle

Inference Scheduler:
  queue of pending inference requests from sessions
  runs one at a time (shared model, single KV cache)
  round-robin or priority-based scheduling
```

---

## UI / UX

### Commands

| Command | Description |
|---------|-------------|
| `<type request>` | Send prompt to active session (queues if busy) |
| `sessions` | List all sessions with status |
| `session <n>` / `session <name>` | Switch to session n (view its history + live output) |
| `session-new <name>` | Create a new session and switch to it |
| `session-stop <n>` | Force-stop whatever session n is doing |
| `session-close <n>` | Close and delete session n |
| `session-peek <n>` | Quick glance at session n's output without switching |
| `stop` | Force-stop active session's current execution |
| `bulletin` | Read the bulletin board |
| `bulletin-clear` | Clear the bulletin board |
| `quit` / `exit` | Stop all sessions, save transcripts, exit |

### Status Indicators

Each session shows:
- **ID** — number or name
- **Status** — `idle` or `running` (with elapsed time if running)
- **Current activity** — brief description of what it's doing (from last prompt)

### Switching Sessions

- `session 2` → switch to session 2
- Session 1 keeps running if it was busy
- You see session 2's full history + any live streaming output
- Your next prompt goes to session 2
- Switch back anytime — no state lost

### Queued Prompts

- If you type while the active session is running → prompt queues
- When current execution finishes → queued prompt starts automatically
- Visual indicator: `> (queued) your prompt here`
- `stop` cancels current execution AND clears the queue

---

## Bulletin Board

**Location:** `~/ECAssistant/bulletin.md`

- Simple markdown file, any session can read/write via EShellAgent
- No queues, no messaging code, no timing issues
- The LLM handles coordination through a file — same way it handles everything else
- System prompt includes: "You can check `~/ECAssistant/bulletin.md` for updates from other sessions"
- Sessions write findings, status updates, alerts to the bulletin
- User can read it with `bulletin` command or let the LLM check it

**Example bulletin:**
```markdown
# ECAssistant Bulletin Board

## [watcher] 2026-08-13 14:22
File changed: /src/Program.cs (modified)
Diff: 3 lines added, 1 removed

## [research] 2026-08-13 14:15
Found 7 references to RunShellAsync across 4 files.
Main call sites: EShellAgent.cs:141, BackgroundProcessManager.cs:27
```

---

## Model Sharing & Inference Scheduling

**Problem:** Multiple sessions need inference, but only one model is loaded in RAM.

**Solution:** Inference scheduler queues requests.

- One `EAgentEngine` instance, one model in RAM
- Sessions submit inference requests to a shared queue
- Scheduler runs one request at a time (single KV cache)
- Between requests, KV cache is saved/restored per session
- Sessions wait their turn — no parallel inference (CPU can't handle it anyway)

**KV Cache per Session:**
- Each session has its own context window (conversation history)
- Before inference: load that session's context into KV cache
- After inference: save KV cache state for that session
- Switching sessions = save current → load target

**Alternative (simpler but slower):**
- Each session has its own `EAgentEngine` but they share the same model file
- LLamaSharp loads model once, multiple executor instances reference it
- Each executor has its own KV cache
- Inference still serialized (one at a time) but no save/restore needed
- **This needs validation** — verify LLamaSharp supports multiple executors on one model

---

## What Gets Removed

| Component | Reason |
|-----------|--------|
| `BackgroundSubAgent` class | Sessions replace this — a session IS the background worker |
| `BackgroundAgentManager` | Session manager handles all session lifecycle |
| `NotificationQueue` | Bulletin board replaces inter-session communication |
| `ENotifyTool` | No push notifications — user checks sessions manually |
| `EDispatchTool` | No background agent spawning — user creates sessions |
| 3-phase notification display | No notifications to display |
| `PromptWithNotifications` | UI thread separation replaces this |

## What Stays

| Component | Reason |
|-----------|--------|
| `SubAgentManager` (v10.18) | Short-lived task delegation still useful within a session |
| `ESubAgentTool` | LLM can still spawn sub-agents for focused tasks |
| `AgentSession` / `SessionManager` | Base infrastructure already exists — extend it |
| All 7 original tools | Unchanged |
| Two-phase planning (StepMapper) | Unchanged |
| Parallel multi-tool execution | Unchanged |
| Self-correction, project context, memory | Unchanged |

---

## What Needs Building

1. **UI thread separation** — break `Console.ReadLine()` blocking, make UI a loop that checks session output + user input concurrently
2. **Session runner** — background task per session running the orchestrator loop independently
3. **Inference scheduler** — queue model inference requests, serialize execution, manage KV cache switching
4. **Output buffer per session** — capture streaming output so it can be rendered when viewing that session
5. **Prompt queue per session** — accept user input even when session is busy
6. **Session commands** — `sessions`, `session <n>`, `session-new`, `session-stop`, `session-close`, `session-peek`
7. **Bulletin board** — create `~/ECAssistant/bulletin.md`, add system prompt note about it
8. **KV cache save/restore** — persist and restore KV cache state per session for clean switching

---

## Migration Plan

### Phase 1: Thread Separation
- Break UI loop into async pattern (non-blocking input + output rendering)
- Session runs on background Task
- Output buffer captures streaming tokens
- Single session works as before but UI doesn't block

### Phase 2: Multi-Session
- SessionManager creates/manages multiple sessions
- Switch between sessions (save/restore KV cache or context window)
- `sessions`, `session <n>`, `session-new` commands
- Queued prompts

### Phase 3: Session Control
- `session-stop`, `session-close`, `session-peek`
- `stop` command targets active session
- Clean shutdown (stop all sessions on exit)

### Phase 4: Bulletin Board
- Create `~/ECAssistant/bulletin.md`
- Add bulletin reference to system prompt
- `bulletin` and `bulletin-clear` commands

### Phase 5: Cleanup
- Remove `BackgroundSubAgent`, `BackgroundAgentManager`, `NotificationQueue`
- Remove `EDispatchTool`, `ENotifyTool`
- Remove `PromptWithNotifications`, 3-phase notification code
- Update ARCHITECTURE.md, SUMMARY.md
- Update tests (remove bgagent tests, add session tests)

---

## Open Questions

1. **KV cache switching** — save/restore per session, or separate executor per session? Needs LLamaSharp validation.
2. **Max sessions** — hard limit? Default to 5? Configurable?
3. **Session naming** — auto-generated ("session-2") or user-named? Both?
4. **Bulletin size** — truncate when it gets large? Rotate?
5. **ESC key** — still stops active session? Or only `stop` command?
6. **Transcript per session** — each session saves its own transcript? Where? (`~/ECAssistant/transcripts/<name>.json`)

---

**Status:** Design agreed 2026-08-13. Ready for implementation planning.