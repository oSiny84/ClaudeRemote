# ClaudeRemote — Android Session Handoff Memory

**Last updated**: 2026-04-20
**Purpose**: Resume Android-side development on another computer without losing context.
**Role**: Android Programmer (Kotlin / Jetpack Compose / Material3)

---

## 1. Project at a glance

**ClaudeRemote** = Android-from-anywhere remote control for Claude Desktop on a Windows PC.

```
┌──────────────────────┐      WebSocket (8765)       ┌──────────────────────┐
│  Android (Compose)   │◄───── messages (JSON) ─────►│  Windows host (WPF)  │
│  ─ MainViewModel     │                             │  ─ MessageProcessor  │
│  ─ ChatScreen        │      HTTP (Kestrel, 8766)   │  ─ FileServerService │
│  ─ FileBrowserDialog │◄────── /download ───────────┤  ─ ClaudeAutomation  │
└──────────────────────┘                             └──────────┬───────────┘
                                                                │ UIAutomation
                                                                ▼
                                                     ┌──────────────────────┐
                                                     │   Claude Desktop     │
                                                     │   (Electron GUI)     │
                                                     └──────────────────────┘
```

- **Android client** (`C:/D_Develop/ClaudeRemote.Android/`): Kotlin + Jetpack Compose + OkHttp + kotlinx.serialization. **This is my repo.**
- **Windows host** (`C:/D_Develop/ClaudeRemote.Windows/`): handled by the C# programmer. Scrapes Claude Desktop via UIAutomation, exposes WebSocket, serves files via Kestrel.
- **Shared docs** (`C:/D_Develop/docs/`): PM charter, role definitions (`AndroidProgrammer.md`), UI maps, handoff memos, phase reports.
- **Protocol** (`C:/D_Develop/protocol/`): WebSocket message shape specs (shared authority).

---

## 2. Build + run

### Environment (Windows)
```
JAVA_HOME        = C:/Program Files/Android/Android Studio/jbr
ANDROID_SDK_ROOT = C:/Users/<USER>/AppData/Local/Android/Sdk
Gradle           = 8.5 (wrapper regenerated; cached under ~/.gradle/wrapper/dists/)
Kotlin           = 1.9.22 (compose compiler 1.5.8)
Compose BOM      = 2024.02.00 (Compose UI 1.6.2, Material3 1.2.x)
minSdk = 26, targetSdk = 34
```

### Build command
```bash
cd C:/D_Develop/ClaudeRemote.Android
./gradlew assembleDebug
# APK → app/build/outputs/apk/debug/app-debug.apk
```

Typical build times: 30 s cached, 1–2 min cold, 2+ min on first dependency fetch.

### local.properties (per machine, git-ignored)
```
sdk.dir=C\:\\Users\\<USER>\\AppData\\Local\\Android\\Sdk
```

### Install + log
```bash
adb install -r app-debug.apk
adb logcat -s FileBrowser FileBrowserVM   # Android Studio UI uses tag:FileBrowser package:mine
```

---

## 3. What's done (Phase 7–13)

| Phase | Summary | Report |
|---|---|---|
| 7 | Tool permission request UI — red/amber/green buttons + HIGH notification + haptic | `Phase7_Android_WorkReport.md` |
| 8–9 | (Mostly ambient — mode cleanup to Code-only, chunk reassembly, session limits) | — |
| 10 | Folder-tree project UI — `ProjectInfo.expanded`, Projects→Sessions reordering, section header shows active project | `Phase10_Android_WorkReport.md` |
| 11 | Usage dashboard dialog — `ContextWindow / fiveHourLimit / weeklyAllModels / weeklySonnetOnly / model / plan`, 3-tier progress colors | `Phase11_Android_WorkReport.md` |
| 12 | File browser + download — `ModalBottomSheet` + `browse_files` / `request_download` + `DownloadManager` | `Phase12_Android_WorkReport.md` |
| 12-Hotfix-1 | Drive-tap bug — lenient `isDirectoryLike()` + adopted server-authoritative `FileEntry.path` | `Phase12_Hotfix1_Android_WorkReport.md` |
| **13** | Chat-bubble UI + Markwon markdown + terminal-wrap preprocessor | `Phase13_Android_WorkReport.md` |

