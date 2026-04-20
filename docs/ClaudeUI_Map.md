# Claude Desktop App - UI Automation Map (v2)

**분석일**: 2026-04-15
**Claude 앱 버전**: **1.2581.0** (Electron/Chromium, `Chrome_WidgetWin_1`)
**프로세스명**: `claude` (멀티프로세스)
**이전 버전 매핑**: `ClaudeUI_Map_v1.md` 참고

> v1 → v2 변화 핵심: Claude Code가 `dframe-*` 기반 레이아웃으로 전면 개편됨. 기존 `main-content`, `turn-form`, `cli-button-container` AID는 **모두 제거됨**. `main-content` 대신 `dframe-main`, `Primary pane` Group이 루트 역할. 입력 필드는 `Enter your turn` → `Prompt` (tiptap ProseMirror)로 변경.

---

## 1. 윈도우 구조

```
Window [Chrome_WidgetWin_1] Name='Claude'
  └─ Pane [RootView]
       └─ Pane [NonClientView] → WinCaptionButtonContainer (Min/Max/Restore/Close)
            └─ ...View chain...
                 └─ Document Name="Claude" AID="RootWebArea"   ★ Chromium 렌더러
                      └─ Group Class="bg-bg-100 text-text-100 font-ui min-h-screen"
                           └─ Group AID="root"                  ★ React 루트
                                └─ Group [flex min-h-0 min-w-0 w-full ...]
                                     └─ Group Class="dframe-root draggable-none"  ★ 레이아웃 루트
                                          ├─ Group [draggable absolute top-0 ...]   ← 타이틀바 영역
                                          │     Button Menu / Collapse sidebar / Search / Back / Forward
                                          ├─ Group Class="dframe-sidebar"            ★ 사이드바
                                          │     └─ Group AID="frame-peek-popover"
                                          │           Class="dframe-sidebar-body ..."
                                          └─ Group AID="dframe-main"                  ★ 메인 영역
                                                Class="dframe-content"
                                                └─ Group Class="dframe-pane-host ..."
                                                      └─ Group Name="Primary pane"
                                                            Class="dframe-pane dframe-pane-primary ..."
```

**메인 루트 찾기**:
- `Document` AID=`RootWebArea` Name=`Claude`  → 없으면 `FromHandle(HWND)` fallback
- 이후 루트의 자손에서 `Name="Primary pane"` 또는 AID=`dframe-main` 탐색

---

## 2. 사이드바 (`dframe-sidebar`)

### 2-1. 사이드바 컨테이너

| 속성 | 값 |
|------|------|
| ControlType | Group |
| Class | `dframe-sidebar` (시작 매칭) |
| 내부 AID | `frame-peek-popover` (자식 Group) |

### 2-2. 모드 전환 (Mode 버튼)

v1: `ControlType.RadioButton` + `Name`
**v2: `ControlType.Button`**, 컨테이너 `Group Name="Mode"` + `Class="df-pills"`

| Mode | ControlType | Name | 비고 |
|------|-------------|------|------|
| Chat | Button | `Chat` | |
| Cowork | Button | `Cowork` | |
| Code | Button | `Code` | 활성 시 Width 확장 (~70px) |

**탐색**: sidebar 내 `Group Name="Mode"` 찾고 자식 `Button` 수집.

### 2-3. 내비게이션/세션 스크롤 영역

| 속성 | 값 |
|------|------|
| ControlType | Group |
| Class 시작 | `dframe-nav-scroll` |

이 영역은 sidebar body의 주 스크롤 컨테이너. 아래 항목들을 순서대로 포함:

```
Button "New session ⌘N"   ← Name에 단축키 포함 (Text 자식 "New session")
Button "Routines"
Button "Customize"
Button "More navigation items"    ← 더보기 메뉴
Button "Pinned"                   ← label (section header)
Button "<Project Name>"           ← label — 활성 프로젝트 (예: "ClaudeRemote")
Button "Filter (active)"          ← 필터
Group...                          ← 세션 항목들 (아래 2-4 참조)
Button "Watchdog"                 ← 다른 프로젝트 label
Button "sw-platform-workspace"    ← 다른 프로젝트 label
Button "PO_SOFC_MCU_C28346"       ← 다른 프로젝트 label
```

