# Phase 9 작업 보고서 - Claude Code UI 재매핑 (v2)

**작성일**: 2026-04-15
**담당**: Windows Developer Agent
**프로젝트**: ClaudeRemote Windows
**유형**: Phase 9 - Claude Code 앱 업데이트 대응 UI 재매핑 & ClaudeAutomationService 전면 리팩토링

---

## 1. 작업 배경

Claude Code 데스크톱 앱이 버전 업데이트되면서 UI 구조가 **완전히 새로운 `dframe-*` / `epitaxy-*` 기반 레이아웃**으로 개편됨. 기존 UIAutomation 매핑(`main-content`, `turn-form`, `Enter your turn`, `Interrupt` 등)이 **전부 동작 불가**. Phase 3 절차를 처음부터 다시 수행하여 UI 트리를 분석하고 `ClaudeAutomationService.cs`를 재작성함.

| 항목 | 값 |
|------|-----|
| **Claude 앱 버전** | **1.2581.0** |
| 앱 유형 | Electron/Chromium, Window Class `Chrome_WidgetWin_1` |
| 분석 방법 | 자작 UITreeDumper (standalone console) + Idle / AskUserQuestion / Permission 3상태 dump |
| 새 매핑 파일 | `docs/ClaudeUI_Map.md` (v2) |
| 이전 매핑 백업 | `docs/ClaudeUI_Map_v1.md` |
| UI dump 원본 | `docs/screenshot/uitree-phase9-{main,askuserq,permission}.txt` |

---

## 2. 작업 절차 요약

1. **사전 확인**: ClaudeRemote.Windows 실행 상태 확인 (PID 24740), Claude 앱 버전 수집
2. **기존 매핑 백업**: `ClaudeUI_Map.md` → `ClaudeUI_Map_v1.md`
3. **UI 트리 덤프 도구 제작**: `UITreeDumper/Program.cs` + `UITreeDumper.csproj` (standalone .NET 8 콘솔 앱)
4. **3 상태 덤프 수집**:
   - Idle 상태 (547 노드) — 기본 레이아웃 / 사이드바 / 입력 필드 / 모드 탭
   - AskUserQuestion 활성 상태 (333 노드) — 한국어 질문 "좋아하는 과일은?" + 4개 옵션 + Other
   - Permission 활성 상태 (337 노드) — `wget` 명령 권한 대기
5. **매핑 분석 및 문서화**: `ClaudeUI_Map.md` 전면 재작성, v1→v2 변화 매트릭스 정리
6. **ClaudeAutomationService.cs 전면 재작성**: 2036 → 1785줄, 모든 메서드 v2 기준 재구현
7. **Release 빌드 검증**: C# 컴파일 에러 0, 경고 0 (기존 SessionManager.cs CS0067 warning 유지)
8. **Phase 9 보고서 작성** (현재 문서)

---

## 3. v1 → v2 UI 변화 매트릭스

### 3-1. 레이아웃 루트

| 요소 | v1 | v2 |
|------|-----|-----|
| 메인 컨테이너 | `Group AID='main-content'` | `Group AID='dframe-main'` + 자식 `Group Name='Primary pane'` |
| 레이아웃 루트 | — | `Group Class='dframe-root draggable-none'` |
| 사이드바 | `Group Name='Sidebar'` | `Group Class='dframe-sidebar'` |
| 사이드바 body | — | `Group AID='frame-peek-popover'` Class='dframe-sidebar-body ...' |

### 3-2. 사이드바

| 요소 | v1 | v2 |
|------|-----|-----|
| 모드 전환 | `ControlType.RadioButton` + Name | `ControlType.Button` in `Group Name='Mode'` `Class='df-pills'` |
| 세션 스크롤 컨테이너 | 전용 AID 없음 | `Class='dframe-nav-scroll ...'` |
| 세션 목록 | `Text Name='Sessions'` 헤더 기반 수집 | 부모 `Group Class='group relative rounded-[var(--df-radius-pill)]...'` |
| 세션 생성 중 표시 | `StatusBar` 주변 Text | `Button Name='Running <이름>'` + 자식 `StatusBar Name='Running'` |
| 새 세션 | `Hyperlink Name='New session'` | `Button Name='New session ⌘N'` |
| 프로젝트 label | `Button` with `AID='radix-*'` | `Button Class='group/label flex min-w-0 flex-1 items-center...'` |
| Pinned 섹션 | (없음) | `Button Name='Pinned'` Class=`group/label ...` (=섹션 헤더) |

