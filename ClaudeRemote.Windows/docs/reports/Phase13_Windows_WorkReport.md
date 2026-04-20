# Phase 13: Structured Chat Output (Windows)

## Goal

Replace the monolithic text dump returned by `get_output` with a structured list of `ChatMessage { role, content }` objects. Android now renders proper chat bubbles (user right-aligned, assistant left-aligned, tool as a collapsible row) instead of parsing a terminal-style blob.

## Protocol

### `output_update` / `output_full` payload (new)

```json
{
  "type": "content",
  "action": "output_update",
  "payload": {
    "scope": "latest",
    "messages": [
      { "role": "user",      "content": "함수 하나 만들어줘" },
      { "role": "tool",      "content": "Read 2 files, searched code" },
      { "role": "assistant", "content": "```python\ndef hello():\n    print('hello')\n```\n\n이 함수는…" }
    ],
    "content": "함수 하나 만들어줘\n\nRead 2 files, searched code\n\n```python\ndef hello():\n    print('hello')\n```\n\n이 함수는…",
    "isStreaming": false
  }
}
```

- `messages[]` — NEW. Structured turns for bubble rendering.
- `content` — LEGACY. Same messages joined with `\n\n`. Kept for older Android clients.
- Protocol agreed with Android dev: **unwrapped prose** — one paragraph per line, `\n\n` between paragraphs, no mid-paragraph breaks. Android preprocessing becomes a no-op.

### Roles

| Role | Source in Claude Desktop DOM | Android render |
|---|---|---|
| `user` | Group + `rounded-r7 rounded-bl-[var(--r1)]` (chat bubble with tail) | Right-aligned bubble |
| `assistant` | Run of Groups/Lists with `text-assistant-primary` | Left-aligned, markdown |
| `tool` | Button + `group/tool` (Read / Ran / Edited / Created …) | Collapsible card |

## Approach

### Step 1 — Diagnostic dump

Added `DumpChatStructureDiagnostic` (one-shot per session, ~200 ms). First `get_output` after app start logs three perspectives of the scroll container:

```
[ChatDump] --- [1] top-level children ---
  top[0] ct=Group class='text-body text-assistant-primary break-words …'
  top[1] ct=Button class='relative group/tool …' texts=5 snippet='Read'
  top[11] ct=Group class='relative flex flex-col gap-g4 py-p6 px-p8 rounded-r7 rounded-bl-[var(--r1)] …' snippet='아래와 같은 요구사항 …'
  …
[ChatDump] --- [2] buttons (name + class) ---
  btn[0] name='Read 2 files, searched code' class='… group/tool …'
  btn[2] name='Copy message' class='… group/btn …'
  btn[8] name='ui/components/MessageBubble.kt' class='text-code text-assistant-primary …'
  …
[ChatDump] --- [3] text nodes with ancestor chain ---
  txt[17] '에 '      anc=[mb-[var(--p5)] ← list-disc … ← scroll …]
  txt[18] ' 필드 분리 (display '   anc=[mb-[var(--p5)] ← list-disc …]
  …
```

From this I derived the class markers used for classification (see table above).

### Step 2 — `CollectChatMessages`

One cached-subtree walk (`CacheRequest` with `TreeScope.Subtree`, same pattern as the existing `CollectTextsCached` — single RPC, local-cache reads thereafter). For each top-level child of the scroll container:

| DOM shape | Action |
|---|---|
| User bubble Group | flush assistant buffer → emit `user` message |
| `group/tool` Button | flush assistant buffer → emit `tool` message using `Button.Name` (already holds full label like `Ran 2 commands`) |
| `group/btn` Button | skip (UI chrome: Copy / Pin / Rewind / Fork / Show more) |
| Assistant paragraph Group | collect inline content → push to assistant buffer |
| Assistant List | render each direct child as one list line → push to assistant buffer |
| Unknown | fallback: collect inline content → assistant buffer (safety net for future layouts) |

Assistant buffer is joined with `\n\n` when flushed.

### Step 3 — Inline content with embedded code chips

Previously Claude Desktop's inline-code tokens (wrapped in Buttons with class `text-code`) were invisible to the Text-only walker. They show up as styled chips visually ("`다`", "`요`", "`MessageBubble.kt`") but are not `ControlType.Text` — the old walker dropped them, producing broken sentences like "한국어는 문장이\n\n등으로 끝나는데".

The new inline collector walks in tree order and:

