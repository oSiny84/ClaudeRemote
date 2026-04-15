# ClaudeRemote - PM Phase 관리 문서

## 프로젝트 개요
- **프로젝트명**: ClaudeRemote
- **목적**: Windows에서 Claude 데스크톱 앱을 모니터링/제어하고, Android에서 원격으로 조작
- **구조**: Windows(C# WPF) ↔ WebSocket ↔ Android(Kotlin Compose)

## 현재 상태
- Phase 1 완료: 프로젝트 골격, 모든 소스 파일 생성됨
- 빌드/실행은 아직 미검증

## 세션 운영 방식
- **PM 세션**: 이 문서 기반으로 작업 지시 및 통합 검증
- **C# 세션**: `docs/Windows_Tasks.md` 참조하여 구현
- **Android 세션**: `docs/Android_Tasks.md` 참조하여 구현

---

# Phase 2: 통신 레이어 구축

## 목표
Windows WebSocket 서버와 Android WebSocket 클라이언트가 JSON 메시지를 주고받을 수 있어야 한다.

## C# 프로그래머 지시사항
> 상세: `docs/Windows_Tasks.md` → Phase 2 참조

1. .NET 8 SDK 설치 확인 후 솔루션 빌드 가능하게 만들기
2. NuGet 패키지 복원 (Fleck, CommunityToolkit.Mvvm, Serilog 등)
3. `WebSocketServerService.cs` 동작 검증 - 서버 시작/중지, 클라이언트 접속 처리
4. `MessageProcessor.cs` - 수신 메시지 파싱 및 응답 생성 검증
5. 간단한 테스트: 서버 시작 → 외부 WebSocket 클라이언트(wscat 등)로 접속 → JSON 메시지 송수신

## Android 프로그래머 지시사항
> 상세: `docs/Android_Tasks.md` → Phase 2 참조

1. Android Studio에서 프로젝트 열기, Gradle 빌드 성공 확인
2. `WebSocketClient.kt` 동작 검증 - 연결/해제/재연결
3. `ConnectionScreen.kt` - IP/Port 입력 후 연결 버튼 동작 확인
4. 연결 상태 UI 반영 (Connected/Disconnected/Reconnecting)

## 통합 검증 (PM)
- [ ] Windows 서버 시작 → Android 에뮬레이터에서 연결 성공
- [ ] Android에서 `send_input` command 전송 → Windows에서 수신 확인
- [ ] Windows에서 `content` 메시지 전송 → Android에서 표시 확인
- [ ] 연결 끊김 → 자동 재연결 동작 확인

---

# Phase 3: Claude 앱 연동

## 목표
Windows 프로그램이 UIAutomation으로 Claude 데스크톱 앱을 탐색하고, 출력 읽기/입력 넣기/모드 전환을 수행할 수 있어야 한다.

## 선행 조건
- Phase 2 통합 검증 완료
- Claude 데스크톱 앱이 Windows에 설치되어 있어야 함

## C# 프로그래머 지시사항
> 상세: `docs/Windows_Tasks.md` → Phase 3 참조

1. Claude 앱 프로세스 탐지 (`Process.GetProcessesByName`)
2. UIAutomation으로 Claude 앱 UI 트리 탐색 - Inspect.exe 또는 Accessibility Insights 도구 활용
3. **핵심 요소 매핑**:
   - 대화 출력 영역 (Text 요소들)
   - 입력 필드 (Edit/Document 요소)
   - Chat/Code 모드 전환 탭
   - 세션 목록, 새 세션 버튼
   - (Code 모드) 프로젝트 목록
4. `ClaudeAutomationService.cs` 의 각 메서드 실제 동작 확인:
   - `FindClaudeWindowAsync()` → Claude 윈도우 핸들 확보
   - `GetOutputAsync()` → 대화 내용 텍스트 추출
   - `SendInputAsync()` → 텍스트 입력 + Enter
   - `SwitchModeAsync()` → Chat ↔ Code 전환
   - `GetSessionsAsync()` / `SelectSessionAsync()` / `AddSessionAsync()`
   - `GetProjectsAsync()` / `SelectProjectAsync()`
5. 모니터링 루프 (`StartMonitoring`) - 출력 변경 감지 후 이벤트 발생

## Android 프로그래머 지시사항
> 이 Phase에서 Android 작업 없음. Phase 4 대기.
> 단, Phase 2에서 발견된 버그가 있으면 이 기간에 수정.

## 통합 검증 (PM)
- [ ] Claude 앱 실행 → Windows 프로그램에서 "Claude connected" 표시
- [ ] 대화 출력 텍스트 정상 추출 (최소 마지막 응답)
- [ ] 텍스트 입력 후 Claude가 실제로 응답 생성
- [ ] Chat ↔ Code 모드 전환 성공
- [ ] 세션 목록 조회 및 전환 성공
- [ ] 출력 변경 감지 → 자동으로 Android에 전달

---

# Phase 4: Android UI/UX 완성

## 목표
Android 앱의 모든 화면이 완성도 있게 동작하고, Windows에서 전달받은 Claude 출력을 보여주며, 원격 명령을 보낼 수 있어야 한다.

## 선행 조건
- Phase 3 통합 검증 완료

## C# 프로그래머 지시사항
> 이 Phase에서 Windows 신규 작업 없음.
> 단, Android에서 요청하는 프로토콜 변경이나 버그 수정 대응.

## Android 프로그래머 지시사항
> 상세: `docs/Android_Tasks.md` → Phase 4 참조

1. **OutputScreen 완성**
   - Claude 출력 마크다운 렌더링 (기본적인 수준)
   - 전체/최신/요약 토글 동작
   - 실시간 업데이트 수신 시 자동 스크롤
   - 클립보드 복사 기능

2. **CommandScreen 완성**
   - 빠른 명령 버튼 (Continue, Stop, New Chat) 동작 확인
   - 커스텀 명령 입력 및 전송
   - 전송 후 피드백 (성공/실패 Toast 또는 Snackbar)

3. **ManageScreen 완성**
   - Chat/Code 모드 전환 → 서버에 switch_mode 전송
   - 세션 목록 조회/선택/추가
   - (Code 모드) 프로젝트 목록 조회/선택
   - Pull-to-refresh 또는 새로고침 버튼

4. **UX 다듬기**
   - 화면 전환 애니메이션
   - 로딩 상태 표시 (Skeleton/Shimmer)
   - 빈 상태 안내 문구
   - 에러 상태 처리 (연결 끊김 시 알림)
   - 햅틱 피드백 (버튼 클릭 시)

## 통합 검증 (PM)
- [ ] Android에서 명령 입력 → Claude가 실제 응답 → Android에 표시
- [ ] 모드 전환(Chat↔Code) → 양쪽 동기화 확인
- [ ] 세션 추가/전환 → 양쪽 동기화 확인
- [ ] 프로젝트 전환 → 양쪽 동기화 확인
- [ ] 전체/최신/요약 출력 모드 각각 정상 동작

---

# Phase 5: 통합 및 안정화

## 목표
전체 시스템이 안정적으로 동작하고, 에러 상황에서도 graceful하게 복구된다.

## 선행 조건
- Phase 4 통합 검증 완료

## C# 프로그래머 지시사항
> 상세: `docs/Windows_Tasks.md` → Phase 5 참조

1. Claude 앱 종료/재시작 감지 → 자동 재연결
2. WebSocket 연결 끊김 시 메시지 큐잉
3. 대용량 출력 청크 분할 전송 (10KB 초과 시)
4. 로그 파일 관리 (일별 롤링, 최대 7일 보관)
5. 시스템 트레이 최소화 지원 (선택)

## Android 프로그래머 지시사항
> 상세: `docs/Android_Tasks.md` → Phase 5 참조

1. 백그라운드 연결 유지 (Foreground Service)
2. 알림: 연결 끊김/재연결/새 출력 수신
3. 설정 화면: 서버 주소 저장 (DataStore), 자동 연결, 알림 설정
4. 앱 안정성: ANR 방지, 메모리 누수 검사
5. 에지 케이스: 화면 회전, 멀티윈도우, 배터리 최적화

## 최종 통합 검증 (PM)
- [ ] **E2E 시나리오 1**: 앱 시작 → 연결 → 명령 전송 → 응답 확인
- [ ] **E2E 시나리오 2**: Claude 앱 종료 → 재시작 → 자동 감지 → 계속 동작
- [ ] **E2E 시나리오 3**: 네트워크 끊김 → 재연결 → 큐잉된 메시지 전달
- [ ] **E2E 시나리오 4**: 대용량 출력 (코드 생성 등) → Android에서 정상 표시
- [ ] **E2E 시나리오 5**: Chat↔Code 모드 반복 전환 + 세션/프로젝트 전환
- [ ] 1시간 연속 운영 → 메모리/CPU 안정성 확인

---

# Phase 6 (예정): Claude 선택 버튼 원격 제어

## 목표
Claude가 사용자에게 선택지를 제시하는 버튼(예: "Option A", "Option B")을 Android에서 감지하고 선택할 수 있어야 한다.

## 프로토콜 확장 필요
- 새 action: `action_buttons` (Windows → Android) — 버튼 목록 전달
- 새 action: `click_button` (Android → Windows) — 버튼 선택 명령

```json
// Windows → Android: 버튼 목록
{"type":"content","action":"action_buttons","payload":{"buttons":[{"id":"btn_0","text":"Option A"},{"id":"btn_1","text":"Option B"}]}}

// Android → Windows: 버튼 클릭
{"type":"command","action":"click_button","payload":{"buttonId":"btn_0"}}
```

## C# 작업
1. `ClaudeAutomationService` — 대화 영역 내 `ControlType.Button` 감지 (Name으로 텍스트 추출)
2. 모니터링 루프에서 버튼 등장/소멸 감지
3. `click_button` 명령 수신 → 해당 Button 요소에 InvokePattern 실행
4. `MessageProcessor`에 `action_buttons` / `click_button` 핸들러 추가

## Android 작업
1. `action_buttons` 메시지 수신 → ViewModel 상태로 관리
2. OutputScreen 하단에 선택 버튼 Row 표시 (Material3 버튼)
3. 버튼 탭 → `click_button` 명령 전송
4. 선택 후 버튼 목록 클리어

---

# Phase 7: 도구 실행 권한 요청 원격 제어

## 목표
Claude Code가 도구 실행(Bash, 파일 읽기 등) 권한을 요청할 때, Android에서 원격으로 승인/거부할 수 있어야 한다.

## 프로토콜 확장
- `action_buttons`에 `category` 필드 추가: `"permission"` (권한) / `"selection"` (선택지)
- `prompt` 필드에 실행할 명령 텍스트 포함

## C# 작업
1. 권한 다이얼로그 UI 트리 분석 (Inspect.exe)
2. "Deny" / "Allow once" / "Always allow" 버튼 감지
3. 권한 요청 텍스트 추출
4. `action_buttons` + `category: "permission"` 전송

## Android 작업
1. `category` 파싱 → 권한/선택지 UI 분기
2. 권한 요청: 빨간/초록 강조 버튼, 명령 텍스트 표시
3. 백그라운드 HIGH importance 알림

---

# 참조 문서
| 문서 | 경로 | 용도 |
|------|------|------|
| PM 역할 규칙 | `docs/PM.md` | PM의 의사결정 원칙 |
| Windows 역할 규칙 | `docs/WindowsProgrammer.md` | C# 코딩 규칙/아키텍처 |
| Android 역할 규칙 | `docs/AndroidProgrammer.md` | Kotlin/Compose 규칙/UI설계 |
| 통신 프로토콜 | `protocol/MessageProtocol.md` | JSON 메시지 상세 스펙 |
| Windows 작업 지시 | `docs/Windows_Tasks.md` | C# 세션용 상세 작업 목록 |
| Android 작업 지시 | `docs/Android_Tasks.md` | Android 세션용 상세 작업 목록 |
