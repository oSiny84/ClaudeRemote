# Phase 10 작업 보고서 — 프로젝트 Expand/Collapse 대응

**작업 일자**: 2026-04-15
**대상 플랫폼**: Windows (ClaudeRemote.Windows)
**Claude Desktop 버전**: 1.2581.0+
**선행 작업**: Phase 9 (UI 재매핑 · v2)

---

## 1. 작업 배경

Phase 9에서 Claude Code 데스크탑 앱의 새 UI(v2, `dframe-*` / `epitaxy-*` 클래스 기반)를
재매핑하고 `ClaudeAutomationService.cs`를 전면 재작성했다. 이후 Android 클라이언트 쪽에서
"프로젝트를 눌러도 세션 목록이 갱신되지 않는다"는 현상이 확인되었다.

원인을 추적한 결과, 새 UI는 사이드바의 프로젝트가 **토글형 Expand/Collapse 노드**가 되었고,
펼쳐졌을 때에만 자식 세션 Row가 사이드바 DOM에 나타난다는 구조적 변경이 있었다.

### 1-1. 구체적 문제 상황

1. **v1 동작 (Phase 8 이전)**
   - 사이드바는 "현재 프로젝트 + 그 세션 목록" 하나의 세트만 표시
   - 프로젝트를 클릭 → 해당 프로젝트로 교체(replace)되고 항상 세션이 노출됨
   - `GetSessionsAsync`가 사이드바 아무 곳에서나 세션 Row를 긁으면 정답

2. **v2 동작 (1.2581.0+)**
   - 사이드바는 여러 프로젝트 라벨 Button을 수직 나열 (Accordion 구조)
   - 프로젝트 라벨 클릭 → **그 프로젝트가 펼쳐지고**, 자식 세션 Row가 flat 형제로 등장
   - 같은 프로젝트를 다시 클릭 → 접혀서 세션 Row가 사라짐
   - 동시에 **하나만 펼쳐져 있음**을 보장하지 않음 (관찰 결과 1개만 펼쳐지는 것으로 확인됨)

### 1-2. Phase 9 코드의 한계

Phase 9의 `GetSessionsAsync`는 `dframe-nav-scroll` 자식 중 `group relative rounded-` 클래스로
시작하는 Group을 세션 Row로 잡았다. 이 방식은 "어떤 프로젝트에 속한 세션인지" 알 수 없었고,
`GetProjectsAsync`는 프로젝트의 펼침/접힘 상태를 전혀 감지하지 못해 Android에 전달할 수단도
없었다. 또한 `SelectProjectAsync`는 클릭만 던지고 UI 애니메이션을 기다리지 않아 이어지는
`get_sessions` 호출이 옛 상태를 보게 되는 레이스 가능성이 있었다.

---

## 2. 작업 목표 (요구사항)

사용자 지시 요약:

1. `ProjectInfo` 모델에 `IsExpanded` 추가
2. `GetProjectsAsync`에서 펼침/접힘 감지 — `ExpandCollapsePattern` 시도 후 자식 요소 존재
   여부 fallback
3. `SelectProjectAsync`에 300ms 대기 추가 + 호출부에서 자동 세션 재조회
4. `GetSessionsAsync`는 **현재 펼쳐진 프로젝트의 자식 세션만** 수집
5. `MessageProcessor.select_project` 응답이 **업데이트된 projects + sessions 양쪽**을
   한번에 포함
6. `get_projects` 프로토콜에 `"expanded": true/false` 필드 추가

---

## 3. UI 구조 검증

Phase 9에서 캡처한 `docs/screenshot/uitree-phase9-main.txt` (라인 97~141)로
flat 구조가 확인되었다:

```
[Group] Class="dframe-nav-scroll ..."
  [Button] Name="New session ⌘N"
  [Button] Name="Routines"
  [Button] Name="Customize"
  [Button] Name="More navigation items"
  [Button] Name="Pinned"          Class="group/label ..."   ← 섹션 헤더 (건너뜀)
  [Button] Name="ClaudeRemote"    Class="group/label ..."   ← 펼쳐진 프로젝트
  [Group]  Class="group relative rounded-..." → "Running c#프로그래머"
  [Group]  Class="group relative rounded-..." → "PM 역할"
  [Group]  Class="group relative rounded-..." → "테스트 엔지니어"
  [Group]  Class="group relative rounded-..." → "안드로이드 프로그래머"
  [Button] Name="Watchdog"        Class="group/label ..."   ← 접힘
  [Button] Name="sw-platform-workspace" Class="group/label ..." ← 접힘
  [Button] Name="PO_SOFC_MCU_C28346"    Class="group/label ..." ← 접힘
```

