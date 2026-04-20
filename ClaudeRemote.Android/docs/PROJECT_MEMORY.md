# ClaudeRemote — Project Memory

**마지막 갱신**: 2026-04-20
**대상**: 다른 머신/세션에서 이어 작업할 때 빠른 맥락 복구용

---

## 1. 프로젝트 개요

**ClaudeRemote** — Windows PC의 Claude Code를 Android에서 원격 제어하는 시스템.

| 구성 | 기술 | 역할 |
|------|------|------|
| 서버 | C# WPF (Windows) | Claude Code 터미널 스크래핑/제어, WebSocket 호스팅, HTTP 파일 서버 |
| 클라이언트 | Kotlin + Jetpack Compose (Android) | 원격 UI, 세션/프로젝트 관리, 파일 브라우저, 마크다운 출력 렌더 |
| 전송 | WebSocket (제어) + HTTP (파일) | JSON 프로토콜 v1.2 (Code 모드 전용) |

개발자 구성: 사용자(PM)가 Android 쪽 주도, 별도 C# 개발자가 서버 쪽 담당. 프로토콜 조율만 같이.

---

## 2. 경로 / 환경

### 저장소
- Android: `C:\D_Develop\ClaudeRemote.Android\`
- 서버: `C:\D_Develop\ClaudeRemote\` (본 리포에는 없음, C# 개발자 로컬)

### Android 빌드 환경 (Windows)
```
JAVA_HOME:        C:/Program Files/Android/Android Studio/jbr
ANDROID_SDK_ROOT: C:/Users/osiny/AppData/Local/Android/Sdk
Gradle:           8.5 (cached at ~/.gradle/wrapper/dists/gradle-8.5-bin/)
Kotlin:           1.9.22 (compose compiler 1.5.8)
Compose BOM:      2024.02.00  (Compose UI 1.6.2, Material3 1.2.x)
minSdk:           26,  targetSdk: 34
```

### 빌드 커맨드
```bash
cd C:/D_Develop/ClaudeRemote.Android
./gradlew assembleDebug
# 산출물: app/build/outputs/apk/debug/app-debug.apk
```

빌드 시간 기준: 약 1~2분 (캐시 히트 시 30초 이내).

---

## 3. 아키텍처 요약

### Android 패키지 구조
```
com.clauderemote
├── ClaudeRemoteApp          — Application (singleton WebSocketClient, AppSettings, 알림 채널)
├── MainActivity             — 단일 Activity + Compose
├── data/
│   ├── model/Messages.kt    — 프로토콜 상수, 데이터 클래스 (ClaudeMessage, SessionInfo,
│   │                          ProjectInfo, ButtonInfo, UsageDashboard+UsageLimit+ContextWindow,
│   │                          FileEntry, ChatMessage, ConnectionState, ClaudeStatus)
│   └── settings/AppSettings — DataStore Preferences
├── network/WebSocketClient  — OkHttp WebSocket, 자동 재연결, rawMessages Flow
├── service/WebSocketService — Foreground Service, 백그라운드 알림 (output/disconnect/permission)
├── viewmodel/MainViewModel  — 모든 상태 소유, 메시지 파싱, 명령 전송
└── ui/
    ├── screens/
    │   ├── ChatScreen       — 채팅 말풍선 LazyColumn + Action Buttons + Input
    │   ├── ManageScreen     — Projects → Sessions + 대시보드/파일 브라우저 버튼
    │   └── SettingsScreen
    │   └── MainScreen       — 하단 탭 네비 (Chat | Manage)
    └── components/
        ├── MessageBubble       — user/assistant/tool 버블
        ├── MarkdownView        — Markwon + 터미널 wrap 전처리
        ├── UsageDashboardDialog— Plan/Weekly 사용량
        └── FileBrowserDialog   — ModalBottomSheet 파일 탐색
