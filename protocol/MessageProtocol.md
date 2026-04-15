# ClaudeRemote 통신 프로토콜 v1.1

## 개요
Windows(서버) ↔ Android(클라이언트) 간 WebSocket JSON 메시지 프로토콜

- **전송 형식**: JSON (UTF-8)
- **기본 포트**: 8765
- **하트비트**: 30초 간격 ping/pong

---

## 메시지 기본 구조

```json
{
  "id": "uuid-v4",
  "type": "command | response | content | status",
  "action": "string",
  "mode": "chat | cowork | code | null",
  "payload": {},
  "timestamp": "2024-01-01T00:00:00.000Z"
}
```

| 필드 | 타입 | 설명 |
|------|------|------|
| id | string | 메시지 고유 ID (UUID v4) |
| type | string | 메시지 유형 |
| action | string | 수행할 동작 |
| mode | string? | 현재/대상 모드 (chat/cowork/code) |
| payload | object | 동작별 데이터 |
| timestamp | string | ISO 8601 타임스탬프 |

---

## 메시지 타입별 상세

### 1. `command` (Android → Windows)

Android가 Windows에 동작을 요청

#### `send_input` - Claude에 텍스트 입력
```json
{
  "id": "...",
  "type": "command",
  "action": "send_input",
  "mode": "chat",
  "payload": {
    "text": "안녕하세요, 파이썬으로 hello world를 만들어주세요"
  }
}
```

#### `get_output` - Claude 출력 요청
```json
{
  "type": "command",
  "action": "get_output",
  "payload": {
    "scope": "full | latest | summary",
    "maxLength": 5000
  }
}
```
- `full`: 전체 대화 내용
- `latest`: 마지막 응답만
- `summary`: 마지막 핵심 부분 요약

#### `switch_mode` - Chat/Cowork/Code 모드 전환
```json
{
  "type": "command",
  "action": "switch_mode",
  "payload": {
    "targetMode": "chat | cowork | code"
  }
}
```

#### `get_sessions` - 세션 목록 요청
```json
{
  "type": "command",
  "action": "get_sessions",
  "mode": "chat"
}
```

#### `select_session` - 세션 선택
```json
{
  "type": "command",
  "action": "select_session",
  "mode": "chat",
  "payload": {
    "sessionId": "session-identifier"
  }
}
```

#### `add_session` - 새 세션 생성
```json
{
  "type": "command",
  "action": "add_session",
  "mode": "chat"
}
```

#### `get_projects` - 프로젝트 목록 요청 (Code 모드)
```json
{
  "type": "command",
  "action": "get_projects",
  "mode": "code"
}
```

#### `select_project` - 프로젝트 선택 (Code 모드)
```json
{
  "type": "command",
  "action": "select_project",
  "mode": "code",
  "payload": {
    "projectId": "project-identifier"
  }
}
```

#### `click_button` - Claude 선택 버튼 클릭 (Phase 6)
```json
{
  "type": "command",
  "action": "click_button",
  "payload": {
    "buttonId": "btn_0"
  }
}
```

---

### 2. `response` (Windows → Android)

Windows가 command에 대한 결과 전달

#### 성공 응답
```json
{
  "id": "...",
  "type": "response",
  "action": "send_input",
  "payload": {
    "success": true,
    "message": "입력 완료"
  }
}
```

#### 실패 응답
```json
{
  "id": "...",
  "type": "response",
  "action": "send_input",
  "payload": {
    "success": false,
    "error": "CLAUDE_NOT_FOUND",
    "message": "Claude 앱을 찾을 수 없습니다"
  }
}
```

#### 세션 목록 응답
```json
{
  "type": "response",
  "action": "get_sessions",
  "mode": "chat",
  "payload": {
    "success": true,
    "sessions": [
      {"id": "s1", "name": "파이썬 도움", "active": true, "lastMessage": "..."},
      {"id": "s2", "name": "새 채팅", "active": false, "lastMessage": "..."}
    ]
  }
}
```

#### 프로젝트 목록 응답
```json
{
  "type": "response",
  "action": "get_projects",
  "mode": "code",
  "payload": {
    "success": true,
    "projects": [
      {"id": "p1", "name": "MyWebApp", "path": "C:\\Projects\\MyWebApp", "active": true},
      {"id": "p2", "name": "DataPipeline", "path": "C:\\Projects\\DataPipeline", "active": false}
    ]
  }
}
```

---

### 3. `content` (Windows → Android)

Claude의 출력 내용 및 상호작용 요소를 전달

#### 선택 버튼 목록 (Phase 6)
```json
{
  "type": "content",
  "action": "action_buttons",
  "payload": {
    "buttons": [
      {"id": "btn_0", "text": "Option A"},
      {"id": "btn_1", "text": "Option B"},
      {"id": "btn_2", "text": "Other"}
    ]
  }
}
```
- 버튼이 없어지면 빈 배열 `[]`로 전송하여 Android 측 클리어

#### 버튼 클릭 결과
```json
{
  "type": "response",
  "action": "click_button",
  "payload": {
    "success": true,
    "buttonText": "Option A"
  }
}
```

#### 실시간 출력 스트리밍
```json
{
  "type": "content",
  "action": "output_update",
  "mode": "chat",
  "payload": {
    "scope": "latest",
    "content": "마크다운 형식의 Claude 출력 텍스트...",
    "isStreaming": true,
    "messageIndex": 5
  }
}
```

#### 전체 출력
```json
{
  "type": "content",
  "action": "output_full",
  "mode": "chat",
  "payload": {
    "messages": [
      {"role": "user", "content": "질문...", "index": 0},
      {"role": "assistant", "content": "답변...", "index": 1}
    ],
    "totalMessages": 2
  }
}
```

---

### 4. `status` (양방향)

연결 상태 및 시스템 상태 동기화

#### Claude 앱 상태
```json
{
  "type": "status",
  "action": "claude_status",
  "payload": {
    "claudeRunning": true,
    "currentMode": "chat",
    "isGenerating": false,
    "activeSession": "s1",
    "activeProject": null
  }
}
```

#### 연결 확인 (Heartbeat)
```json
{
  "type": "status",
  "action": "heartbeat",
  "payload": {
    "uptime": 3600,
    "clientCount": 1
  }
}
```

---

## 에러 코드

| 코드 | 설명 |
|------|------|
| `CLAUDE_NOT_FOUND` | Claude 앱 프로세스를 찾을 수 없음 |
| `CLAUDE_NOT_RESPONDING` | Claude 앱이 응답하지 않음 |
| `ELEMENT_NOT_FOUND` | UI 요소를 찾을 수 없음 |
| `MODE_SWITCH_FAILED` | 모드 전환 실패 |
| `SESSION_NOT_FOUND` | 세션을 찾을 수 없음 |
| `PROJECT_NOT_FOUND` | 프로젝트를 찾을 수 없음 |
| `INPUT_FAILED` | 텍스트 입력 실패 |
| `TIMEOUT` | 작업 시간 초과 |
| `INTERNAL_ERROR` | 내부 오류 |

---

## 대용량 메시지 처리

10KB 이상의 메시지는 청크로 분할:

```json
{
  "type": "content",
  "action": "output_chunk",
  "payload": {
    "chunkId": "uuid",
    "chunkIndex": 0,
    "totalChunks": 3,
    "data": "... 청크 데이터 ..."
  }
}
```
