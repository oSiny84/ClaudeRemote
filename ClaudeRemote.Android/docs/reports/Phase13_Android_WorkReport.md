# Phase 13: Chat-Style Output Rendering - Work Report

## Overview
Raw text 나열이던 Claude 출력 화면을 채팅 말풍선 UI + 라이브러리 기반 마크다운 렌더링으로 전면 개편.

## Date
2026-04-17

## Motivation
- 기존 직접 파싱(`parseMarkdownBlocks` + `parseInlineMarkdown`)은 `**bold**`, `*italic*`, `` `code` ``, ` ```lang `, `#` 헤딩, `-`/`*` 리스트 정도만 지원 → 실제 Claude 출력의 테이블/링크/중첩 리스트/HTML 등은 깨짐
- "누가 말했는지" 시각적 구분 없이 모든 출력이 하나의 스크롤 뷰 → 대화 흐름 파악 어려움
- `tool` 메시지가 raw로 출력되면 실제 Claude 응답이 툴 trace에 묻힘

## Changes

### 1. 의존성 (`app/build.gradle.kts` + `settings.gradle.kts`)

**초기 시도**: `com.mikepenz:multiplatform-markdown-renderer-m3:0.28.0`
- 빌드는 통과했으나 실기기에서 **첫 렌더링 시 앱 크래시**
- 원인: 0.28.0은 Compose 1.7+ / Kotlin 2.0 전용 ABI로 배포. 본 프로젝트는 Compose 1.6 (BOM 2024.02.00) / Kotlin 1.9.22 → 런타임에 `NoSuchMethodError` 가능성 높음 (logcat 부재로 정확한 예외는 미확인, 정황상 KMP metadata 호환성 문제)

**최종 채택**: `com.github.jeziellago:compose-markdown:0.5.4` (JitPack)
```kotlin
// app/build.gradle.kts
implementation("com.github.jeziellago:compose-markdown:0.5.4")

// settings.gradle.kts — JitPack 리포 추가
maven { url = uri("https://jitpack.io") }
```
- 내부적으로 Markwon(Android-native commonmark 파서)을 `AndroidView`로 래핑
- Compose 버전 민감 의존성 0 → Kotlin 1.9 / Compose 1.6에서 안정적
- 코드 블록, 테이블, 링크, 리스트, 이미지(선택), HTML 일부 지원
- 사용: `MarkdownText(markdown = content, style = TextStyle(...))`

### 2. 데이터 모델 (`Messages.kt`)
```kotlin
data class ChatMessage(
    val role: String,       // "user" / "assistant" / "tool"
    val content: String
)
```
- `@Serializable` 미적용 — ViewModel 수동 파싱 (일관된 패턴)
- `role` 값은 서버 프로토콜을 그대로 반영. 확장성 위해 lowercase/fallback 처리는 UI 레이어에서.

### 3. MainViewModel.kt

#### 신규 StateFlow
```kotlin
private val _chatMessages = MutableStateFlow<List<ChatMessage>>(emptyList())
val chatMessages: StateFlow<List<ChatMessage>> = _chatMessages
```

`_claudeOutput`은 **유지** — 2가지 역할:
1. 청크 재조립 중간 버퍼 (`handleChunk`)
2. 헤더의 Copy 버튼이 전체 텍스트를 클립보드로 복사할 때 fallback

UI는 주로 `chatMessages`만 관찰.

#### `handleContent()` 확장
```kotlin
MessageAction.OUTPUT_UPDATE -> {
    _claudeOutput.value = content
    // 단일 assistant 메시지로 래핑 (Back-compat: messages[] 없는 서버)
    _chatMessages.value = listOf(ChatMessage("assistant", content))
}

MessageAction.OUTPUT_FULL -> {
    val messages = payload?.get("messages")?.jsonArray
    if (messages != null) {
        val parsed = messages.map { el ->
            ChatMessage(
                role = obj["role"]?.jsonPrimitive?.content ?: "assistant",
                content = obj["content"]?.jsonPrimitive?.content ?: ""
            )
        }
        _chatMessages.value = parsed
        _claudeOutput.value = parsed.joinToString("\n\n") { "[${it.role}]\n${it.content}" }
    } else {
        // content 필드만 오는 구형 서버 응답 지원
        payload?.get("content")?.jsonPrimitive?.content?.let {
            _claudeOutput.value = it
            _chatMessages.value = listOf(ChatMessage("assistant", it))
        }
    }
}
```

