# Hotfix 2 작업 보고서 - Code 모드 전용 고정

**작성일**: 2026-04-10
**담당**: Windows Developer Agent
**프로젝트**: ClaudeRemote Windows
**유형**: Hotfix

---

## 1. 작업 요약

앱을 **Code 모드 전용**으로 고정. Chat/Cowork 모드 전환 기능을 제거하고, 앱 연결 시 자동으로 Code 모드로 전환되도록 변경했습니다.

세션/프로젝트 관리 기능(조회, 전환, 추가)은 모두 유지됩니다.

---

## 2. 변경 파일 목록

| # | 파일 | 변경 유형 | 주요 내용 |
|---|------|-----------|-----------|
| 1 | `Services/ClaudeAutomationService.cs` | 기능 추가 | FindClaudeWindowAsync 성공 후 Code 모드 자동 전환 |
| 2 | `Services/SessionManager.cs` | 로직 변경 | CurrentMode="code" 고정, SwitchModeAsync 무시 |
| 3 | `Services/MessageProcessor.cs` | 로직 변경 | switch_mode 명령 무시, mode 항상 "code" 응답 |
| 4 | `MainWindow.xaml` | UI 제거 | Chat/Code 전환 버튼 제거, "Mode: Code" 고정 텍스트 |
| 5 | `ViewModels/MainViewModel.cs` | 코드 제거 | SwitchToChatCommand/SwitchToCodeCommand 제거, CurrentMode="code" 고정 |

---

## 3. 상세 변경 내역

### 3-1. ClaudeAutomationService - 자동 Code 모드 전환

**위치**: `FindClaudeWindowAsync()` 성공 후

```
Claude 프로세스 발견 → 윈도우 연결 → 메인 문서 확인
  ↓
CurrentMode != "code" → SwitchModeAsync("code") 자동 호출
  (500ms 대기 후 비동기 실행, UI 초기화 대기)
```

| 항목 | 사양 |
|------|------|
| 전환 타이밍 | FindClaudeWindowAsync 성공 직후 |
| 전환 조건 | CurrentMode가 "code"가 아닌 경우에만 |
| 대기 시간 | 500ms (앱 UI 안정화) |
| 실행 방식 | `Task.Run` 비동기 (UI 블로킹 없음) |

---

### 3-2. SessionManager - Code 모드 고정

| 항목 | 이전 | 수정 후 |
|------|------|---------|
| `CurrentMode` 초기값 | `"chat"` | `"code"` |
| `SwitchModeAsync()` | 모드 전환 + 세션/프로젝트 새로고침 | code 외 입력 무시, 로그만 출력 |
| `ModeChanged` 이벤트 | 모드 전환 시 발행 | 미사용 (인터페이스 호환 유지) |

**유지된 기능**:
- `RefreshSessionsAsync()` - 세션 목록 새로고침
- `RefreshProjectsAsync()` - 프로젝트 목록 새로고침
- `SelectSessionAsync()` - 세션 선택
- `AddSessionAsync()` - 새 세션 추가
- `SelectProjectAsync()` - 프로젝트 선택

---

### 3-3. MessageProcessor - switch_mode 명령 처리

| 항목 | 이전 | 수정 후 |
|------|------|---------|
| `switch_mode` 수신 시 | `SwitchModeAsync` 호출 + 세션/프로젝트 데이터 전송 | 무시, `{ success: true, mode: "code" }` 응답만 |
| 응답의 mode 필드 | 실제 전환된 모드 | 항상 `"code"` |

Android 클라이언트가 `switch_mode`를 보내도 에러 없이 `success: true`로 응답하되, 실제 모드 전환은 수행하지 않습니다.

---

### 3-4. MainWindow.xaml - UI 변경

| 항목 | 이전 | 수정 후 |
|------|------|---------|
| 모드 영역 | "Chat" 버튼 + "Code" 버튼 | `"Mode: Code"` 고정 텍스트 (PrimaryBrush, SemiBold) |
| 출력 헤더 | `(Binding CurrentMode)` 동적 표시 | `"(code mode)"` 고정 텍스트 |

---

### 3-5. MainViewModel - 명령 제거

| 제거 항목 | 이유 |
|-----------|------|
| `SwitchToChatCommand` | Chat 모드 미지원 |
| `SwitchToCodeCommand` | 항상 Code 모드 |
| `OnModeChanged` 이벤트 핸들러 | ModeChanged 이벤트 미사용 |
| `ModeChanged` 이벤트 구독 | 위와 동일 |

| 변경 항목 | 이전 | 수정 후 |
|-----------|------|---------|
| `_currentMode` 초기값 | `"chat"` | `"code"` |

---

## 4. 테스트 결과

### 자동 회귀 테스트

| 테스트 그룹 | 결과 | 상세 |
|------------|------|------|
| Phase 2 통합 (8개) | **8/8 PASS** | WebSocket, 메시지 처리, 프로토콜 준수 |
| Phase 5 통합 (5개) | **5/5 PASS** | Heartbeat, 큐잉, 청크 분할, 프로세스 워처 |

### 수동 검증 필요 항목

| 시나리오 | 검증 방법 | 확인 포인트 |
|----------|-----------|------------|
| 자동 Code 전환 | 앱 실행 → Claude 연결 | Chat 모드였다면 자동으로 Code 탭 선택 |
| UI 표시 | 앱 화면 확인 | Chat/Code 버튼 없음, "Mode: Code" 표시 |
| 세션 조회 | get_sessions 명령 | Code 모드 세션 목록 반환 |
| 프로젝트 조회 | get_projects 명령 | 프로젝트 목록 반환 |
| switch_mode 거부 | Android에서 switch_mode 전송 | success: true, mode: "code" 응답 (전환 안 됨) |

---

## 5. 변경 영향 분석

| 변경 사항 | 영향 범위 | 위험도 |
|-----------|----------|--------|
| 자동 Code 전환 | Claude 연결 시점 | 낮음 (비동기, UI 무관) |
| SessionManager 고정 | 전체 모드 관련 로직 | 낮음 (기존 기능 무력화, 제거 아님) |
| switch_mode 무시 | Android 클라이언트 호환성 | 낮음 (에러 없이 success 응답) |
| UI 버튼 제거 | MainWindow | 없음 (표시 변경만) |
| ViewModel 정리 | MainViewModel | 낮음 (미사용 코드 제거) |

**하위 호환성**: Android 클라이언트가 `switch_mode`를 보내도 에러 없이 처리됩니다. `mode` 필드는 항상 `"code"`로 응답하므로 클라이언트가 모드 상태를 올바르게 인식합니다.