### 3-3. 메인 컨텐츠 (Primary pane)

| 요소 | v1 | v2 |
|------|-----|-----|
| 세션 헤더 Local 표시 | — | `Button Name='Local'` |
| 프로젝트 버튼 | 미상 | `Button Name='<project>'` Class=`inline-flex items-center h-base px-p3 ...cursor-def...` |
| 세션 액션 메뉴 | — | `Button Name='Session actions'` |
| 출력 스크롤 컨테이너 | `AID='cli-button-container'` | `Group Class='h-full overflow-y-auto overflow-x-hidden [contain:strict]'` |
| Session chapters mini-map | — | `Group Name='Session chapters'` |

### 3-4. 입력 영역 (epitaxy-chat-column)

| 요소 | v1 | v2 |
|------|-----|-----|
| 폼 컨테이너 | `AID='turn-form'` | `Class='epitaxy-chat-column epitaxy-chat-size ...'` |
| 프롬프트 박스 | — | `Class='epitaxy-prompt relative isolate rounded-r7'` |
| **입력 필드 (핵심)** | `Name='Enter your turn'` Edit | `Group Name='Prompt'` Class=`tiptap ProseMirror ProseMirror-focused` |
| 스트리밍 버튼 | `Button Name='Interrupt'` | `Button Name='Stop'` (epitaxy-prompt 내부) |
| 자동 편집 수락 | `Button Name='Auto accept edits'` | `Button Name='Accept edits'` |

### 3-5. 권한 다이얼로그

| 요소 | v1 | v2 |
|------|-----|-----|
| 컨테이너 | 커스텀 | `Group Class='epitaxy-approval-card'` (단독, no `outline-none`) |
| 헤더 | 일반 Text | **`Button`** Name=`'Allow Claude to <verb> <target> ?'` (클릭 대상 아님, 첫 비-액션 버튼으로 skip) |
| 커맨드 상세 | 위치 변동 | `Group Class='bg-t1 rounded-r4 ...'` 자식 `Text` |
| 버튼 개수 | **4개** | **3개** (`(local)` 변형 제거) |
| Deny | `'Deny Esc'` | `'Deny esc'` |
| Allow once | `'Allow once Enter'` | `'Allow once ⌘⇧⏎'` |
| Always allow | `'Always allow for project (local) Ctrl Enter'` / `'Always allow for project Ctrl Enter'` | `'Always allow ⌘⏎'` |

### 3-6. AskUserQuestion

| 요소 | v1 | v2 |
|------|-----|-----|
| 컨테이너 | `Class~'rounded-xl'` + Name 포함 `'Skip'` | `Group Class='epitaxy-approval-card outline-none'` |
| 구분 핵심 | — | **`outline-none` 존재 여부** (Permission 과 구분) |
| 프롬프트 | 첫 Text 자식 | 동일 (첫 Text 자식) |
| 옵션 버튼 Name | — | `'<main> <sub> <index>'` 평탄화 |
| Other 버튼 | — | `Button Name='Other N'` (N = 옵션수+1) |
| Other 입력 필드 | — | `Edit Name='Other option'` Class=`epitaxy-textarea w-full` |
| Submit 버튼 | — | `Button Name='Submit ⏎'` |

---

## 4. ClaudeAutomationService.cs 주요 변경 사항

### 4-1. 설계 원칙 (사용자 요구사항 반영)

