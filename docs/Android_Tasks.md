# ClaudeRemote - Android 프로그래머 작업 지시서

## 프로젝트 정보
- **프로젝트 경로**: `ClaudeRemote.Android/`
- **기술 스택**: Kotlin / Jetpack Compose / Material Design 3 / OkHttp WebSocket
- **역할 규칙**: `docs/AndroidProgrammer.md` 참조
- **프로토콜**: `protocol/MessageProtocol.md` 참조

## 현재 소스 파일 구조
```
ClaudeRemote.Android/app/src/main/java/com/clauderemote/
├── ClaudeRemoteApp.kt                        # Application 클래스
├── MainActivity.kt                           # 메인 Activity (Compose 진입점)
├── data/
│   ├── model/
│   │   └── Messages.kt                      # 데이터 모델 + 상수
│   └── network/
│       └── WebSocketClient.kt               # OkHttp WebSocket 클라이언트
├── ui/
│   ├── theme/
│   │   └── Theme.kt                         # Material3 테마 (Dark/Light)
│   └── screens/
│       ├── ConnectionScreen.kt              # 연결 화면
│       ├── MainScreen.kt                    # 메인 화면 (Bottom Nav)
│       ├── OutputScreen.kt                  # Claude 출력 뷰어
│       ├── CommandScreen.kt                 # 명령 입력 화면
│       └── ManageScreen.kt                  # 세션/프로젝트 관리
└── viewmodel/
    └── MainViewModel.kt                     # 메인 ViewModel
```

---

# Phase 2: 통신 레이어 구축

## 작업 목록

### 2-1. 빌드 환경 확인
- [ ] Android Studio (최신 안정판) 에서 프로젝트 열기
- [ ] Gradle Sync 성공 확인
- [ ] 빌드 오류 해결:
  - `compileSdk 34` / `targetSdk 34` 관련 SDK 설치
  - Kotlin 버전, Compose BOM 버전 호환성 확인
- [ ] 에뮬레이터 또는 실기기에서 앱 실행 확인

### 2-2. WebSocket 클라이언트 검증
- [ ] ConnectionScreen에서 IP/Port 입력 → 연결 버튼 탭
- [ ] Windows 서버(Phase 2 완료 후)와 연결 테스트
  - 같은 네트워크에서: Windows PC의 로컬 IP (예: 192.168.0.x)
  - 에뮬레이터에서: `10.0.2.2` (호스트 PC 접근용)
- [ ] 연결 성공 → 상태 표시 초록색, 자동으로 MainScreen으로 전환
- [ ] 연결 실패 → 에러 표시, 재시도 가능

### 2-3. 재연결 로직 검증
- [ ] 연결 후 서버 중지 → 자동 재연결 시도 확인
- [ ] 지수 백오프 확인: 1s → 2s → 4s → ... → 최대 30s
- [ ] 서버 재시작 → 자동 연결 복구 확인
- [ ] 재연결 중 상태 표시 ("Reconnecting...")

### 2-4. 메시지 송수신
- [ ] 연결 후 간단한 command 전송 테스트
- [ ] 서버로부터 content 메시지 수신 → ViewModel 상태 업데이트 확인
- [ ] 큐잉: 연결 끊긴 상태에서 명령 전송 → 재연결 후 자동 전송

### 2-5. 알려진 수정 필요 사항 (완료)
- [x] `MainScreen.kt`의 상태 표시 dot → `Box` + `CircleShape`로 교체, `dp.times()` → `4.dp` 수정
- [x] Settings 아이콘 → LinkOff(Disconnect) 아이콘으로 변경
- [x] `AndroidManifest.xml`에 `android:usesCleartextTraffic="true"` 추가
- [x] WebSocket 재연결 시 상태 race condition 수정 (`DISCONNECTED` → `RECONNECTING` 직접 전환)
- [x] `ClaudeMessage.timestamp` ISO 8601 형식으로 수정 (프로토콜 준수)
- [x] `ClaudeMessage.payload` → `JsonObject?`로 변경 (중첩 객체 지원)
- [x] `output_full`, `output_chunk` 메시지 핸들러 추가
- [x] `ClaudeStatus`에 `activeSession`, `activeProject` 필드 추가
- [x] ConnectionScreen/MainScreen 네비게이션 `LaunchedEffect`로 안전하게 처리
- [x] `isLoading` 상태 추가 (네트워크 요청 중 UI 피드백용)

---

# Phase 4: Android UI/UX 완성

## 작업 목록

