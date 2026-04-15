# Claude Desktop App - UI Automation Map

**분석일**: 2026-04-10
**Claude 앱 버전**: Electron 기반 (Chromium)
**프로세스명**: `claude` (소문자, 복수 프로세스 - Electron 멀티프로세스)
**윈도우 클래스**: `Chrome_WidgetWin_1`

---

## 윈도우 구조 요약

```
Window [Chrome_WidgetWin_1] Name='Claude'
  └─ Pane [RootView] Name='Claude'
       └─ Pane [NonClientView]
            ├─ WinCaptionButtonContainer (Minimize, Maximize, Restore, Close)
            └─ Pane (Chromium renderer)
                 ├─ Document [RootWebArea] Name=''        ← 타이틀바 영역
                 └─ Document [RootWebArea] Name='Claude'  ← 메인 콘텐츠 ★
                      └─ Group AID='root'                 ← React 루트
```

**메인 콘텐츠 찾기**: `AutomationId='RootWebArea'` + `Name='Claude'`인 Document 요소

---

## 핵심 UI 요소 매핑

### 1. 모드 전환 탭

| 속성 | 값 |
|------|------|
| ControlType | RadioButton |
| Name | `Chat`, `Cowork`, `Code` |
| 위치 | root 직계 자손, Sidebar 이전 |
| 패턴 | SelectionItemPattern (예상) |

**탐색**: `ControlType.RadioButton` + `Name` 조건으로 검색

### 2. 사이드바

| 속성 | 값 |
|------|------|
| ControlType | Group |
| Name | `Sidebar` |
| 주요 자식 | New session, Search, Scheduled, Customize, 세션 목록 |

#### 2-1. 새 세션 버튼

| 속성 | 값 |
|------|------|
| ControlType | Hyperlink |
| Name | `New session` |
| 단축키 | Ctrl+Shift+O |

#### 2-2. 세션 목록

구조:
```
Group (Sidebar)
  └─ Group
       └─ Group
            └─ Text Name='Sessions'  ← 헤더
       └─ Group
            ├─ Group  ← 날짜 그룹 (Today 등)
            │    └─ Text Name='Today'
            ├─ Group  ← 개별 세션
            │    ├─ Image Name='No pull request'
            │    ├─ Text Name='세션 제목'  ★
            │    └─ Button Name=''  (옵션)
            ├─ Group  ← 개별 세션
            │    └─ ...
```

**세션 탐색**: Sidebar 내에서 `Text` Name='Sessions' 이후의 형제 Group들 안에 있는 `Text` 요소들

#### 2-3. 프로젝트 선택 (Code 모드)

| 속성 | 값 |
|------|------|
| ControlType | Button |
| Name | 프로젝트 폴더명 (예: `D_Develop`) |
| AutomationId | `radix-*` (동적) |
| 위치 | Sidebar 내, Sessions 위 |

### 3. 메인 콘텐츠 영역

| 속성 | 값 |
|------|------|
| ControlType | Group |
| AutomationId | `main-content` |
| 주요 자식 | 세션 제목, 메시지들, 입력 폼 |

#### 3-1. 세션 제목 바

| 속성 | 값 |
|------|------|
| ControlType | Button |
| Name | 현재 세션 제목 |
| 형제 | `Session options` Button, `Preview` Button |

#### 3-2. 대화 메시지 영역

`AID='cli-button-container'` 하위 구조:
```
Group AID='cli-button-container'
  └─ Group
       └─ Group
            ├─ Group (사용자 메시지)
            │    └─ Text Name='사용자 입력 텍스트'
            ├─ Button Name='Copy message'
            ├─ Button Name='Read 3 files' (도구 사용 접기/펼치기)
            ├─ StatusBar
            │    └─ Text Name='Read 3 files'
            ├─ Button Name='Copy message'
            ├─ Group (Claude 응답)
            │    └─ Text Name='응답 텍스트'
            ├─ List (마크다운 목록)
            │    └─ ListItem → Text
            ├─ Table (마크다운 테이블)
            │    └─ DataItem → Text
            └─ ...반복...
```

**출력 추출 전략**:
- `main-content` 아래 `cli-button-container` 찾기
- 그 안의 모든 `ControlType.Text` 요소의 `Name` 수집
- `Copy message`, StatusBar 텍스트 등 UI 요소 필터링

#### 3-3. 스트리밍 상태 감지

| 상태 | 감지 방법 |
|------|-----------|
| 응답 생성 중 | `Button` Name='Interrupt'가 존재 + `Text` Name에 'Forging...' 포함 |
| 유휴 상태 | Interrupt 버튼 없음 또는 비활성 |

상태 텍스트 위치: `main-content` 하단, `Text` Name에 시간/토큰 정보 (예: `21m 5s · 2.8k tokens`)

### 4. 입력 폼

| 속성 | 값 |
|------|------|
| ControlType | Group |
| AutomationId | `turn-form` |
| 위치 | main-content 하단 |

#### 4-1. 텍스트 입력 필드

| 속성 | 값 |
|------|------|
| ControlType | Group |
| Name | `Enter your turn` |
| IsKeyboardFocusable | true |
| 패턴 | TextPattern |
| 플레이스홀더 | Text Name='Reply...' |

**입력 전략**: ValuePattern 미지원 (contenteditable div)
→ SetFocus() → 클립보드(Ctrl+A, Ctrl+V) → Enter 전송

#### 4-2. 하단 버튼들

| 버튼 | Name | 용도 |
|------|------|------|
| Toggle menu | `Toggle menu` | 입력 옵션 메뉴 |
| Auto accept | `Auto accept edits` | 편집 자동 수락 토글 |
| Model selector | `Opus 4.6 1M` | 모델 선택 |
| Interrupt | `Interrupt` | 생성 중단 |
| Project path | `C:\D_Develop` | 현재 프로젝트 경로 |

---

## 요소 탐색 요약 (AutomationId 기반)

| 요소 | 탐색 방법 |
|------|-----------|
| 메인 문서 | `AID='RootWebArea'` + `Name='Claude'` |
| React 루트 | `AID='root'` |
| 메인 콘텐츠 | `AID='main-content'` |
| 대화 영역 | `AID='cli-button-container'` |
| 입력 폼 | `AID='turn-form'` |
| 입력 필드 | `Name='Enter your turn'` (turn-form 내부) |
| 모드 탭 | `ControlType.RadioButton` + `Name` |
| 사이드바 | `Name='Sidebar'` |
| 새 세션 | `Hyperlink` + `Name='New session'` |