### 3-1. 도출된 감지 규칙

1. **프로젝트 라벨 Button** — `Class` 접두사 `group/label ` + `IsSectionHeader(Name)` 탈락
2. **세션 Row Group** — `Class` 접두사 `group relative rounded-`
3. **Flat 형제 순회**: 프로젝트 라벨 → 그 다음 프로젝트 라벨(또는 nav scroll 끝)까지
   나타나는 세션 Row는 "앞쪽 프로젝트"에 귀속된다
4. **펼침 판정**: 세션 Row가 1개 이상 귀속되면 `IsExpanded = true`, 아니면 false
   (`ExpandCollapsePattern`은 fallback으로 시도)
5. **섹션 헤더**는 현재 owner를 null로 리셋 → "Pinned" 밑의 pinned 세션이 엉뚱한
   프로젝트에 붙지 않게 함

---

## 4. 구현 변경 사항

### 4-1. `Models/ProjectInfo.cs`

```csharp
public class ProjectInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsActive { get; set; }

    /// <summary>
    /// Whether the project node is currently expanded in the sidebar (sessions visible).
    /// Detected via ExpandCollapsePattern when available, falling back to structural check
    /// (presence of session rows between this project label and the next).
    /// </summary>
    public bool IsExpanded { get; set; }
}
```

### 4-2. `Services/ClaudeAutomationService.cs`

#### 4-2-1. 신규 헬퍼 클래스 `ProjectLayoutEntry`

```csharp
private sealed class ProjectLayoutEntry
{
    public AutomationElement Label { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public int Index { get; set; }
    public List<AutomationElement> SessionGroups { get; } = new();
}
```

`dframe-nav-scroll`를 한 번 순회하면서 "이 프로젝트에 귀속된 세션 Row 리스트"를
같이 들고 다니는 단일 표현. 이후의 `GetProjectsAsync` / `GetSessionsAsync` /
`SelectProjectAsync` / `SelectSessionAsync`가 모두 이 헬퍼를 공유한다.

#### 4-2-2. 신규 `CollectProjectLayout(navScroll)`

Raw tree walker로 nav scroll의 직계 자식을 한번 훑으며:

- Button + `group/label ` 클래스 + 섹션 헤더 아님 → 새 `ProjectLayoutEntry` 시작
- Button + `group/label ` 클래스 + 섹션 헤더(또는 Name 없음) → `current = null`
- `group relative rounded-` 클래스의 Group → `current.SessionGroups.Add(child)`

이 한 번의 walk으로 프로젝트 목록과 "어느 프로젝트가 어떤 세션 Row를 소유하는지"가
동시에 나온다.

#### 4-2-3. 신규 `DetectProjectExpanded(entry)`

```csharp
private static bool DetectProjectExpanded(ProjectLayoutEntry entry)
{
    // 구조적 확인 우선 — 가장 신뢰할 수 있는 사실
    if (entry.SessionGroups.Count > 0) return true;

    // Fallback: ExpandCollapsePattern (React 버튼에서 구현되지 않는 경우 많음)
    try
    {
        if (entry.Label.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern))
        {
            var state = ((ExpandCollapsePattern)pattern).Current.ExpandCollapseState;
            return state == ExpandCollapseState.Expanded;
        }
    }
    catch { }

    return false;
}
```

구조 확인을 **주**로, `ExpandCollapsePattern`을 **보조**로 둔 것이 핵심. v2 UI에서
`ExpandCollapsePattern`이 구현되어 있는지는 확실치 않지만, 구현되어 있고 접혀있다고
보고해도 구조적으로 세션 Row가 있으면 true를 돌려 "최후 사실(observed truth)"을 따른다.

#### 4-2-4. `GetProjectsAsync` 재작성

