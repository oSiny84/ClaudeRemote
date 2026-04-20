# ClaudeRemote — Session Handoff Memory

**Last updated**: 2026-04-17
**Purpose**: Resume development on another computer without losing context.

---

## 1. Project at a glance

**ClaudeRemote** = Android-from-anywhere remote control for Claude Desktop on a Windows PC.

```
┌──────────────────────┐      WebSocket (8765)       ┌──────────────────────┐
│  Android (Compose)   │◄───── messages (JSON) ─────►│  Windows host (WPF)  │
│  ─ MainViewModel.kt  │                             │  ─ MessageProcessor  │
│  ─ ChatScreen        │      HTTP (Kestrel, 8766)   │  ─ FileServerService │
│  ─ FileBrowserDialog │◄────── /download ───────────┤  ─ ClaudeAutomation  │
└──────────────────────┘                             │        Service       │
                                                     └──────────┬───────────┘
                                                                │ UIAutomation
                                                                ▼
                                                     ┌──────────────────────┐
                                                     │   Claude Desktop     │
                                                     │   (Electron GUI)     │
                                                     └──────────────────────┘
```

- **Windows host** (`C:/D_Develop/ClaudeRemote.Windows/`): C# WPF + .NET 8. Scrapes Claude Desktop via UIAutomation, exposes commands over WebSocket, serves files over Kestrel.
- **Android client** (`C:/D_Develop/ClaudeRemote.Android/`): Kotlin + Jetpack Compose + Coroutines + Ktor WebSocket.
- **Shared docs** (`C:/D_Develop/docs/`): PM charter, UI maps, phase reports.
- **Protocol** (`C:/D_Develop/protocol/`): WebSocket message shape specs.

---

## 2. Build + run

### Windows

```bash
# Kill running instance (it locks the exe)
taskkill /F /IM ClaudeRemote.Windows.exe

# Build
cd C:/D_Develop/ClaudeRemote.Windows
dotnet build ClaudeRemote.Windows/ClaudeRemote.Windows.csproj -c Release

# Run
C:/D_Develop/ClaudeRemote.Windows/ClaudeRemote.Windows/bin/Release/net8.0-windows/ClaudeRemote.Windows.exe
```

Logs: `C:/D_Develop/ClaudeRemote.Windows/ClaudeRemote.Windows/bin/Release/net8.0-windows/logs/clauderemote-YYYYMMDD.log`

### Android

Build via Android Studio (no `gradlew` in this repo). APK installs to device; the app then targets the Windows host IP + port 8765.

---

## 3. What's done (Phases 1–13)

| Phase | Summary | Report |
|---|---|---|
| 1–10 | Core WebSocket pipeline, session/project/mode switching, input/output, action buttons | `docs/reports/Phase*_*.md` |
| **11** | Usage dashboard scraping — opens footer popup, parses context window + 5-hour/weekly/Sonnet limits + model+plan | `Phase11_Windows_WorkReport.md` |
| **12** | File transfer — WebSocket `browse_files` / `request_download` + Kestrel HTTP server on port 8766 | `Phase12_Windows_WorkReport.md` |
| 12-Hotfix-1 | Drive entries returned with `type="directory"` and explicit `path` field | `Phase12_Hotfix1_Windows_WorkReport.md` |
| 12-Hotfix-2 | HttpListener → **Kestrel** (no admin, no URL ACL) + URL logging | `Phase12_Hotfix2_Windows_WorkReport.md` |
| **13** | Structured chat output — `messages[]` of `{role, content}` + legacy `content` fallback; fixes inline-code loss, middle-of-response truncation, spurious mid-paragraph `\n` | `Phase13_Windows_WorkReport.md` |

**User confirmed working** (2026-04-17):
- Usage dashboard popup (context 7%, 5h 84%, weekAll 13%, weekSonnet 1%, etc.)
- Project + session selection (exclusive single-select after fix)
- File browser + download over LAN (Kestrel-based, no admin)
- Chat bubble rendering via `messages[]` on Android

---

