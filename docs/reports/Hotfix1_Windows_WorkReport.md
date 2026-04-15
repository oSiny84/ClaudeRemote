# Hotfix 1 작업 보고서 - Windows 버그 수정 및 개선

**작성일**: 2026-04-10
**담당**: Windows Developer Agent
**프로젝트**: ClaudeRemote Windows
**유형**: Hotfix (Phase 5 이후)

---

## 1. 작업 요약

테스트에서 발견된 3가지 버그/개선사항을 수정했습니다:

1. **서버 IP 주소 표시** - Android에서 접속할 IP를 바로 확인할 수 있도록 UI에 표시
2. **Chat 모드 세션 목록 빈 리스트 버그 수정** - "Sessions" 헤더가 없는 Chat 모드에서도 세션 목록 반환
3. **모드 전환 시 세션/프로젝트 데이터 자동 전송** - switch_mode 응답 후 즉시 데이터 push

회귀 테스트 전원 PASS (Phase 2: 8/8, Phase 5: 5/5)

---

## 2. 변경 파일 목록

| # | 파일 | 변경 유형 | 주요 내용 |
|---|------|-----------|-----------|
| 1 | `ViewModels/MainViewModel.cs` | 기능 추가 | LocalIpAddress 프로퍼티 + IP 감지 로직 |
| 2 | `MainWindow.xaml` | UI 추가 | 헤더에 IP 주소 표시 영역 |
| 3 | `Services/ClaudeAutomationService.cs` | 버그 수정 | GetSessionsAsync 폴백 로직, FindSettingsButtonRect 분리 |
| 4 | `Services/MessageProcessor.cs` | 기능 개선 | switch_mode 후 세션/프로젝트 데이터 자동 전송 |

---

## 3. 상세 변경 내역

### Hotfix 1-1. 서버 IP 주소 표시

**구현 위치**: `MainViewModel.cs`, `MainWindow.xaml`

| 항목 | 사양 |
|------|------|
| 프로퍼티 | `LocalIpAddress` (ObservableProperty) |
| 감지 방법 | UDP 소켓 연결(8.8.8.8:80) → LocalEndPoint에서 IP 추출 |
| 폴백 | `Dns.GetHostEntry` → IPv4 주소 필터링 |
| UI 위치 | 헤더 우측 상태 표시줄, Server 상태 옆 |
| 표시 형식 | `IP: 192.168.x.x` (PrimaryBrush 색상, SemiBold) |

**IP 감지 우선순위**:
1. UDP 소켓 방식 (실제 라우팅 인터페이스의 IP를 정확히 반환)
2. DNS 호스트명 조회 (소켓 실패 시 폴백)
3. `"Unknown"` (모든 방법 실패 시)

---

### Hotfix 1-2. Chat 모드 세션 목록 빈 리스트 버그 수정

**원인 분석**:

`GetSessionsAsync()`의 세션 수집 로직이 "Sessions"라는 텍스트 헤더를 필수적으로 찾은 후에만 세션 항목을 수집했습니다. Chat 모드에서는 이 헤더가 없거나 다른 구조로 되어 있어 `foundSessionsHeader`가 `true`로 전환되지 않고, 결과적으로 빈 리스트가 반환되었습니다.

**수정 내용**:

| 항목 | 이전 | 수정 후 |
|------|------|---------|
| 헤더 필수 여부 | "Sessions" 필수 | 없으면 폴백 스캔 |
| 폴백 로직 | 없음 | `CollectSessionsFallback()` 메서드 추가 |
| Settings 버튼 감지 | 인라인 코드 | `FindSettingsButtonRect()` 분리 (재사용) |
| GetSessionTextElements | 헤더 필수 | 동일 폴백 적용 |

**동작 흐름**:
```
GetSessionsAsync()
  ├─ 1차: "Sessions" 헤더 기반 스캔 (Code 모드에서 작동)
  │   └─ 성공 → 세션 목록 반환
  └─ 2차 (폴백): 헤더 없이 전체 텍스트 스캔 (Chat 모드)
      ├─ UI 라벨/날짜 헤더/단축키/크롬 텍스트 필터링
      └─ 남은 텍스트 → 세션 목록으로 반환
```

**리팩토링**:
- `FindSettingsButtonRect()` 메서드 분리: Settings 버튼 영역 감지를 `GetSessionsAsync()`와 `GetSessionTextElements()` 양쪽에서 재사용
- `CollectSessionsFallback()` 메서드 추가: 폴백 세션 수집 로직 캡슐화

---

### Hotfix 1-3. 모드 전환 시 세션/프로젝트 데이터 자동 전송

**원인 분석**:

`MessageProcessor.ProcessIncomingMessageAsync()`의 `switch_mode` 핸들러가 `CreateResponseMessage(message.Action, true)`만 반환하여 Android 클라이언트는 성공 여부만 알 수 있었습니다. `SessionManager.SwitchModeAsync()`가 내부적으로 `RefreshSessionsAsync()`를 호출하지만, 그 결과가 WebSocket으로 전송되지 않았습니다.

**수정 내용**:

| 항목 | 이전 | 수정 후 |
|------|------|---------|
| switch_mode 응답 | `{ success: true }` | `{ success: true, mode: "chat" }` |
| 세션 데이터 | 미전송 | 응답 직후 `get_sessions` 형식으로 자동 전송 |
| 프로젝트 데이터 | 미전송 | code 모드일 때 `get_projects` 형식으로 자동 전송 |

**전송 순서**:
```
Android → switch_mode (targetMode: "code")
  ← 1. response: { success: true, mode: "code" }
  ← 2. response: { action: "get_sessions", sessions: [...] }
  ← 3. response: { action: "get_projects", projects: [...] }  // code 모드만
```

이렇게 구현하면 Android 클라이언트가 모드 전환 후 별도 요청 없이 UI를 즉시 갱신할 수 있습니다.

---

## 4. 테스트 결과

### 회귀 테스트

| 테스트 그룹 | 결과 | 상세 |
|------------|------|------|
| Phase 2 통합 (8개) | **8/8 PASS** | WebSocket, 메시지 처리, 프로토콜 준수 |
| Phase 5 통합 (5개) | **5/5 PASS** | Heartbeat, 큐잉, 청크 분할, 프로세스 워처 |

### 수동 검증 필요 항목

| 시나리오 | 검증 방법 |
|----------|-----------|
| IP 주소 정확성 | Windows 앱 실행 → 헤더에서 IP 확인 → `ipconfig`와 비교 |
| Chat 모드 세션 | Chat 모드에서 `get_sessions` 명령 → 비어있지 않은 목록 반환 확인 |
| 모드 전환 데이터 | Android에서 Chat↔Code 전환 → 세션/프로젝트 목록 자동 갱신 확인 |

---

## 5. 변경 영향 분석

| 변경 사항 | 영향 범위 | 위험도 |
|-----------|----------|--------|
| IP 표시 | UI only (ViewModel + XAML) | 낮음 |
| 세션 폴백 | GetSessionsAsync, GetSessionTextElements, SelectSessionAsync | 중간 (기존 로직 유지, 폴백만 추가) |
| 모드 전환 응답 | switch_mode 핸들러 | 낮음 (추가 메시지, 기존 응답 구조 유지) |

기존 동작은 모두 유지되며, 새 로직은 기존 실패 케이스에서만 활성화됩니다.