1. **AutomationId 캐시 완전 제거**: `_cachedMainContent`, `_cachedSidebar`, `_cachedTurnForm`, `_cachedInputField` 모두 삭제. 매 호출마다 fresh lookup.
2. **블랙리스트 접근 금지**: 이전 Phase에서 문제였던 "세션 아닌 것들을 나열해서 제외" 방식 폐기. 대신 **부모 Group의 `Class='group relative rounded-...'` 패턴으로 세션을 positive 매칭**.
3. **이름/클래스 기반 lookup만 사용**: AID는 radix-* 같은 random ID가 많아 재시작/업데이트마다 바뀜. 오직 Name과 Class 접두어만 사용.
4. **구조 기반 탐색**: 알려진 앵커(예: `dframe-sidebar`, `Primary pane`, `epitaxy-approval-card`) 발견 후 자식/형제를 네비게이션.

### 4-2. 제거된 상수/필드

```csharp
// 삭제됨 (v1 캐시 및 마커)
private AutomationElement? _mainDocument;        // → _rootDocument
private AutomationElement? _cachedMainContent;   // → 삭제, 매번 FindPrimaryPane()
private AutomationElement? _cachedSidebar;       // → 삭제, 매번 FindSidebar()
private AutomationElement? _cachedTurnForm;      // → 삭제, 매번 FindPromptBox()
private AutomationElement? _cachedInputField;    // → 삭제, 매번 FindPromptField()
private const string AskUserQuestionClassMarker = "rounded-xl";    // → ApprovalCardClassMarker
private const string AskUserQuestionNameMarker = "Skip";           // → AskUserOutlineMarker
private bool _lastTurnFormWasEmpty;
```

### 4-3. 신규 상수

```csharp
private const string DFrameSidebarClass = "dframe-sidebar";
private const string DFrameMainAid = "dframe-main";
private const string PrimaryPaneName = "Primary pane";
private const string DFrameNavScrollClass = "dframe-nav-scroll";
private const string ModeGroupName = "Mode";
private const string ModePillsClass = "df-pills";
private const string EpitaxyPromptClass = "epitaxy-prompt";
private const string InputFieldName = "Prompt";
private const string StopButtonName = "Stop";
private const string OutputScrollClass = "h-full overflow-y-auto overflow-x-hidden";
private const string NewSessionPrefix = "New session";
private const string SessionRowClassPrefix = "group relative rounded-";
private const string ProjectLabelClassPrefix = "group/label ";

// 권한/질문 카드 공유 클래스 + 구분자
private const string ApprovalCardClassMarker = "epitaxy-approval-card";
private const string AskUserOutlineMarker = "outline-none";

// 새 permission 버튼 접두사 (v1 "Always allow for project" → v2 "Always allow")
private static readonly string[] PermissionButtonPrefixes = { "Deny", "Allow once", "Always allow" };

// v2 shortcut suffix (longest-first order 중요)
private static readonly string[] PermissionShortcutSuffixes =
{
    " ⌘⇧⏎", " ⌘⏎", " esc",         // v2 (신)
    " Ctrl Enter", " Enter", " Esc"  // v1 legacy fallback
};
```

### 4-4. 메서드별 변경 요약

