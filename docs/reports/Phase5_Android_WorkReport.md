# Phase 5 작업 보고서 - Android 안정화

**작성일**: 2026-04-10  
**담당**: Android Developer Agent  
**프로젝트**: ClaudeRemote Android  
**Phase**: 5 - 안정화

---

## 1. 작업 요약

Phase 5의 목표: **백그라운드 WebSocket 유지, 알림 시스템, 설정 영속화, 안정성 확보**

**신규 파일**: 3개  
**변경 파일**: 6개  
**핵심 아키텍처 변경**: WebSocketClient 소유권이 ViewModel → Application 싱글톤으로 이전

---

## 2. 변경 파일 목록

| # | 파일 | 유형 | 변경 |
|---|------|------|------|
| 1 | `data/settings/AppSettings.kt` | **신규** | DataStore Preferences 래퍼 |
| 2 | `service/WebSocketService.kt` | **신규** | Foreground Service |
| 3 | `ui/screens/SettingsScreen.kt` | **신규** | 설정 UI |
| 4 | `ClaudeRemoteApp.kt` | 수정 | 싱글톤 Client + 알림 채널 |
| 5 | `MainActivity.kt` | 수정 | Service 생명주기 + 자동연결 |
| 6 | `viewmodel/MainViewModel.kt` | 수정 | DataStore 통합 + 싱글톤 사용 |
| 7 | `ui/screens/MainScreen.kt` | 수정 | Settings 아이콘 추가 |
| 8 | `ui/theme/Theme.kt` | 수정 | themeOverride 파라미터 |
| 9 | `AndroidManifest.xml` | 수정 | Service 선언 + 퍼미션 |

---

## 3. 상세 변경 내역

### 3-1. AppSettings — DataStore Preferences

**신규 파일**: `data/settings/AppSettings.kt`

| 설정 키 | 타입 | 기본값 | 용도 |
|---------|------|--------|------|
| `server_host` | String | "192.168.0.1" | 서버 IP |
| `server_port` | String | "8765" | 서버 포트 |
| `auto_connect` | Boolean | false | 앱 시작 시 자동 연결 |
| `notify_output` | Boolean | true | 새 출력 알림 |
| `notify_disconnect` | Boolean | true | 연결 끊김 알림 |
| `theme_mode` | String | "system" | 테마 (system/dark/light) |

- 모든 값은 `Flow<T>`로 노출 → Compose에서 실시간 반영
- `saveConnection()` — host + port 원자적 저장

---

### 3-2. WebSocketService — Foreground Service

**신규 파일**: `service/WebSocketService.kt`

**Intent Actions**:
| Action | 동작 |
|--------|------|
| `ACTION_START` | 서비스 시작 + Foreground 전환 |
| `ACTION_STOP` | 서비스 종료 |
| `ACTION_APP_FOREGROUND` | 앱이 포그라운드 → 알림 억제 |
| `ACTION_APP_BACKGROUND` | 앱이 백그라운드 → 알림 활성화 |

**알림 채널 3개**:
| 채널 ID | 중요도 | 용도 |
|---------|--------|------|
| `channel_service` | LOW | Persistent 연결 상태 알림 (서비스 유지용) |
| `channel_output` | DEFAULT | 새 Claude 출력 알림 |
| `channel_alert` | HIGH | 연결 끊김 경고 |

**동작 상세**:
- `START_STICKY` — OS kill 후 자동 재시작
- 연결 상태 변경 시 Persistent Notification 텍스트 업데이트
- 백그라운드에서 `content` 메시지 수신 → 출력 알림 (100자 미리보기)
- Connected → Reconnecting 전환 시 → 끊김 경고 알림
- 모든 알림 탭 → `MainActivity` 복귀 (`FLAG_ACTIVITY_SINGLE_TOP`)
- 알림 발송 전 DataStore 설정 확인 (OFF면 발송 안 함)

---

### 3-3. SettingsScreen — 설정 UI

**신규 파일**: `ui/screens/SettingsScreen.kt`

