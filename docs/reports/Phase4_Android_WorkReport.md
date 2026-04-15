# Phase 4 작업 보고서 - Android UI/UX 완성

**작성일**: 2026-04-10  
**담당**: Android Developer Agent  
**프로젝트**: ClaudeRemote Android  
**Phase**: 4 - Android UI/UX 완성  
**프로토콜 버전**: v1.1 (Cowork 모드 추가 반영)

---

## 1. 작업 요약

Phase 4의 목표인 **모든 화면의 기능 완성 및 UX 다듬기**를 수행했습니다.  
프로토콜 v1.1의 핵심 변경사항인 **Cowork 모드**(Chat/Cowork/Code 3개 모드)를 ManageScreen과 ViewModel에 반영했습니다.

**변경 파일**: 5개  
**신규 기능**: 마크다운 렌더링, 명령 히스토리, Snackbar 피드백, Crossfade 애니메이션, 재연결 배너

---

## 2. 변경 파일 목록

| # | 파일 | 변경 유형 |
|---|------|-----------|
| 1 | `ui/screens/OutputScreen.kt` | 전면 재작성 |
| 2 | `ui/screens/CommandScreen.kt` | 기능 추가 |
| 3 | `ui/screens/ManageScreen.kt` | Cowork 모드 + UX 개선 |
| 4 | `ui/screens/MainScreen.kt` | 전면 재작성 |
| 5 | `viewmodel/MainViewModel.kt` | 기능 추가 |

---

## 3. 상세 변경 내역

### 3-1. OutputScreen — 마크다운 렌더링 + 자동 스크롤

**주요 변경**:

| 기능 | 구현 방식 |
|------|-----------|
| 마크다운 렌더링 | 외부 라이브러리 없이 직접 파싱 (의존성 추가 없음) |
| 코드 블록 (```) | `Surface` + `Monospace` 폰트, 언어명 표시, 가로 스크롤 |
| **볼드** / *이탤릭* | `AnnotatedString` + `SpanStyle` |
| `인라인 코드` | Primary 색상 + 배경 하이라이트 |
| 헤딩 (#, ##, ###) | 폰트 크기 차등 (18/16/14sp) + Bold |
| 리스트 (-, *) | 불릿 문자(•) 치환 |
| 자동 스크롤 | `LaunchedEffect(output)` → `scrollState.animateScrollTo(max)` |
| 클립보드 복사 | Toast "Copied!" 표시 |
| 빈 상태 | "No output yet" + Refresh 버튼 |
| 로딩 상태 | `CircularProgressIndicator` + "Loading output..." |
| Generating 표시 | `claudeStatus.isGenerating` → "Claude is running • Generating..." |

**설계 결정**: `compose-richtext` 라이브러리 대신 직접 파싱을 선택.
- 이유: 외부 의존성 최소화, 프로젝트 규모에 비해 라이브러리 오버헤드가 큼
- 지원 범위: 코드 블록, 볼드, 이탤릭, 인라인 코드, 헤딩, 리스트
- 한계: 테이블, 링크, 이미지는 미지원 (필요 시 확장 가능)

---

### 3-2. CommandScreen — 히스토리 + 피드백

**추가 기능**:

| 기능 | 구현 |
|------|------|
| 명령 히스토리 | 최근 10개 저장, `LazyRow` + `SuggestionChip`으로 표시 |
| 히스토리 토글 | History 아이콘 버튼으로 표시/숨김 |
| 히스토리 재사용 | 칩 탭 → `commandText`에 복사 |
| Quick Input | `sendQuickInput()` — commandText를 거치지 않고 직접 전송 |
| 전송 피드백 | Snackbar "Sent!" / "Sent: continue" 등 |

**변경된 동작**:
- 이전: QuickCommand 버튼이 `updateCommandText()` + `sendInput()` 호출 → 히스토리에 "continue" 같은 빠른 명령이 쌓임
- 수정: `sendQuickInput()` 별도 메서드 → 히스토리 오염 방지, commandText 유지

---

### 3-3. ManageScreen — Cowork 모드 추가 (프로토콜 v1.1)

**핵심 변경: 2-button → 3-button 모드 전환**

```
[이전] [Chat] [Code]
[수정] [Chat] [Cowork] [Code]
```

| 기능 | 구현 |
|------|------|
| 3개 모드 버튼 | Chat / Cowork / Code — `switchMode("cowork")` 전송 |
| 모드 설명 | 버튼 아래 한 줄 설명 (Chat: "General conversation...", Cowork: "Collaborative work...", Code: "Code editing...") |
| 동일 모드 방지 | `switchMode()` 내부에서 현재 모드와 동일하면 무시 |
| 전환 피드백 | Snackbar "Switched to Cowork mode" |
| 로딩 상태 | `EmptyStateBox`에 `CircularProgressIndicator` 표시 |
| Projects 섹션 | Code 모드에서만 표시 (기존 동작 유지) |

**프로토콜 v1.1 대응**:
- `switch_mode` 명령의 `targetMode` 값에 `"cowork"` 추가
- `claude_status` 수신 시 `currentMode: "cowork"` 처리
- ManageScreen 모드 버튼 3개로 확장

---

### 3-4. MainScreen — 애니메이션 + 에러 배너 + Snackbar

**추가 기능**:

| 기능 | 구현 |
|------|------|
| 탭 전환 애니메이션 | `Crossfade(targetState = selectedTab)` |
| 재연결 배너 | TopAppBar 아래 amber 바: "Reconnecting to server..." + spinner |
| SnackbarHost | MainScreen `Scaffold`에 통합 — 모든 하위 화면의 피드백 표시 |
| 모드 배지 | TopAppBar 타이틀 옆에 `SecondaryContainer` 색상 배지 (Chat/Cowork/Code) |
| 상태 dot 애니메이션 | `animateColorAsState` → 부드러운 색상 전환 |
| 접근성 | 모든 NavigationBarItem 아이콘에 contentDescription 추가 |

---

### 3-5. MainViewModel — 새 기능 지원

| 추가 항목 | 설명 |
|-----------|------|
| `snackbarEvent: SharedFlow<String>` | 일회성 Snackbar 메시지 발행 |
| `commandHistory: StateFlow<List<String>>` | 최근 10개 명령 기록 (중복 제거, LIFO) |
| `sendQuickInput(text)` | Quick command 전용 — 히스토리 불포함, commandText 미변경 |
| `switchMode()` 개선 | 동일 모드 재선택 방지, Snackbar 피드백 |
| `addSession()` 개선 | Snackbar "New session created" |
| Response 실패 | 모든 실패 응답에 Snackbar 피드백 추가 |

---

## 4. 프로토콜 v1.1 반영 현황

| v1.1 변경 사항 | Android 반영 상태 |
|---------------|------------------|
| mode 필드: `chat \| cowork \| code` | 완료 — ManageScreen 3-button, ViewModel switchMode |
| `switch_mode` → `targetMode: "cowork"` | 완료 |
| `claude_status` → `currentMode: "cowork"` | 완료 |
| TopAppBar 모드 배지 표시 | 완료 |

---

## 5. 화면별 상태 매트릭스

각 화면의 상태별 UI 표시:

| 상태 | OutputScreen | CommandScreen | ManageScreen | MainScreen |
|------|-------------|---------------|--------------|------------|
| 연결됨 | 출력 표시 | 입력 활성 | 세션/프로젝트 표시 | 초록 dot |
| 재연결 중 | 기존 출력 유지 | 입력 가능 (큐잉) | 기존 목록 유지 | amber 배너 + dot |
| 로딩 중 | Spinner + 텍스트 | - | Spinner + 텍스트 | - |
| 빈 데이터 | 안내 + Refresh | - | 안내 텍스트 | - |
| 에러 | - | Snackbar | Snackbar | Snackbar |
| Generating | "Generating..." | - | - | - |

---

## 6. 파일 크기 변화

| 파일 | Phase 2 후 (LOC) | Phase 4 후 (LOC) | 변화 |
|------|-----------------|-----------------|------|
| OutputScreen.kt | ~120 | ~280 | +160 (마크다운 렌더러) |
| CommandScreen.kt | ~152 | ~165 | +13 (히스토리 UI) |
| ManageScreen.kt | ~281 | ~280 | -1 (리팩토링) |
| MainScreen.kt | ~97 | ~155 | +58 (배너, 배지, 애니메이션) |
| MainViewModel.kt | ~342 | ~310 | -32 (정리) |

---

## 7. QA 검증 항목

### OutputScreen
- [ ] 코드 블록이 Surface 배경으로 구분되는지
- [ ] 볼드/이탤릭/인라인코드 렌더링 확인
- [ ] 긴 출력 시 자동 스크롤 동작
- [ ] Copy 버튼 → Toast "Copied!" 표시
- [ ] Refresh 버튼 → 로딩 스피너 → 출력 갱신

### CommandScreen
- [ ] Quick Command 버튼 6개 정상 동작
- [ ] Custom Command 전송 후 입력 필드 클리어
- [ ] Snackbar "Sent!" 표시
- [ ] History 아이콘 → 최근 명령 칩 표시 → 탭 시 입력 복사

### ManageScreen
- [ ] **Chat/Cowork/Code 3개 버튼** 모드 전환
- [ ] 모드 설명 텍스트 변경 확인
- [ ] 동일 모드 버튼 재탭 → 무반응 (서버 불필요한 요청 방지)
- [ ] Snackbar "Switched to Cowork mode" 표시
- [ ] 세션/프로젝트 목록 조회 + 선택

### MainScreen
- [ ] 탭 간 Crossfade 애니메이션
- [ ] 재연결 시 amber 배너 표시
- [ ] 모드 배지 (TopAppBar) 실시간 업데이트
- [ ] 상태 dot 색상 부드럽게 전환

---

## 8. 다음 Phase 준비사항

Phase 5 (안정화) 진행 전:
- [ ] Foreground Service 구현 설계 (WebSocket 백그라운드 유지)
- [ ] 알림 채널 설계 (새 출력, 연결 끊김)
- [ ] DataStore를 이용한 서버 주소/포트/설정 영속화
- [ ] Cowork 모드의 프로젝트 섹션 표시 여부 확인 (PM 결정 필요)