| 메서드 | v1 전략 | v2 전략 |
|--------|--------|--------|
| `FindClaudeWindowAsync` | MainWindowHandle 첫 매치 | 가장 큰 working set 프로세스 선택 (UITreeDumper와 동일) |
| `GetOutputAsync` | `cli-button-container` 탐색 | `Primary pane` → `h-full overflow-y-auto` 스크롤 컨테이너 |
| `SendInputAsync` | `'Enter your turn'` | `Name='Prompt'` + Class 포함 `"ProseMirror"` (tiptap) |
| `SwitchModeAsync` | RadioButton + SelectionItemPattern | `Group Name='Mode'` 내 Button + InvokePattern |
| `GetSessionsAsync` | `Text Name='Sessions'` 헤더 기반 | `dframe-nav-scroll` 아래 `Class='group relative rounded-...'` Group |
| `SelectSessionAsync` | Text 부모 Group 클릭 | 세션 row Group의 첫 자식 Button `Invoke()` |
| `AddSessionAsync` | `Hyperlink Name='New session'` / Ctrl+Shift+O | `Button Name prefix='New session'` / Ctrl+N |
| `GetProjectsAsync` | `radix-*` AID 필터 | `Button Class prefix='group/label '` + 섹션 헤더 필터 |
| `SelectProjectAsync` | radix-* Button 인덱스 | `group/label` Button 인덱스 |
| `GetActionButtonsAsync` | Permission먼저→AskUser | `epitaxy-approval-card` 발견 후 `outline-none` 여부로 분기 |
| `DetectPermissionButtons` | 커스텀 컨테이너 walk | `Group Class='epitaxy-approval-card'` (outline-none 없음) 자식 분석 |
| `DetectAskUserQuestionButtons` | `rounded-xl` + Name `Skip` walk | `Group Class='epitaxy-approval-card outline-none'` 자식 분석 |
| `DetectStreamingState` | `Interrupt` Button in turn-form | `Stop` Button in epitaxy-prompt |
| `CleanPermissionButtonName` | 3개 suffix (Ctrl Enter, Enter, Esc) | 6개 suffix (v2 3개 + v1 3개 fallback) |

### 4-5. 신규 헬퍼 메서드

- `FindSidebar()` — `dframe-sidebar` class prefix 기반
- `FindNavScroll()` — `dframe-nav-scroll` class prefix 기반
- `FindModeGroup()` — `Group Name='Mode' Class startsWith 'df-pills'`
- `FindPrimaryPane()` — `dframe-main` AID → `Primary pane` Name
- `FindPromptField()` — `epitaxy-prompt` Group → Name `Prompt` + Class 포함 `ProseMirror`
- `FindApprovalCard(root)` — `epitaxy-approval-card` class prefix 기반
- `FindByClassPrefixRaw(root, prefix)` — 범용 class prefix walker
- `FindButtonByNamePrefixRaw(root, prefix)` — 범용 버튼 prefix walker
- `FindFirstButtonRaw(root)` — 첫 Button 자손
- `WalkFindFirst(root, predicate, ...)` — 범용 predicate walker
- `CollectTextsRaw(root)` — 컨텐츠 Text 수집 (UI 크롬 텍스트 필터링 포함)
- `SafeGetName/SafeGetClassName` — 예외 억제 wrapper

### 4-6. 구분자 로직: Permission vs AskUserQuestion

두 UI가 동일한 `epitaxy-approval-card` base class를 공유하므로, **`outline-none` 추가 클래스 존재 여부**로 구분:

```csharp
private List<ButtonInfo> DetectApprovalButtons()
{
    var primaryPane = FindPrimaryPane();
    if (primaryPane == null) return new();

    var card = FindApprovalCard(primaryPane);
    if (card == null) return new();

    var cardClass = SafeGetClassName(card);
    bool isAskUser = cardClass.Contains(AskUserOutlineMarker, StringComparison.Ordinal);

    if (isAskUser)
    {
        LastButtonCategory = "askuser";
        return CollectAskUserQuestionButtons(card);
    }
    else
    {
        LastButtonCategory = "permission";
        return CollectPermissionButtons(card);
    }
}
```

이 단일 진입점이 `GetActionButtonsAsync`와 `ClickButtonAsync` 양쪽에서 공유됨 (v1의 `GetActionButtonsSync` 중복 제거).

### 4-7. Permission 카드 내부 파싱 (헤더 Button 처리)

v2의 중요한 차이: 헤더 "Allow Claude to <verb> <target> ?"가 **Button 타입**이지만 실제 액션 버튼이 아니므로 스킵해야 함. 아래 로직으로 처리:

```csharp
bool headerSeen = false;
// ... iterate children ...
if (ct == ControlType.Button)
{
    var cleanName = CleanPermissionButtonName(rawName);
    if (!headerSeen && !IsPermissionActionButton(cleanName))
    {
        // 첫 번째 비-액션 Button = 헤더
        promptParts.Add(rawName);
        headerSeen = true;
    }
    else if (IsPermissionActionButton(cleanName))
    {
        buttons.Add(...);  // 실제 액션 (Deny/Allow once/Always allow)
    }
}
```

