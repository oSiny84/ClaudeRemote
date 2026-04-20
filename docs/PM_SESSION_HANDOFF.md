# ClaudeRemote — PM Session Handoff

> **이 문서의 목적**: 다른 컴퓨터 또는 새 Claude Code 세션에서 이 프로젝트의 PM 역할을 이어받을 때 빠르게 컨텍스트를 복구하기 위한 문서.
>
> **사용 방법**: 새 세션에서 "`docs/PM_SESSION_HANDOFF.md` 읽고 PM 역할 이어받아" 라고 지시하면 됩니다.

**최종 업데이트**: 2026-04-15

---

## 1. 프로젝트 개요

**ClaudeRemote**: Windows PC의 Claude Code 데스크톱 앱을 Android 스마트폰에서 원격으로 모니터링/제어하는 시스템.

- Windows(C# WPF) 서버가 Claude Code 앱을 UIAutomation으로 제어
- Android(Kotlin Compose) 클라이언트가 WebSocket으로 원격 조작
- 같은 Wi-Fi 네트워크에서만 동작

## 2. 기술 스택

### Windows
- .NET 8.0 (net8.0-windows) + WPF
- C# 12, MVVM 패턴
- UIAutomation API (System.Windows.Automation)
- Fleck (WebSocket 서버, 포트 8765)
- Kestrel (HTTP 파일 서버, 포트 8766) — `Microsoft.AspNetCore.App` FrameworkReference
- CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, Serilog

### Android
- Kotlin 1.9.22
- Jetpack Compose (BOM 2024.02.00) + Material Design 3
- compileSdk 34, minSdk 26
- OkHttp 4.12.0 (WebSocket)
- kotlinx-serialization-json 1.6.3
- DataStore Preferences (설정 저장)
- Foreground Service (백그라운드 연결 유지)

### 통신
- WebSocket JSON (`ws://`, 포트 8765) — 메시지
- HTTP GET (포트 8766) — 파일 다운로드
- 하트비트 30초, 대용량 메시지 10KB 초과 시 8KB 청크 분할

---

## 3. 세션 운영 방식 (중요)

이 프로젝트는 **3개의 독립된 Claude Code 세션**을 운영합니다:

| 세션 | 역할 | 참조 문서 |
|------|------|-----------|
| **PM** (현재) | 아키텍처 설계, 작업 지시, 통합 검증 | `docs/PM.md`, `docs/PM_Phases.md` |
| **C#** | Windows 서버 구현 | `docs/Windows_Tasks.md`, `docs/WindowsProgrammer.md` |
| **Android** | Android 클라이언트 구현 | `docs/Android_Tasks.md`, `docs/AndroidProgrammer.md` |

### PM 세션 지시 형식
```
C# 세션에 붙여넣기:
> docs/Windows_Tasks.md 읽고 Phase N 작업 진행해.
> [구체적인 작업 내용]
> 완료 후 docs/reports/PhaseN_Windows_WorkReport.md로 보고서 작성.

Android 세션에 붙여넣기:
> docs/Android_Tasks.md 읽고 Phase N 작업 진행해.
> [구체적인 작업 내용]
> 완료 후 docs/reports/PhaseN_Android_WorkReport.md로 보고서 작성.
```

### PM 작업 흐름
1. 양쪽 세션에 작업 지시
2. 양쪽 보고서 수령 후 PM 리뷰 작성 (`docs/reports/PhaseN_PM_Review.md`)
3. 프로토콜 정합성 검증
4. 다음 Phase 또는 Hotfix 지시

---

## 4. 완료된 Phase 요약

| Phase | 주요 내용 |
|-------|-----------|
| 1 | 프로젝트 골격, 역할별 .md 생성, 프로토콜 정의 |
| 2 | WebSocket 양방향 통신 (8/8 테스트 PASS) |
| 3 | UIAutomation으로 Claude 앱 제어 (ClaudeUI_Map.md 작성) |
| 4 | Android 4개 화면 완성 (Output, Command, Manage, Settings) |
| 5 | 안정화: 프로세스 워처, Heartbeat, 큐잉, 청크 분할, Foreground Service |
| Hotfix1 | 서버 IP 표시, Chat 세션 폴백, 모드 전환 자동 갱신, 서버 주소 저장 |
| **Hotfix2** | **Code 모드 전용 전환** (Chat/Cowork 제거 — 중요) |
| 6 | Claude 선택 버튼 원격 제어 (`action_buttons` / `click_button`) |
| 6 Hotfix | 버튼 필터링 개선 (블랙리스트 → 위치 기반 + 화이트리스트) |
| 7 | 도구 실행 권한 다이얼로그 (Deny/Allow once/Always allow) — `category` 필드 추가 |
| 8 | 최소화 창 자동 복원 (`ShowWindow` + 작업 후 복원) |
| **9** | **Claude 앱 1.2581.0+ 새 UI 대응** (폴더 구조로 전면 재매핑) |
| 10 | 프로젝트 펼침/접힘 (`expanded` 필드), 선택 시 자동 세션 갱신 |
| 11 | 토큰 사용량 (인라인 + 종합 대시보드 팝업) |
| 12 | 파일 전송 (`browse_files` WebSocket + HTTP 다운로드) |
| 12 Hotfix1 | 드라이브 항목이 파일로 인식되는 버그 수정 |
| 12 Hotfix2 | HTTP.sys ACL 문제 → Kestrel 전환 |
| **13** | **채팅 말풍선 UI + 마크다운 렌더링** (진행 중 — 사용자가 다음에 지시 예정) |

**진행 중 / 보류:**
- Phase 13 (채팅 UI 고급화) — 사용자 지시 대기

---

## 5. 현재 릴리즈 상태

### v1.0
- Claude Code **`1.2581.0 (f10398)` 미만** 전용
- `Asset/ClaudeCodeRemote-1.0-Windows-x64.zip` (~3.0 MB)
- `Asset/ClaudeCodeRemote-1.0-android.apk` (~16.3 MB)

### v2.0 (현재 개발 버전)
- Claude Code **`1.2581.0 (f10398)` 이상** 전용 (폴더 구조 UI)
- `Asset/ClaudeCodeRemote-2.0-Windows-x64.zip` (업로드 대기)
- `Asset/ClaudeCodeRemote-2.0-android.apk` (업로드 대기)
- Phase 6~12까지 포함

---

## 6. 주요 아키텍처 결정사항

1. **MVVM + DI** (Windows): CommunityToolkit.Mvvm + Microsoft.Extensions.DependencyInjection
2. **WebSocket 싱글톤** (Android): `ClaudeRemoteApp.webSocketClient` — Service와 ViewModel이 공유
3. **UIAutomation RawView 필수**: Chromium/Electron의 웹 콘텐츠 요소는 ControlView 미포함 → `TreeWalker.RawViewWalker` DFS 사용
4. **텍스트 입력**: ValuePattern 미지원 → `SetFocus()` + 클립보드(Ctrl+A, Ctrl+V) + Enter
5. **Code 모드 고정**: Chat/Cowork 제거. `SwitchMode` 명령은 하위호환으로 남기되 무시
6. **WebSocket + HTTP 분리**: 메시지는 WebSocket(8765), 파일 다운로드는 HTTP GET(8766)
7. **재연결 race 방지**: Android `isUserDisconnect` 플래그 — 서버 끊김 시 `RECONNECTING` 유지 (MainScreen 튕김 방지)
8. **캐싱 + 해시 비교**: Windows UIAutomation 요소 캐싱 + SHA-256 해시로 변경 감지
9. **청크 분할**: 10KB 초과 메시지 → 8KB 청크 (`output_chunk`)
10. **Heartbeat**: 30초 간격
11. **프로세스 워처**: 5초 간격 Claude 앱 종료/재시작 자동 감지
12. **로그 롤링**: Serilog 일별 + 10MB/파일 + 최대 7일 보관

---

## 7. 반복 발생한 이슈 패턴 (중요!)

### 7-1. Claude 앱 업데이트 → UI 전면 변경
- **사례**: 1.2581.0 시점에 대대적 UI 변경. 프로젝트가 폴더 구조로 바뀜.
- **대응**: `ClaudeAutomationService`는 추상화 레이어로 분리되어 있어 이 파일과 `ClaudeUI_Map.md`만 수정하면 됨
- **향후 대비**: UI 변경 시 Phase 9처럼 재매핑 작업 필요

### 7-2. 블랙리스트 필터링 실패 반복 (중요!)
- **증상**: "Copy message", "Preview", "Create PR", "main" 등 UI 버튼이 계속 action_buttons로 잡힘
- **실패한 접근**: UI 버튼 이름을 하나씩 제외 목록에 추가 → 끝이 없음
- **성공한 접근**: **위치 기반 + 화이트리스트** — `turn-form` 근처의 특정 컨테이너 안에 있는 버튼만 수집
- **원칙**: 블랙리스트로 필터링하지 말고, 정확한 컨테이너를 찾아서 그 안의 요소만 수집

### 7-3. AutomationId 동적 변경
- `radix-_r_xxx_` 같은 ID는 앱 재시작/업데이트 시 변경됨
- **원칙**: Name 기반 탐색 우선

### 7-4. 최소화 상태
- Chromium은 최소화 시 렌더링 중단 → UIAutomation 트리 업데이트 안 됨
- **대응**: Phase 8의 `WithWindowRestoredAsync()` 헬퍼 — 작업 전 복원, 작업 후 원상복구

### 7-5. 버튼 클릭 후 텍스트 입력 실패
- **증상**: 선택 버튼 클릭 후 `SendInputAsync()` 실패
- **원인**: 캐시된 입력 필드 요소가 stale, 포커스 상실
- **해결**: 입력 시 항상 fresh 탐색 + 짧은 대기(500ms) 추가

### 7-6. "Type something else" 후 전송 실패
- **원인**: AskUserQuestion의 자유 입력 모드에서는 별도 입력 필드가 활성화됨
- **해결**: 입력 필드 탐색 시 `IsKeyboardFocusable=true` 요소 폴백

---

## 8. 프로토콜 주요 메시지

### Command (Android → Windows)
- `send_input` — 텍스트 입력
- `get_output` — 출력 조회 (scope: full/latest/summary)
- `switch_mode` — 모드 전환 (Code 고정, 무시됨)
- `get_sessions` / `select_session` / `add_session`
- `get_projects` / `select_project`
- `click_button` — 선택/권한 버튼 클릭
- `get_usage` / `get_usage_dashboard` — 사용량 조회
- `browse_files` / `request_download` — 파일 탐색/다운로드

### Content / Status (Windows → Android)
- `output_update` / `output_full` / `output_chunk`
- `action_buttons` — `category: "selection"|"permission"` + 버튼 목록
- `claude_status` — Claude 실행 상태, 현재 모드, usage 포함
- `heartbeat` — 30초 간격

---

## 9. 프로젝트 구조

```
C:\D_Develop\
├── README.md / README.ko.md           # 사용자 문서
├── Asset/                              # 빌드된 실행 파일 (v1.0, v2.0)
├── .gitignore
├── backup_claude_memory.ps1            # 메모리 백업 스크립트
├── docs/
│   ├── PM.md                           # PM 역할 규칙
│   ├── PM_Phases.md                    # Phase 관리 문서
│   ├── PM_SESSION_HANDOFF.md           # 이 문서
│   ├── WindowsProgrammer.md
│   ├── AndroidProgrammer.md
│   ├── Windows_Tasks.md
│   ├── Android_Tasks.md
│   ├── ClaudeUI_Map.md                 # 현재 Claude 앱 UI 분석
│   ├── ClaudeUI_Map_v1.md              # v1.0 시점 백업 (있는 경우)
│   ├── screenshot/                     # README 스크린샷
│   └── reports/                        # Phase별 작업 보고서
│
├── protocol/
│   └── MessageProtocol.md              # WebSocket JSON 프로토콜
│
├── ClaudeRemote.Windows/               # C# WPF 솔루션
│   ├── ClaudeRemote.Windows.sln
│   ├── ClaudeRemote.Windows/
│   │   ├── Models/                     # ClaudeMessage, SessionInfo, ProjectInfo, ButtonInfo, UsageInfo, UsageDashboard, ChatMessage
│   │   ├── Services/                   # ClaudeAutomationService, WebSocketServerService, FileServerService, SessionManager, MessageProcessor
│   │   ├── ViewModels/                 # MainViewModel
│   │   ├── MainWindow.xaml(.cs)
│   │   └── App.xaml(.cs)               # DI 컨테이너
│   └── ClaudeRemote.Tests/             # 통합 테스트 (콘솔 앱)
│
└── ClaudeRemote.Android/               # Android Compose 프로젝트
    └── app/src/main/java/com/clauderemote/
        ├── ClaudeRemoteApp.kt          # Application (싱글톤 WebSocket + 알림 채널)
        ├── MainActivity.kt             # 진입점 (자동 연결, 테마)
        ├── data/
        │   ├── model/Messages.kt       # 모든 데이터 모델
        │   ├── network/WebSocketClient.kt
        │   └── settings/AppSettings.kt # DataStore 래퍼
        ├── service/WebSocketService.kt # Foreground Service
        ├── ui/
        │   ├── theme/Theme.kt          # Material3
        │   ├── screens/                # Chat, Manage, Connection, Settings
        │   └── components/             # UsageDashboardDialog, FileBrowserDialog
        └── viewmodel/MainViewModel.kt
```

---

## 10. 다른 컴퓨터에서 작업 이어가기

### 백업 필수 항목
1. **프로젝트 폴더**: `C:\D_Develop\` 전체
2. **Claude 메모리**: `C:\Users\{username}\.claude\projects\C--D-Develop\memory\` 전체
   - `backup_claude_memory.ps1` 실행 시 OneDrive에 자동 백업됨

### 복원 순서
1. `D_Develop` 폴더 복사 또는 `git clone` (public repo)
2. `.claude\projects\...\memory` 폴더 복사 (선택 — 없어도 이 문서로 복구 가능)
3. **.NET 8 SDK** 설치
4. **Visual Studio 2022 (17.8+)** 또는 Rider
5. **Android Studio** + SDK 34
6. **Claude Code 데스크톱 앱** (버전에 맞는 ClaudeRemote 선택)

### 새 세션에서 PM 역할 이어받기
```
PM 세션에:
> docs/PM_SESSION_HANDOFF.md 읽고 PM 역할 이어받아.
> 현재 상태 파악 후 다음 단계 제안해.
```

---

## 11. 다음 작업 후보

사용자가 다음에 지시할 가능성이 있는 작업:

1. **Phase 13 (채팅 말풍선 UI 완성)** — 이미 부분 구현됨, 사용자 테스트 후 개선 지시 예정
   - C#: `GetChatMessagesAsync()` 구조화 완료
   - Android: 마크다운 렌더링 라이브러리 도입 필요 (예: `com.mikepenz:multiplatform-markdown-renderer-m3`)
2. **v2.0 Asset 빌드 업로드** — `ClaudeCodeRemote-2.0-*.zip/apk` 파일 생성
3. **Git 태그** — `v1.0`, `v2.0` 태그 달기
4. **GitHub public 공개 후 이슈 대응**
5. **Claude 앱 다음 업데이트 시 UI 재매핑** — Phase 9와 유사한 작업

---

## 12. 현재 세션 종료 시점 상태

- Phase 12 (파일 전송) 동작 확인 완료 — 드라이브 탭 버그 수정됨
- Phase 13 (채팅 UI) 기본 구조 진행 중 — 데이터 모델(`ChatMessage`), Windows `GetChatMessagesAsync` 구현됨
- README v1/v2 공존 버전으로 업데이트 완료
- .gitignore 정리 및 빌드 산출물 삭제 완료
- 백업 스크립트 `backup_claude_memory.ps1` 생성 완료