## 4. Key technical patterns (read before changing code)

### 4.1 UIA serialization gate
`_uiaGate` (SemaphoreSlim(1,1), 30 s timeout) serializes every public UIA call. Cross-process UIA RPCs deadlock if run concurrently with the monitor loop. **Every** new public service method MUST wrap its body in `WithUiaGateAsync`.

### 4.2 Cached subtree walking
`CollectTextsCached` pattern: single `CacheRequest` with `TreeScope.Subtree` + `AutomationElementMode.None`, then walk the local cache. **~10–30× faster** than raw cross-process walking. Used by `GetOutputAsync`, `CollectChatMessages`, etc. Use this for any tree walk > ~200 nodes.

### 4.3 Reverse DFS for Radix portals
`WalkFindFirstReverse` / `WalkCollectReverse` walk children last-first. Radix UI portals (popups, menus) attach late in sibling order, so reverse DFS hits them quickly without exhausting the node budget inside the chat tree.

### 4.4 Walker view consistency
`RawWalker.GetParent()` must be used on elements discovered via RawView. Mixing with `ControlViewWalker.GetParent()` returns `null` on nodes that are invisible in the control view. Burned by this in Phase 11.

### 4.5 Structural validation to reject false positives
When anchoring on keyword text (e.g. "Context window"), the same keyword can appear in chat content. Validate the returned subtree structurally — check for co-present landmarks AND small node count — before trusting it. See `LooksLikePopupContainer` in `ClaudeAutomationService.cs`.

### 4.6 One-shot diagnostic dumps
When a UI-scraping path is flaky, add a `_thingDumped` static flag + `DumpXxxDiagnostic()` that logs the relevant subtree once per process. Ask the user to trigger the flow and share the log. Worked great for `[ChatDump]` (Phase 13) and `[UsageDiag]` (Phase 11).

---

## 5. Claude Desktop DOM map (verified 2026-04-17, v1.2581.0+)

Chat scroll container (`h-full overflow-y-auto overflow-x-hidden [contain:strict]`) top-level children:

| Class marker | ControlType | Meaning |
|---|---|---|
| `rounded-r7 rounded-bl-[var(--r1)]` | Group | **User bubble** |
| `text-body text-assistant-primary break-words` | Group | Assistant paragraph |
| `text-assistant-primary list-disc` / `list-decimal` | List | Assistant bullet / numbered list |
| `group/tool` | Button | **Tool invocation** — `Name` has full label ("Read 2 files, searched code") |
| `group/btn` | Button | UI chrome: Copy message / Pin as chapter / Rewind / Fork / Show more — SKIP |
| `text-code text-assistant-primary` | Button | **Inline code chip** — wrap `Name` in backticks |

Full dump with ancestor chains: run once via `[ChatDump]` log (set `_chatStructureDumped = false` and restart).

### Usage popup (footer "Usage: context X%, plan Y%" button)
- Anchor 1: Text starting with `"Context window"`
- Anchor 2: Text starting with `"5-hour limit"` / `"Weekly"` / `"Sonnet only"`
- Anchor 3: Text containing `"resets"`
- Container: small subtree (≤80 Text nodes) containing all three — see `LooksLikePopupContainer`.

### Sidebar projects/sessions
- Nav scroll: class `dframe-nav-scroll`
- Project label Button class starts with `group/label `
- Session row Group class starts with `group relative rounded-`
- Expanded detection: `ExpandCollapsePattern` (aria-expanded) > `IsOffscreen` check > session-group count (least reliable)

---

## 6. Protocol reference

### 6.1 Commands (Android → Windows)

| Action | Payload | Purpose |
|---|---|---|
| `send_input` | `{text}` | Send text to Claude input |
| `get_output` | `{scope}` — "full" / "latest" / "summary" | Scrape chat |
| `get_sessions` | — | List sessions in active project |
| `select_session` | `{sessionId}` — "session_N" | Click a session |
| `add_session` | — | New session |
| `get_projects` | — | List projects |
| `select_project` | `{projectId}` — "project_N" | **Exclusive** expand (collapses others) |
| `get_usage` | — | Inline usage (button only) |
| `get_usage_dashboard` | — | Full popup scrape |
| `click_button` | `{buttonId}` | Click an action button |
| `browse_files` | `{path}` — empty = drives | Directory listing |
| `request_download` | `{path}` | Returns HTTP URL |