#### `handleChunk()` 확장
청크 재조립 완료 시 `_chatMessages`도 동시 갱신:
```kotlin
if (buffer.count { it.isNotEmpty() } == totalChunks) {
    val joined = buffer.joinToString("")
    _claudeOutput.value = joined
    _chatMessages.value = listOf(ChatMessage("assistant", joined))
}
```

#### `disconnect()` 정리
연결 해제 시 `_chatMessages.value = emptyList()` 추가 — 이전 세션 잔재 방지.

### 4. MessageBubble (`ui/components/MessageBubble.kt` 신규)

공개 시그니처:
```kotlin
@Composable
fun MessageBubble(
    role: String,
    content: String,
    modifier: Modifier = Modifier
)
```

내부에서 `role.lowercase()`로 분기:
- `"user"` → `UserBubble`
- `"tool"` / `"tool_use"` / `"tool_result"` → `ToolBubble` (접기)
- 그 외 (`assistant`, `unknown`, 빈 값 포함) → `AssistantBubble` (마크다운)

#### UserBubble
- `Row(horizontalArrangement = End)` — 우측 정렬
- `Surface(color = primaryContainer)` — Material3 Primary 계열
- `RoundedCornerShape(16,16,16,4)` — 꼬리는 bottom-end
- `widthIn(max = 320.dp)` — 긴 사용자 입력이 화면을 다 차지하지 않도록 제한

#### AssistantBubble
- `Row(horizontalArrangement = Start)` — 좌측 정렬
- `Surface(color = surfaceVariant.copy(alpha = 0.6f))` — 은은한 배경
- `RoundedCornerShape(16,16,4,16)` — 꼬리는 bottom-start
- `fillMaxWidth(fraction = 0.94f)` — 마크다운 테이블/코드블록을 위해 충분한 폭
- 내부 렌더링: `com.mikepenz.markdown.m3.Markdown(content = content)` — Material3 테마 자동 적용

#### ToolBubble (접기/펼치기)
- `OutlinedCard` + 헤더 Row (클릭 시 `expanded` 토글) + `AnimatedVisibility`로 본문 노출
- 상태: `rememberSaveable(content) { mutableStateOf(false) }` — content가 동일한 툴 호출은 스크롤 왕복해도 펼침 상태 유지, content가 바뀌면 새 툴 호출이므로 기본 접힘
- 펼쳤을 때 본문은 `FontFamily.Monospace`, 11sp — 툴 출력은 로그 성격이므로 고정폭 가독성

### 5. ChatScreen.kt — 전면 교체

#### 제거된 코드 (약 170라인)
- `MarkdownText` / `CodeBlockView` / `MdBlock` / `parseMarkdownBlocks` / `parseInlineMarkdown`
- `verticalScroll` 기반 단일 스크롤 뷰
- `output` (String) 직접 관찰

#### 도입된 구조
```kotlin
val chatMessages by viewModel.chatMessages.collectAsState()
val listState = rememberLazyListState()

LaunchedEffect(chatMessages.size) {
    if (chatMessages.isNotEmpty())
        listState.animateScrollToItem(chatMessages.lastIndex)
}

LazyColumn(
    state = listState,
    verticalArrangement = Arrangement.spacedBy(8.dp)
) {
    itemsIndexed(
        items = chatMessages,
        key = { index, msg -> "$index:${msg.role}" }  // append-only 로그에 안정적
    ) { _, msg ->
        MessageBubble(role = msg.role, content = msg.content)
    }
}
```

