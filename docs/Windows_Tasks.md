# ClaudeRemote - Windows 프로그래머 작업 지시서

## 프로젝트 정보
- **솔루션**: `ClaudeRemote.Windows/ClaudeRemote.Windows.sln`
- **기술 스택**: C# / .NET 8 / WPF / UIAutomation / Fleck WebSocket
- **역할 규칙**: `docs/WindowsProgrammer.md` 참조
- **프로토콜**: `protocol/MessageProtocol.md` 참조

## 현재 소스 파일 구조
```
ClaudeRemote.Windows/ClaudeRemote.Windows/
├── App.xaml / App.xaml.cs                    # DI 컨테이너, Serilog 설정
├── MainWindow.xaml / MainWindow.xaml.cs       # 메인 UI (다크 테마)
├── Models/
│   ├── ClaudeMessage.cs                      # JSON 메시지 모델 + 상수
│   ├── SessionInfo.cs                        # 세션 정보
│   └── ProjectInfo.cs                        # 프로젝트 정보
├── Services/
│   ├── IClaudeAutomationService.cs           # Claude 제어 인터페이스
│   ├── ClaudeAutomationService.cs            # UIAutomation 구현체
│   ├── IWebSocketServerService.cs            # WebSocket 서버 인터페이스
│   ├── WebSocketServerService.cs             # Fleck 기반 서버 구현체
│   ├── ISessionManager.cs                    # 세션/프로젝트 관리 인터페이스
│   ├── SessionManager.cs                     # 세션 관리 구현체
│   ├── IMessageProcessor.cs                  # 메시지 처리 인터페이스
│   └── MessageProcessor.cs                   # 메시지 파싱/라우팅 구현체
└── ViewModels/
    └── MainViewModel.cs                      # MVVM ViewModel
```

---

# Phase 2: 통신 레이어 구축

## 작업 목록

### 2-1. 빌드 환경 확인
- [ ] .NET 8 SDK 설치 확인 (`dotnet --version` → 8.x)
- [ ] 솔루션 열기: Visual Studio 2022 (17.8+)
- [ ] NuGet 패키지 복원 실행
- [ ] 빌드 성공 확인 (오류 0개)
- [ ] 빌드 오류 발생 시: `System.Windows.Forms` 참조가 필요하면 csproj에 `<UseWindowsForms>true</UseWindowsForms>` 추가

### 2-2. WebSocket 서버 검증
- [ ] 앱 실행 → "Start Server" 버튼 클릭 → 포트 8765 리슨 확인
- [ ] 외부 도구로 테스트: `wscat -c ws://localhost:8765` 또는 브라우저 콘솔에서 `new WebSocket('ws://localhost:8765')`
- [ ] 클라이언트 접속 시 UI에 "Connected clients: 1" 표시 확인
- [ ] 클라이언트 메시지 수신 시 Log 영역에 표시 확인
- [ ] 서버 중지 → 클라이언트 연결 해제 확인

### 2-3. 메시지 처리 검증
- [ ] wscat으로 아래 JSON 전송, 응답 확인:
```json
{"type":"command","action":"get_output","payload":{"scope":"latest"}}
```
- [ ] 응답 형식이 `protocol/MessageProtocol.md` 규격에 맞는지 확인
- [ ] 잘못된 JSON 전송 시 에러 응답 또는 무시 확인

### 2-4. 알려진 수정 필요 사항
- `ClaudeAutomationService.cs`에서 `System.Windows.Forms.SendKeys` 사용 → csproj에 `<UseWindowsForms>true</UseWindowsForms>` 필요할 수 있음
- `MainWindow.xaml`의 `StackPanel Spacing` 속성 → .NET 8 WPF에서 미지원 시 `Margin`으로 대체

---

# Phase 3: Claude 앱 연동

## 작업 목록

### 3-1. Claude 앱 UI 트리 분석 (가장 중요)
- [ ] **Inspect.exe** 또는 **Accessibility Insights for Windows** 설치
- [ ] Claude 데스크톱 앱 실행 후 UI 트리 탐색
- [ ] 다음 요소의 AutomationId, ClassName, ControlType 기록:
  - 대화 출력 영역 (메시지들이 표시되는 영역)
  - 텍스트 입력 필드
  - 전송 버튼 (있는 경우)
  - Chat/Code 탭 또는 전환 버튼
  - 사이드바 세션 목록
  - 새 대화/세션 버튼
  - (Code 모드) 프로젝트 선택 영역
- [ ] 분석 결과를 `docs/ClaudeUI_Map.md`로 문서화

### 3-2. ClaudeAutomationService 구체화
- [ ] `FindClaudeWindowAsync()` - 프로세스명 확인 후 정확한 이름으로 수정
  - Claude 앱의 실제 프로세스명을 `Process.GetProcesses()` 또는 작업관리자로 확인
- [ ] `GetOutputAsync()` - UI 트리 분석 결과에 맞게 요소 탐색 로직 수정
  - Claude는 Electron 기반일 가능성 높음 → `ControlType.Document` 또는 `ControlType.Text` 탐색
  - 필요 시 `TreeWalker`로 깊이 우선 탐색
- [ ] `SendInputAsync()` - 실제 입력 필드 요소에 맞게 수정
  - `ValuePattern`이 안 되면 `SetFocus()` + `SendKeys` 사용
  - 한글 입력 이슈 확인 (IME 관련)
- [ ] `SwitchModeAsync()` - 실제 탭/버튼 요소에 맞게 수정
- [ ] `GetSessionsAsync()` - 사이드바 세션 목록 요소 탐색
- [ ] `AddSessionAsync()` - 새 대화 버튼 요소 탐색
- [ ] `GetProjectsAsync()` / `SelectProjectAsync()` - Code 모드 전용

### 3-3. 모니터링 안정화
- [ ] 폴링 간격 500ms → 실제 테스트 후 최적값 조정
- [ ] 출력 비교 로직: 단순 문자열 비교 → 해시 비교로 최적화
- [ ] Claude가 응답 생성 중(스트리밍)인지 감지하는 방법 탐색
- [ ] 메모리 누수 확인: 장시간 모니터링 시 메모리 증가 없어야 함

### 3-4. 통합 테스트
- [ ] Claude 앱 실행 → "Connect Claude" → 연결 성공
- [ ] Claude Output 영역에 실제 대화 내용 표시
- [ ] Android에서 명령 → Claude에 입력 → 응답 → Android에 전달
- [ ] 모드 전환 동작 확인

---

# Phase 5: 안정화

## 작업 목록

### 5-1. Claude 앱 상태 관리
- [ ] Claude 앱 종료 감지 → 상태를 "Disconnected"로 변경
- [ ] Claude 앱 재시작 감지 → 자동 재연결
- [ ] 감지 방법: 프로세스 모니터링 (별도 타이머, 5초 간격)

### 5-2. 통신 안정성
- [ ] WebSocket 연결 끊김 시 보내지 못한 메시지 큐잉
- [ ] 재연결 후 큐 flush
- [ ] 대용량 출력 (10KB+) 청크 분할 전송

### 5-3. 로깅 및 운영
- [ ] Serilog 일별 롤링 로그 (최대 7일)
- [ ] 에러 발생 시 사용자 알림 (StatusBar 또는 Toast)
- [ ] 시스템 트레이 최소화 (선택사항)

### 5-4. 성능
- [ ] UIAutomation 폴링 시 CPU 사용률 5% 이하
- [ ] 메모리 사용량 100MB 이하 유지
- [ ] UI 응답 시간 100ms 이내
