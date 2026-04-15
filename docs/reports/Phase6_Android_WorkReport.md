# Phase 6 작업 보고서 - Claude 선택 버튼 원격 제어

**작성일**: 2026-04-10  
**담당**: Android Developer Agent  
**프로젝트**: ClaudeRemote Android  
**프로토콜**: v1.2 (action_buttons / click_button 추가)

---

## 1. 개요

Claude가 사용자에게 선택지를 제시할 때 (예: "Option A / Option B / Other") 서버가 버튼 목록을 전송하고, Android에서 해당 버튼을 탭하여 원격으로 클릭하는 기능 구현.

---

## 2. 변경 파일 목록

| # | 파일 | 변경 유형 | 핵심 변경 |
|---|------|-----------|-----------|
| 1 | `Messages.kt` | 수정 | `ButtonInfo` 데이터 클래스, `ACTION_BUTTONS`/`CLICK_BUTTON` 상수 추가 |
| 2 | `MainViewModel.kt` | 수정 | `_actionButtons` StateFlow, `clickButton()`, content/response 핸들러 |
| 3 | `OutputScreen.kt` | 수정 | 하단 액션 버튼 UI (FlowRow + FilledTonalButton + 로딩 상태) |

---

## 3. 상세 변경 내역

### 3-1. Messages.kt — 데이터 모델

```kotlin
// 신규 데이터 클래스
data class ButtonInfo(
    val id: String,    // "btn_0", "btn_1", ...
    val text: String   // "Option A", "Option B", ...
)

// MessageAction 상수 추가
const val ACTION_BUTTONS = "action_buttons"  // content 타입 (서버 → 앱)
const val CLICK_BUTTON = "click_button"      // command 타입 (앱 → 서버)
```

### 3-2. MainViewModel.kt — 상태 관리 및 명령

**StateFlow 추가**:
```kotlin
private val _actionButtons = MutableStateFlow<List<ButtonInfo>>(emptyList())
val actionButtons: StateFlow<List<ButtonInfo>> = _actionButtons
```

**handleContent() — action_buttons 수신**:
```kotlin
MessageAction.ACTION_BUTTONS -> {
    _actionButtons.value = payload?.get("buttons")?.jsonArray?.map { el ->
        val obj = el.jsonObject
        ButtonInfo(
            id = obj["id"]?.jsonPrimitive?.content ?: "",
            text = obj["text"]?.jsonPrimitive?.content ?: ""
        )
    } ?: emptyList()
}
```
- 서버가 빈 배열 `[]` 전송 시 자동 클리어

**clickButton() — 버튼 클릭 명령 전송**:
```kotlin
fun clickButton(buttonId: String) {
    webSocketClient.sendCommand(
        action = MessageAction.CLICK_BUTTON,
        payload = mapOf("buttonId" to buttonId)
    )
}
```

**handleResponse() — click_button 응답 처리**:
```kotlin
MessageAction.CLICK_BUTTON -> {
    if (success) {
        val buttonText = payload?.get("buttonText")?.jsonPrimitive?.content
        _snackbarEvent.tryEmit("Clicked: ${buttonText ?: "button"}")
        _actionButtons.value = emptyList()  // 버튼 클리어
    } else {
        val error = payload?.get("error")?.jsonPrimitive?.content
        _snackbarEvent.tryEmit("Button click failed: ${error ?: "unknown"}")
    }
}
```

### 3-3. OutputScreen.kt — 버튼 UI

**레이아웃 구조**:
```
┌─ Output Screen ────────────────┐
│ Claude Output         [↻][📋] │
│ Claude is running              │
│                                │
│ [Full] [Latest] [Summary]      │
│                                │
│ ┌────────────────────────────┐ │
│ │ (출력 내용 - weight(1f))    │ │
│ │                            │ │
│ └────────────────────────────┘ │
│                                │
│ Claude is asking:              │
│ [Option A] [Option B] [Other]  │
└────────────────────────────────┘
```

**FlowRow 사용**: 버튼 수가 많거나 텍스트가 길 때 자동 줄바꿈

**로딩 상태**:
- 버튼 클릭 → 해당 버튼에 `CircularProgressIndicator` 표시
- 모든 버튼 `enabled = false` (중복 클릭 방지)
- 서버 응답 수신 → 버튼 영역 자동 소멸

**ActionButton 컴포저블**:
```kotlin
@Composable
private fun ActionButton(
    button: ButtonInfo,
    isClicked: Boolean,   // 이 버튼이 클릭된 버튼인지
    enabled: Boolean,     // 아무 버튼도 클릭 안 된 상태인지
    onClick: () -> Unit
)
```
- `FilledTonalButton` (Material3) 사용
- 클릭된 버튼: spinner + 텍스트
- 미클릭 버튼: 비활성화 (disabled)

---

## 4. 프로토콜 메시지 흐름

```
[서버 → 앱] Claude가 선택지 제시
  type: "content"
  action: "action_buttons"
  payload: { buttons: [{id:"btn_0", text:"Option A"}, ...] }
     ↓
  Android: _actionButtons 업데이트 → UI에 버튼 표시

[앱 → 서버] 사용자가 버튼 탭
  type: "command"
  action: "click_button"
  payload: { buttonId: "btn_0" }
     ↓
  서버: Claude UI에서 해당 버튼 클릭

[서버 → 앱] 클릭 결과
  type: "response"
  action: "click_button"
  payload: { success: true, buttonText: "Option A" }
     ↓
  Android: Snackbar "Clicked: Option A" + 버튼 목록 클리어
```

---

## 5. QA 검증 항목

### 버튼 표시
- [ ] 서버에서 action_buttons 수신 → Output 하단에 버튼 표시
- [ ] "Claude is asking:" 라벨 표시
- [ ] 버튼 텍스트 정상 표시
- [ ] 버튼 3개 이상일 때 FlowRow 줄바꿈 정상

### 버튼 클릭
- [ ] 버튼 탭 → click_button 명령 서버 전송
- [ ] 클릭한 버튼에 로딩 스피너 표시
- [ ] 다른 버튼 비활성화 (중복 클릭 방지)
- [ ] 서버 응답 후 Snackbar 표시 ("Clicked: Option A")
- [ ] 서버 응답 후 버튼 영역 자동 소멸

### 엣지 케이스
- [ ] 빈 배열 수신 → 버튼 영역 미표시
- [ ] click_button 실패 → 에러 Snackbar + 버튼 유지 (재시도 가능)
- [ ] 새로운 action_buttons 수신 → 이전 버튼 교체 + clickedButtonId 리셋
