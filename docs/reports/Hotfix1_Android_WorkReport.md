# Hotfix 1 작업 보고서 - 테스트 피드백 반영

**작성일**: 2026-04-10  
**담당**: Android Developer Agent  
**프로젝트**: ClaudeRemote Android  
**유형**: Hotfix (QA 테스트 피드백)

---

## 1. 수정 사항 요약

QA 테스트에서 발견된 2건의 UX 개선사항을 수정했습니다.

| # | 이슈 | 파일 | 심각도 |
|---|------|------|--------|
| 1 | 모드 전환 시 이전 세션/프로젝트 데이터 잔류 | `MainViewModel.kt` | Medium |
| 2 | 연결 성공 시 서버 주소 자동 저장 누락 | `ConnectionScreen.kt` | Medium |

---

## 2. 상세 수정 내역

### 2-1. 모드 전환 시 자동 새로고침

**문제**: ManageScreen에서 Chat → Code (또는 다른 모드)로 전환 시, 이전 모드의 세션/프로젝트 목록이 그대로 남아있음. 사용자가 수동으로 Refresh 버튼을 눌러야 새 모드의 데이터가 로드됨.

**원인**: `switchMode()`가 서버에 `switch_mode` 명령만 전송하고, 로컬 데이터 갱신을 하지 않음.

**수정** (`MainViewModel.kt:switchMode()`):
```kotlin
fun switchMode(mode: String) {
    if (mode == _currentMode.value) return
    // ... switch_mode 명령 전송 ...
    _currentMode.value = mode

    // 추가: 기존 데이터 클리어 + 자동 새로고침
    _sessions.value = emptyList()
    _projects.value = emptyList()
    requestSessions()
    if (mode == "code") {
        requestProjects()
    }
}
```

**동작 변경**:
| 시나리오 | 이전 | 수정 후 |
|----------|------|---------|
| Chat → Code 전환 | Chat 세션 목록이 그대로 표시 | 목록 클리어 → 로딩 표시 → Code 세션/프로젝트 로드 |
| Code → Cowork 전환 | Code 프로젝트 목록이 잔류 | 목록 클리어 → 로딩 표시 → Cowork 세션 로드 |
| Chat → Chat (동일) | 무반응 | 무반응 (변경 없음) |

---

### 2-2. 연결 성공 시 서버 주소 자동 저장

**문제**: 사용자가 ConnectionScreen에서 IP/Port를 입력하고 연결에 성공해도, 해당 값이 DataStore에 저장되지 않음. 앱 재시작 시 기본값("192.168.0.1:8765")으로 초기화됨.

**원인**: `ConnectionScreen`의 `LaunchedEffect(isConnected)`에서 `onConnected()`만 호출하고 `saveConnectionSettings()`를 호출하지 않음.

**수정** (`ConnectionScreen.kt`):
```kotlin
LaunchedEffect(isConnected) {
    if (isConnected) {
        viewModel.saveConnectionSettings()  // 추가
        onConnected()
    }
}
```

**DataStore 초기값 로딩 검증**:
- `MainViewModel.init` → `loadSettings()` → `settings.serverHost.collect { _serverHost.value = it }`
- DataStore에 저장된 값이 있으면 → StateFlow 업데이트 → ConnectionScreen에 반영
- 저장된 값이 없으면 → AppSettings 기본값 ("192.168.0.1", "8765") 사용
- 결론: **정상 동작** — DataStore에서 저장된 값이 초기값으로 로드됨

**동작 변경**:
| 시나리오 | 이전 | 수정 후 |
|----------|------|---------|
| 연결 성공 | 주소 미저장 | DataStore에 host+port 자동 저장 |
| 앱 재시작 | 기본값 표시 | 마지막 연결 성공 주소 표시 |
| 자동 연결 | 기본값으로 시도 | 저장된 주소로 시도 |

---

## 3. 영향 범위

- `MainViewModel.kt` — `switchMode()` 메서드 내 5줄 추가
- `ConnectionScreen.kt` — `LaunchedEffect` 내 1줄 추가
- 다른 파일 변경 없음
- 기존 동작에 대한 부작용 없음

---

## 4. QA 재검증 항목

### 모드 전환 자동 새로고침
- [ ] Chat → Code 전환: 이전 Chat 세션 사라지고 Code 세션/프로젝트 로드
- [ ] Code → Cowork 전환: 프로젝트 섹션 사라지고 Cowork 세션 로드
- [ ] Cowork → Chat 전환: 세션 목록 갱신
- [ ] 모드 전환 중 로딩 상태 표시 (EmptyStateBox spinner)
- [ ] 동일 모드 재탭 → 무반응 (불필요한 요청 없음)

### 서버 주소 저장
- [ ] 새 IP 입력 → 연결 성공 → 앱 종료 → 재시작 → 입력했던 IP 표시
- [ ] Auto-connect ON + 저장된 주소 → 앱 시작 시 자동 연결
- [ ] Settings 화면에서 수동 Save → 값 유지 확인
