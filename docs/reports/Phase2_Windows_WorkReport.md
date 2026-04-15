# Phase 2 작업 보고서 - Windows 통신 레이어 구축

**작성일**: 2026-04-10  
**담당**: Windows Developer Agent  
**프로젝트**: ClaudeRemote Windows  
**Phase**: 2 - 통신 레이어 구축  

---

## 1. 작업 요약

Phase 2의 핵심 목표인 **빌드 환경 확보, 통신 레이어 검증, 알려진 빌드 오류 수정**을 완료했습니다.  
.NET 8 SDK 설치, csproj/XAML 빌드 오류 수정, WebSocket 서버 8개 항목 통합 테스트 통과가 주요 성과입니다.

---

## 2. 변경 파일 목록

| # | 파일 | 변경 유형 |
|---|------|-----------|
| 1 | `ClaudeRemote.Windows.csproj` | 빌드 설정 수정 |
| 2 | `MainWindow.xaml` | XAML 호환성 수정 |
| 3 | `ClaudeRemote.Tests/` (신규) | 통합 테스트 프로젝트 추가 |

---

## 3. 상세 변경 내역

### 3-1. 빌드 환경 구축

**상태**: .NET SDK 미설치 상태에서 시작

**수행 내역**:
- .NET 8 SDK 8.0.419 설치 (`dotnet-install.ps1` 스크립트 사용, `~/.dotnet` 경로)
- NuGet 패키지 복원 성공 (Fleck, CommunityToolkit.Mvvm, Serilog 등)
- 솔루션 빌드 성공 (0 errors, 0 warnings)

---

### 3-2. ClaudeRemote.Windows.csproj — 빌드 오류 수정

**문제 2건**:

| 항목 | 문제 | 수정 |
|------|------|------|
| `SendKeys` 참조 | `System.Windows.Forms.SendKeys` 사용 시 어셈블리 참조 누락 | `<UseWindowsForms>true</UseWindowsForms>` 추가 |
| `Application` 모호성 | WPF `Application`과 WinForms `Application` 충돌 (CS0104) | `<Using Remove="System.Windows.Forms" />` 추가 |

**수정 후 csproj 핵심부**:
```xml
<UseWPF>true</UseWPF>
<UseWindowsForms>true</UseWindowsForms>
...
<Using Remove="System.Windows.Forms" />
```

**설계 결정**: WinForms 글로벌 using을 제거하되 어셈블리 참조는 유지. `ClaudeAutomationService.cs`에서 이미 `System.Windows.Forms.SendKeys`로 정규화된 이름을 사용하고 있어 코드 변경 불필요.

---

### 3-3. MainWindow.xaml — WPF 호환성 수정

**문제**: `StackPanel Spacing` 속성은 WinUI/UWP 전용이며 WPF에서 미지원 (런타임 XAML 파싱 오류 발생)

**수정 위치 3곳**:

| 위치 | 이전 | 수정 후 |
|------|------|---------|
| Header Status Indicators | `Spacing="16"` | 자식 StackPanel에 `Margin="16,0,0,0"` |
| Controls 영역 | `Spacing="12"` | 각 Button에 `Margin="0,0,12,0"` |
| Mode/Session 버튼 | (간접 영향) | `Margin="0,0,6,0"` 개별 적용 |

**영향 범위**: UI 레이아웃 간격만 변경, 기능 영향 없음

---

## 4. 통합 테스트 결과

Mock 기반 ClaudeAutomationService를 사용하여 통신 레이어를 독립 검증했습니다.

### 테스트 항목 및 결과 (8/8 PASS)

| # | 테스트 | 검증 내용 | 결과 |
|---|--------|-----------|------|
| 1 | WebSocket Server Start/Stop | 서버 시작/중지, 상태 플래그 정상 | PASS |
| 2 | Client Connection | 클라이언트 접속/해제 감지, 카운트 추적 | PASS |
| 3 | Message Receive | 클라이언트→서버 JSON 메시지 수신 정상 | PASS |
| 4 | Server Send | 서버→클라이언트 메시지 발신 정상 | PASS |
| 5 | get_output 처리 | command 수신 → content/output_update 응답 | PASS |
| 6 | send_input 처리 | command 수신 → response/send_input 응답 + Mock 입력 전달 확인 | PASS |
| 7 | Invalid JSON 처리 | 잘못된 JSON → error response 반환 (앱 크래시 없음) | PASS |
| 8 | 프로토콜 준수 | 응답에 id, type, action, payload, timestamp 필드 포함 확인 | PASS |

### 테스트 환경
- 테스트 프로젝트: `ClaudeRemote.Tests/` (콘솔 앱, .NET 8.0-windows)
- WebSocket 클라이언트: `System.Net.WebSockets.ClientWebSocket`
- 포트: 18765~18772 (프로덕션 포트 8765와 분리)
- 실행 명령: `dotnet run --project ClaudeRemote.Tests`

---

## 5. 프로토콜 준수 현황

`protocol/MessageProtocol.md` 대비 Windows 서버의 구현 상태:

| 프로토콜 항목 | 구현 상태 | 비고 |
|--------------|-----------|------|
| 메시지 기본 구조 (id, type, action, mode, payload, timestamp) | 완료 | UUID v4 자동 생성 |
| `send_input` 수신 처리 | 완료 | ClaudeAutomationService 위임 |
| `get_output` 수신 처리 (scope: full/latest/summary) | 완료 | |
| `switch_mode` 수신 처리 | 완료 | SessionManager 경유 |
| `get_sessions` / `select_session` / `add_session` | 완료 | |
| `get_projects` / `select_project` | 완료 | |
| `response` 송신 (success/error + 에러 코드) | 완료 | |
| `content` - `output_update` 송신 | 완료 | 폴링 기반 변경 감지 |
| `content` - `output_full` 송신 | 완료 | |
| `status` - `claude_status` 송신 | 완료 | |
| 에러 코드 9종 | 완료 | MessageProtocol.md 전체 코드 정의 |
| 하트비트 (heartbeat) | **미구현** | Phase 5 예정 |
| 대용량 청크 분할 (output_chunk) | **미구현** | Phase 5 예정 |

---

## 6. 현재 코드 상태 진단

빌드 및 통신 레이어는 정상이나, Phase 3에서 해결해야 할 항목:

| 항목 | 상태 | 설명 |
|------|------|------|
| Claude 앱 프로세스 탐색 | 미검증 | 실제 Claude 앱 프로세스명 확인 필요 |
| UIAutomation 요소 매핑 | 미구현 | Inspect.exe로 UI 트리 분석 후 구체화 필요 |
| 한글 입력 (IME) | 미검증 | SendKeys의 한글 처리 이슈 가능성 |
| 실시간 모니터링 | 구현됨 | 500ms 폴링, 실제 환경 최적화 필요 |

---

## 7. 다음 Phase 준비사항

Phase 3 (Claude 앱 연동) 진행 전 필요 사항:
- **Inspect.exe** 또는 **Accessibility Insights for Windows** 설치 → Claude 앱 UI 트리 분석
- Claude 데스크톱 앱 실제 프로세스명 확인 (작업 관리자)
- Android Phase 2 완료와 맞춰 **양방향 연동 테스트** 가능 여부 확인
- 하트비트, 청크 분할은 Phase 5로 이관 (현재 우선순위 낮음)
