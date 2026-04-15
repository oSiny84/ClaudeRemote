# Hotfix 2 작업 보고서 - Code 모드 전용 전환

**작성일**: 2026-04-10  
**담당**: Android Developer Agent  
**프로젝트**: ClaudeRemote Android  
**유형**: Refactor (프로젝트 방향 변경)

---

## 1. 변경 배경

프로젝트 방향 변경: **Chat/Cowork 모드를 제거하고 Code 모드 전용으로 전환**.

기존 3-모드(Chat/Cowork/Code) 전환에서 발생하던 레이스 컨디션, 데이터 꼬임 등의 복잡성을 근본적으로 제거.

---

## 2. 변경 파일 목록

| # | 파일 | 변경 유형 | 핵심 변경 |
|---|------|-----------|-----------|
| 1 | `MainViewModel.kt` | 수정 | Code 모드 고정, switchMode() 제거, 연결 시 자동 Code 모드 전환 |
| 2 | `ManageScreen.kt` | 수정 | 모드 전환 버튼 영역 전체 제거, 프로젝트 항상 표시 |
| 3 | `CommandScreen.kt` | 수정 | 모드 표시 텍스트 제거 |
| 4 | `MainScreen.kt` | 수정 | TopAppBar 모드 배지 제거 |

---

## 3. 상세 변경 내역

### 3-1. MainViewModel.kt

**`_currentMode` 고정**:
```kotlin
// 이전: "chat" 기본값 → 서버/UI에서 변경 가능
private val _currentMode = MutableStateFlow("chat")

// 수정: "code" 고정 → 변경하는 코드 없음
private val _currentMode = MutableStateFlow("code")
```

**`switchMode()` 제거**:
- 함수 전체 삭제
- UI에서 호출하는 곳 없음 (ManageScreen의 ModeButton도 제거)

**연결 시 자동 Code 모드 보장**:
```kotlin
// observeMessages() 내 connectionState 수집
if (state == ConnectionState.CONNECTED) {
    webSocketClient.sendCommand(
        action = MessageAction.SWITCH_MODE,
        payload = mapOf("targetMode" to "code")
    )
    _isLoading.value = true
}
```
- 연결 직후 서버에 `switch_mode("code")` 1회 전송
- 서버 확인 응답(SWITCH_MODE success) 수신 후 `requestSessions()` + `requestProjects()` 호출

**`handleResponse()` 간소화**:
- `responseMode` 파라미터 제거 (모드 전환 없으므로 stale 응답 필터링 불필요)
- `SWITCH_MODE` 응답 시 항상 `requestSessions()` + `requestProjects()` 호출
- 디버그 로그(Log.d) 전체 제거

**`handleStatus()` 간소화**:
- 서버 heartbeat의 `currentMode` 무시 → 항상 `"code"` 고정
- 모드 변경 감지/동기화 로직 전체 제거

**`processMessage()` 간소화**:
- `responseMode` 추출 제거
- 디버그 로그 제거

---

### 3-2. ManageScreen.kt

**제거된 UI 요소**:
- "Mode" 타이틀
- Chat / Cowork / Code 3개 모드 전환 버튼 (Row)
- 모드 설명 텍스트 ("General conversation...", "Collaborative work...", "Code editing...")
- `ModeButton` Composable 함수 전체
- `currentMode` StateFlow 수집

**프로젝트 섹션 변경**:
```kotlin
// 이전: Code 모드에서만 표시
if (currentMode == "code") {
    // Projects Section
}

// 수정: 항상 표시 (Code 모드 고정이므로)
// Projects Section
```

**최종 레이아웃**:
```
┌─ Manage ───────────────────┐
│ Sessions     [Refresh][Add]│
│ ┌─ Session 1 ────────── ✓ ┐│
│ └────────────────────────┘│
│ ┌─ Session 2 ──────────  ┐│
│ └────────────────────────┘│
│                            │
│ Projects         [Refresh] │
│ ┌─ Project 1 ────────── ✓┐│
│ └────────────────────────┘│
│ ┌─ Project 2 ──────────  ┐│
│ └────────────────────────┘│
└────────────────────────────┘
```

---

### 3-3. CommandScreen.kt

- `currentMode` StateFlow 수집 제거
- "Mode: chat/cowork/code" 표시 텍스트 제거
- 헤더가 `Column` → 단일 `Text("Send Command")`로 간소화

---

### 3-4. MainScreen.kt

- `currentMode` StateFlow 수집 제거
- TopAppBar 타이틀: `Row(Text + 모드배지)` → `Text("ClaudeRemote")`만 표시

---

## 4. 동작 흐름 (수정 후)

```
[연결 흐름]
  사용자 Connect 탭
    → WebSocket CONNECTED
    → switch_mode("code") 서버 전송
    → 서버 SWITCH_MODE 응답 (success)
    → requestSessions() + requestProjects()
    → 세션/프로젝트 목록 로드

[재연결 흐름]
  WebSocket RECONNECTING → CONNECTED
    → switch_mode("code") 재전송
    → 세션/프로젝트 재로드

[Manage 화면]
  세션 목록 + 프로젝트 목록 항상 표시
  Refresh → requestSessions() / requestProjects()
  세션/프로젝트 탭 → 즉시 체크 이동 (Optimistic UI)
```

---

## 5. 제거된 코드/복잡성

| 제거 항목 | 효과 |
|-----------|------|
| `switchMode()` 함수 | 모드 전환 레이스 컨디션 근본 제거 |
| `responseMode` 필터링 | stale 응답 처리 로직 불필요 |
| `handleStatus()` 모드 동기화 | 서버 모드 불일치 감지 불필요 |
| `ModeButton` Composable | UI 코드 간소화 |
| 모드별 조건 분기 (`if code`) | 항상 Code이므로 조건 불필요 |
| 디버그 로그 (Log.d) | 이전 이슈 조사용, 방향 변경으로 불필요 |

---

## 6. 유지된 기능

- 세션 조회/선택/추가 (`requestSessions`, `selectSession`, `addSession`)
- 프로젝트 조회/선택 (`requestProjects`, `selectProject`)
- Optimistic UI (세션/프로젝트 선택 시 즉시 체크 이동)
- 명령 입력/전송 (Quick Commands + Custom Command)
- 출력 조회 (Latest/Full/Summary)
- `MessageAction.SWITCH_MODE` 상수 (Messages.kt에 유지, 연결 시 1회 사용)

---

## 7. QA 검증 항목

### 연결 및 초기 데이터 로드
- [ ] 연결 성공 → 세션 목록 자동 로드
- [ ] 연결 성공 → 프로젝트 목록 자동 로드
- [ ] 재연결 후 → 세션/프로젝트 재로드

### ManageScreen
- [ ] 모드 전환 버튼 영역 없음
- [ ] 세션 목록 + 프로젝트 목록 동시 표시
- [ ] 세션 Refresh → 정상 갱신
- [ ] 프로젝트 Refresh → 정상 갱신
- [ ] 세션 선택 → 즉시 체크 이동
- [ ] 프로젝트 선택 → 즉시 체크 이동

### CommandScreen
- [ ] 모드 표시 텍스트 없음
- [ ] 명령 입력/전송 정상 동작
- [ ] Quick Commands 정상 동작

### MainScreen
- [ ] TopAppBar에 모드 배지 없음 ("ClaudeRemote"만 표시)
- [ ] 탭 전환 정상 (Output/Command/Manage)