### 2-4. 세션 목록

v1과 달리 `Text Name="Sessions"` 헤더가 없고, 각 세션은 Group으로 래핑된 Button:

```
Group Class="group relative rounded-[...] hover:bg-[var(--df-hover)] ..."
  ├─ Button Name="<세션 이름>" (또는 "Running <세션 이름>" 생성 중)
  │   ├─ (생성 중일 때) StatusBar Name="Running"
  │   ├─ (선택 시) Image AID="" (polling 아이콘)
  │   └─ Text Name="<세션 이름>"
  └─ Group (hover 시 노출)
       └─ Button Name="More options for <세션 이름>"  ← 세션별 옵션 버튼
```

**세션 탐색 전략**: `dframe-nav-scroll` 내의 자손 Button들 중 다음을 제외:
- `"New session ⌘N"`, `"Routines"`, `"Customize"`, `"More navigation items"`
- `"Pinned"`, `"Filter ..."`, `"More options for ..."`, `"osiny"`, `"Appearance"`
- 프로젝트 label 버튼 (활성 프로젝트 Name, 다른 프로젝트 Name들)

**더 단단한 전략**: 세션 버튼은 부모 `Group` Class가 `group relative rounded-[...]`로 시작. 이 패턴으로 세션만 골라냄.

**현재 실행 중 세션 감지**: Button 자식에 `StatusBar Name="Running"`이 있으면 생성 중.

### 2-5. 프로젝트 선택

v1에서는 `radix-*` AID Button이었으나, **v2에서는 프로젝트 label이 일반 Button** (sidebar nav 스크롤 하단 group/label 클래스).

```
Button Name="<프로젝트 폴더명>" Class="group/label flex min-w-0 flex-1 items-center gap-1 text-left ..."
```

활성 프로젝트는 맨 위 `Button Name="<현재 프로젝트>"`, 다른 프로젝트들은 스크롤 아래쪽에 순차 배치.

### 2-6. 사용자/설정 버튼

```
Button Name="osiny" AID="radix-*"       ← 계정/사용자
Button Name="Appearance" AID="radix-*"  ← 테마 설정
```

### 2-7. 새 세션 생성

v1: `Hyperlink Name="New session"`
**v2: `Button Name="New session ⌘N"`** (단축키 포함)

**탐색**: sidebar 내 `Button` 중 `Name`이 `"New session"`으로 시작하는 것. 또는 `Text` Name="New session" 자식을 가진 Button.

**단축키 fallback**: `Ctrl+N` (이전 `Ctrl+Shift+O`에서 변경 가능성)

---

## 3. 메인 영역 (`dframe-main`)

### 3-1. dframe-main

| 속성 | 값 |
|------|------|
| ControlType | Group |
| AutomationId | `dframe-main` |
| Class | `dframe-content` |

### 3-2. Primary pane

| 속성 | 값 |
|------|------|
| ControlType | Group |
| Name | `Primary pane` |
| Class | `dframe-pane dframe-pane-primary min-w-0 relative flex flex-col` |

이 안쪽으로 여러 Group 레이어를 거쳐 실제 내용 컨테이너가 나옴. 현재 구조:

```
Primary pane
  └─ Group (scroll wrapper)
       └─ Group
            └─ Group
                 └─ Group
                      └─ Group
                           └─ Group (Class="h-full w-full min-w-0 relative isolate rounded-r6")
                                └─ Group (Class="relative h-full min-w-0 flex flex-col")
                                     ├─ [3-3] 세션 헤더 Group
                                     ├─ [3-4] 콘텐츠 영역 Group
                                     └─ [3-5] epitaxy-chat-column (입력 폼)
```

