# Phase 7: Permission Remote Control - Work Report

## Overview
Claude Code의 도구 실행 권한 요청(permission request)을 Android에서 원격으로 승인/거부할 수 있는 기능 구현.

## Date
2026-04-14

## Changes

### 1. Data Model (`Messages.kt`)
- `ButtonInfo(id, text)` — Phase 6에서 추가됨, Phase 7에서도 재사용
- `ACTION_BUTTONS`, `CLICK_BUTTON` 액션 상수 — Phase 6에서 추가됨

### 2. MainViewModel.kt
- `_buttonCategory: MutableStateFlow<String?>` 추가 — `"permission"` / `"selection"` 구분
- `handleContent()` ACTION_BUTTONS 핸들러에서 `category` 필드 파싱
- `clickButton()` 응답 처리: 실패 시에만 snackbar, 성공 시 버튼 미삭제 (서버가 다음 상태 전송)

### 3. ChatScreen.kt — Permission UI
- **Category 분기**: `buttonCategory == "permission"` 여부에 따라 UI 분기
- **Permission UI**:
  - "Permission Required:" 레이블 (error color, Bold)
  - 명령 요약 미리보기 (Monospace, 12sp, errorContainer Surface)
  - 색상 구분 버튼 (`PermissionButton` composable):
    - Deny/Reject → `OutlinedButton` (error border + text)
    - Always Allow → `Button` (green #10B981)
    - Allow 등 기타 → `FilledTonalButton`
  - `FlowRow` 레이아웃으로 버튼 동적 배치
- **Haptic Feedback**: permission 카테고리 도착 시 `HapticFeedbackType.LongPress` 진동
- **일반 Action Buttons**: 기존 UI 유지 (FilledTonalButton)

### 4. WebSocketService.kt — Background Notification
- `handleBackgroundMessage()`: `ACTION_BUTTONS` + `category == "permission"` 감지
- `showPermissionNotification(preview)`: 
  - Channel: `CHANNEL_ALERT` (IMPORTANCE_HIGH)
  - Title: "Permission Required"
  - Content: 명령 요약 (최대 120자)
  - BigTextStyle로 긴 텍스트 표시
  - PRIORITY_HIGH 설정
  - 탭 시 앱 복귀 (FLAG_ACTIVITY_SINGLE_TOP)
  - Auto-cancel 활성화
- Notification ID: `NOTIFICATION_ID_PERMISSION = 4` (disconnect 알림과 분리)

## Protocol
- WebSocket Protocol v1.2
- Server → Client: `{ type: "content", action: "action_buttons", payload: { buttons: [...], prompt: "...", category: "permission" } }`
- Client → Server: `{ type: "command", action: "click_button", payload: { buttonId: "..." } }`
- Server → Client: `{ type: "response", action: "click_button", payload: { success: true/false } }`

## Architecture
```
Server (action_buttons, category=permission)
  → WebSocketClient (rawMessages flow)
    → MainViewModel (parse → _actionButtons, _buttonCategory, _actionButtonsPrompt)
      → ChatScreen (permission UI with PermissionButton composable)
        → User clicks → clickButton(id)
          → WebSocketClient.sendCommand(click_button)
            → Server processes
  → WebSocketService (background → showPermissionNotification → CHANNEL_ALERT)
```

## Files Modified
| File | Change |
|------|--------|
| `MainViewModel.kt` | `_buttonCategory` StateFlow, category 파싱 |
| `ChatScreen.kt` | Permission UI, PermissionButton composable, haptic feedback |
| `WebSocketService.kt` | Permission detection, `showPermissionNotification()` |

## Status
Complete
