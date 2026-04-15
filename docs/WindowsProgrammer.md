# Windows Programmer - 역할 규칙

## 역할 정의
- **직급**: 최고 수준의 Windows 데스크톱 프로그래머
- **전문 분야**: C# / WPF / .NET / UIAutomation / 네트워크 프로그래밍
- **프로젝트 담당**: ClaudeRemote Windows 데스크톱 애플리케이션

---

## 기술 스택 및 규칙

### 프레임워크
- **.NET 8** (최신 LTS)
- **WPF** (Windows Presentation Foundation)
- **MVVM 패턴** 필수 적용

### 핵심 라이브러리
- `System.Windows.Automation` - UIAutomation (Claude 앱 제어)
- `System.Net.WebSockets` 또는 **Fleck/WebSocketSharp** - WebSocket 서버
- `System.Text.Json` - JSON 직렬화/역직렬화
- `CommunityToolkit.Mvvm` - MVVM 헬퍼

### NuGet 패키지 (승인됨)
- `CommunityToolkit.Mvvm` - ObservableObject, RelayCommand
- `Fleck` - 경량 WebSocket 서버
- `Microsoft.Extensions.DependencyInjection` - DI 컨테이너
- `Serilog` - 구조화된 로깅

---

## 코딩 규칙

### 1. 아키텍처 원칙
- **MVVM 패턴 엄수**: View ↔ ViewModel ↔ Model 분리
- **DI(Dependency Injection)**: 서비스는 인터페이스로 정의 후 DI 컨테이너에 등록
- **비동기 우선**: UI 스레드 블로킹 금지, `async/await` 사용
- **UIAutomation 추상화**: Claude 앱 UI 변경 시 수정 최소화를 위해 래퍼 클래스 사용

### 2. 네이밍 규칙
- 클래스: PascalCase (`ClaudeAutomationService`)
- 인터페이스: I 접두사 (`IWebSocketServer`)
- 프라이빗 필드: _camelCase (`_sessionManager`)
- 메서드: PascalCase, 동사 시작 (`GetClaudeOutputAsync`)
- 비동기 메서드: Async 접미사 (`SendMessageAsync`)

### 3. 에러 처리
- UIAutomation 실패 시 재시도 로직 (최대 3회)
- WebSocket 연결 끊김 시 자동 재접속 시도
- 모든 예외를 Serilog로 기록
- 사용자에게 상태 표시 (연결됨/끊김/오류)

### 4. 스레딩
- UIAutomation 작업은 별도 스레드에서 실행
- UI 업데이트는 반드시 `Dispatcher.Invoke` 사용
- WebSocket 메시지 처리는 비동기로 수행
- 동시성 문제 방지를 위해 `ConcurrentQueue` 활용

---

## 모듈별 책임

### ClaudeAutomationService
```
책임:
- Claude 앱 프로세스 찾기 및 메인 윈도우 핸들 확보
- 대화 영역에서 텍스트 읽기 (출력 모니터링)
- 입력 필드에 텍스트 입력 및 전송
- Chat/Code 모드 전환
- 세션 목록 조회, 새 세션 생성, 세션 선택
- Code 모드에서 프로젝트 목록 조회 및 전환
- 변경 감지 (폴링 기반, 설정 가능한 간격)
```

### WebSocketServerService
```
책임:
- WebSocket 서버 시작/중지 (설정 가능한 포트)
- 클라이언트 연결/해제 관리
- 메시지 수신 → MessageProcessor로 전달
- 메시지 송신 (Claude 출력 → Android)
- 연결 상태 모니터링 및 이벤트 발생
```

### SessionManager
```
책임:
- 현재 활성 모드 (Chat/Code) 추적
- 세션 목록 캐싱 및 갱신
- 프로젝트 목록 캐싱 및 갱신
- ClaudeAutomationService를 통한 세션/프로젝트 전환 조율
```

### MessageProcessor
```
책임:
- Android에서 온 JSON 메시지 파싱
- 명령 타입별 적절한 서비스로 라우팅
- Claude 출력을 프로토콜 형식으로 변환
- 전체 출력 / 요약(마지막 핵심부분) 모드 지원
```

---

## UIAutomation 전략

### Claude 앱 탐색 순서
1. `Process.GetProcessesByName("claude")` 로 프로세스 찾기
2. `AutomationElement.FromHandle(process.MainWindowHandle)` 로 루트 요소 확보
3. `TreeWalker` 또는 `FindAll`로 UI 트리 탐색
4. AutomationId, ClassName, Name 속성으로 요소 식별
5. 텍스트 패턴(`TextPattern`), 값 패턴(`ValuePattern`) 등으로 상호작용

### 폴링 전략
- 기본 폴링 간격: 500ms
- 변경 감지 시 즉시 Android로 전송
- 이전 출력과 비교하여 변경분만 감지
- CPU 부하 모니터링으로 폴링 간격 자동 조절

---

## 품질 기준
- 메모리 누수 없음 (Dispose 패턴 준수)
- UI 응답 시간 100ms 이내
- WebSocket 메시지 지연 50ms 이내
- Claude 앱 미실행 시 우아한 대기 모드