### 3-3. 세션 헤더

Primary pane 내부 첫 레벨 자식. Class 시작: `relative flex items-center h-[32px] pl-[16px] pr-[var(--epitaxy-tr-clearance,...)]`

자식 요소:
| 요소 | 속성 |
|------|------|
| Local 인디케이터 | `Button Name="Local"` |
| 프로젝트 버튼 | `Button Name="<프로젝트명>"` (사이드바와 동일 이름) |
| 세션 타이틀 Text | `Text Name="<세션 이름>"` (Group 래핑) |
| 세션 액션 | `Button Name="Session actions"` |

### 3-4. 콘텐츠 영역 (대화 출력)

| 속성 | 값 |
|------|------|
| ControlType | Group |
| Class 시작 | `h-full overflow-y-auto overflow-x-hidden [contain:strict]` |

**출력 추출 전략**: 이 Group의 자손 중 `ControlType.Text` 요소의 Name 수집 (재귀 탐색).

자손에는 다음이 섞여 있음:
- Group `Class="text-body text-assistant-primary break-words ..."` — assistant 텍스트
- Group `Class="text-body ..."` — user 메시지
- Button `"Read N files"`, `"Searched code, read a file"`, `"Edited ..."`, `"Ran ..."`, `"Updated todos..."` — tool invocation 요약
- List / ListItem — 마크다운 리스트
- Table / DataItem — 마크다운 테이블
- Group `Class="text-code ..."` — inline 코드

스트리밍 상태는 화면 하단 입력 영역의 `Button Name="Stop"` 존재로 판단 (v1의 `Interrupt`에서 변경).

### 3-5. 스크롤바 (Session chapters)

사이드 레일에 `Group Name="Session chapters"` — 세션 네비게이션용 미니맵. 출력 추출에 방해 안 되는지 주의.

---

## 4. 입력 폼 (`epitaxy-chat-column` / `epitaxy-prompt`)

v1의 `turn-form` AID는 **제거됨**. v2 새 위치:

### 4-1. 채팅 컬럼

| 속성 | 값 |
|------|------|
| ControlType | Group |
| Class 시작 | `epitaxy-chat-column epitaxy-chat-size relative shrink-0 flex flex-col ...` |

### 4-2. 프롬프트 박스

| 속성 | 값 |
|------|------|
| ControlType | Group |
| Class 시작 | `epitaxy-prompt relative isolate rounded-r7` |

### 4-3. 입력 필드 (핵심)

| 속성 | 값 |
|------|------|
| ControlType | Group |
| **Name** | `Prompt` |
| Class | `tiptap ProseMirror ProseMirror-focused` |
| 패턴 | TextPattern 가능성 (미확인) |
| 구조 | contenteditable div |

**표시 상태**:
- 빈 필드: 자식 Group Class="is-empty is-editor-empty" 안에 Text Name="Type / for commands" (플레이스홀더)
- Text Name="\n" — ProseMirror trailingBreak
- 입력 시: tiptap 편집기가 자식 Text로 채워짐

**입력 전략 (기존 유지)**:
1. 필드 `SetFocus()` 또는 클릭
2. Clipboard.SetText(text) → Ctrl+A → Ctrl+V
3. Enter

### 4-4. 전송/중단 버튼

| 버튼 | Name | 위치 | 상태 |
|------|------|------|------|
| Stop | `Stop` | epitaxy-prompt 내부 오른쪽 | **생성 중** 표시 — v1의 `Interrupt` 대체 |
| Send | (Stop 버튼 자리에 표시) | 동일 | 입력 있을 때 Send로 바뀜 — 확인 필요 |

### 4-5. 상태 표시자

| 요소 | 용도 |
|------|------|
| StatusBar Class="sr-only" | Screen reader용, 자식 Text에 현재 모드 명 (`"Chat mode"`, `"Code mode"`) |

### 4-6. 하단 액션 버튼 바 (prompt 아래)