Phase 13 is the most recent and went through several iterations — see §5.

**User confirmed working (2026-04-20)**:
- Connection + session/project selection (exclusive single-select)
- Code-mode only (Chat/Cowork removed)
- Chat bubbles (user right / assistant left / tool collapsible)
- Markdown rendering: tables, task lists, code blocks, inline code, headings, blockquotes, auto-linkify
- Terminal wrap → space, paragraph boundaries (`\n\n`) → visible
- Permission request UI with haptic + high-priority notification
- Usage dashboard with color-coded progress bars
- File browser (drive listing, navigation, Back)
- **File download over LAN → saves to `/storage/emulated/0/Download/`** (after server fixed `HttpListener` → Kestrel hostname binding)

---

## 4. Android package map

```
com.clauderemote
├── ClaudeRemoteApp                 — Application; singleton WebSocketClient, AppSettings, 3 notification channels
├── MainActivity                    — Single Activity + Compose host
├── data/
│   ├── model/Messages.kt           — Protocol constants + data classes:
│   │                                  ClaudeMessage, SessionInfo, ProjectInfo(expanded),
│   │                                  ButtonInfo, UsageDashboard/ContextWindow/UsageLimit,
│   │                                  FileEntry(name,path,type,size,modified),
│   │                                  ChatMessage(role,content),
│   │                                  ConnectionState, ClaudeStatus
│   └── settings/AppSettings        — DataStore Preferences (host/port/autoConnect/notify/theme)
├── network/WebSocketClient         — OkHttp WebSocket, auto-reconnect, exposes rawMessages Flow + connectionState
├── service/WebSocketService        — Foreground Service, 3 notification kinds (service/output/alert), permission push
├── viewmodel/MainViewModel         — Owns ALL state, parses messages, sends commands, manages lifecycle
└── ui/
    ├── theme/                      — M3 color schemes
    ├── screens/
    │   ├── MainScreen              — Bottom nav (Chat | Manage), wires onDisconnect/onExitApp
    │   ├── ChatScreen              — LazyColumn of MessageBubble + action buttons + quick commands + input
    │   ├── ManageScreen            — Projects (top) → Sessions (bottom) + Usage/Browse buttons + dialogs
    │   └── SettingsScreen          — Server config, theme, Exit App button
    └── components/
        ├── MessageBubble           — 3 variants (user / assistant / tool-collapsible)
        ├── MarkdownView            — Markwon wrapper + terminal wrap preprocessor + theme binding
        ├── UsageDashboardDialog    — AlertDialog with Current usage + Weekly limits + footer(model/plan)
        └── FileBrowserDialog       — ModalBottomSheet + file list + download confirmation AlertDialog
```

### State-flow ownership (MainViewModel)
- Connection: `serverHost / serverPort / connectionState / statusMessage`
- Claude: `claudeStatus / claudeOutput / chatMessages / outputScope / isLoading`
- Sessions: `sessions`
- Projects: `projects` (with per-item `expanded`)
- Buttons: `actionButtons / actionButtonsPrompt / buttonCategory`
- Usage: `usageDashboard / usageDashboardLoading / showUsageDashboard`
- Files: `fileEntries / currentPath / parentPath / fileBrowserLoading / showFileBrowser`
- Input: `commandText / commandHistory`
- Settings: `autoConnect / notifyOutput / notifyDisconnect / themeMode`
- Events: `snackbarEvent` (SharedFlow one-shot)

---

## 5. Phase 13 detail — the part that required most iteration

### 5.1 Markdown library journey
1. **mikepenz:multiplatform-markdown-renderer-m3:0.28.0** — build passed, **crashed at first render**. Root cause: the 0.28.0 ABI targets Compose 1.7+/Kotlin 2.0, but we ship Compose 1.6.2 / Kotlin 1.9.22. Signature drift → likely `NoSuchMethodError`. Don't retry without downgrading to ≤0.17.x.
2. **com.github.jeziellago:compose-markdown:0.5.4** (JitPack) — ran but was cosmetically weak; no plugin control.
3. **Markwon direct integration** (current, final) — `io.noties.markwon:*` 4.6.2, seven plugins: core, ext-tables, ext-strikethrough, ext-tasklist, linkify, html, image. Full `MarkwonTheme` + `TableTheme` customization, bound to M3 colors at composable call time.