```csharp
var layout = CollectProjectLayout(navScroll);

foreach (var entry in layout)
{
    projects.Add(new ProjectInfo
    {
        Id = $"project_{entry.Index}",
        Name = entry.Name,
        Path = string.Empty,
        IsActive = false, // 아래에서 header 매칭으로 설정
        IsExpanded = DetectProjectExpanded(entry)
    });
}

// Active 프로젝트: Primary pane 헤더 텍스트 매칭이 최우선
var primaryPane = FindPrimaryPane();
string? headerProjectName = primaryPane != null
    ? FindHeaderProjectName(primaryPane)
    : null;

if (!string.IsNullOrEmpty(headerProjectName))
{
    for (int i = 0; i < projects.Count; i++)
        projects[i].IsActive = projects[i].Name == headerProjectName;
}
else if (projects.Count > 0)
{
    // Fallback 1: 펼쳐진 첫 프로젝트
    var firstExpanded = projects.FindIndex(p => p.IsExpanded);
    if (firstExpanded >= 0)
        projects[firstExpanded].IsActive = true;
    else
        projects[0].IsActive = true; // Fallback 2: 첫 라벨
}
```

`IsActive` 우선순위:
1. Primary pane의 헤더 프로젝트 이름과 일치
2. 펼쳐진 첫 프로젝트
3. 첫 라벨

헤더 매칭이 우선인 이유: 새 v2에서도 "활성 프로젝트"는 본문 창에 이름이 박히므로 가장
믿을 수 있는 진실이다. 펼침 여부로는 판정하지 않는다 — 펼쳐졌더라도 본문이 다른
프로젝트를 보여주는 과도기가 있을 수 있기 때문.

#### 4-2-5. `GetSessionsAsync` 재작성

```csharp
for (int pass = 0; pass <= ScrollPassCount; pass++)
{
    // 스크롤이 AutomationElement 참조를 무효화할 수 있으므로 매 패스 재-walk
    var layout = CollectProjectLayout(navScroll);
    var expandedEntry = layout.FirstOrDefault(e => e.SessionGroups.Count > 0);

    if (expandedEntry == null)
    {
        Log.Information("[GetSessions] Pass {Pass}: no expanded project — returning {Count}",
            pass, allSessions.Count);
        break;  // 펼쳐진 프로젝트 없음 → 빈 리스트 반환
    }

    expandedProjectName = expandedEntry.Name;

    foreach (var rowGroup in expandedEntry.SessionGroups)
    {
        // ... "Running " 접두사 제거 후 SessionInfo 추가 ...
    }
    // 스크롤 시도 & 다음 패스
}
```

핵심: "펼쳐진 프로젝트가 없으면 빈 리스트"를 명시적으로 처리한다. Android가 이 상태를
"세션 없음"으로 표시해 주면 UX가 자연스러워진다.

#### 4-2-6. `SelectProjectAsync` 재작성

```csharp
var layout = CollectProjectLayout(navScroll);
if (idx < 0 || idx >= layout.Count) { /* warn & return false */ }

var entry = layout[idx];
bool wasExpanded = entry.SessionGroups.Count > 0;
Log.Information("[SelectProject] Clicking '{Name}' (idx={Idx}, wasExpanded={Exp})",
    entry.Name, idx, wasExpanded);

if (entry.Label.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
    ((InvokePattern)pattern).Invoke();
else
    ClickElement(entry.Label);

// 300ms: Claude desktop React 트랜지션이 정착하는 시간. 이보다 짧으면
// 이어지는 GetProjectsAsync가 옛 상태를 보는 경우가 있었음.
Thread.Sleep(300);
```

`wasExpanded`를 로그에 남긴 이유: "접혀있었는데 열림 OK", "열려있었는데 닫힘 OK" 두
시나리오를 로그로 구분하기 위함. 디버깅 시 Android에서 왜 특정 탭이 사라졌는지
거슬러 올라가기 쉽다.

#### 4-2-7. `SelectSessionAsync` 업데이트 (부수)

세션 인덱스가 "글로벌 session_N"이 아니라 "**펼쳐진 프로젝트 안에서의 N**"으로
재정의되었기 때문에, `SelectSessionAsync`도 layout 헬퍼를 거쳐 펼쳐진 프로젝트의
`SessionGroups[idx]`를 클릭한다.