epitaxy-chat-column 내 prompt 바로 아래 위치:

| 버튼 | Name | 용도 |
|------|------|------|
| Accept edits | `Accept edits` | 편집 자동 수락 토글 (v1: `Auto accept edits`) |
| Transcript view mode | `Transcript view mode` | 출력 형식 |
| Add | `Add` | 첨부 추가 |
| Dictation | `Press and hold to record` + `Dictation settings` (Group Name="Dictation") | 음성 입력 |
| Model | `Opus 4.6 · Max` | 모델 선택 |
| Usage | `Usage: context 106.1k, plan 49%` | 사용량/컨텍스트 |

---

## 5. 탐색 요약 (new)

| 요소 | v1 경로 | **v2 경로 (신규)** |
|------|---------|-------------------|
| Document 루트 | `AID='RootWebArea'` + `Name='Claude'` | 동일 |
| React 루트 | `AID='root'` | 동일 |
| 레이아웃 루트 | — | `Class='dframe-root ...'` |
| 사이드바 | `Name='Sidebar'` | `Class='dframe-sidebar'` |
| 사이드바 body | — | `AID='frame-peek-popover'` |
| 메인 컨테이너 | `AID='main-content'` | **`AID='dframe-main'`** |
| 대화 영역 | `AID='cli-button-container'` | **`Class='h-full overflow-y-auto overflow-x-hidden [contain:strict]'` (Primary pane 내부)** |
| 입력 폼 | `AID='turn-form'` | **`Class='epitaxy-prompt ...'`** |
| 입력 필드 | `Name='Enter your turn'` | **`Name='Prompt'`** (tiptap ProseMirror) |
| 모드 탭 | `RadioButton` + Name | **`Button` + Name 컨테이너 `Group Name='Mode'` Class='df-pills'`** |
| 새 세션 | `Hyperlink Name='New session'` | **`Button` Name 시작 'New session'** |
| 스트리밍 버튼 | `Button Name='Interrupt'` | **`Button Name='Stop'`** (epitaxy-prompt 내부) |
| 모델 선택 | `Button Name='Opus 4.6 1M'` | `Button Name='Opus 4.6 · Max'` 등 |
| 자동 수락 | `Button Name='Auto accept edits'` | `Button Name='Accept edits'` |
| 프로젝트 경로 버튼 | `Name` 포함 `C:\` | (확인 필요 — 현재 dump에 미노출) |

---

## 6. 권한 다이얼로그 / AskUserQuestion (검증됨)

⚠ **공통**: 두 UI 모두 `epitaxy-chat-column` 내 prompt 위쪽에 `epitaxy-approval-card` 클래스로 나타남.  
🔑 **구분 핵심**: Class 문자열에 `outline-none` 포함 여부

| 상태 | Container Class |
|------|----------------|
| Permission dialog | `epitaxy-approval-card` (단독) |
| AskUserQuestion | `epitaxy-approval-card outline-none` |

### 6-1. Permission Dialog (Tool 실행 권한)

**검증**: 2026-04-15 `wget` 명령 권한 대기 상태 dump

```
Group Class="epitaxy-approval-card" Rect=[1292,732,556x166]
  ├─ Button Name="Allow Claude to <verb> <target> ?"       ★ 헤더 (클릭 불가)
  │   Class="text-body-semibold text-t9 min-h-[24px] flex items-center gap-1 pb-p6 w-full ..."
  │   ├─ Text "Allow Claude to"
  │   ├─ Text "<verb>"                  (예: "run")
  │   ├─ Text "<target>"                (예: "테스트 파일 다운로드")
  │   └─ Text "?"
  ├─ Group Class="bg-t1 rounded-r4 py-p6 px-p8 text-code text-t7 break-all select-text"
  │   └─ Text "<실제 command 내용>"     (예: `wget https://... -O /tmp/...`)
  ├─ Group Class="text-footnote text-t7 select-text break-words"
  │   └─ Text "This command requires approval"
  ├─ Button Name="Deny esc"             ★ 1번째 액션
  │   ├─ Text "Deny"
  │   └─ Group > Text "esc"
  ├─ Button Name="Allow once ⌘⇧⏎"      ★ 2번째 액션
  │   ├─ Text "Allow once"
  │   └─ Group > Text "⌘⇧⏎"
  └─ Button Name="Always allow ⌘⏎"     ★ 3번째 액션
      ├─ Text "Always allow"
      └─ Group > Text "⌘⏎"