```

### 프로토콜 (WebSocket JSON v1.2, Code 모드 전용)

**공통 envelope**
```json
{ "type": "...", "action": "...", "mode": "code", "payload": { ... } }
```

**type**: `command` (→서버) / `response` (서버→) / `content` (서버→, 푸시) / `status` (서버→, 푸시)

**주요 action**:
- 세션: `get_sessions`, `select_session`, `add_session`
- 프로젝트: `get_projects`, `select_project` (응답에 `projects[]` + `sessions[]` 동반)
- 출력: `get_output`(scope=full/latest/summary), `output_update`, `output_full`(messages[]), `output_chunk`(재조립)
- 버튼: `action_buttons`(category=permission/selection), `click_button`
- 사용량: `get_usage_dashboard`
- 파일: `browse_files`(currentPath/parentPath/entries[name,path,type,size]), `request_download`(→downloadUrl, fileName)
- 상태: `claude_status`, `heartbeat`, `switch_mode`

### HTTP 다운로드
- 서버가 `request_download` 응답으로 `downloadUrl` 반환 (예: `http://LAN_IP:8766/download?path=...`)
- Android `DownloadManager`가 `DIRECTORY_DOWNLOADS`로 저장, 시스템 알림 자동 처리
- 경로 권위는 서버가 보유 (`FileEntry.path` 필드) — 클라는 그대로 전달

---

## 4. Phase 히스토리 (완료)

각 Phase별 상세 보고서는 `docs/reports/Phase{N}_Android_WorkReport.md`.

| Phase | 주제 | 핵심 결과 |
|-------|------|-----------|
| ~6 | 초기 UI, 채팅/코워크 모드 정리 | 이력 대부분 유실, 요약만 존재 |
| 7 | 권한 요청 UI + 알림 | `action_buttons` category=permission → 빨강/주황 UI, 햅틱, HIGH 알림 |
| ~9 | (기록 불완전) | 청크 재조립, 세션 관리 등 정착 |
| 10 | 프로젝트 폴더 트리 UI | `ProjectInfo.expanded`, 섹션 재정렬(Projects→Sessions), Sessions 헤더에 프로젝트명 |
| 11 | 사용량 대시보드 다이얼로그 | `UsageDashboard/ContextWindow/UsageLimit`, Material3 AlertDialog, progress bar 색상 3단계 |
| 12 | 파일 브라우저 + 다운로드 | ModalBottomSheet, `browse_files`/`request_download`, DownloadManager 연동 |
| 12-Hotfix1 | 드라이브 탭 버그 | C# 개발자가 `FileEntry.path` 분리로 정리 (display name vs filesystem path) |
| 13 | 채팅 말풍선 + 마크다운 | MessageBubble 3종, Markwon 직접 연동, 터미널 wrap 전처리 |

### Phase 13 후일담 (렌더러 선정 과정)
1. `com.mikepenz:multiplatform-markdown-renderer-m3:0.28.0` 시도 → 빌드 통과 후 **런타임 크래시** (Compose 1.7/Kotlin 2.0 ABI 요구, 본 프로젝트는 1.6/1.9)
2. `com.github.jeziellago:compose-markdown:0.5.4` (JitPack) → 동작하나 렌더 품질 기본 수준
3. **Markwon 직접 연동**(현재) — AndroidView + 7개 플러그인(core, tables, strikethrough, tasklist, linkify, html, image) + MarkwonTheme 커스터마이즈

### Phase 13 터미널 wrap 이슈 해결 과정
- 1차: `SoftLineBreak` visitor → 모든 `\n` 가시화 → 터미널 wrap도 모두 줄바꿈되는 과다 현상
- 2차: 전처리기 추가 (line 끝 구두점 / block 시작 / 빈 줄 감지) → 중간 wrap만 공백으로 join
- 3차: 서버 측에서 `\n\n` 권위적 정리하기로 결정 → 클라 구두점 rule 제거, block-start/blank-line/code-fence 감지만 유지

---

## 5. 현재 상태 (2026-04-20 기준)