### 6.2 Responses / pushes (Windows → Android)

**Phase 13 structured output**:
```json
{
  "type": "content",
  "action": "output_update",           // or "output_full"
  "payload": {
    "scope": "latest",
    "messages": [
      {"role": "user", "content": "..."},
      {"role": "tool", "content": "Ran 2 commands"},
      {"role": "assistant", "content": "..."}
    ],
    "content": "...\n\n...\n\n...",    // legacy, messages joined with \n\n
    "isStreaming": false
  }
}
```

**Phase 12 file browse** (every entry has explicit `path`):
```json
{
  "action": "browse_files",
  "payload": {
    "success": true,
    "currentPath": "C:\\D_Develop",
    "parentPath": "C:\\",
    "entries": [
      {"name": "C: (Local Disk)", "path": "C:\\", "type": "directory", "size": 0, "modified": ""},
      {"name": "bin", "path": "C:\\D_Develop\\bin", "type": "directory", ...},
      {"name": "app.apk", "path": "C:\\D_Develop\\app.apk", "type": "file", "size": 17100294, ...}
    ],
    "fileServerPort": 8766
  }
}
```

**Phase 12 download URL**:
```json
{
  "action": "request_download",
  "payload": {
    "success": true,
    "url": "http://192.168.0.10:8766/download?path=C%3A%5C...",
    "fileName": "app.apk",
    "fileSize": 17100294,
    "fileSizeText": "16.3 MB"
  }
}
```

---

## 7. Key file locations

### Windows
- `Services/ClaudeAutomationService.cs` — THE file. ~3500 lines. All UIA scraping.
- `Services/MessageProcessor.cs` — WebSocket message router + response builders.
- `Services/FileServerService.cs` — Kestrel HTTP server (Phase 12 Hotfix 2).
- `Services/WebSocketServerService.cs` — Fleck-based WS server.
- `Services/SessionManager.cs` — high-level session/project state.
- `Models/ChatMessage.cs` — Phase 13 `{Role, Content}` DTO.
- `Models/ClaudeMessage.cs` — root message envelope + `MessageAction` constants.
- `App.xaml.cs` — DI container setup.
- `ViewModels/MainViewModel.cs` — WPF UI binding.

### Android
- `app/src/main/java/com/clauderemote/viewmodel/MainViewModel.kt` — single god-VM, holds StateFlows for everything.
- `app/src/main/java/com/clauderemote/ui/screens/ChatScreen.kt` — Phase 13 bubble renderer.
- `app/src/main/java/com/clauderemote/ui/components/MessageBubble.kt` — Phase 13 bubble component (user / assistant / tool variants).
- `app/src/main/java/com/clauderemote/ui/components/FileBrowserDialog.kt` — Phase 12.
- `app/src/main/java/com/clauderemote/data/model/Messages.kt` — protocol DTOs incl. `ChatMessage`, `FileEntry`.

### Shared docs
- `docs/PM.md`, `docs/PM_Phases.md` — charter + roadmap
- `docs/WindowsProgrammer.md`, `docs/AndroidProgrammer.md` — role guides
- `docs/ClaudeUI_Map.md` (+ `_v1.md`) — UI element reference
- `docs/ClaudeUI_UsagePanel.md`, `ClaudeUI_UsagePopup.md` — usage-specific maps
- `docs/reports/` — per-phase work reports

---

## 8. Known follow-ups / open issues