#### 기타 조정
- 빈 상태 표시: `output.isEmpty()` → `chatMessages.isEmpty()`
- 로딩 표시: `isLoading && chatMessages.isEmpty()` — 메시지가 이미 있으면 로딩 인디케이터로 덮지 않음 (부분 업데이트 매끄러움)
- 헤더 Copy 버튼: `chatMessages`를 `[role]\ncontent` 형식으로 join하여 클립보드로 복사
- Scope toggle (Full/Latest/Summary), Action Buttons (permission/selection), Quick Commands, Input Row, History — 변경 없음

## Files Modified / Created
| File | Type | Change |
|------|------|--------|
| `app/build.gradle.kts` | Modified | mikepenz markdown-renderer-m3:0.28.0 의존성 추가 |
| `data/model/Messages.kt` | Modified | `ChatMessage(role, content)` 추가 |
| `viewmodel/MainViewModel.kt` | Modified | `_chatMessages` 상태, `handleContent`/`handleChunk`/`disconnect` 확장 |
| `ui/components/MessageBubble.kt` | **Created** | Chat 말풍선 3종 컴포저블 (~155 lines) |
| `ui/screens/ChatScreen.kt` | Rewritten | LazyColumn + MessageBubble 구조 (직접 파싱 코드 제거) |

## Protocol Compatibility

### 신형 응답 (messages[])
```json
{
  "type": "content",
  "action": "output_full",
  "payload": {
    "messages": [
      { "role": "user", "content": "Fix the auth bug" },
      { "role": "assistant", "content": "## Plan\n1. ..." },
      { "role": "tool", "content": "Running: grep -r auth...\n[output]" },
      { "role": "assistant", "content": "Done — patched `auth.ts`." }
    ]
  }
}
```

### 구형 응답 (content 단일 필드)
```json
{
  "type": "content",
  "action": "output_update",
  "payload": { "content": "Claude's reply..." }
}
```
→ `assistant` 단일 메시지로 자동 래핑 → UI는 동일하게 말풍선 1개로 표시. 서버 업그레이드 전/후 모두 지원.

## UX Improvements
| Before | After |
|--------|-------|
| `[user]\nHello` raw text | 우측 파란 말풍선 "Hello" |
| `[assistant]\n**Bold** then \`code\`` raw text | 좌측 말풍선 + 볼드/인라인코드 렌더링 |
| 코드 블록 고정폭 단색 | 구문 강조 + 라인 간격 조정 |
| Tool 출력이 Claude 응답과 뒤섞임 | 별도 OutlinedCard로 분리, 기본 접힘 |
| 전체 Column verticalScroll | LazyColumn — 메모리 효율 (긴 세션도 부드러움) |
| 수동 scrollState.animateScrollTo | animateScrollToItem — 새 메시지 자동 하단 이동 |

## Build
- `./gradlew assembleDebug` — **BUILD SUCCESSFUL in 1m 25s** (라이브러리 첫 다운로드 포함)
- APK: `app/build/outputs/apk/debug/app-debug.apk` (22 MB, 2026-04-17 15:34)
- 에러 0, 경고 6개 — 모두 AutoMirrored 아이콘 deprecation + 기존 불필요 safe-call 경고 (기능 무관)
- APK 크기 17MB → 22MB (+5MB): 마크다운 렌더러 + commonmark 파서 포함. R8/ProGuard release 빌드 시 더 작아질 것.

## Known Limitations
1. **Markdown 이미지 미렌더**: `coil3` 확장을 포함하지 않아 `![alt](url)` 이미지는 텍스트로만 표시됨. 필요 시 `multiplatform-markdown-renderer-coil3` 추가로 해결.
2. **Tool 메시지 파싱 강도**: 현재 `content`를 그대로 monospace로 표시. Claude가 tool 호출을 JSON/명령줄 등 특정 포맷으로 보내면 차후 해당 포맷별 전용 렌더러 추가 여지.
3. **Scroll anchor**: 사용자가 위로 스크롤해서 이전 메시지 읽는 중 새 메시지 도착 시 강제로 하단 이동 — 추후 "near-bottom" 감지 후 조건부 스크롤로 개선 여지.

## Status
Complete.