`IsPermissionActionButton`는 clean name이 `Deny`/`Allow once`/`Always allow` 와 정확히 일치하거나 `"<prefix> "`로 시작하는지로 판단 → 헤더 `"Allow Claude to ..."`는 `"Allow once"` prefix와 매칭 안 되므로 자연스럽게 걸러짐.

---

## 5. 빌드 결과

```
$ dotnet build -c Release
Build FAILED. (실제로는 컴파일 성공 — EXE 파일 잠김)

11 Warning(s)
  - SessionManager.cs(14,40): CS0067 (ModeChanged 이벤트 미사용, 기존 경고)
  - MSB3026 x10: EXE copy retry 경고 (실행 중 프로세스 PID 24740 파일 잠금)

2 Error(s)
  - MSB3027 / MSB3021: EXE copy 실패 (파일 잠금, C# 컴파일과 무관)

C# 컴파일 에러: 0
C# 컴파일 경고: 0
DLL 출력: obj/Release/net8.0-windows/ClaudeRemote.Windows.dll (111616 bytes, 11:21)
```

**결론**: 코드 자체는 clean compile. EXE 교체만 대기 중 (PID 24740 종료 후 재빌드하면 완료).

---

## 6. 통합 테스트 예상 시나리오 (향후 실행)

Phase 3 / Phase 6 절차 기반:

| # | 테스트 | 기대 결과 |
|---|--------|-----------|
| 1 | Claude 앱 실행 + Android 앱 연결 | 세션 목록 / 모드 / 프로젝트 자동 로드 |
| 2 | 모드 전환 (Chat ↔ Code) | 사이드바 Mode 그룹의 Button Invoke → 활성 표시 |
| 3 | 세션 선택 | 해당 세션 row Button 클릭 → 메인 컨텐츠 변경 |
| 4 | 새 세션 생성 | `Button Name='New session ⌘N'` 클릭 → 빈 세션 |
| 5 | 텍스트 입력 전송 (한국어) | tiptap Prompt 필드 포커스 → Clipboard paste → Enter |
| 6 | 출력 스트리밍 감지 | `Stop` 버튼 존재 → `IsGenerating = true` |
| 7 | Permission 다이얼로그 | `Deny` / `Allow once` / `Always allow` 3개 버튼 노출 |
| 8 | Permission 버튼 클릭 | Invoke → 다이얼로그 닫힘 → 다음 출력 |
| 9 | AskUserQuestion 옵션 | 옵션 버튼 N개 + Other + Skip + Submit |
| 10 | AskUserQuestion 옵션 클릭 | Invoke → 선택 확정 |
| 11 | Other 선택 + 텍스트 입력 | `Edit Name='Other option'` 입력 → Submit 클릭 |
| 12 | 최소화 상태 원격 작업 (Phase 8 유지) | `WithWindowRestoredAsync` 래퍼 그대로 동작 |

⚠ **현재 ClaudeRemote.Windows.exe (PID 24740)는 구버전** (v1 매핑). 사용자가 종료하고 새 빌드를 실행해야 실제 테스트 가능.

---

## 7. 주의사항 / 알려진 제약

1. **Send 버튼 미확인**: 입력 중 상태(입력이 있을 때)에서 Stop이 Send로 바뀌는지 여부는 dump하지 못함. 현재 구현은 Enter 키 전송으로 대응 — 실제 동작 확인 필요.
2. **프로젝트 Path 미노출**: v1에서는 `"C:\..."` 형식 Path가 Button Name으로 노출됐으나 v2에서는 폴더 이름만 노출. `ProjectInfo.Path`는 빈 문자열로 반환됨.
3. **Local 버튼**: 메인 헤더의 `Button Name='Local'`은 단순 인디케이터로 판단. 클릭 동작은 추후 확인.
4. **세션 스크롤**: `dframe-nav-scroll`에 ScrollPattern이 있는지 확인되지 않음. Fallback mouse wheel 경로가 주로 동작할 가능성.
5. **AskUserQuestion Other flow**: Other 옵션 선택 시 `Edit Name='Other option'`이 등장한다는 것은 dump로 확인. 하지만 "Other 5" Button을 먼저 클릭해야 Edit이 활성화되는지, 아니면 초기부터 보이는지는 추가 검증 필요.
6. **Accept edits 토글**: UI 상 토글 상태가 Name 변경으로 반영되는지 Class 변경으로 반영되는지 미확인.

