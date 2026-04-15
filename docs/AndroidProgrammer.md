# Android Programmer - 역할 규칙

## 역할 정의
- **직급**: 최고 수준의 Android 프로그래머 + UI/UX 디자이너
- **전문 분야**: Kotlin / Jetpack Compose / Material Design 3 / 네트워크 프로그래밍
- **프로젝트 담당**: ClaudeRemote Android 모바일 애플리케이션

---

## 기술 스택 및 규칙

### 프레임워크
- **Kotlin** (최신 안정 버전)
- **Jetpack Compose** + **Material Design 3**
- **MVVM + Clean Architecture**
- **minSdk 26** (Android 8.0), **targetSdk 34** (Android 14)

### 핵심 라이브러리
- `androidx.compose.*` - UI 프레임워크
- `androidx.lifecycle:lifecycle-viewmodel-compose` - ViewModel
- `androidx.navigation:navigation-compose` - 화면 네비게이션
- `OkHttp` - WebSocket 클라이언트
- `kotlinx.serialization` - JSON 직렬화
- `kotlinx.coroutines` - 비동기 처리
- `androidx.datastore` - 설정 저장 (서버 IP, 포트 등)

### Gradle 의존성 (승인됨)
```kotlin
// Compose BOM
implementation(platform("androidx.compose:compose-bom:2024.02.00"))
implementation("androidx.compose.material3:material3")
implementation("androidx.compose.ui:ui")
implementation("androidx.compose.ui:ui-tooling-preview")

// Navigation
implementation("androidx.navigation:navigation-compose:2.7.7")

// ViewModel
implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.7.0")
implementation("androidx.lifecycle:lifecycle-runtime-compose:2.7.0")

// WebSocket
implementation("com.squareup.okhttp3:okhttp:4.12.0")

// Serialization
implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.6.3")

// DataStore
implementation("androidx.datastore:datastore-preferences:1.0.0")
```

---

## 코딩 규칙

### 1. 아키텍처 원칙
- **MVVM 패턴**: Screen(Composable) → ViewModel → Repository → DataSource
- **단방향 데이터 흐름**: State는 ViewModel에서 관리, Event는 위로 전달
- **Composable 함수 분리**: 화면(Screen) / 컴포넌트(Component) / 테마(Theme) 분리
- **상태 호이스팅**: 상태는 가능한 상위로 끌어올려 관리

### 2. 네이밍 규칙
- 패키지: 소문자 (`com.clauderemote.ui.screens`)
- 클래스/인터페이스: PascalCase (`WebSocketClient`)
- 함수: camelCase (`connectToServer`)
- Composable 함수: PascalCase (`ChatScreen`, `MessageCard`)
- 상수: UPPER_SNAKE_CASE (`DEFAULT_PORT`)
- 상태 변수: camelCase with 타입 (`uiState`, `isConnected`)

### 3. Compose UI 규칙
- `remember`와 `LaunchedEffect`의 키 관리 철저
- `State<T>`는 ViewModel에서 `StateFlow`로 관리
- 리컴포지션 최소화를 위해 `Stable`/`Immutable` 어노테이션 활용
- Preview 함수 필수 작성

### 4. 에러 처리
- WebSocket 연결 실패 시 사용자에게 시각적 피드백
- 자동 재연결 로직 (지수 백오프: 1s → 2s → 4s → 8s, 최대 30s)
- 오프라인 상태에서의 graceful degradation
- Coroutine 예외 처리: `CoroutineExceptionHandler` 활용

---

## UI/UX 디자인 원칙

### Material Design 3 적용
- **Dynamic Color**: 사용자 기기 테마 색상 자동 적용
- **Dark/Light Theme**: 시스템 설정 자동 추적
- **Typography**: Material3 타이포그래피 스케일 준수
- **Elevation & Shadow**: Surface 계층 일관성 유지

### 화면 구성

#### 1. 연결 화면 (ConnectionScreen)
```
┌─────────────────────────┐
│     ClaudeRemote        │
│                         │
│  ┌───────────────────┐  │
│  │ 서버 IP 주소      │  │
│  └───────────────────┘  │
│  ┌───────────────────┐  │
│  │ 포트 (8080)       │  │
│  └───────────────────┘  │
│                         │
│  [    연결하기    ]     │
│                         │
│  ● 연결 상태 표시       │
└─────────────────────────┘
```

#### 2. 메인 화면 (MainScreen) - Bottom Navigation
```
┌─────────────────────────┐
│ ☰ Claude Output    ⚙   │
│─────────────────────────│
│                         │
│  Claude 대화 내용       │
│  (스크롤 가능)          │
│  마크다운 렌더링        │
│                         │
│─────────────────────────│
│  [전체] [요약]          │
│─────────────────────────│
│  출력 │ 명령 │ 관리     │
└─────────────────────────┘
```

#### 3. 명령 화면 (CommandScreen)
```
┌─────────────────────────┐
│ 명령 입력               │
│─────────────────────────│
│                         │
│  빠른 명령 버튼들       │
│  [계속] [중지] [새대화] │
│                         │
│  ┌───────────────────┐  │
│  │ 명령 입력 영역    │  │
│  │ (여러 줄)         │  │
│  └───────────────────┘  │
│                         │
│  [    전송    ]         │
│─────────────────────────│
│  출력 │ 명령 │ 관리     │
└─────────────────────────┘
```

#### 4. 관리 화면 (ManageScreen)
```
┌─────────────────────────┐
│ 세션/프로젝트 관리      │
│─────────────────────────│
│  [Chat] [Code]          │
│                         │
│  Chat 선택 시:          │
│  - 세션 목록            │
│  - [+ 새 세션]          │
│                         │
│  Code 선택 시:          │
│  - 프로젝트 목록        │
│  - 세션 목록            │
│  - [+ 새 세션]          │
│─────────────────────────│
│  출력 │ 명령 │ 관리     │
└─────────────────────────┘
```

### UX 세부 규칙
- **애니메이션**: 화면 전환 300ms, 요소 등장 200ms
- **피드백**: 모든 사용자 액션에 즉각적 시각/촉각 피드백 (Ripple, Haptic)
- **로딩**: Skeleton UI 또는 CircularProgressIndicator
- **빈 상태**: 연결 안됨 / 데이터 없음 시 안내 일러스트 + 메시지
- **접근성**: contentDescription 필수, 최소 터치 영역 48dp

---

## WebSocket 클라이언트 규칙

### 연결 관리
- 앱 시작 시 마지막 연결 정보로 자동 연결 시도
- 연결 상태: `Disconnected → Connecting → Connected → Reconnecting`
- 백그라운드 진입 시 연결 유지 (Foreground Service 사용)
- 배터리 최적화 고려

### 메시지 처리
- 수신 메시지를 타입별로 분류하여 적절한 ViewModel로 전달
- 대용량 메시지는 청크 수신 후 조합
- 메시지 큐잉: 연결 끊김 시 명령을 큐에 저장, 재연결 후 순차 전송

---

## 품질 기준
- 앱 시작 시간 2초 이내 (Cold Start)
- 스크롤 60fps 유지
- 메모리 사용량 150MB 이하
- ANR(Application Not Responding) 발생 0건
- 접근성 검사 통과
