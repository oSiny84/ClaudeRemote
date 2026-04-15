# Phase 6 Hotfix 3 작업 보고서 - Chat UI 통합

**작성일**: 2026-04-13  
**담당**: Android Developer Agent  
**프로젝트**: ClaudeRemote Android  
**유형**: UI Refactor (Output + Command -> ChatScreen 통합)

---

## 1. 개요

기존 3탭 구조(Output / Command / Manage)를 Claude 앱과 유사한 2탭 구조(Chat / Manage)로 변경. Output과 Command 화면을 하나의 ChatScreen으로 통합하여 위에 출력, 아래에 입력하는 대화형 UX 구현.

---

## 2. 변경 파일 목록

| # | 파일 | 변경 유형 | 핵심 변경 |
|---|------|-----------|-----------|
| 1 | `ChatScreen.kt` | **신규** | Output + Command 통합 화면 |
| 2 | `MainScreen.kt` | 수정 | 3탭 -> 2탭 (Chat / Manage) |
| 3 | `OutputScreen.kt` | **삭제** | ChatScreen으로 대체 |
| 4 | `CommandScreen.kt` | **삭제** | ChatScreen으로 대체 |

---

## 3. 상세 변경 내역

### 3-1. ChatScreen.kt (신규)

**레이아웃 구조**:
```
┌─────────────────────────────┐
│ Claude Output    [↻][📋]   │  ← 헤더 + Refresh/Copy
│ [Full] [Latest] [Summary]  │  ← 스코프 토글 칩
├─────────────────────────────┤
│                             │
│  (출력 내용 - weight(1f))   │  ← 마크다운 렌더링, 스크롤
│                             │
├─────────────────────────────┤
│ Claude is asking:           │  ← 버튼 있을 때만 (Phase 6)
│ [Option A] [Option B]       │
├─────────────────────────────┤
│ [Continue][Stop][New] [H][▼]│  ← Quick Commands (접기 가능)
│ [recent1] [recent2]        │  ← History (토글)
├─────────────────────────────┤
│ [▲] ┌──────────────┐ [➤]  │  ← 입력 + Send
│     │Enter command..│      │
│     └──────────────┘      │
└─────────────────────────────┘
```

**통합된 기능**:

| 기존 위치 | 기능 | ChatScreen 위치 |
|-----------|------|-----------------|
| OutputScreen | 마크다운 렌더링 | 중앙 출력 영역 |
| OutputScreen | Full/Latest/Summary 토글 | 헤더 아래 |
| OutputScreen | Refresh/Copy 버튼 | 헤더 우측 |
| OutputScreen | 자동 스크롤 | 출력 변경 시 |
| OutputScreen | action_buttons | 출력 아래 |
| OutputScreen | 로딩/빈 상태 | 출력 영역 |
| CommandScreen | Quick Commands | 입력 영역 위 (접기 가능) |
| CommandScreen | Command History | Quick Commands 아래 (토글) |
| CommandScreen | 텍스트 입력 + Send | 화면 최하단 |

**입력 영역 특징**:
- `OutlinedTextField`: 1~3줄 자동 확장 (`maxLines = 3`)
- `FilledIconButton`: 원형 Send 버튼, primary 색상
- placeholder: "Enter command..."

**Quick Commands (접기/펼치기)**:
```kotlin
AnimatedVisibility(visible = showQuickCommands) {
    // Continue, Stop, New 칩 + History 토글 + 접기 버튼
}
// 접힌 상태: 입력 Row에 펼치기 버튼(▲) 표시
```
- 펼친 상태: `[Continue][Stop][New]` SuggestionChip + `[History]` + `[▼]` 접기
- 접힌 상태: 입력 Row 좌측에 `[▲]` 펼치기 버튼 추가
- `AnimatedVisibility`로 부드러운 전환 애니메이션

**Command History**:
- History 아이콘 토글 → `LazyRow`로 최근 명령 칩 표시
- `SuggestionChip` 탭 → 입력 필드에 자동 입력

---

### 3-2. MainScreen.kt

**Bottom Navigation 변경**:
```kotlin
// 이전: 3탭
Output (Visibility) | Command (Send) | Manage (ManageAccounts)

// 수정: 2탭
Chat (Chat) | Manage (ManageAccounts)
```

**탭 라우팅**:
```kotlin
when (tab) {
    0 -> ChatScreen(viewModel)
    1 -> ManageScreen(viewModel)
}
```

---

### 3-3. 삭제된 파일

- `OutputScreen.kt` — ChatScreen으로 완전 대체 (마크다운 렌더러 포함)
- `CommandScreen.kt` — ChatScreen으로 완전 대체 (Quick Commands, History 포함)

---

## 4. 유지된 기능 체크리스트

- [x] 마크다운 렌더링 (코드 블록, 볼드, 이탤릭, 인라인 코드, 헤딩, 리스트)
- [x] Full/Latest/Summary 스코프 토글
- [x] 출력 Refresh / Copy
- [x] 자동 스크롤 (출력 변경 시 하단으로)
- [x] 로딩 스피너 / 빈 상태 표시
- [x] action_buttons 표시 및 클릭 (Phase 6)
- [x] Quick Commands (Continue, Stop, New)
- [x] Command History (LazyRow SuggestionChip)
- [x] 텍스트 입력 + Send
- [x] Snackbar 피드백

---

## 5. QA 검증 항목

### Chat 탭
- [ ] 출력 영역 마크다운 렌더링 정상
- [ ] Full/Latest/Summary 토글 정상
- [ ] Refresh 탭 → 출력 갱신
- [ ] Copy 탭 → 클립보드 복사 + Toast
- [ ] 출력 변경 시 자동 스크롤
- [ ] 텍스트 입력 → Send → 전송 + 입력 필드 클리어
- [ ] Send 버튼 비활성화 (빈 입력)
- [ ] 입력 필드 1~3줄 자동 확장

### Quick Commands
- [ ] Continue/Stop/New 칩 탭 → 즉시 실행
- [ ] 접기 버튼(▼) → Quick Commands 숨김
- [ ] 펼치기 버튼(▲) → Quick Commands 표시
- [ ] AnimatedVisibility 전환 애니메이션

### Command History
- [ ] History 아이콘 토글 → 최근 명령 칩 표시/숨김
- [ ] 칩 탭 → 입력 필드에 텍스트 입력

### Action Buttons
- [ ] 서버에서 action_buttons 수신 → 버튼 표시
- [ ] 버튼 탭 → 로딩 스피너 + 나머지 비활성화
- [ ] 서버 응답 → 버튼 소멸

### Navigation
- [ ] 2탭 (Chat / Manage) 정상 표시
- [ ] 탭 전환 Crossfade 애니메이션
