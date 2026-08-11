# ECAssistant → Full AI Agent — Gap Analysis & Checklist

**Created:** 2026-08-11  
**Goal:** Identify everything ECAssistant needs to match OpenClaw-level AI agent capabilities  
**Comparison baseline:** OpenClaw (running instance, v2026.6.6)

---

## Executive Summary

ECAssistant is a solid foundation — it has a working LLM engine, tool system, orchestrator, memory, and context management. But it's roughly at **"OpenClaw v0.1"** level. To become a full AI agent like OpenClaw, it needs major additions across **10 areas**: messaging/integration, tool infrastructure, scheduling/cron, session management, multi-agent/sub-agent support, config/skill system, security/sandboxing, background work, health/heartbeat, and developer experience.

**Current capability coverage:** ~15% of OpenClaw features  
**Estimated effort:** Significant — this is essentially building a platform, not just an app.

---

## 1. 📡 Messaging & Channel Integration

**OpenClaw has:** Telegram, Discord, Signal, WhatsApp, Slack, Matrix — all configurable, with inbound/outbound routing, reactions, threads, attachments, voice notes, approval cards, inline buttons.

**ECAssistant has:** Console-only (EGuiConsole). No messaging integrations at all.

### Checklist

- [ ] **1.1** Abstract messaging channel interface (`IChannel`)
  - Send/receive text messages
  - Send/receive attachments (images, files, audio)
  - Reactions (emoji, thumbs up/down)
  - Threading/replies
  - Inline buttons / approval cards
- [ ] **1.2** Telegram channel adapter
  - Bot API integration (long-polling or webhook)
  - Message routing inbound → agent engine
  - Outbound: agent response → Telegram chat
  - Support: reactions, replies, attachments, voice notes
  - Chat type detection (direct vs group)
- [ ] **1.3** Discord channel adapter
  - Bot token auth, gateway connection
  - Slash commands, reactions, threads
- [ ] **1.4** Signal channel adapter (optional)
- [ ] **1.5** WhatsApp channel adapter (optional)
- [ ] **1.6** Channel routing engine
  - Map inbound message → correct agent session
  - Map agent response → correct channel/recipient
  - Multi-channel concurrent sessions