- Text node → append name
- Button with `text-code` → append `` `name` `` (preserves the code-span semantic)
- Button with `group/btn` → skip
- Any other container → recurse

Finally, `CollapseWhitespace` merges runs of 2+ spaces (introduced by adjacent span-joins each carrying a trailing/leading space around a code chip).

### Step 4 — Scope slicing

Replaces the old `ExtractLatestResponse` / `ExtractSummary` which had a hard-coded 50-item flat-text cap that truncated long responses in the middle.

| `scope` | Result |
|---|---|
| `full` | all messages |
| `latest` | slice starting at the last `user` message (covers user + any tool rows + the assistant reply). If there is no user message yet, returns everything. |
| `summary` | last assistant message only, truncated to 500 chars. |

### Step 5 — Legacy compatibility

`GetOutputAsync` still exists and returns `string` — now implemented as `RenderMessagesAsString(GetChatMessagesAsync(scope))`. That gives the same join-with-`\n\n` output via the new structured pipeline. The Windows-side `MonitorLoop` and `MainViewModel.OnOutputChanged` need no change.

## Files

### New
| File | Purpose |
|---|---|
| `Models/ChatMessage.cs` | `{ Role, Content }` DTO |

### Modified
| File | Change |
|---|---|
| `Services/IClaudeAutomationService.cs` | +`Task<List<ChatMessage>> GetChatMessagesAsync(string scope)` |
| `Services/ClaudeAutomationService.cs` | +`CollectChatMessages`, `ProcessTopLevelChild`, `CollectInlineContent`, `CollectListContent`, `CollapseWhitespace`, `SliceByScope`, `SummarizeLast`, `RenderMessagesAsString`; `GetOutputAsync` refactored as thin wrapper; class markers declared as consts; old `ExtractLatestResponse` / `ExtractSummary` removed |
| `Services/IMessageProcessor.cs` | `CreateOutputMessage` gains optional `List<ChatMessage>? messages` |
| `Services/MessageProcessor.cs` | `get_output` handler now calls `GetChatMessagesAsync`, serializes `messages[]` alongside legacy `content` |
| `ClaudeRemote.Tests/Program.cs`, `Phase5Test.cs` | Mock service gets the new method + pre-existing Phase 11/12 members (LastAskUserQuestionPrompt, LastPermissionPrompt, LastButtonCategory, GetUsageInfoAsync, GetUsageDashboardAsync, IFileServerService) |

## Fixed bugs (observed before Phase 13)

1. **Missing inline code**: "다", "요" (and file-path chips like `MessageBubble.kt`) were dropped because the walker only collected `ControlType.Text` and they render as `ControlType.Button` with class `text-code`. → Now captured and wrapped in backticks inline.

2. **Missing middle sections on long responses**: `ExtractLatestResponse` truncated to the last 50 non-whitespace text items. Responses with inline styling can easily produce 80-150 Text leaves per turn, so headers / list items in the middle were silently cut. → Structural slicing by message (not text-item count) fixes this.

3. **Spurious mid-paragraph line breaks**: Joining every Text leaf with `\n` turned a multi-span paragraph like `"한국어는 문장이 " + "다" + ", " + "요" + " 등으로…"` into 5 separate lines. → Now each paragraph is one line; only `\n\n` appears (between paragraphs / between messages).

## Build

```
ClaudeRemote.Windows → 0 errors, 1 warning (pre-existing ModeChanged CS0067)
ClaudeRemote.Tests   → 0 errors, 8 warnings (pre-existing unused-event CS0067 in mocks)
```

## Open follow-ups

- `MonitorLoop` currently emits a plain string on `OutputChanged`; the streaming path therefore sends only `content` (no `messages[]`) over the WebSocket. Android client falls back to `content` gracefully — no functional issue — but a Phase 13.1 could plumb `List<ChatMessage>` through the event for bubble-aware live updates.
- Code-block fences (`` ``` ``) — haven't confirmed how Claude Desktop exposes multi-line code blocks in UIA (might be a distinct class like `text-code-block` or a nested structure). If they render unexpectedly, re-run the one-shot `ChatDump` and extend `CollectInlineContent` / `CollectListContent`.
- Headings — markdown H1-H6 in Claude Desktop likely use a class distinct from `text-body`. Current code falls through to the generic paragraph path via the safety-net fallback, so content isn't lost, but emphasis is flattened. Future refinement could detect heading classes and emit a `#` prefix.