---

## 8. Phase 10 권고사항 (별도 이슈)

사용자 요청 사항 중 향후 권장 과제:

1. **UI 매핑 외부화**: 현재 클래스/Name 상수를 JSON config(`ClaudeUIMap.json`)로 분리. 다음 UI 업데이트 시 코드 변경 없이 config만 수정.
2. **UI 변경 자동 감지**: 주요 앵커(`dframe-sidebar`, `epitaxy-prompt`, `epitaxy-approval-card`) 미발견 시 이벤트 발행 + 사용자 알림 (예: "Claude 앱이 업데이트된 것 같습니다. 매핑을 재검증하세요").
3. **매핑 버전 tag**: `ClaudeUI_Map.md`에 SHA 또는 semver 기록, 런타임에서 매핑 버전과 Claude 앱 버전을 함께 로그.
4. **통합 테스트 자동화**: 현재 수동 테스트 절차를 xUnit + Playwright-style 검증으로 전환.

---

## 9. 변경 파일 요약

| # | 파일 | 변경 유형 | 주요 내용 |
|---|------|-----------|-----------|
| 1 | `docs/ClaudeUI_Map_v1.md` | 신규 (백업) | 기존 v1 매핑 스냅샷 (변경 이력용) |
| 2 | `docs/ClaudeUI_Map.md` | 전면 재작성 | v2 매핑, v1→v2 변화 매트릭스, 검증 표식 |
| 3 | `docs/screenshot/uitree-phase9-main.txt` | 신규 | Idle 상태 dump (547 노드) |
| 4 | `docs/screenshot/uitree-phase9-askuserq.txt` | 신규 | AskUserQuestion 상태 dump (333 노드) |
| 5 | `docs/screenshot/uitree-phase9-permission.txt` | 신규 | Permission 상태 dump (337 노드) |
| 6 | `ClaudeRemote.Windows/UITreeDumper/UITreeDumper.csproj` | 신규 | Standalone dump tool 프로젝트 |
| 7 | `ClaudeRemote.Windows/UITreeDumper/Program.cs` | 신규 | UI 트리 콘솔 덤프 (AutomationElement.FromHandle 기반) |
| 8 | `ClaudeRemote.Windows/ClaudeRemote.Windows/Services/ClaudeAutomationService.cs` | 전면 재작성 | v1: 2036줄 → v2: 1785줄, 모든 메서드 신규 매핑 |
| 9 | `docs/reports/Phase9_Windows_WorkReport.md` | 신규 | 본 문서 |

---

## 10. 체크리스트

- [x] Claude 앱 버전 확인 (1.2581.0)
- [x] 기존 `ClaudeUI_Map.md` 백업
- [x] UITreeDumper 도구 제작
- [x] Idle / AskUser / Permission 3 상태 dump
- [x] `ClaudeUI_Map.md` 전면 재작성
- [x] 모든 캐시 로직 제거 (`_cachedMainContent`, `_cachedSidebar`, `_cachedTurnForm`, `_cachedInputField`)
- [x] Name 기반 + 컨테이너 class prefix 기반 매핑 적용
- [x] 블랙리스트 접근 회피 (positive 매칭)
- [x] `ClaudeAutomationService.cs` 전면 재작성
- [x] Release 빌드 C# 컴파일 검증 (0 error / 0 warning)
- [x] Phase 9 보고서 작성
- [ ] 사용자 측 통합 테스트 (PID 24740 종료 + 새 빌드 실행 필요)
- [ ] Phase 10 (UI 매핑 외부화) 결정

---

**작성 완료**: 2026-04-15