```csharp
var layout = CollectProjectLayout(navScroll);
var expandedEntry = layout.FirstOrDefault(e => e.SessionGroups.Count > 0);

if (expandedEntry == null) { /* warn & false */ }
if (idx < 0 || idx >= expandedEntry.SessionGroups.Count) { /* warn & false */ }

var sessionBtn = FindFirstButtonRaw(expandedEntry.SessionGroups[idx]);
// ... Invoke or Click ...
```

이것으로 `GetSessionsAsync`가 리턴한 인덱스와 `SelectSessionAsync`가 받는 인덱스의
의미가 완벽히 맞춰진다.

### 4-3. `Services/SessionManager.cs`

```csharp
public async Task SelectProjectAsync(string projectId)
{
    if (await _claudeService.SelectProjectAsync(projectId))
    {
        // v2 UI: 프로젝트 라벨 클릭은 그 프로젝트의 세션 목록을 펼침/접음.
        // 프로젝트(IsExpanded)와 세션(새 펼침 집합)을 모두 새로고침해
        // Android가 단일 응답으로 양쪽 패널을 업데이트할 수 있게 함.
        await RefreshProjectsAsync();
        await RefreshSessionsAsync();
    }
}
```

변경은 `RefreshSessionsAsync()` 호출 한 줄 추가뿐. 하지만 이 한 줄이 "클릭했으니 세션
목록도 다시 가져와라"는 핵심 플로우를 만들어낸다.

### 4-4. `Services/MessageProcessor.cs`

#### 4-4-1. `select_project` 핸들러

```csharp
case MessageAction.SelectProject:
    var projectId = GetPayloadString(message, "projectId");
    EmitLog($"← select_project: {projectId}");
    await _sessionManager.SelectProjectAsync(projectId);  // 내부에서 projects+sessions 리프레시
    var selectedProject = _sessionManager.Projects
        .FirstOrDefault(p => p.Id == projectId);
    EmitLog(
        $"  ✓ select_project OK — projects={_sessionManager.Projects.Count}, " +
        $"sessions={_sessionManager.Sessions.Count}, " +
        $"expanded={selectedProject?.IsExpanded.ToString() ?? "?"}");
    response = CreateSelectProjectResponseMessage(
        _sessionManager.Projects,
        _sessionManager.Sessions);
    break;
```

#### 4-4-2. 신규 `CreateSelectProjectResponseMessage`

`select_project` 응답은 기존의 단순 `{success: true}`에서, 아래처럼 **projects +
sessions 양쪽을 실은 복합 응답**으로 격상된다:

```json
{
  "type": "response",
  "action": "select_project",
  "mode": "code",
  "payload": {
    "success": true,
    "projects": [
      {"id": "project_0", "name": "ClaudeRemote", "path": "",
       "active": true, "expanded": true},
      {"id": "project_1", "name": "Watchdog", "path": "",
       "active": false, "expanded": false}
    ],
    "sessions": [
      {"id": "session_0", "name": "c#프로그래머", "active": true, "lastMessage": ""},
      {"id": "session_1", "name": "PM 역할", "active": false, "lastMessage": ""}
    ]
  }
}
```

Android는 이 하나의 응답으로 사이드바 두 영역(프로젝트 아코디언 상태 + 세션 리스트)을
동시에 동기화할 수 있다. 한 번의 RTT에 한 번의 UI 업데이트.

#### 4-4-3. `CreateProjectListMessage`에 `expanded` 필드 추가

`get_projects` 응답도 동일한 스키마를 쓰도록 확장:

```csharp
["projects"] = projects.Select(p => new Dictionary<string, object>
{
    ["id"] = p.Id,
    ["name"] = p.Name,
    ["path"] = p.Path,
    ["active"] = p.IsActive,
    ["expanded"] = p.IsExpanded  // Phase 10 신규
}).ToList()
```

### 4-5. `Services/IMessageProcessor.cs`

인터페이스에 신규 메서드 등록(일관성):

```csharp
string CreateSelectProjectResponseMessage(
    List<Models.ProjectInfo> projects,
    List<Models.SessionInfo> sessions);
```

---

## 5. 프로토콜 변경 요약

| 메시지 | 변경 전 | 변경 후 |
|---|---|---|
| `get_projects` 응답 | `{id, name, path, active}` | `{id, name, path, active, expanded}` |
| `select_project` 응답 | `{success: true}` | `{success, projects[…+expanded], sessions[…]}` |
| `session_N` 인덱스 의미 | 사이드바 내 절대 순서 | **펼쳐진 프로젝트 내**의 순서 |