### 동작 확인 완료 ✅
- 연결, 세션/프로젝트 관리, 모드 전환
- 채팅 말풍선 UI + 마크다운 렌더 (터미널 wrap 정상 처리)
- 권한 요청 UI + 알림
- 사용량 대시보드
- 파일 브라우저 (폴더 탐색, 뒤로가기)
- **파일 다운로드 (최종 동작 확인)** — 서버 `HttpListener`의 hostname 바인딩 이슈 해결 후 정상

### 핵심 디자인 결정
- **Mode는 "code" 고정** — Chat/Cowork 모드 제거됨 (연결 시 switch_mode("code") 자동)
- **UI 단일 Activity + Compose**, 하단 2탭 (Chat | Manage)
- **Service 생존 관리** — ViewModel onCleared에서 WebSocketClient 파괴 안 함 (Foreground Service가 유지)
- **경로 권위는 서버** — 클라가 path 재구성하지 않고 서버가 준 `FileEntry.path` 그대로 사용
- **로그는 릴리즈에서 최소** — WebSocketClient는 Log 제거됨, 진단용 FileBrowser/FileBrowserVM 태그는 유지(디버그용)

### 주요 의존성
```kotlin
// 핵심
implementation(composeBom)                              // 2024.02.00
implementation("androidx.compose.material3:material3")
implementation("androidx.compose.material:material-icons-extended")

// 네트워킹
implementation("com.squareup.okhttp3:okhttp:4.12.0")
implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.6.3")

// 저장소
implementation("androidx.datastore:datastore-preferences:1.0.0")

// 마크다운 (Markwon 직접)
implementation("io.noties.markwon:core:4.6.2")
implementation("io.noties.markwon:ext-tables:4.6.2")
implementation("io.noties.markwon:ext-strikethrough:4.6.2")
implementation("io.noties.markwon:ext-tasklist:4.6.2")
implementation("io.noties.markwon:linkify:4.6.2")
implementation("io.noties.markwon:html:4.6.2")
implementation("io.noties.markwon:image:4.6.2")
```

### AndroidManifest 핵심 권한
```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
<uses-permission android:name="android.permission.FOREGROUND_SERVICE_DATA_SYNC" />
<uses-permission android:name="android.permission.POST_NOTIFICATIONS" />

<application android:usesCleartextTraffic="true" ...>
    <service android:foregroundServiceType="dataSync" />
</application>
```

---

## 6. Known Issues / 예정 작업

### Deprecation 경고 (기능 영향 없음)
- `Icons.Default.Send` → `Icons.AutoMirrored.Filled.Send` (ChatScreen)
- `Icons.Default.ArrowBack` → `Icons.AutoMirrored.Filled.ArrowBack` (SettingsScreen)
- `Icons.Default.InsertDriveFile` → `Icons.AutoMirrored.Filled.InsertDriveFile` (FileBrowserDialog)
- `Icons.Default.KeyboardArrowRight` → `Icons.AutoMirrored.Filled.KeyboardArrowRight` (ManageScreen)

### 추후 개선 후보
- **구문 강조**: Prism4j (KAPT + 언어 번들 설정 필요)
- **이미지 로딩**: Markwon image 플러그인 + Coil/Glide 연동 (현재 data URI만 처리)
- **Scroll anchor**: 사용자가 위로 스크롤 중일 때 새 메시지 도착 시 강제 하단 이동 방지
- **SSL/TLS**: 현재 cleartext HTTP. 필요 시 self-signed cert + trust config 추가
- **세션 영속화**: 앱 재실행 시 마지막 활성 세션 복원
- **공유 기능**: Claude 응답을 다른 앱으로 공유 (현재 Copy만)
- **다운로드 진행률 UI**: 현재 스낵바만 — 전용 진행률 표시 없음
- **파일 업로드**: 현재 다운로드 전용, 업로드는 미구현
- **음성 입력**: Speech-to-text로 명령 입력
- **위젯**: 홈 화면 위젯으로 quick command
- **Tablet UI**: 현재 폰 전제 레이아웃. 태블릿 대응 미검토