### 5.2 Terminal wrap ↔ markdown mismatch
Claude Desktop wraps long lines at the terminal width with real `\n`. CommonMark treats single `\n` as soft break = space. Three fixes:
1. First attempt: SoftLineBreak visitor always emits newline → but then every wrap becomes a visible break.
2. Added a **client-side preprocessor** (`unwrapTerminalLines` in `MarkdownView.kt`) that collapses wrap-`\n` to space while preserving paragraph breaks (`\n\n`), block starts (lists, headings, quotes, tables, fences), and fenced-code contents. Plus a sentence-punctuation heuristic.
3. **Removed the punctuation heuristic** after the C# dev promised to send authoritative `\n\n` paragraph separators. Current preprocessor rules:
   - inside fence → keep `\n`
   - either side blank → keep `\n` (paragraph)
   - next line is a block element (`-` `*` `+` `•` `#` `>` `|` numbered-list `` ``` ``) → keep `\n`
   - otherwise → replace with space

### 5.3 Bubble styling
- `user`: right-aligned, `primaryContainer`, shape 16/16/16/4 (tail bottom-end), `widthIn(max = 320.dp)`
- `assistant`: left-aligned, **solid** `surfaceVariant` (not translucent, so code backgrounds don't muddle), shape 16/16/4/16 (tail bottom-start), `fillMaxWidth(0.96f)`, Markwon inside
- `tool`: `OutlinedCard`, header tappable to toggle expansion, `rememberSaveable(content)` survives scroll

---

## 6. Current state (2026-04-20)

### Working (user-verified)
- Every feature listed in §3 "User confirmed working"

### Deprecation warnings (non-functional)
- `Icons.Default.Send` / `.ArrowBack` / `.InsertDriveFile` / `.KeyboardArrowRight` → should move to `Icons.AutoMirrored.Filled.*`. Low priority.

### Debug logging (left in for diagnostics)
- `FileBrowser` tag — in `FileBrowserDialog.onClick` (name/path/type/isDir on tap)
- `FileBrowserVM` tag — in `MainViewModel`: browseFiles URL, browseDirectory, browseParent, requestDownload, `browse_files` response, download lifecycle (`startDownload`, `enqueue → id`, PAUSED/FAILED/SUCCESSFUL status poll)
- `WebSocketClient` was deliberately stripped of Log calls back in the release prep.
- Consider stripping FileBrowser* logs when release-packaging.

---

## 7. Protocol contracts we depend on (with Windows)

- `get_projects` + `select_project` responses both carry the full `projects[]`. `select_project` additionally carries the focused project's `sessions[]`.
- `browse_files` response: `{currentPath, parentPath, entries:[{name, path, type, size, modified}]}`. Client navigates using `entry.path` verbatim — no client-side path composition.
- `request_download` response: `{downloadUrl, fileName}`. The URL must be reachable from the phone (LAN IP, not `localhost`). Server must bind HTTP listener to `0.0.0.0` or LAN IP.
- `output_full` payload: `{messages:[{role, content}]}` is the preferred shape. Single `{content}` remains accepted as a legacy single-assistant fallback.
- Server sends paragraph breaks as `\n\n`. Intra-paragraph `\n` from terminal wrapping is treated as wrap and collapsed to space by the client.
- `action_buttons.category`: `"permission"` triggers red/amber/green button styling + haptic + HIGH-priority notification; `"selection"` (or unset) uses tonal buttons.

---

## 8. Open items (next phases)

### Quick wins
- Swap deprecated icons to `Icons.AutoMirrored.Filled.*`
- Strip FileBrowser* debug logs from release build (or wrap in `BuildConfig.DEBUG`)
- Scroll anchor: don't force-scroll to bottom when user has scrolled up to read

### Medium
- Prism4j syntax highlighting (needs KAPT + language bundle; Markwon plugin ready, just wiring)
- Image loading in markdown (Markwon image plugin is in, but needs Coil/Glide integration for remote URLs)
- Per-session persistence: remember last active session across app restarts
- Upload (counterpart to download): send a file from phone to PC

### Larger
- Tablet / landscape layout pass
- HTTPS with self-signed cert + trust config
- Widget for quick commands on home screen
- Voice input (STT)
- Full release pipeline: signed APK, R8 rules for Markwon + OkHttp + kotlinx-serialization, `usesCleartextTraffic` policy

---

## 9. Gotchas / lessons (don't repeat)

1. **Compose library version drift**: anything labeled "multiplatform" + Kotlin 2.0 era will silently fail at runtime on our 1.9 stack. Prefer Android-native (Markwon, AndroidView-based) for pre-Kotlin-2 projects.
2. **`HttpListener` on Windows**: `http://localhost:PORT/` prefixes return HTTP 400 "Invalid Hostname" to anything but localhost. Server must use `http://+:PORT/` (needs urlacl/admin) or bind the actual LAN IP, or — the chosen fix — switch to Kestrel. Phone-side symptom: `DownloadManager` stuck in `STATUS_PAUSED reason=1` (waiting to retry).
3. **CommonMark single `\n` = space**: always be explicit about paragraph vs wrap. Our preprocessor + SoftLineBreak visitor is the source of truth.
4. **`rememberSaveable(content)`**: fine for boolean toggles; don't try to save large objects through it.
5. **`LazyColumn items(key = ...)`**: for append-only chat log, `"$index:$role"` is stable. Avoid `indexOf(item)` scans.
6. **Optimistic state**: `selectProject` sets `expanded=true` on the selected one and `false` on all others before the server confirms. Server response (which carries the authoritative list) replaces the optimistic state — make sure server semantics match (expand target, collapse others).
7. **`DownloadManager` failures are silent**: `enqueue()` returns success even if the URL is unreachable; you must poll the status cursor. This is why `pollDownloadStatus` exists.
8. **FileEntry path is display-vs-filesystem split**: `name` is a label (`"C: (Local Disk)"`), `path` is what you send back (`"C:\\"`). Never re-build paths client-side.

---

## 10. Handoff checklist when resuming on a new machine

1. Clone or sync `C:/D_Develop/ClaudeRemote.Android/`.
2. Set `JAVA_HOME` to Android Studio's JBR and `ANDROID_SDK_ROOT` to the local SDK.
3. Create `app/local.properties` with `sdk.dir=...` if missing.
4. `./gradlew assembleDebug` — must succeed clean.
5. Read, in order: this file → `docs/reports/Phase13_Android_WorkReport.md` → `docs/reports/Phase12_Android_WorkReport.md` → the rest as needed.
6. Confirm with C# side (read `Cshap프로그래머_SESSION_HANDOFF.md`) which server build is current; our contracts must match.
7. Smoke test on a real device: connect → open a project → open a session → receive output → tap file browser → download a small file → open usage dashboard.
8. Capture logs with `tag:FileBrowser package:mine` filter before and after the change.

---

## 11. Frequently touched files (cheat sheet)

| File | Role | Approx LoC |
|------|------|-----|
| `viewmodel/MainViewModel.kt` | Hub of state + protocol parsing + command emission | ~700 |
| `data/model/Messages.kt` | Protocol constants + data classes | ~135 |
| `ui/screens/ChatScreen.kt` | Chat tab (messages, action buttons, input, quick commands) | ~450 |
| `ui/screens/ManageScreen.kt` | Manage tab (Projects, Sessions, Usage + Files buttons) | ~360 |
| `ui/components/MarkdownView.kt` | Markwon config + terminal-wrap preprocessor | ~230 |
| `ui/components/MessageBubble.kt` | 3 bubble variants | ~155 |
| `ui/components/FileBrowserDialog.kt` | File list + download-confirmation dialog | ~285 |
| `ui/components/UsageDashboardDialog.kt` | Usage dashboard dialog | ~275 |
| `network/WebSocketClient.kt` | OkHttp WebSocket + auto-reconnect | ~200 |
| `service/WebSocketService.kt` | Foreground Service + background notifications | ~230 |
| `app/build.gradle.kts` | Dep list (Compose BOM, OkHttp, Markwon×7, kotlinx-serialization, DataStore) | ~100 |
| `AndroidManifest.xml` | Permissions, service declaration, `usesCleartextTraffic` | ~40 |

---

**End of Android handoff.**