> **호환성**: 기존 `get_projects` 응답에 필드가 추가되는 것뿐이므로 Android의 이전
> 파서가 모르는 필드를 무시하면 호환. `select_project` 응답은 완전히 새로운 형태로
> 바뀌었지만 이전에도 Android가 `{success}`만 보고 세션을 별도 `get_sessions`로
> 따로 부르던 구조였다면, 이제는 한번에 받도록 개선되는 것이 된다.

---

## 6. 빌드 결과

```
> dotnet build -c Release

  Determining projects to restore...
  All projects are up-to-date for restore.
  ClaudeRemote.Windows -> bin\Release\net8.0-windows\ClaudeRemote.Windows.dll

Build succeeded.

    1 Warning(s)
    0 Error(s)

Time Elapsed 00:00:02.90
```

- 경고 1건(`SessionManager.ModeChanged` 미사용, 선행 Phase부터 존재하던 잡음) — Phase 10 변경과 무관
- 오류 0건
- `ClaudeRemote.Windows.exe` / `.dll` 2026-04-15 12:37 재생성 확인

`ClaudeAutomationService.cs` 라인 수: **1,785 → 1,852** (+67 라인, 신규 헬퍼 3개)

---

## 7. 통합 테스트 예상 시나리오 (향후 실행)

Claude Code 앱이 실행 중인 상태에서 Android 에뮬레이터/디바이스로 다음을 검증한다:

### 7-1. 초기 상태: 프로젝트 A 펼쳐짐

- [ ] `get_projects` → projects 중 하나만 `expanded=true`
- [ ] 그 프로젝트의 `active=true`
- [ ] `get_sessions` → 그 프로젝트의 세션들만 반환

### 7-2. 다른 프로젝트 B 클릭

- [ ] `select_project` 응답: projects에서 B가 `expanded=true`, A가 `expanded=false`
- [ ] 응답의 sessions 필드가 B의 세션 리스트로 교체
- [ ] 이어지는 별도 `get_sessions`도 동일한 B 세션 리스트

### 7-3. 같은 프로젝트 B 다시 클릭 (접기)

- [ ] `select_project` 응답: projects에서 B가 `expanded=false`
- [ ] 응답의 sessions 필드가 **빈 배열**
- [ ] 이어지는 `get_sessions`도 빈 배열

### 7-4. 접힌 상태에서 프로젝트 C 클릭

- [ ] C가 열리고 sessions에 C의 세션 리스트 등장

### 7-5. `select_session` 경로 검증

- [ ] 현재 펼쳐진 프로젝트의 N번째 세션이 클릭됨 (글로벌 인덱스가 아니라)

### 7-6. Primary pane 헤더 vs 사이드바 펼침 불일치

- [ ] 헤더가 A인데 사이드바에 B가 펼쳐져 있다면 `active=A, expanded=B` 로그 출력
- [ ] `IsActive`가 헤더를 우선 따르는 것을 재확인

---

## 8. 주의사항 / 알려진 제약

### 8-1. "펼쳐진 프로젝트 없음" 케이스

사용자가 모든 프로젝트를 한번씩 토글해 전부 접은 상태에서는 `GetSessionsAsync`가
빈 리스트를 반환한다. Android는 이 상태를 "활성 프로젝트를 선택해 주세요" 플레이스홀더로
처리하면 된다. `IsActive`는 여전히 Primary pane 헤더로 결정되므로 "어디로
되돌아갈지"는 알 수 있다.

### 8-2. 동시에 여러 프로젝트가 펼쳐지는 경우

관찰된 범위에서 Claude v2는 한 번에 하나만 펼친다. 하지만 방어적으로
`layout.FirstOrDefault(e => e.SessionGroups.Count > 0)`이 첫 번째만 가져간다.
여러 개가 펼쳐진 예외 상황이 생기면 첫 번째만 보인다 — 실제로 문제되면
Phase 11에서 "가장 위에 있는 펼침"이 아니라 "IsActive와 일치하는 펼침"을
고르도록 바꿀 수 있다.

### 8-3. `ExpandCollapsePattern` 미지원 가능성

구조 확인이 primary이므로 pattern이 없어도 동작하는데, pattern이 있고 "Collapsed"를
리턴하면 false로 가지 않고 구조 기준 true를 따른다. 이 보수적 로직이 React 구현의
a11y 상태 지연을 흡수한다.