**UI 구조**:
```
┌─ Settings ──────────────────┐
│ ← Back                      │
│                              │
│ ▸ Server Connection          │
│   [IP Address field]         │
│   [Port field]               │
│   [Save Connection button]   │
│                              │
│ ▸ Behavior                   │
│   Auto-connect on launch [•] │
│                              │
│ ▸ Notifications              │
│   Output notifications   [•] │
│   Disconnect alerts      [•] │
│                              │
│ ▸ Appearance                 │
│   (System) (Dark) (Light)    │
│                              │
│ ▸ About                      │
│   ClaudeRemote v1.0          │
│   Protocol v1.1              │
└──────────────────────────────┘
```

- `SettingsSection` — 섹션별 카드 레이아웃
- `SettingsSwitch` — 라벨 + 설명 + Switch
- TopAppBar Back 버튼 → MainScreen 복귀

---

### 3-4. ClaudeRemoteApp — 아키텍처 변경

**핵심 변경: WebSocketClient 소유권 이전**

```
[이전] MainViewModel → WebSocketClient (ViewModel 파괴 시 연결 끊김)
[수정] ClaudeRemoteApp → WebSocketClient (앱 프로세스 생존 동안 유지)
```

| 추가 항목 | 설명 |
|-----------|------|
| `webSocketClient` | Application 레벨 싱글톤 |
| `appSettings` | AppSettings 인스턴스 |
| `createNotificationChannels()` | 3개 채널 생성 (onCreate) |

---

### 3-5. MainActivity — Service 생명주기

| 변경 | 설명 |
|------|------|
| `onConnected` 콜백 | `startWebSocketService()` — Foreground Service 시작 |
| `onDisconnect` 콜백 | `stopWebSocketService()` — Service 종료 |
| `onStart()` | `ACTION_APP_FOREGROUND` 전송 → 알림 억제 |
| `onStop()` | `ACTION_APP_BACKGROUND` 전송 → 알림 활성화 |
| `LaunchedEffect(Unit)` | 자동 연결 설정 확인 → `viewModel.connect()` |
| `POST_NOTIFICATIONS` | Android 13+ 런타임 퍼미션 요청 |
| `themeMode` | ViewModel에서 수집 → `ClaudeRemoteTheme(themeOverride=)` 전달 |

---

### 3-6. MainViewModel — DataStore 통합

| 변경 | 설명 |
|------|------|
| WebSocketClient 출처 | `ClaudeRemoteApp.instance.webSocketClient` (싱글톤) |
| `loadSettings()` | DataStore의 6개 Flow를 StateFlow로 수집 |
| `saveConnectionSettings()` | host + port DataStore 저장 |
| `setAutoConnect/setNotifyOutput/setNotifyDisconnect/setThemeMode` | 설정 변경 즉시 DataStore 반영 |
| `onCleared()` | `webSocketClient.destroy()` 제거 — Service가 관리 |

---

### 3-7. AndroidManifest

| 추가 항목 | 설명 |
|-----------|------|
| `FOREGROUND_SERVICE_DATA_SYNC` | API 34 Foreground Service 타입 퍼미션 |
| `POST_NOTIFICATIONS` | Android 13+ 알림 퍼미션 |
| `<service>` 선언 | `WebSocketService`, `foregroundServiceType="dataSync"` |
| `configChanges` | Activity에 회전/크기 변경 처리 (불필요한 재생성 방지) |

---

### 3-8. Theme.kt

| 변경 | 설명 |
|------|------|
| `themeOverride` 파라미터 | `"system"`, `"dark"`, `"light"` 중 선택 |
| 기존 `darkTheme` 파라미터 | 제거 → `themeOverride`로 통합 |

---

## 4. 아키텍처 변경 다이어그램

```
[이전 구조]
  MainActivity → MainViewModel → WebSocketClient
                                 (ViewModel 스코프)

[Phase 5 구조]
  ClaudeRemoteApp (Application)
    ├── webSocketClient (싱글톤)
    └── appSettings (DataStore)

  WebSocketService (Foreground Service)
    ├── observes: webSocketClient.connectionState
    ├── observes: webSocketClient.rawMessages
    └── manages: Notifications (3 channels)

  MainActivity
    ├── starts/stops: WebSocketService
    ├── sends: foreground/background events
    └── reads: themeMode from ViewModel

  MainViewModel
    ├── uses: ClaudeRemoteApp.webSocketClient
    ├── reads/writes: AppSettings (DataStore)
    └── exposes: all state flows to UI
```