```

**v1 → v2 변화**:
- 버튼 수: **4개 → 3개** (`Always allow for project (local)` 제거됨)
- 버튼 이름 포맷:
  - `"Deny Esc"` → **`"Deny esc"`** (소문자)
  - `"Allow once Enter"` → **`"Allow once ⌘⇧⏎"`** (심볼)
  - `"Always allow for project Ctrl Enter"` → **`"Always allow ⌘⏎"`**
- Header는 이제 `Button` 타입이지만 클릭해도 의미 없음 — action은 아래 3개 버튼

**탐색 전략**:
1. Main 하위 자손 Group 중 Class가 **정확히** `"epitaxy-approval-card"`로 시작하고 `outline-none` 포함 **안 함**
2. 자식 Button 중 Name prefix `"Deny"`, `"Allow once"`, `"Always allow"` 매칭
3. 버튼 한국어 label 프리픽스로 매핑:
   - `Deny` → `"거부"` (또는 `"deny"`)
   - `Allow once` → `"한번만 허용"`
   - `Always allow` → `"항상 허용"`

### 6-2. AskUserQuestion

**검증**: 2026-04-15 한국어 질문 "좋아하는 과일은?" + 4 옵션 + Other 활성 상태 dump

```
Group Class="epitaxy-approval-card outline-none"                   ★ outline-none가 구분자
Name="<prompt> <opt1main> <opt1sub> 1 <opt2main> <opt2sub> 2 ... Other N Other option Skip Submit ⏎"
Rect=[1292,645,556x385]
  ├─ Text Name="<prompt>"                                           (예: "좋아하는 과일은?")
  ├─ Button Name="<opt1main> <opt1sub> 1"                           ★ 옵션 버튼 1
  │   Class="flex items-center gap-g6 rounded-r4 px-p6 py-p6 text-left outline-none hide-focus-..."
  │   ├─ Group Class="text-body text-t9"
  │   │   └─ Text Name="<opt1main>"
  │   ├─ Group Class="text-footnote text-t6"
  │   │   └─ Text Name="<opt1sub>"
  │   └─ Text Name="1"                                              (인덱스)
  ├─ Button Name="<opt2main> <opt2sub> 2"                           ★ 옵션 버튼 2
  ├─ Button Name="<opt3main> <opt3sub> 3"
  ├─ Button Name="<opt4main> <opt4sub> 4"
  ├─ Button Name="Other 5"                                          ★ Other 선택 (인덱스는 옵션수+1)
  │   Class="flex items-center gap-g6 text-body text-t9 text-left outline-none hide-focus-rin..."
  │   ├─ Text "Other"
  │   └─ Text "5"
  ├─ Edit Name="Other option" Class="epitaxy-textarea w-full"       ★ Other 선택 시 텍스트 입력
  ├─ Button Name="Skip"                                             ★ Skip
  └─ Button Name="Submit ⏎"                                         ★ Submit
      ├─ Text "Submit"
      └─ Group > Text "⏎"