### 8-4. 300ms 대기 하드코딩

너무 짧으면 이어지는 refresh가 옛 상태를 보고, 너무 길면 UX가 느려진다. 300ms는
경험적 값이며, 느린 장비에서 문제가 생기면 설정 파일로 빼는 것을 Phase 11에서 고려.

### 8-5. `Pinned` 섹션의 pinned 세션

현재 `IsSectionHeader("Pinned") == true` 처리로 "Pinned 라벨 밑의 세션"은 어떤
프로젝트에도 귀속시키지 않는다 (`current = null`로 리셋). Pinned 세션을 Android
사이드바에 별도 표시해야 한다면 `CollectProjectLayout`이 섹션도 반환하도록 확장해야
한다 — 이번 Phase는 프로젝트-세션 관계에만 집중했다.

---

## 9. 파일 변경 요약

| 파일 | 변경 | 핵심 |
|---|---|---|
| `Models/ProjectInfo.cs` | +7 라인 | `IsExpanded` 속성 추가 |
| `Services/ClaudeAutomationService.cs` | +67 라인 net | `ProjectLayoutEntry`, `CollectProjectLayout`, `DetectProjectExpanded` 신규. `GetProjectsAsync` / `GetSessionsAsync` / `SelectProjectAsync` / `SelectSessionAsync` 모두 layout 헬퍼 기반으로 재작성 |
| `Services/SessionManager.cs` | +1 라인 | `SelectProjectAsync`가 `RefreshSessionsAsync`도 호출 |
| `Services/MessageProcessor.cs` | +40 라인 | `select_project` 핸들러가 복합 응답 생성. `CreateSelectProjectResponseMessage` 신규. `CreateProjectListMessage`에 `expanded` 필드 |
| `Services/IMessageProcessor.cs` | +1 라인 | `CreateSelectProjectResponseMessage` 등록 |

---

## 10. 체크리스트

### Phase 10 요구사항 충족

- [x] `ProjectInfo.IsExpanded` 추가
- [x] `GetProjectsAsync`에서 `IsExpanded` 판정 (구조 + ExpandCollapsePattern fallback)
- [x] `SelectProjectAsync` 300ms 대기
- [x] `GetSessionsAsync`가 펼쳐진 프로젝트의 세션만 반환
- [x] `MessageProcessor.select_project`가 projects + sessions 양쪽 포함한 응답 생성
- [x] `get_projects` / `select_project` 응답에 `expanded` 필드
- [x] Release 빌드 성공 (0 오류)
- [x] Phase 10 보고서 작성

### Phase 9 원칙 준수 확인

- [x] 블랙리스트 없음 — 모든 식별은 `group/label ` / `group relative rounded-` 접두사로 positive 매칭
- [x] AutomationId 캐싱 없음 — 매 호출마다 `FindNavScroll()` 재조회
- [x] Container 기반 — nav scroll 자체를 기준으로 flat 순회
- [x] 섹션 헤더 처리(Pinned/Recent/Favorites/Filter*)로 오작동 방지

### Android 측 대응 필요 사항 (별도 이슈)

- [ ] 프로젝트 아이템 UI에 pen/chevron 아이콘으로 `expanded` 시각화
- [ ] `select_project` 응답 파서가 projects/sessions 양쪽을 동시 반영
- [ ] "모두 접힘" 상태의 세션 패널 플레이스홀더

---

## 11. 다음 단계 (Phase 11 권고사항)

1. **실기 테스트**: Claude Code에 실제 연결해 7절 시나리오 수동 검증
2. **`session_N` 재매핑 이슈**: 다른 프로젝트를 펼치면 기존 `session_0`이 가리키던
   요소가 바뀐다. Android가 "방금 있던 세션이 사라진/교체된" 상황을 에러 없이
   처리하는지 확인
3. **300ms 상수화**: 설정으로 노출
4. **`Pinned` 섹션 세션 처리**: Android에서 Pinned 세션 UI가 필요하면 레이아웃 헬퍼
   확장
5. **Monitor loop**: 사용자가 Claude 앱에서 직접 프로젝트를 접었을 때 Android에
   자동 푸시되도록, 모니터 루프에서 `expanded` 상태 변화도 감지해 broadcast