**변경 이유**:
- ViewModel은 Activity 재생성(회전, 설정 변경)에는 살아남지만, 프로세스 kill에는 파괴됨
- Foreground Service가 프로세스를 alive로 유지 → WebSocket 연결 보존
- Application 싱글톤이 Client를 소유 → Service와 ViewModel 모두 동일 인스턴스 사용

---

## 5. QA 검증 항목

### Foreground Service (5-1)
- [ ] 연결 후 홈 버튼 → 알림 바에 "Connected to server" 표시
- [ ] 서버 중지 → 알림이 "Reconnecting..." 으로 변경
- [ ] 앱 최근앱에서 스와이프 → 서비스 재시작 확인 (`START_STICKY`)
- [ ] Doze 모드 진입 후 → WebSocket ping/pong 유지 여부

### 알림 (5-2)
- [ ] 백그라운드에서 새 출력 수신 → "New Claude Output" 알림
- [ ] 백그라운드에서 연결 끊김 → "Connection Lost" 알림
- [ ] 알림 탭 → 앱 복귀
- [ ] 포그라운드에서는 알림 미표시
- [ ] 설정에서 알림 OFF → 알림 미표시

### 설정 (5-3)
- [ ] IP/Port 입력 → Save → 앱 재시작 → 값 유지
- [ ] Auto-connect ON → 앱 재시작 → 자동 연결 시도
- [ ] 테마 System/Dark/Light 전환 → 즉시 반영
- [ ] Android 13+ 알림 퍼미션 요청 다이얼로그

### 안정성 (5-4)
- [ ] 화면 회전 → 연결 상태/출력/세션 유지
- [ ] 멀티윈도우 → 정상 동작
- [ ] 30분+ 사용 후 메모리 확인
- [ ] ANR 미발생 확인

---

## 6. 소스 파일 구조 (최종)

```
ClaudeRemote.Android/app/src/main/java/com/clauderemote/
├── ClaudeRemoteApp.kt                        # Application (싱글톤 Client + Settings + 알림 채널)
├── MainActivity.kt                           # Activity (Service 생명주기 + 자동연결 + 테마)
├── data/
│   ├── model/
│   │   └── Messages.kt                      # 프로토콜 v1.1 데이터 모델
│   ├── network/
│   │   └── WebSocketClient.kt               # WebSocket 통신 (싱글톤으로 사용)
│   └── settings/
│       └── AppSettings.kt                   # ★ DataStore Preferences 래퍼
├── service/
│   └── WebSocketService.kt                  # ★ Foreground Service + 알림
├── ui/
│   ├── theme/
│   │   └── Theme.kt                         # Material3 테마 (Override 지원)
│   └── screens/
│       ├── ConnectionScreen.kt              # 연결 화면
│       ├── MainScreen.kt                    # 메인 (Settings 아이콘 추가)
│       ├── OutputScreen.kt                  # 마크다운 출력 뷰어
│       ├── CommandScreen.kt                 # 명령 입력 + 히스토리
│       ├── ManageScreen.kt                  # 세션/프로젝트/모드 관리 (3-mode)
│       └── SettingsScreen.kt                # ★ 설정 화면
└── viewmodel/
    └── MainViewModel.kt                     # ViewModel (DataStore + 싱글톤 Client)
```

★ = Phase 5에서 신규 생성

---

## 7. 다음 단계 제안

Phase 5로 Android 앱의 핵심 기능 구현이 완료되었습니다.

**남은 QA 작업**:
- 실기기/에뮬레이터에서 전체 플로우 테스트
- Windows 서버와의 통합 테스트
- Doze 모드/배터리 최적화 실측
- 장시간 연결 유지 안정성 검증

**향후 개선 가능 항목**:
- LeakCanary 의존성 추가 (디버그 빌드 전용)
- ProGuard/R8 난독화 설정 (릴리즈 빌드)
- 앱 아이콘 커스텀 디자인
- 연결 히스토리 (최근 서버 목록)