- [ ] **1.7** Group chat support
  - Distinguish direct vs group chats
  - Mention detection (@bot)
  - Silent mode (don't respond to every message)
  - Context metadata (chat_id, sender_id, sender_name, message_id)
- [ ] **1.8** Attachment handling
  - Receive images → pass to LLM as multimodal input
  - Receive files → save to workspace, reference in prompt
  - Send images/files back to user
- [ ] **1.9** Approval workflow
  - Inline buttons for user to approve/deny tool executions
  - Timeout handling for pending approvals
- [ ] **1.10** Rich message formatting
  - Markdown rendering per channel (Discord, Telegram, etc.)
  - Code blocks, bold/italic, links
  - Platform-specific formatting rules

---

## 2. 🔧 Tool Infrastructure & Policy

**OpenClaw has:** Policy-filtered tools, sandboxing, elevated execution, approval gates, tool permissions per agent, exec security, process management, file operations, cron, canvas, web search, browser automation.

**ECAssistant has:** 3 tools (PowerShell, FileResearch, FileAnalyzer), no sandboxing, no approval gates, no process management, no web access.

### Checklist

- [ ] **2.1** Tool permission system
  - Per-agent tool allowlists (not all tools available to all agents)
  - Tool-level config (enable/disable per agent)
  - Runtime tool filtering based on policy
- [ ] **2.2** Sandboxing
  - Filesystem sandbox (restrict to workspace, block system dirs)
  - Command sandbox (whitelist/blacklist commands)
  - Network sandbox (allow/deny per tool)
  - Elevated execution support (ask-first for dangerous ops)
- [ ] **2.3** Approval gate system
  - Tool execution → approval required for dangerous operations
  - User sees: command, tool name, args → approve/deny
  - Timeout on pending approvals
  - Approval card UI (inline buttons in Telegram, etc.)
- [ ] **2.4** Process management
  - Start background processes
  - Poll running processes for status
  - Kill/interrupt running processes
  - Stream stdout/stderr in real-time
  - Session management for long-running exec
- [ ] **2.5** Web access tools
  - HTTP fetch tool (curl-like, simple GET/POST)
  - Web search tool (DuckDuckGo, Google)
  - Browser automation tool (Playwright equivalent for .NET — Selenium or Playwright.NET)
  - JavaScript-rendered page scraping
- [ ] **2.6** File operation tools
  - Read file (with offset/limit)
  - Write file (create/overwrite)
  - Edit file (precise text replacement, multi-edit)
  - Patch file (multi-file patches)
  - Directory listing
  - File search (glob patterns)
- [ ] **2.7** Code execution tools
  - Run C# scripts (Roslyn scripting)
  - Run Python (if installed)
  - Run Node.js (if installed)
  - Compile/build .NET projects
- [ ] **2.8** External API tools
  - HTTP client tool (REST API calls, auth headers, JSON body)
  - Webhook sender
  - Rate limit awareness (Retry-After handling)
- [ ] **2.9** Canvas/presentation tool
  - Render HTML to a display surface
  - Navigate URLs
  - Screenshot capture
- [ ] **2.10** Diagram/visual tools
  - Generate SVG/HTML diagrams
  - Mermaid graph generation
- [ ] **2.11** Dynamic tool discovery
  - Scan `Tools/` directory at startup
  - Load tool DLLs via reflection (plugin system)
  - Hot-reload tools without restart
- [ ] **2.12** Tool result standardization
  - Consistent success/failure/metadata contract (partially done with EToolResult)
  - Tool timeout enforcement
  - Tool retry logic with backoff

---

## 3. ⏰ Scheduling & Cron Jobs

**OpenClaw has:** Full cron system — one-shot, recurring, cron expressions, wake events, session targeting (main/isolated/current), payload types (systemEvent, agentTurn), delivery modes (none, announce, webhook), failure alerts, run history.

**ECAssistant has:** Nothing. No scheduling at all.

### Checklist

- [ ] **3.1** Cron job scheduler
  - One-shot: `at` (ISO-8601 timestamp)
  - Recurring: `every` (interval in ms)
  - Cron expression: standard cron syntax with timezone support
- [ ] **3.2** Job payload types
  - SystemEvent (inject text as system event to main session)
  - AgentTurn (run agent with prompt in isolated session)
- [ ] **3.3** Session targeting
  - Main session (system events)
  - Isolated session (ephemeral, one-shot agent run)
  - Current session (bind to active conversation)
  - Named sessions (persistent, addressable)
- [ ] **3.4** Delivery modes
  - None (silent execution)
  - Announce (send result to chat channel)
  - Webhook (POST result to URL)
- [ ] **3.5** Job management API
  - Add/update/remove/list/get jobs
  - Enable/disable jobs
  - Run job immediately (manual trigger)
  - View run history per job
- [ ] **3.6** Wake events
  - Send wake event to session
  - Next-heartbeat mode (coalesce with heartbeat)
  - Now mode (immediate wake)
- [ ] **3.7** Failure alerts
  - Configurable: after N failures, cooldown period
  - Alert delivery to channel
  - Include skipped runs in failure count
- [ ] **3.8** Reminder system
  - User-facing reminders ("remind me in 20 minutes")
  - Natural language → cron expression
  - Reminder text formatted as system event

---

## 4. 🧵 Session Management

**OpenClaw has:** Multi-session (main, isolated, named), session history, session spawning, session listing, session status, model override per session, context compaction, session resumption.

**ECAssistant has:** Single session (one CLI loop, one transcript). No multi-session support.

### Checklist

- [ ] **4.1** Session abstraction
  - Session key (unique identifier)
  - Session type (main, isolated, named, sub-agent)
  - Session state (active, idle, archived)
  - Per-session model override
  - Per-session context window
  - Per-session transcript
- [ ] **4.2** Session lifecycle
  - Create session (main, isolated, named)
  - Resume session (load transcript + context)
  - Archive/cleanup session
  - Session timeout/idle management
- [ ] **4.3** Session history
  - Full message history per session
  - Sanitized history export (for debugging)
  - Include/exclude tool messages
  - Pagination (limit, offset)
- [ ] **4.4** Context compaction
  - Auto-compact when context window exceeds threshold
  - Summary-based compaction (LLM summarizes old context)
  - Configurable compaction strategy
  - Compaction failure recovery
- [ ] **4.5** Session spawning (sub-agents)
  - Spawn isolated child session from parent
  - Pass task/objective to child
  - Child inherits workspace
  - Context modes: isolated (clean) or fork (parent transcript)
  - Push-based completion (child notifies parent on done)
  - Cleanup modes: delete or keep
- [ ] **4.6** Session listing & status
  - List active/idle sessions
  - Filter by kind, agent, activity, label
  - Session status card (model, tokens, cost, time)
- [ ] **4.7** Cross-session messaging
  - Send message from one session to another
  - Route to parent/child
  - Wait for reply (synchronous send)

---

## 5. 🤖 Multi-Agent & Orchestration

**OpenClaw has:** Agent configuration, agent directories, agent-specific skills, sub-agent spawning, task delegation, goal/plan management.

**ECAssistant has:** Single agent, single orchestrator, no goals/plans, no sub-agent support.

### Checklist

- [ ] **5.1** Agent configuration system
  - Agent identity (name, model, system prompt, persona)
  - Agent-specific tool permissions
  - Agent-specific memory/workspace
  - Multiple agent profiles (different models for different tasks)
- [ ] **5.2** Goal & plan management
  - Create/track/complete goals
  - Multi-step plan tracking (ordered steps, status: pending/in_progress/completed)
  - Goal status: active, complete, blocked
  - Token budget per goal
- [ ] **5.3** Task delegation / sub-agent orchestration
  - Spawn sub-agent with task brief
  - Sub-agent runs in isolated session
  - Parent receives completion notification
  - Sub-agent results integrated into parent context
- [ ] **5.4** Skill system
  - Skills directory (scan for available skills)
  - Skill definition files (SKILL.md equivalent — markdown instructions)
  - Skill versioning (hash-based, re-read on change)
  - Skill discovery (agent scans skills, reads relevant ones)
  - Skill workshop (create/revise/apply/reject proposals)
- [ ] **5.5** Agent workspace isolation
  - Each agent has own workspace directory
  - Shared vs private workspace
  - File system sandbox per agent

---

## 6. ⚙️ Configuration & Model Management

**OpenClaw has:** Gateway config (JSON), config schema lookup/patch/apply, multiple LLM providers (Ollama, OpenAI, Anthropic, etc.), model fallbacks, agent defaults, channel config, tool config, secrets management, auth store, restart mechanism.

**ECAssistant has:** Single appsettings.json, single model (GGUF file), single provider (LLamaSharp/local), no secrets, no auth, no fallbacks.

### Checklist

- [ ] **6.1** Multi-provider LLM support
  - Local GGUF (LLamaSharp) — ✅ already done
  - Ollama API client
  - OpenAI API client
  - Anthropic API client
  - Custom OpenAI-compatible endpoints
  - Provider auto-detection / config
- [ ] **6.2** Model management
  - Model fallback chains (primary → secondary → tertiary)
  - Per-session model override
  - Model switching at runtime
  - Model health check (is the endpoint alive?)
- [ ] **6.3** Configuration system
  - JSON config with schema validation
  - Config hot-reload (watch file for changes)
  - Config API (get/set/patch/apply at runtime)
  - Environment variable overrides
  - Command-line argument overrides — ✅ partially done
- [ ] **6.4** Secrets management
  - Encrypted credential store (API keys, tokens)
  - Per-agent auth profiles
  - Secret injection into tools (env vars, headers)
  - No secrets in system prompt or logs
- [ ] **6.5** Gateway/server lifecycle
  - Start/stop/restart mechanism
  - Health check endpoint
  - Graceful shutdown (save state, notify channels)
  - Process daemonization (Windows service or tray app)

---

## 7. 🔒 Security & Safety

**OpenClaw has:** Sandboxing, tool policy, elevated execution, approval gates, safe-by-default, no-external-action-without-asking, trash > rm, auth store encryption, operator scopes.

**ECAssistant has:** None. PowerShell tool can execute anything. No safety gates.

### Checklist

- [ ] **7.1** Safe-by-default policy
  - Read/search/explore: allowed freely
  - Write/modify: allowed with logging
  - Delete: requires confirmation (trash > rm)
  - External actions (email, web post, network): require approval
  - System modifications: require approval
- [ ] **7.2** Tool execution approval system
  - Dangerous tool calls → approval card sent to user
  - User approves/denies via channel (button, text reply)
  - Timeout on pending approvals (configurable)
  - Deny → tool skipped, agent notified
- [ ] **7.3** Filesystem sandbox
  - Workspace boundary enforcement
  - Block access to system directories (C:\Windows, etc.)
  - Allow-list of readable/writable paths
  - Trash directory for safe deletion
- [ ] **7.4** Command sandboxing
  - PowerShell command filtering (block dangerous patterns)
  - Rate limiting (max commands per minute)
  - Output size limits (already partially in config)
  - Background process isolation
- [ ] **7.5** Network sandboxing
  - Allow/deny network access per tool
  - Domain allowlists for outbound requests
  - Proxy support
- [ ] **7.6** Audit log
  - All tool executions logged (tool, args, result, timestamp)
  - All external actions logged
  - Configurable log retention
  - Log review API

---

## 8. 🔄 Background Work & Async Operations

**OpenClaw has:** Background exec sessions, process management, yield/resume, automatic completion wake, long-running work patterns, detached task flows.

**ECAssistant has:** Synchronous only. Every tool call blocks until complete. No background work.

### Checklist

- [ ] **8.1** Background exec system
  - Start command in background → get session ID
  - Poll running session (status, output, completion)
  - Kill/interrupt running session
  - Write input to running process stdin
  - Session list (all running background tasks)
- [ ] **8.2** Async task patterns
  - Fire-and-forget (start, don't wait)
  - Start-and-poll (start, check later)
  - Start-and-yield (start, yield turn, resume on completion)
- [ ] **8.3** TaskFlow / durable jobs
  - Multi-step tasks with persistent state
  - Owner context (which session started it)
  - Wait conditions (wait for external event)
  - Child task spawning
  - Task resume after restart
- [ ] **8.4** Completion notifications
  - Background task completes → wake parent session
  - Automatic delivery of results to channel
  - Retry on failure (configurable)

---

## 9. 💓 Health, Heartbeat & Proactive Behavior

**OpenClaw has:** Heartbeat polling, HEARTBEAT.md checklist, proactive checks (email, calendar, weather, mentions), heartbeat state tracking, quiet time rules, memory maintenance during heartbeats.

**ECAssistant has:** Nothing. Fully reactive — only responds when user types.

### Checklist

- [ ] **9.1** Heartbeat system
  - Periodic wake (configurable interval, e.g., every 30 min)
  - Heartbeat checklist file (HEARTBEAT.md equivalent)
  - Heartbeat state tracking (last check times per service)
  - Quiet time rules (don't bother user at night)
- [ ] **9.2** Proactive checks
  - Email monitoring (IMAP polling or push)
  - Calendar monitoring (upcoming events)
  - Weather checking
  - Social media mention checking
  - System health checks (disk space, process status)
- [ ] **9.3** Proactive notifications
  - Important email arrived → notify user
  - Calendar event approaching → remind
  - Weather alert → notify
  - System issue detected → alert
- [ ] **9.4** Self-maintenance
  - Memory file review and consolidation
  - Workspace cleanup
  - Log rotation
  - Config validation
- [ ] **9.5** Heartbeat state persistence
  - JSON state file (lastChecks per service)
  - Survives restart
  - Configurable check frequency per service

---

## 10. 🛠️ Developer Experience & Quality

**OpenClaw has:** Test suite, skill creator, healthcheck skill, diagnostics, doctor command, status command, logging, OpenTelemetry, Prometheus metrics, docs.

**ECAssistant has:** No tests, no diagnostics, no logging framework, no metrics, no docs (beyond SUMMARY.md/ARCHITECTURE.md).

### Checklist

- [ ] **10.1** Test suite
  - Unit tests for engine, orchestrator, tools, memory
  - Integration tests (full agent loop)
  - Mock LLM for deterministic testing
  - Test fixtures (sample conversations, tool results)
  - Benchmark suite (response quality over time)
- [ ] **10.2** Logging framework
  - Structured logging (Serilog or Microsoft.Extensions.Logging)
  - Log levels (Debug, Info, Warning, Error)
  - Log to file + console + optional remote
  - Log rotation
- [ ] **10.3** Diagnostics
  - Status command (model, tokens, cost, uptime)
  - Doctor command (check config, model, tools, memory, disk)
  - Debug mode (verbose logging, prompt dumps)
  - Performance profiling
- [ ] **10.4** Metrics (optional)
  - Token usage tracking
  - Cost tracking (if using paid APIs)
  - Response time metrics
  - Tool success/failure rates
  - Uptime tracking
- [ ] **10.5** Documentation
  - User guide (how to use ECAssistant)
  - Developer guide (how to add tools, channels, skills)
  - Architecture docs (keep ARCHITECTURE.md updated)
  - API reference (if exposing HTTP API)
- [ ] **10.6** Dead code cleanup
  - Remove `SystemPrompt.json` + `EEngineSystemPrompt.cs` (dead code)
  - Remove `SystemPrompt.json` file
  - Clean up obj/ artifacts from repo
- [ ] **10.7** Real LLM summarization wiring
  - SummaryService currently injected with `null` delegate
  - Wire to actual EAgentEngine.GenerateAsync() for real summarization
  - Test summarization quality
- [ ] **10.8** DecisionLoop real implementation
  - Replace placeholder with real user input capture
  - LLM-driven task analysis (not length heuristic)
  - Real option presentation and selection
  - Integration with channel UI (inline buttons for options)

---

## Priority Ranking

### P0 — Critical (Agent is not useful without these)
| # | Item | Effort |
|---|------|--------|
| 2.1 | Tool permission system | Medium |
| 2.3 | Approval gate system | Medium |
| 2.6 | File operation tools (read/write/edit) | Low |
| 4.1 | Session abstraction | High |
| 4.4 | Context compaction (fix auto-summarize) | Low |
| 7.1 | Safe-by-default policy | Medium |
| 7.2 | Tool execution approval | Medium |
| 10.6 | Dead code cleanup | Trivial |
| 10.7 | Wire real LLM summarization | Low |

### P1 — High Priority (Major capability gaps)
| # | Item | Effort |
|---|------|--------|
| 1.1 | Messaging channel interface | Medium |
| 1.2 | Telegram channel adapter | Medium |
| 1.6 | Channel routing engine | High |
| 3.1 | Cron job scheduler | High |
| 4.2 | Session lifecycle | Medium |
| 4.5 | Session spawning (sub-agents) | High |
| 5.1 | Agent configuration system | Medium |
| 5.2 | Goal & plan management | Medium |
| 6.1 | Multi-provider LLM support | Medium |
| 6.3 | Configuration system (hot-reload, schema) | Medium |
| 8.1 | Background exec system | Medium |
| 9.1 | Heartbeat system | Medium |

### P2 — Medium Priority (Polish and power features)
| # | Item | Effort |
|---|------|--------|
| 1.3 | Discord channel adapter | Medium |
| 1.6 | Group chat support | Medium |
| 2.5 | Web access tools | Medium |
| 2.11 | Dynamic tool discovery (plugin system) | Medium |
| 3.5 | Job management API | Medium |
| 4.7 | Cross-session messaging | Medium |
| 5.3 | Task delegation / sub-agent orchestration | High |
| 5.4 | Skill system | High |
| 6.2 | Model management (fallbacks, overrides) | Medium |
| 6.4 | Secrets management | Medium |
| 7.3 | Filesystem sandbox | Medium |
| 8.3 | TaskFlow / durable jobs | High |
| 9.2 | Proactive checks | Medium |
| 10.1 | Test suite | High |
| 10.2 | Logging framework | Low |

### P3 — Nice to Have (Enhancement, not critical)
| # | Item | Effort |
|---|------|--------|
| 1.4 | Signal channel adapter | Medium |
| 1.5 | WhatsApp channel adapter | Medium |
| 1.9 | Approval workflow UI | Medium |
| 2.9 | Canvas/presentation tool | Medium |
| 2.10 | Diagram/visual tools | Low |
| 6.5 | Gateway lifecycle (Windows service) | Medium |
| 7.5 | Network sandboxing | Medium |
| 7.6 | Audit log | Low |
| 8.4 | Completion notifications | Low |
| 9.3 | Proactive notifications | Medium |
| 9.5 | Heartbeat state persistence | Low |
| 10.3 | Diagnostics | Low |
| 10.4 | Metrics | Low |
| 10.5 | Documentation | Medium |

---

## Architecture Changes Required

### Current Architecture (ECAssistant v7.1)
```
User (console) → Program.cs → AgentOrchestrator → EAgentEngine → LLamaSharp
                                      ↓
                              Tools (PowerShell, Research)
                              Memory (keyword-based)
                              ContextWindow (sliding)
```

### Target Architecture (Full AI Agent)
```
                                    ┌─────────────────────┐
                                    │   Channel Manager    │
                                    │  (Telegram, Discord, │
                                    │   Signal, Console)   │
                                    └──────────┬──────────┘
                                               │
                                    ┌──────────▼──────────┐
                                    │   Session Manager    │
                                    │ (main, isolated,     │
                                    │  named, sub-agent)   │
                                    └──────────┬──────────┘
                                               │
                    ┌──────────────────────────┼──────────────────────┐
                    │                          │                       │
          ┌─────────▼─────────┐    ┌───────────▼──────────┐   ┌────────▼────────┐
          │  Agent Engine     │    │   Orchestrator        │   │  Cron Scheduler  │
          │  (multi-provider) │    │ (goals, plans,        │   │ (jobs, wake,    │
          │  LLM abstraction   │    │  sub-agent spawning)  │   │  reminders)      │
          └─────────┬─────────┘    └───────────┬──────────┘   └────────┬────────┘
                    │                          │                       │
          ┌─────────▼──────────────────────────▼───────────────────────▼────────┐
          │                           Tool Router                               │
          │  (permissions, sandbox, approval gates, audit log)                   │
          └──────────────────────────────┬───────────────────────────────────────┘
                                           │
          ┌──────────────────┬─────────────┼──────────────┬───────────────────┐
          │                  │             │              │                   │
   ┌──────▼──────┐  ┌────────▼───────┐ ┌───▼──────┐ ┌─────▼──────┐  ┌─────────▼──────┐
   │ File Tools  │  │ Shell Tools    │ │ Web Tools│ │ API Tools  │  │ Canvas/Diagram │
   │ (read/write │  │ (PowerShell,   │ │ (search, │ │ (HTTP,     │  │ (HTML render, │
   │  edit, find)│  │  process mgmt) │ │  fetch)  │ │  webhook)  │  │  SVG, screenshots)│
   └─────────────┘  └────────────────┘ └──────────┘ └────────────┘  └────────────────┘
                                           │
          ┌───────────────────────────────▼───────────────────────────────┐
          │                    Shared Services                             │
          │  Memory (semantic) | Config | Secrets | Audit Log | Metrics    │
          └─────────────────────────────────────────────────────────────────┘
```

---

## Key Design Decisions Needed

1. **LLM Provider Abstraction:** Keep LLamaSharp for local, add ILLMProvider interface for Ollama/OpenAI/Anthropic API clients. Engine should work with any provider.

2. **Session as First-Class Object:** Sessions need their own identity, state, history, and model config. Not just a global "current conversation."

3. **Tool Router (not just executor):** Tools should go through a router that checks permissions, sandbox, approval requirements BEFORE executing. Currently tools execute directly.

4. **Channel Agnosticism:** Agent should not know or care if it's talking to Telegram, Discord, or console. Channel adapter handles format translation.

5. **Skill System (not just tools):** Skills are markdown instruction files that guide agent behavior for specific task types. Different from tools (which are code). Need a skill scanner and reader.

6. **Cron as First-Class Service:** Scheduling needs to be a core service, not an add-on. Jobs survive restarts, can be inspected/modified at runtime.

7. **Workspace per Session:** Each session should have its own workspace context. Sub-agents inherit parent workspace but can't escape it.

8. **Heartbeat as Periodic Wake:** The heartbeat should be a cron-like mechanism that wakes the agent periodically to check things, not a blocking loop.

---

## What ECAssistant Already Has (✅ Don't Rebuild)

- ✅ LLM inference via LLamaSharp (local GGUF models)
- ✅ XML-style structured response parsing (`<thinking>`, `<toolcall>`, `<output>`)
- ✅ Multi-step orchestration loop with fail-fast
- ✅ Sliding context window with real token counting
- ✅ Conversation transcript persistence (JSON)
- ✅ Persistent memory (keyword-based, across sessions)
- ✅ Tool system with abstract base class (EToolBase)
- ✅ Config file (appsettings.json) with nested structure
- ✅ CLI command system (quit, help, tools, memory, etc.)
- ✅ Abstract UI layer (EGuiBase → EGuiConsole)
- ✅ PowerShell execution tool
- ✅ File research/scanning tool
- ✅ ANSI color output helpers

---

**Status:** Initial gap analysis complete  
**Next Step:** Pick a P0 item to start implementing, or prioritize the list together  
**Added:** 2026-08-11