### Phase 13 — chat output polish
- [ ] **Code fences** (``` ```), multi-line code blocks — haven't verified how Claude Desktop exposes them in UIA. If rendering breaks, re-run `[ChatDump]` and extend `CollectInlineContent`.
- [ ] **Markdown headings** (`# ## ###`) currently flatten to plain paragraphs (safety-net fallback in `ProcessTopLevelChild`). Future: detect heading class and emit `#` prefix.
- [ ] **MonitorLoop streaming** path emits only `content` over `OutputChanged` event (string, not messages). On-demand `get_output` sends `messages[]`. Could plumb `List<ChatMessage>` through the event for bubble-aware live updates (Phase 13.1).

### Monitor cadence / misc
- `_pollingIntervalMs = 500` in `ClaudeAutomationService` — trade-off between freshness and UIA load.
- Action button polling also runs inside the monitor loop under the same gate.

### Android
- ChatScreen has a scope toggle (latest / full) in the top bar — server respects it.
- Bubble expand/collapse state for tool rows is kept external to avoid loss on chunk reassembly.

---

## 9. Debugging cheat sheet

### UI looks wrong / stuff missing from chat
1. Set `_chatStructureDumped = false` (or restart the app — flag resets) and trigger one `get_output`.
2. Grep log for `[ChatDump]` — you'll see the full top-level children + buttons + text+ancestor chains.
3. Compare current Claude Desktop markup to the DOM map in §5.

### Usage popup returns null / wrong data
1. Grep log for `[UsageDiag]` — fires on popup open failure.
2. Check `FindUsagePopupRoot` candidate-validation (needs context-window + second-landmark + resets text in ≤80-text subtree).

### File download 404
1. Hit `http://<host-ip>:8766/` — should return plain-text banner. If 404 → Kestrel didn't bind. Check log for `[FileServer] Kestrel started on port 8766 (all interfaces)`.
2. `request_download` log line now prints the URL — copy-paste into browser to reproduce.

### Project selection flip-flops
Confirm `DetectProjectExpanded` isn't relying on session-group count alone — must try `ExpandCollapsePattern` first. Exclusive selection semantics: see `SelectProjectAsync`.

### Build locks exe
`taskkill /F /IM ClaudeRemote.Windows.exe` before every build.

### Tests don't build
`ClaudeRemote.Tests/Program.cs` + `Phase5Test.cs` have mock `IClaudeAutomationService` + `IFileServerService` — every interface addition on either service needs to be reflected in both mocks.

---

## 10. External dependencies (for a fresh machine)

### Windows host
- .NET 8 SDK (pulls ASP.NET Core shared framework for Kestrel)
- Claude Desktop installed (v1.2581.0+)
- Windows firewall: port 8765 (WS) + 8766 (HTTP) opened for private networks

### Android client
- Android Studio Koala+
- Phone on same LAN as Windows PC (tested with USB tether, WiFi both work)

### NuGet packages (already in csproj)
- `CommunityToolkit.Mvvm` 8.2.2
- `Fleck` 1.2.0 (WebSocket)
- `Microsoft.Extensions.DependencyInjection` 8.0.1
- `Serilog` 3.1.1 + File + Console sinks
- `FrameworkReference Microsoft.AspNetCore.App` (Kestrel — no download, uses shared runtime)

---

## 11. Conventions

- **Commit language**: English commit messages OK, Korean also fine (existing mix).
- **Work reports**: `docs/reports/Phase{N}[_Hotfix{M}]_{Windows|Android}_WorkReport.md` after each task.
- **Logs**: Serilog at Debug level, daily rolling, 7-day retention. `[Tag]` prefix for each subsystem.
- **Emoji in logs**: `✓` success, `✗` failure, `⚠` warning, `←` inbound command, `→` outbound response.
- **No auto-commit**: the assistant never runs `git commit` / `git push` without explicit user instruction.

---

## 12. Most recent user-visible state (2026-04-17)

- Windows app: latest build at `bin/Release/net8.0-windows/ClaudeRemote.Windows.exe`
- WebSocket port: 8765 (default)
- File server port: 8766 (Kestrel, all-interfaces bind, no admin)
- Monitor polling: 500 ms
- Confirmed working: usage dashboard, project/session exclusive select, file browse + download, chat-bubble rendering via `messages[]`