### 4-1. OutputScreen 완성 (완료)
- [x] 마크다운 렌더링: 코드블록, 볼드, 이탤릭, 인라인코드, 헤딩, 리스트 (외부 라이브러리 없이 직접 파싱)
- [x] 전체/최신/요약 FilterChip 토글 → `requestOutput(scope)` 호출
- [x] 실시간 업데이트 수신 시 자동 스크롤 (`LaunchedEffect` + `animateScrollTo`)
- [x] 클립보드 복사 → Toast "Copied!" 표시
- [x] 빈 상태: "No output yet" + Refresh 버튼
- [x] 로딩 상태: CircularProgressIndicator + "Loading output..."
- [x] isGenerating 상태 표시 ("Claude is running • Generating...")

### 4-2. CommandScreen 완성 (완료)
- [x] 빠른 명령 버튼: `sendQuickInput()` 사용 (commandText 건드리지 않음)
- [x] 커스텀 명령: 여러 줄 입력, 전송 후 클리어
- [x] 전송 피드백: Snackbar "Sent!" / 실패 시 에러 메시지
- [x] 명령 히스토리: 최근 10개, LazyRow SuggestionChip으로 재사용

### 4-3. ManageScreen 완성 (완료)
- [x] **Chat/Cowork/Code 3개 모드 전환 버튼** (프로토콜 v1.1 Cowork 모드 반영)
- [x] 모드 설명 텍스트 표시 (Chat/Cowork/Code 별 한 줄 설명)
- [x] 모드 전환 시 Snackbar 피드백 + 동일 모드 재선택 방지
- [x] 세션/프로젝트 목록: LazyColumn + 활성 항목 primaryContainer 강조
- [x] 로딩 상태: EmptyStateBox에 CircularProgressIndicator
- [x] 새로고침/추가 버튼 동작

### 4-4. UX 다듬기 (완료)
- [x] 탭 간 `Crossfade` 애니메이션
- [x] 로딩 상태: OutputScreen/ManageScreen에 progress indicator
- [x] 빈 상태: 각 화면별 안내 메시지
- [x] 재연결 상태: TopAppBar 아래 amber 배너 ("Reconnecting to server...")
- [x] 에러/성공 피드백: SnackbarHost (MainScreen Scaffold)
- [x] 접근성: 모든 아이콘에 contentDescription 추가
- [x] TopAppBar에 현재 모드 배지 표시
- [x] 연결 상태 dot 색상 애니메이션 (`animateColorAsState`)
- [ ] **접근성**:
  - 모든 아이콘에 `contentDescription`
  - 터치 영역 최소 48dp
  - TalkBack 테스트

---

# Phase 5: 안정화

## 작업 목록

### 5-1. 백그라운드 동작 (완료)
- [x] Foreground Service (`WebSocketService`) — WebSocket 연결 백그라운드 유지
- [x] 3개 알림 채널: `channel_service`(연결 상태), `channel_output`(새 출력), `channel_alert`(끊김 알림)
- [x] 연결 상태 Persistent Notification (Connected/Connecting/Reconnecting/Disconnected)
- [x] 앱 foreground/background 전환 감지 (`ACTION_APP_FOREGROUND`/`ACTION_APP_BACKGROUND`)
- [x] `START_STICKY` — OS가 서비스 kill 후 자동 재시작
- [x] WebSocketClient를 `ClaudeRemoteApp` 싱글톤으로 이전 (Service/ViewModel 공유)

### 5-2. 알림 (완료)
- [x] 새 Claude 출력 수신 → `channel_output` 알림 (백그라운드일 때만, 설정으로 ON/OFF)
- [x] 연결 끊김 → `channel_alert` 알림 (Connected→Reconnecting 전환 시, 설정으로 ON/OFF)
- [x] 알림 탭 → `MainActivity` 복귀 (`FLAG_ACTIVITY_SINGLE_TOP`)
- [x] Android 13+ `POST_NOTIFICATIONS` 런타임 퍼미션 요청

### 5-3. 설정 화면 추가 (완료)
- [x] `AppSettings` (DataStore Preferences): 서버 주소/포트, 자동 연결, 알림 ON/OFF, 테마
- [x] `SettingsScreen` UI: 서버 설정, 동작, 알림, 외관 4개 섹션
- [x] TopAppBar 설정(gear) 아이콘 → SettingsScreen 오버레이
- [x] 테마 선택: System/Dark/Light (즉시 반영)
- [x] 자동 연결: 앱 시작 시 저장된 서버로 자동 연결

### 5-4. 안정성 검증 (코드 레벨 완료, QA 필요)
- [x] 화면 회전 대응: `configChanges` 설정 + ViewModel 생존
- [x] WebSocketClient 싱글톤 — Activity 재생성에도 연결 유지
- [x] ViewModel `onCleared()`에서 client destroy 제거 (서비스가 관리)
- [ ] 멀티윈도우 테스트 (QA)
- [ ] 메모리 누수 검사 (QA)
- [ ] ANR 테스트 (QA)
- [ ] 장시간 사용 테스트 (QA)
