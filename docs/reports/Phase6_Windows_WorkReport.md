# Phase 6 작업 보고서 - Claude 선택 버튼 원격 제어

**작성일**: 2026-04-10
**담당**: Windows Developer Agent
**프로젝트**: ClaudeRemote Windows
**유형**: Phase 6 신규 기능

---

## 1. 작업 요약

Claude 데스크톱 앱이 사용자에게 제시하는 **선택 버튼**(action buttons)을 감지하고, Android 클라이언트에서 원격으로 클릭할 수 있도록 구현했습니다.

프로토콜 v1.2의 `action_buttons` (자동 push) / `click_button` (명령) 메시지를 구현하였습니다.

---

## 2. 변경/추가 파일 목록

| # | 파일 | 변경 유형 | 주요 내용 |
|---|------|-----------|-----------|
| 1 | `Models/ButtonInfo.cs` | **신규** | ButtonInfo 모델 (Id, Text, Element) |
| 2 | `Models/ClaudeMessage.cs` | 상수 추가 | ActionButtons, ClickButton 액션 상수 |
| 3 | `Services/IClaudeAutomationService.cs` | 인터페이스 추가 | GetActionButtonsAsync, ClickButtonAsync, ActionButtonsChanged |
| 4 | `Services/ClaudeAutomationService.cs` | 핵심 구현 | 버튼 감지, 클릭, UI 버튼 필터링, 모니터링 |
| 5 | `Services/IMessageProcessor.cs` | 인터페이스 추가 | CreateActionButtonsMessage |
| 6 | `Services/MessageProcessor.cs` | 메시지 처리 | click_button 핸들러, action_buttons 생성 |
| 7 | `ViewModels/MainViewModel.cs` | 이벤트 연결 | ActionButtonsChanged → WebSocket 전송 |
| 8 | `ClaudeRemote.Tests/Program.cs` | Mock 업데이트 | 새 인터페이스 멤버 구현 |
| 9 | `ClaudeRemote.Tests/Phase5Test.cs` | Mock 업데이트 | 새 인터페이스 멤버 구현 |

---

## 3. 상세 구현

### 3-1. 버튼 감지 (`GetActionButtonsAsync`)

| 항목 | 사양 |
|------|------|
| 탐색 루트 | `main-content` (대화 영역) |
| 탐색 대상 | `ControlType.Button` 요소 |
| 탐색 방식 | ControlView `FindAll` → 실패 시 `RawViewWalker` DFS |
| UI 버튼 제외 | `KnownUiButtons` HashSet (22개) + 패턴 필터 |

**제외되는 알려진 UI 버튼 목록**:
```
Copy message, Copy, Interrupt, Session options, Send,
New session, Settings, Submit, Cancel, Close,
Retry, Stop, Resume, Expand, Collapse,
More options, Delete, Edit, Share, Pin,
Thumbs up, Thumbs down
```

추가 필터:
- `name.Contains("Settings")` — Settings 변형
- `name.StartsWith("Ctrl+")` / `name.StartsWith("Alt+")` — 단축키
- `name.Contains(":\\")` — 프로젝트 경로 버튼

### 3-2. 버튼 클릭 (`ClickButtonAsync`)

| 단계 | 방법 |
|------|------|
| 1차 | `InvokePattern` (표준 버튼 패턴) |
| 2차 | `BoundingRectangle` 기반 좌표 클릭 |

클릭 시 fresh한 버튼 목록을 다시 수집하여 stale 참조 방지.

### 3-3. 모니터링 (MonitorLoop)

```
MonitorLoopAsync (500ms interval)
  ├─ GetOutputAsync → 변경 감지 → OutputChanged
  └─ GetActionButtonsAsync → 텍스트 목록 비교 → ActionButtonsChanged
      └─ 비교: _lastButtonTexts.SequenceEqual(currentTexts)
```

| 항목 | 사양 |
|------|------|
| 감지 주기 | 500ms (출력 모니터링과 동일) |
| 변경 감지 | 버튼 텍스트 리스트의 `SequenceEqual` 비교 |
| 버튼 소멸 | 빈 리스트 → `ActionButtonsChanged` 발생 → Android에 `[]` 전송 |

### 3-4. 프로토콜 메시지

**action_buttons** (Windows → Android, 자동 push):
```json
{
  "type": "content",
  "action": "action_buttons",
  "payload": {
    "buttons": [
      {"id": "btn_0", "text": "Option A"},
      {"id": "btn_1", "text": "Option B"}
    ]
  }
}
```

**click_button** (Android → Windows):
```json
{
  "type": "command",
  "action": "click_button",
  "payload": { "buttonId": "btn_0" }
}
```

**click_button 응답** (Windows → Android):
```json
{
  "type": "response",
  "action": "click_button",
  "payload": { "success": true, "buttonText": "Option A" }
}
```

---

## 4. 테스트 결과

| 테스트 그룹 | 결과 | 상세 |
|------------|------|------|
| Phase 2 통합 (8개) | **8/8 PASS** | WebSocket, 메시지 처리, 프로토콜 준수 |
| Phase 5 통합 (5개) | **5/5 PASS** | Heartbeat, 큐잉, 청크 분할, 프로세스 워처 |

### 수동 검증 필요 항목

| 시나리오 | 검증 방법 |
|----------|-----------|
| 버튼 감지 | Claude에 선택지 있는 질문 → action_buttons 메시지 수신 확인 |
| 버튼 클릭 | Android에서 click_button 전송 → Claude 앱에서 해당 버튼 클릭 확인 |
| 버튼 소멸 | Claude 응답 완료 후 → 빈 배열 `[]` 전송 확인 |
| UI 버튼 제외 | Copy message, Interrupt 등이 action_buttons에 미포함 확인 |

---

## 5. 아키텍처

```
Android                    Windows (ClaudeRemote)           Claude Desktop
  │                              │                              │
  │                        MonitorLoop (500ms)                  │
  │                    ┌─── GetActionButtonsAsync ──────────────┤
  │                    │    (main-content → Button 탐색)        │
  │  ◄─action_buttons──┤    → UI 버튼 필터링                    │
  │    {buttons:[...]}  │    → 변경 감지 (SequenceEqual)         │
  │                    └────────────────────────────────────────┘
  │                                                             │
  ├──click_button──►  ClickButtonAsync(btn_0)                   │
  │  {buttonId}        ├─ InvokePattern                   ──────┤ 버튼 클릭
  │                    └─ Coordinate click (fallback)            │
  │  ◄─response────   {success:true, buttonText:"..."}          │
```