### 릴리즈 빌드 전 할 일
- Proguard/R8 규칙 점검 (OkHttp, Markwon, kotlinx-serialization)
- 키스토어 생성 + 서명
- `android:debuggable="false"` 확인
- `usesCleartextTraffic` 유지할지 결정 (내부망용이면 유지, 일반 배포면 HTTPS 전환)
- 로그 레벨 조정 (FileBrowserVM Log.d는 릴리즈에서 제거 권장)

---

## 7. 서버 측 메모 (클라가 의존하는 동작)

C# 서버 개발자와의 합의 사항:

- **`get_projects` / `select_project` 응답**은 `projects[]` + (select 시) `sessions[]` 동반
- **`browse_files` 응답**에 `currentPath`, `parentPath`, `entries[{name,path,type,size,modified}]` — path 필드 필수
- **`request_download` 응답**: `downloadUrl`(LAN IP 포함), `fileName`
- **HTTP 파일 서버**: `HttpListener`의 prefix를 `http://+:PORT/` 또는 `http://0.0.0.0:PORT/` 또는 LAN IP 직접 등록 (localhost/127.0.0.1만 등록하면 폰에서 400 Invalid Hostname)
- **출력 텍스트 정리**: 문단 경계를 `\n\n`으로 권위 있게 표시 (클라가 터미널 wrap 추측 안 하도록)
- **메시지 배열**: `output_full`의 payload는 `messages[{role,content}]` 형식 권장 (role = user/assistant/tool)
- **`FileEntry.name` vs `path` 분리**: name은 display label("C: (Local Disk)"), path는 실제 경로("C:\\")

---

## 8. 세션 복구 체크리스트

다른 머신에서 이어받을 때:

1. **Git pull / 프로젝트 동기화** — `C:\D_Develop\ClaudeRemote.Android\`
2. **환경 변수 확인**:
   - `JAVA_HOME` = Android Studio 설치 경로의 `jbr`
   - `ANDROID_SDK_ROOT` = `%LOCALAPPDATA%/Android/Sdk`
3. **local.properties 확인** (없으면 생성):
   ```
   sdk.dir=C\:\\Users\\<NAME>\\AppData\\Local\\Android\\Sdk
   ```
4. **빌드 테스트**: `./gradlew assembleDebug`
5. **최신 보고서 읽기**: `docs/reports/Phase13_Android_WorkReport.md` (가장 최근 작업 맥락)
6. **서버 측 상황 확인**: C# 개발자와 프로토콜 현황 동기화 — 특히 `\n\n` 문단 정리 및 다운로드 HttpListener 바인딩이 최근 수정 포인트
7. **실기기 테스트**: 연결 → 세션 선택 → 프로젝트 확장 → 출력 확인 → 파일 다운로드 → 사용량 대시보드 각 기능 확인

---

## 9. 자주 참조되는 파일

| 파일 | 역할 | 라인 수(대략) |
|------|------|----|
| `viewmodel/MainViewModel.kt` | 모든 상태/메시지 처리의 중심 | ~700 |
| `data/model/Messages.kt` | 프로토콜 상수 + 모델 | ~130 |
| `ui/screens/ChatScreen.kt` | 채팅 화면 전체 | ~450 |
| `ui/screens/ManageScreen.kt` | 프로젝트/세션 관리 | ~350 |
| `ui/components/MarkdownView.kt` | 마크다운 렌더 + 터미널 wrap 전처리 | ~230 |
| `ui/components/MessageBubble.kt` | 말풍선 3종 | ~160 |
| `ui/components/FileBrowserDialog.kt` | 파일 브라우저 + 다운로드 확인 | ~280 |
| `ui/components/UsageDashboardDialog.kt` | 사용량 대시보드 | ~270 |
| `service/WebSocketService.kt` | Foreground Service + 백그라운드 알림 | ~230 |
| `network/WebSocketClient.kt` | OkHttp WebSocket + 재연결 | ~200 |

---

**End of memory.**