```

**v1 → v2 변화**:
- 컨테이너: `rounded-xl` 기반 → `epitaxy-approval-card outline-none`
- 옵션 Button `Name` 포맷: `"<main> <sub> <index>"` 하나의 평탄화된 문자열
- `Submit` 단축키 심볼 `⏎` 포함

**탐색 전략**:
1. Main 하위 자손 Group 중 Class가 **정확히** `"epitaxy-approval-card outline-none"`으로 시작
2. 자식 Button 수집 후 고정 버튼(`Skip`, `Submit ⏎`, `Other N`) 제외 → 나머지가 실제 옵션 버튼
3. Submit 버튼 prefix: `"Submit"`
4. Skip 버튼 Name: `"Skip"` (정확 일치)

### 6-3. ClaudeRemote.Windows 에서 버튼 매핑 (ActionButtons)

현재 프로토콜 (Phase 7)에서 `ActionButton.Category` 필드 3종:
- `"permission"`: Permission dialog 버튼들
- `"askuser"`: AskUserQuestion 선택지들
- `"other"`: 기타 (기본)

**Detection 우선순위**:
1. `epitaxy-approval-card outline-none` 발견 → AskUserQuestion 버튼 수집 (category=`askuser`)
2. `epitaxy-approval-card` (단독) 발견 → Permission 버튼 수집 (category=`permission`)
3. 둘 다 없음 → 빈 리스트

---

## 7. 현재 dump 기반 검증된 것 vs 추정

### 검증됨 (2026-04-15 UI tree dump 3종)

**Idle 상태 dump** (`uitree-phase9-main.txt`, 547 nodes):
- [x] dframe-root / dframe-sidebar / dframe-main / Primary pane 구조
- [x] Mode 버튼 (Chat/Cowork/Code) — Button 타입, Group Name="Mode"
- [x] 사이드바 세션 리스트 (현재 project 세션들)
- [x] 입력 필드 Name="Prompt" (tiptap ProseMirror)
- [x] 새 세션 Button Name="New session ⌘N"
- [x] 세션 헤더 구조 (Local / Project / 세션명 / Session actions)
- [x] 출력 컨텐츠 스크롤 컨테이너 Class="h-full overflow-y-auto overflow-x-hidden [contain:strict]"
- [x] epitaxy-chat-column / epitaxy-prompt / Stop 버튼

**AskUserQuestion 상태 dump** (`uitree-phase9-askuserq.txt`, 333 nodes):
- [x] Container `Class="epitaxy-approval-card outline-none"`
- [x] 옵션 버튼 Name 포맷 `"<main> <sub> <index>"`
- [x] Other 버튼 + Other option Edit (epitaxy-textarea)
- [x] Skip / Submit 버튼 구조

**Permission dialog 상태 dump** (`uitree-phase9-permission.txt`, 337 nodes):
- [x] Container `Class="epitaxy-approval-card"` (단독, no outline-none)
- [x] Header Button `"Allow Claude to <verb> <target> ?"`
- [x] 3 버튼: `"Deny esc"`, `"Allow once ⌘⇧⏎"`, `"Always allow ⌘⏎"`
- [x] v1의 `(local)` 변형 제거 확인

### 추정 (dump에 없음)

- [ ] Send 버튼 (입력 있을 때 Stop 대체?) — 입력 중 상태 dump 필요
- [ ] Interrupt/Stop 버튼 (생성 중 상태, Stop과 구분) — 구현 테스트 필요
- [ ] 프로젝트 경로 버튼 (v1에서 `C:\...`였던 것) — 현재 dump에 미노출

---

## 부록: 현재 관찰된 이름 패턴

| 패턴 | 의미 | 필터링 시 주의 |
|------|------|---------------|
| `"Running <이름>"` | 생성 중인 세션 | `<이름>` 부분만 추출 |
| `"More options for <이름>"` | 세션별 액션 버튼 | 세션 탐색 시 제외 |
| `"New session ⌘N"` | 새 세션 + 단축키 | prefix `"New session"` 매칭 |
| `"group/label ..."` (class) | 섹션 레이블 (Pinned, 프로젝트명 등) | 세션과 구분 |
| `"group/resize ..."` (class) | 사이드바 리사이즈 핸들 | 탐색에서 제외 |
| `"opacity-0 transition-opacity"` (class) | hover 시 노출되는 요소 | 포커스 고려 |
