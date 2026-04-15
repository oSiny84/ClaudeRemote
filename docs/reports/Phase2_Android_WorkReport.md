# Phase 2 작업 보고서 - Android 통신 레이어 구축

**작성일**: 2026-04-10  
**담당**: Android Developer Agent  
**프로젝트**: ClaudeRemote Android  
**Phase**: 2 - 통신 레이어 구축  

---

## 1. 작업 요약

Phase 2의 핵심 목표인 **WebSocket 통신 레이어의 안정성 확보 및 프로토콜 준수**를 위해 6개 파일을 수정했습니다.  
빌드 오류를 유발하던 문법 버그 수정, 재연결 로직의 상태 경쟁 조건 해결, 프로토콜 스펙 미준수 항목 보정이 주요 작업입니다.

---

## 2. 변경 파일 목록

| # | 파일 | 변경 유형 |
|---|------|-----------|
| 1 | `app/src/main/AndroidManifest.xml` | 설정 추가 |
| 2 | `app/src/main/java/.../data/model/Messages.kt` | 모델 리팩토링 |
| 3 | `app/src/main/java/.../data/network/WebSocketClient.kt` | 핵심 로직 수정 |
| 4 | `app/src/main/java/.../viewmodel/MainViewModel.kt` | 메시지 핸들러 확장 |
| 5 | `app/src/main/java/.../ui/screens/MainScreen.kt` | UI 버그 수정 |
| 6 | `app/src/main/java/.../ui/screens/ConnectionScreen.kt` | 네비게이션 안전 처리 |

---

## 3. 상세 변경 내역

### 3-1. AndroidManifest.xml — cleartext 통신 허용

**문제**: Android 9(API 28)부터 평문 HTTP/WS 통신이 기본 차단됨. `ws://` WebSocket 연결이 에뮬레이터 및 실기기에서 실패할 수 있음.

**수정**:
```xml
<application
    android:usesCleartextTraffic="true"
    ...>
```

**영향 범위**: 로컬 네트워크 `ws://` 연결 전체

---

### 3-2. Messages.kt — 데이터 모델 프로토콜 정합성

**문제 3건**:

| 항목 | 이전 | 수정 후 |
|------|------|---------|
| `payload` 타입 | `Map<String, String>?` | `JsonObject?` |
| `timestamp` 형식 | epoch millis (`"1712745600000"`) | ISO 8601 (`"2026-04-10T09:00:00.000Z"`) |
| `ClaudeStatus` 필드 | 3개 (claudeRunning, currentMode, isGenerating) | 5개 (+activeSession, +activeProject) |

**추가 상수**: `MessageAction.OUTPUT_CHUNK` (대용량 메시지 청크 처리용)

**수정 이유**:
- `Map<String, String>`은 프로토콜의 중첩 객체(sessions 배열, messages 배열 등)를 표현할 수 없음
- 프로토콜 스펙(`protocol/MessageProtocol.md`)이 ISO 8601 타임스탬프를 명시

---

### 3-3. WebSocketClient.kt — 재연결 상태 경쟁 조건 수정

**문제 (Critical)**:  
서버가 끊기면 `onFailure` → `DISCONNECTED` 설정 → MainScreen이 ConnectionScreen으로 이동 → 이후 `scheduleReconnect`가 `RECONNECTING`을 설정해도 이미 화면이 전환됨.

**결과**: 서버 일시 중지 시 사용자가 ConnectionScreen으로 튕김. 자동 재연결이 무의미해짐.

**수정**:
```
[이전] onFailure → DISCONNECTED → scheduleReconnect → RECONNECTING
[수정] onFailure → isUserDisconnect 체크 → RECONNECTING → scheduleReconnect
```

- `isUserDisconnect` 플래그 도입: 사용자가 명시적으로 disconnect 버튼을 눌렀을 때만 `true`
- 서버 끊김/연결 실패 시 `DISCONNECTED`를 거치지 않고 바로 `RECONNECTING`으로 전환
- OkHttpClient 재생성 로직 추가 (이전 client가 shutdown된 경우 대비)
- 큐 flush 시 전송 건수 로깅 추가

**동작 변경**:
| 시나리오 | 이전 동작 | 수정 후 동작 |
|----------|-----------|-------------|
| 서버 일시 중지 | ConnectionScreen으로 이동 | MainScreen 유지 + amber dot 표시 |
| 서버 재시작 | 수동 재연결 필요 | 자동 복구 (지수 백오프) |
| 사용자 Disconnect 버튼 | ConnectionScreen 이동 | ConnectionScreen 이동 (동일) |

---

### 3-4. MainViewModel.kt — 메시지 핸들러 확장

**추가된 핸들러**:

| 메시지 타입 | action | 처리 내용 |
|------------|--------|-----------|
| `content` | `output_update` | 실시간 출력 업데이트 (기존) |
| `content` | `output_full` | messages 배열 파싱 → `[role]\ncontent` 형식으로 조합 |
| `content` | `output_chunk` | 청크 재조립 (chunkId별 버퍼링 → 전체 수신 시 병합) |
| `status` | `heartbeat` | 수신 확인 (로깅, 별도 처리 없음) |

**추가 상태**:
- `isLoading: StateFlow<Boolean>` — 네트워크 요청 중 UI 로딩 표시용
- `requestOutput()`, `requestSessions()`, `requestProjects()` 호출 시 `true` → 응답 수신 시 `false`

**개선**:
- `handleResponse` 실패 시 `error` + `message` 필드 모두 확인하여 사용자에게 표시
- `disconnect()` 시 출력/세션/프로젝트 상태 초기화

---

### 3-5. MainScreen.kt — UI 버그 수정

**수정 3건**:

| 항목 | 이전 | 수정 후 |
|------|------|---------|
| 상태 dot 구현 | `Surface` + `Text(" ")` + `dp.times(4)` (컴파일 오류) | `Box(10.dp)` + `CircleShape` + `background(color)` |
| 아이콘 | `Icons.Default.Settings` (설정 아이콘인데 disconnect 동작) | `Icons.Default.LinkOff` + "Disconnect" description |
| 화면 전환 | 직접 `onDisconnect()` 호출 (composition 중 side-effect) | `LaunchedEffect(connectionState)`로 안전 처리 |

---

### 3-6. ConnectionScreen.kt — 네비게이션 안전 처리

**문제**: `onConnected()` 콜백이 composition 중에 직접 호출되어 Compose 런타임 경고 발생 가능.

**수정**: `LaunchedEffect(isConnected)` 블록 내에서 호출하도록 변경.

---

## 4. 프로토콜 준수 현황

`protocol/MessageProtocol.md` 대비 Android 클라이언트의 구현 상태:

| 프로토콜 항목 | 구현 상태 | 비고 |
|--------------|-----------|------|
| 메시지 기본 구조 (id, type, action, mode, payload, timestamp) | 완료 | JsonObject payload, ISO 8601 |
| `send_input` 명령 | 완료 | |
| `get_output` 명령 (scope: full/latest/summary) | 완료 | |
| `switch_mode` 명령 | 완료 | |
| `get_sessions` / `select_session` / `add_session` | 완료 | |
| `get_projects` / `select_project` | 완료 | |
| `response` 수신 (success/error) | 완료 | |
| `content` - `output_update` 수신 | 완료 | |
| `content` - `output_full` 수신 (messages 배열) | **이번에 추가** | |
| `content` - `output_chunk` 수신 (대용량 청크) | **이번에 추가** | |
| `status` - `claude_status` 수신 | 완료 | activeSession/Project 추가 |
| `status` - `heartbeat` 수신 | **이번에 추가** | |
| 하트비트 30초 ping/pong | 완료 | OkHttp pingInterval 설정 |
| 에러 코드 처리 | 완료 | error + message 필드 파싱 |

---

## 5. 검증 필요 항목 (QA)

아래 항목은 실제 빌드 및 실행 환경에서 검증이 필요합니다:

### 빌드 (2-1)
- [ ] Android Studio에서 Gradle Sync 성공
- [ ] 에뮬레이터/실기기 빌드 및 실행

### 연결 (2-2)
- [ ] ConnectionScreen IP/Port 입력 → 연결 성공 시 초록 dot + MainScreen 전환
- [ ] 연결 실패 시 에러 표시 + 재시도 가능
- [ ] 에뮬레이터 `10.0.2.2` 주소로 호스트 PC 접근

### 재연결 (2-3)
- [ ] 서버 중지 → amber dot 표시 + "Reconnecting..." 메시지
- [ ] 지수 백오프 동작: 로그에서 1s → 2s → 4s → ... 확인
- [ ] 서버 재시작 → 자동 복구 + 초록 dot

### 메시지 (2-4)
- [ ] command 전송 → 서버 수신 확인
- [ ] content 메시지 수신 → 출력 화면 반영
- [ ] 큐잉: 끊긴 상태 명령 전송 → 재연결 후 자동 전송

---

## 6. 다음 Phase 준비사항

Phase 4 (UI/UX 완성) 진행 전 확인 필요:
- Windows 서버 Phase 2 완료 여부 (실제 연동 테스트를 위해)
- 마크다운 렌더링 라이브러리 선정 (`compose-richtext` vs 직접 파싱)
- 에뮬레이터 vs 실기기 테스트 환경 결정
