# Phase 11 작업 보고서 — 사용량 Observability (UsageInfo + UsageDashboard)

**작업 일자**: 2026-04-15
**대상 플랫폼**: Windows (ClaudeRemote.Windows)
**Claude Desktop 버전**: 1.2581.0+
**선행 작업**: Phase 9 (UI 재매핑), Phase 10 (프로젝트 Expand/Collapse)

---

## 1. 작업 배경

Phase 10까지 완료된 시점에서 Android 클라이언트는 프로젝트/세션/입력/승인 버튼까지
모든 조작 기능을 갖췄지만, **"지금 내 Claude 사용량이 어떻게 되는지"를 확인할 수 없었다.**
사용자가 장시간 세션을 진행하다가 갑자기 Plan 한도에 걸리거나 Weekly limit이 리셋되기
직전인지를 알 방법이 없었다.

Claude Code 데스크탑 앱에는 두 종류의 사용량 표시가 있다:

1. **인라인 작은 버튼**: 입력창 푸터에 항상 떠 있는 "Usage: context 106.1k, plan 49%"
2. **종합 패널**: 사용자 프로필 드롭다운 → "Usage" 선택 시 열리는 상세 패널
   - Plan usage limits (Max/Pro/... + Current session)
   - Weekly limits (All models + Sonnet only)
   - Additional features (Daily included routine runs)
   - Last updated 타임스탬프

Phase 11은 **두 데이터 소스를 모두 Android에 노출**하는 것이 목표다.

---

## 2. 작업 목표 (요구사항)

사용자 지시 요약:

1. UI 분석 → `docs/ClaudeUI_UsagePanel.md`에 경로 문서화
2. `Models/UsageDashboard.cs` (+ `UsageLimit`, `RoutineUsage`) 신규
3. `ClaudeAutomationService.GetUsageDashboardAsync()` 구현
   - 패널 열기 → 500ms 대기 → 텍스트 추출 → 정규식 파싱 → 닫기
   - Phase 8 `WithWindowRestoredAsync()` 사용
4. 새 프로토콜 `get_usage_dashboard`
5. `MessageProcessor`에 핸들러 추가
6. **UsageInfo와 UsageDashboard는 별도** — 둘 다 구현

---

## 3. 두 데이터 소스의 분리 설계

| 구분 | UsageInfo | UsageDashboard |
|---|---|---|
| 데이터 출처 | 인라인 Button Name | 별도 패널 내 Text/Button |
| 스크레이핑 비용 | **매우 쌈** (버튼 하나 찾기) | **비쌈** (패널 열기/닫기) |
| 사용자 방해 | 없음 | 일시적으로 포커스 뺏음 |
| 호출 빈도 | 하트비트/폴링/send_input 직후도 OK | 사용자가 "새로고침" 눌렀을 때 |
| 필드 | `rawText`, `contextText`, `planPercent` | `planName`, `currentSession`, `weeklyAllModels`, `weeklySonnetOnly`, `routineRuns`, `lastUpdated` |
| 프로토콜 | `get_usage` | `get_usage_dashboard` |

이 분리로 Android는 **자주 가볍게 UsageInfo만 받다가, 사용자가 상세 화면에 들어올 때
한 번 UsageDashboard를 받는** 2-tier 소비 패턴을 쓸 수 있다.

---

## 4. 구현 상세

### 4-1. 신규 모델 (`Models/UsageDashboard.cs`, `Models/UsageInfo.cs`)

```csharp
// UsageInfo.cs — 인라인 버튼 1개에서 파싱
public class UsageInfo
{
    public string? RawText { get; set; }      // "Usage: context 106.1k, plan 49%"
    public string? ContextText { get; set; }  // "106.1k" (단위 포함 문자열 그대로)
    public int? PlanPercent { get; set; }     // 49
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

// UsageDashboard.cs — 패널 전체
public class UsageDashboard
{
    public string? PlanName { get; set; }                 // "Max (5x)"
    public UsageLimit? CurrentSession { get; set; }
    public UsageLimit? WeeklyAllModels { get; set; }
    public UsageLimit? WeeklySonnetOnly { get; set; }
    public string? LastUpdated { get; set; }              // "4 minutes ago"
    public RoutineUsage? RoutineRuns { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

public class UsageLimit
{
    public string? Label { get; set; }
    public string? ResetText { get; set; }  // 원문 그대로 ("Resets Fri 5:00 PM")
    public int? PercentUsed { get; set; }
}

public class RoutineUsage
{
    public string? Label { get; set; }
    public int? Used { get; set; }
    public int? Total { get; set; }
}
```

`ResetText`를 파싱하지 않고 원문 그대로 보존하는 것이 핵심 설계 선택이다:

- `"Resets in 1 hr 26 min"`
- `"Resets in 21 hr 26 min"`
- `"Resets Fri 5:00 PM"`

이 텍스트들은 Claude가 상황에 따라 다른 형식을 쓰며(상대 시간 vs 절대 시간), Android가
그대로 표시하는 편이 재포맷 버그 없이 안전하다. PercentUsed만 구조화해 0~100 정수로
노출한다.

### 4-2. `ClaudeAutomationService` — 신규 섹션 "Usage observability"

#### 4-2-1. 정규식 (컴파일 캐시)

```csharp
InlineUsageRegex  = ^Usage:\s*context\s*([^,]+?),\s*plan\s*(\d+)\s*%\s*$
PercentUsedRegex  = (\d+)\s*%\s*used
RoutineUsageRegex = (\d+)\s*/\s*(\d+)
LastUpdatedRegex  = ^Last updated:\s*(.+)$
```

네 개 모두 `RegexOptions.Compiled`로 미리 컴파일해 매 호출에서 재사용.

#### 4-2-2. `GetUsageInfoAsync()`

```csharp
var primaryPane = FindPrimaryPane();
var btn = FindButtonByNamePrefixRaw(primaryPane, "Usage:");
var raw = SafeGetName(btn);
var info = new UsageInfo { RawText = raw };

var m = InlineUsageRegex.Match(raw);
if (m.Success)
{
    info.ContextText = m.Groups[1].Value.Trim();
    if (int.TryParse(m.Groups[2].Value, out var pct))
        info.PlanPercent = pct;
}
return info;
```

Primary pane 스코프로 검색해 인라인 버튼만 매치 (전역 스캔으로 Usage 관련 다른
요소와 충돌 방지). Regex가 실패해도 `RawText`는 채워서 반환 → Android에 "원문만이라도"
보여줄 수 있게.

#### 4-2-3. `GetUsageDashboardAsync()`

```csharp
return await WithWindowRestoredAsync(() => Task.Run(() =>
{
    bool openedByUs = false;
    try
    {
        var panel = FindUsagePanelRoot();          // Step 1: idempotent

        if (panel == null)
        {
            if (!OpenUsagePanel()) return null;     // Step 2: click profile → menu item
            openedByUs = true;
            Thread.Sleep(500);                      // React transition settle
            panel = FindUsagePanelRoot();
            if (panel == null) return null;
        }

        var dashboard = ParseUsagePanel(panel);     // Step 3: state machine

        if (openedByUs) TryCloseUsagePanel();       // Step 4: ESC if we opened it
        return dashboard;
    }
    catch
    {
        if (openedByUs) { try { TryCloseUsagePanel(); } catch { } }
        return null;
    }
}));
```

`Phase 8`의 `WithWindowRestoredAsync`를 그대로 사용해 **창이 최소화돼 있으면 복원**
→ 작업 수행 → **원래 최소화 상태였다면 다시 최소화**하는 동작이 자동으로 보장된다.

`openedByUs` 플래그 핵심 의미: **사용자가 이미 패널을 열어둔 상태에서 요청이 들어오면
닫지 않는다**. 사용자가 수동으로 확인 중일 때 Android 요청 한 번에 패널이 닫혀버리면
UX가 나빠진다.

#### 4-2-4. `FindUsagePanelRoot()`

패널을 찾는 문제는 "패널이 아닌 곳에서 같은 텍스트가 나타나지 않는다"라는 가정 위에
풀 수 있다.

```csharp
// 1. "Plan usage limits" Text anchor 찾기
var anchor = WalkFindFirst(_claudeWindow, el =>
    el.Current.ControlType == ControlType.Text &&
    SafeGetName(el) == "Plan usage limits");
if (anchor == null) return null;

// 2. ControlViewWalker로 조상을 따라 올라가며 "Weekly limits"도 밑에 있는
//    가장 작은 ancestor를 찾는다 (최대 10단계)
var walker = TreeWalker.ControlViewWalker;
var parent = walker.GetParent(anchor);
for (int i = 0; i < 10 && parent != null; i++)
{
    var weekly = WalkFindFirst(parent, el =>
        el.Current.ControlType == ControlType.Text &&
        SafeGetName(el) == "Weekly limits");
    if (weekly != null) return parent;
    parent = walker.GetParent(parent);
}

// 3. Fallback: anchor의 5-up ancestor
```

두 개의 anchor("Plan usage limits" + "Weekly limits")를 모두 포함하는 가장 작은
공통 조상을 찾아 그걸 "패널 루트"로 정의한다. 이 방식은 패널의 실제 React 클래스
이름을 몰라도 동작한다 — Phase 9의 "class prefix positive matching" 원칙과 같은 맥락.

#### 4-2-5. `OpenUsagePanel()`

```csharp
// 1. 프로필 버튼 찾기: radix-prefix AID + 부모 "max-w-[75%]" 클래스
var profileBtn = WalkFindFirst(sidebar, el =>
{
    if (el.Current.ControlType != ControlType.Button) return false;
    var aid = el.Current.AutomationId ?? "";
    if (!aid.StartsWith("radix-")) return false;
    var par = TreeWalker.ControlViewWalker.GetParent(el);
    return par != null && SafeGetClassName(par).Contains("max-w-[75%]");
});

// 2. InvokePattern으로 클릭 (Radix onClick이 dropdown을 엽니다)
InvokePattern.Invoke() or ClickElement();
Thread.Sleep(250);

// 3. 창 전체에서 Name에 "Usage" 포함된 MenuItem/Button 찾기
//    단 "Usage:"로 시작하는 인라인 버튼은 제외
var usageItem = WalkFindFirst(_claudeWindow, el =>
{
    if (el.Current.ControlType != MenuItem && != Button) return false;
    var name = SafeGetName(el);
    if (name.StartsWith("Usage:")) return false;
    return name.Contains("Usage", OrdinalIgnoreCase);
});

// 4. MenuItem Invoke
```

**Radix 포털 주의**: Radix UI 드롭다운은 document body에 포털로 렌더링되므로, 메뉴 항목은
사이드바 subtree 안이 아니라 **창 전체 트리**를 뒤져야 찾을 수 있다.

**인라인 버튼 제외**: `"Usage:"` 접두사로 시작하는 건 이미 가지고 있는 인라인 버튼이므로
메뉴 항목 검색에서 탈락시켜야 오픈 대상을 잘못 클릭하지 않는다.

#### 4-2-6. `ParseUsagePanel()` 상태 머신

수집된 라인 배열을 section state(`NONE / PLAN / WEEKLY / ADDITIONAL`)를 유지하며 순회한다:

```
Plan usage limits  → section = PLAN
Max (5x)           → dashboard.PlanName
Current session    → sessionCursor = new UsageLimit
Resets in 1 hr 26 min, 78% used  → sessionCursor.ResetText + PercentUsed

Weekly limits      → section = WEEKLY
All models         → weeklyCursor = new UsageLimit (dashboard.WeeklyAllModels)
Resets in 21 hr 26 min
53% used           → weeklyCursor 채움
Sonnet only        → weeklyCursor = new UsageLimit (dashboard.WeeklySonnetOnly)
Resets Fri 5:00 PM
0% used            → 새 weeklyCursor 채움

Last updated: 4 minutes ago  → dashboard.LastUpdated (섹션 무관)

Additional features → section = ADDITIONAL
Daily included routine runs → routineCursor = new RoutineUsage
0 / 15             → routineCursor.Used/Total
```

**양쪽 값이 한 라인에 있는 경우** (`"Resets in 1 hr 26 min, 78% used"`)와 **분리된
경우** (`"Resets in 21 hr 26 min"` + `"53% used"`) 모두 `TryFillLimit`이 동일하게
처리한다.

#### 4-2-7. `CollectUsagePanelStrings()` — 커스텀 수집기

기존 `CollectTextsRaw`는 `ControlType.Text`만 수집하지만, React 패널은 라벨을 Button/Group의
Name으로 드러내는 경우가 있다. 전용 수집기를 만들어:

- `ControlType.Text` → Name 수집 (leaf)
- `ControlType.Button` + Name 길이 ≤ 80 + UI chrome 아님 → 수집 (계속 재귀)
- 기타 Group/Container → 자식으로 재귀
- 직전 라인과 동일한 문자열은 dedup

UI chrome 필터: `Close` / `Back` / `Share` / `Copy` / `Dismiss` / `Close *`는 데이터가 아님.

#### 4-2-8. `TryCloseUsagePanel()`

```csharp
System.Windows.Forms.SendKeys.SendWait("{ESC}");
Thread.Sleep(150);
```

ESC는 Radix 다이얼로그/메뉴의 표준 dismiss 경로. 입력 포커스가 Claude 창 밖에 있으면
실패할 수 있으나, 바로 전에 `WithWindowRestoredAsync`가 창을 전면화했으므로 정상
도달한다.

### 4-3. `IClaudeAutomationService` 확장

```csharp
// Phase 11: usage observability
Task<UsageInfo?> GetUsageInfoAsync();
Task<UsageDashboard?> GetUsageDashboardAsync();
```

### 4-4. `MessageAction` 상수 추가

```csharp
// Phase 11: usage observability
public const string GetUsage = "get_usage";
public const string GetUsageDashboard = "get_usage_dashboard";
```

### 4-5. `MessageProcessor` 핸들러

```csharp
case MessageAction.GetUsage:
    EmitLog("← get_usage");
    var usage = await _claudeService.GetUsageInfoAsync();
    if (usage != null)
    {
        EmitLog($"  ✓ get_usage: context={usage.ContextText}, plan={usage.PlanPercent}%");
        response = CreateUsageInfoMessage(usage);
    }
    else
    {
        response = CreateResponseMessage(message.Action, false, ErrorCode.ElementNotFound);
    }
    break;

case MessageAction.GetUsageDashboard:
    EmitLog("← get_usage_dashboard");
    var dashboard = await _claudeService.GetUsageDashboardAsync();
    if (dashboard != null)
    {
        EmitLog($"  ✓ get_usage_dashboard: plan={dashboard.PlanName}, ...");
        response = CreateUsageDashboardMessage(dashboard);
    }
    else
    {
        response = CreateResponseMessage(message.Action, false, ErrorCode.ElementNotFound);
    }
    break;
```

### 4-6. 프로토콜 메시지 빌더

두 신규 메서드:

- `CreateUsageInfoMessage(UsageInfo)` — `type=response, action=get_usage, payload.usage={...}`
- `CreateUsageDashboardMessage(UsageDashboard)` — `type=response, action=get_usage_dashboard, payload.dashboard={...}`

`UsageLimit?`이 null일 경우 JSON에서도 null로 직렬화. `SerializeLimit(UsageLimit?)` 헬퍼로
중복 코드 제거.

### 4-7. `IMessageProcessor`에 등록

```csharp
string CreateUsageInfoMessage(Models.UsageInfo usage);
string CreateUsageDashboardMessage(Models.UsageDashboard dashboard);
```

---

## 5. 프로토콜 샘플

### 5-1. `get_usage` 응답

```json
{
  "type": "response",
  "action": "get_usage",
  "mode": "code",
  "payload": {
    "success": true,
    "usage": {
      "rawText": "Usage: context 106.1k, plan 49%",
      "contextText": "106.1k",
      "planPercent": 49,
      "fetchedAt": "2026-04-15T10:00:00.1234Z"
    }
  }
}
```

### 5-2. `get_usage_dashboard` 응답

```json
{
  "type": "response",
  "action": "get_usage_dashboard",
  "mode": "code",
  "payload": {
    "success": true,
    "dashboard": {
      "planName": "Max (5x)",
      "lastUpdated": "4 minutes ago",
      "fetchedAt": "2026-04-15T10:00:00.1234Z",
      "currentSession": {
        "label": "Current session",
        "resetText": "Resets in 1 hr 26 min",
        "percentUsed": 78
      },
      "weeklyAllModels": {
        "label": "All models",
        "resetText": "Resets in 21 hr 26 min",
        "percentUsed": 53
      },
      "weeklySonnetOnly": {
        "label": "Sonnet only",
        "resetText": "Resets Fri 5:00 PM",
        "percentUsed": 0
      },
      "routineRuns": {
        "label": "Daily included routine runs",
        "used": 0,
        "total": 15
      }
    }
  }
}
```

### 5-3. 실패 응답

```json
{
  "type": "response",
  "action": "get_usage_dashboard",
  "payload": {
    "success": false,
    "error": "ELEMENT_NOT_FOUND"
  }
}
```

---

## 6. 빌드 결과

**C# 컴파일**: 성공 (0 오류, obj DLL `131KB` @ 13:19 — Phase 10 대비 +14KB)

**EXE 교체**: 실패 (MSB3027 — `ClaudeRemote.Windows.exe` PID 13372 실행 중)

```
Build FAILED.
    11 Warning(s)  (MSB3026 파일 잠금 재시도 10회 + 기존 CS0067 1건)
    2 Error(s)     (MSB3027/MSB3021 — EXE copy lock)
```

> **C# 코드 자체에는 오류가 없다.** 컴파일된 DLL은 정상적으로 `obj/Release/net8.0-windows/`에
> 생성되었고, Phase 10 대비 14KB 증가로 Phase 11 코드가 포함됐음이 확인된다. EXE 교체만
> 실패한 이유는 Phase 9/10에서도 겪었던 "앱 실행 중"이므로, **사용자가 `ClaudeRemote.Windows.exe`를
> 종료한 후 재빌드하면 정상 완료될 것이다.**

`ClaudeAutomationService.cs` 라인 수: **1852 → 2166** (+314 라인, Usage observability 섹션 전체)

---

## 7. 통합 테스트 시나리오 (향후 실행)

### 7-1. 인라인 UsageInfo

1. Claude Code 앱 열어두고 아무 세션 열기
2. Android에서 `{"type":"command","action":"get_usage"}` 전송
3. 기대 응답:
   - `success: true`
   - `rawText`가 `"Usage: context …, plan …"` 형태
   - `contextText`가 단위 포함 문자열
   - `planPercent`가 0~100 정수
4. 다른 세션으로 전환 → 다시 호출 → 값이 달라지는지 확인

### 7-2. 종합 UsageDashboard

1. 패널이 닫혀있는 상태에서 호출
   - 드롭다운 메뉴 열림 → Usage 항목 클릭 → 패널 오픈 → 스크레이핑 → ESC 닫힘
   - 전체 동작 1~2초 이내 완료
2. 패널이 이미 열려있는 상태에서 호출
   - 닫지 않고 그대로 스크레이핑만 수행
3. 창이 최소화된 상태에서 호출
   - `WithWindowRestoredAsync`가 복원 후 수행, 완료 후 다시 최소화
4. 각 필드 검증:
   - `planName` = "Max (5x)" / "Pro" / ...
   - `currentSession.percentUsed` = 정수
   - `weeklyAllModels.resetText` = "Resets in X hr Y min" 원문
   - `weeklySonnetOnly.resetText` = "Resets Fri 5:00 PM" (요일 형식)
   - `routineRuns.used` = 0, `total` = 15

### 7-3. 실패 경로

1. Claude Code 앱이 실행되지 않은 상태 → 응답 `success: false, error: CLAUDE_NOT_FOUND`
   (기존 `EnsureConnected` 로직이 자동 처리)
2. 드롭다운에 Usage 메뉴 항목이 없음 → `ELEMENT_NOT_FOUND`, 드롭다운을 ESC로 닫고 종료

---

## 8. 알려진 제약 / 주의사항

### 8-1. "Usage" 메뉴 항목 이름 불확실성

드롭다운 열린 상태의 UI 덤프를 아직 캡처하지 못해, 메뉴 항목의 정확한 Name을
확정하지 못했다. 현재 구현은 `Name.Contains("Usage")`로 **부분 매치**해서 "Usage",
"Usage limits", "Usage & billing" 등 가능한 바리에이션을 모두 커버한다. `Usage:` 접두사
(인라인 버튼)는 제외. 실제 테스트 후 정확한 문자열이 확인되면 엄격 매치로 교체 가능.

### 8-2. Radix 포털 렌더링

Radix UI 드롭다운 메뉴는 document body에 렌더링되므로 **`_claudeWindow` 전체 트리**에서
검색한다. 사이드바 subtree로 제한하면 메뉴 항목이 보이지 않는다.

### 8-3. 하드코딩 대기 시간

- 프로필 클릭 후 **250ms** (dropdown 렌더 대기)
- `OpenUsagePanel` 후 **500ms** (패널 전환 대기)
- ESC 후 **150ms** (닫힘 확인)

느린 머신에서 부족할 수 있음. Phase 12에서 "텍스트 anchor 나타날 때까지 최대 N회
polling" 패턴으로 교체 고려.

### 8-4. `ResetText` 보존 정책

Claude가 상황에 따라 상대 시간(`"Resets in 1 hr 26 min"`)과 절대 시간(`"Resets Fri 5:00 PM"`)을
섞어 쓰므로, ResetText는 **파싱하지 않고 원문 그대로 전달**한다. Android는 이 필드를
그대로 표시하거나 필요 시 자체적으로 파싱. PercentUsed만 구조화 데이터로 노출.

### 8-5. Plan name heuristic

`LooksLikePlanName`은 `"Max"` / `"Pro"` / `"Team"` / `"Free"`로 시작하는 ≤40자 라인만
plan name으로 간주한다. Anthropic이 새 플랜명을 도입하면 heuristic 갱신 필요.

### 8-6. 사용자 작업 방해 최소화

- 패널이 이미 열려있으면 닫지 않음 (`openedByUs = false` 경로)
- 열고 닫는 전체 사이클이 ~1초 이내
- `WithWindowRestoredAsync`가 원래 최소화였던 창은 다시 최소화

### 8-7. UsageInfo / UsageDashboard 호출 빈도 권장

| | UsageInfo | UsageDashboard |
|---|---|---|
| 호출 가능 | 하트비트마다 / 폴링 / send_input 직후 | 사용자 명시적 새로고침 |
| 병렬 호출 | OK | **주의**: 동시 2회 호출 시 패널 토글이 엉킬 수 있음 (Android가 in-flight 가드 필요) |

---

## 9. 파일 변경 요약

| 파일 | 변경 | 핵심 |
|---|---|---|
| `Models/UsageInfo.cs` | **신규** 30 라인 | 인라인 버튼 파싱 결과 |
| `Models/UsageDashboard.cs` | **신규** 54 라인 | 종합 패널 3섹션 |
| `Models/ClaudeMessage.cs` | +3 라인 | `GetUsage`, `GetUsageDashboard` 상수 |
| `Services/IClaudeAutomationService.cs` | +4 라인 | 2개 메서드 등록 |
| `Services/ClaudeAutomationService.cs` | +314 라인 | "Usage observability" 섹션 전체 (regex / GetUsageInfo / GetUsageDashboard / FindUsagePanelRoot / OpenUsagePanel / TryCloseUsagePanel / ParseUsagePanel / TryFillLimit / LooksLikePlanName / CollectUsagePanelStrings / WalkCollectPanelStrings / IsUsagePanelChromeButton) |
| `Services/MessageProcessor.cs` | +85 라인 | `get_usage` / `get_usage_dashboard` 핸들러, `CreateUsageInfoMessage`, `CreateUsageDashboardMessage`, `SerializeLimit` |
| `Services/IMessageProcessor.cs` | +2 라인 | 2개 메시지 빌더 등록 |
| `docs/ClaudeUI_UsagePanel.md` | **신규** | 패널 UI 매핑 문서 |
| `docs/reports/Phase11_Windows_WorkReport.md` | **신규** | 본 보고서 |

---

## 10. 체크리스트

### Phase 11 요구사항 충족

- [x] UI 분석 기반 매핑 문서 (`docs/ClaudeUI_UsagePanel.md`)
- [x] `UsageDashboard` / `UsageLimit` / `RoutineUsage` 모델
- [x] `UsageInfo` 모델 (인라인 버튼 전용)
- [x] `GetUsageInfoAsync` 구현
- [x] `GetUsageDashboardAsync` 구현 (패널 오픈 → 스크레이핑 → 닫기)
- [x] `WithWindowRestoredAsync` 사용
- [x] 정규식 파싱 (context / percent / routine / last updated)
- [x] `MessageAction.GetUsage`, `MessageAction.GetUsageDashboard` 상수
- [x] `MessageProcessor`에 두 핸들러
- [x] `CreateUsageInfoMessage`, `CreateUsageDashboardMessage` 빌더
- [x] 인터페이스 `IClaudeAutomationService`, `IMessageProcessor` 확장
- [x] C# 컴파일 성공 (0 오류)
- [ ] EXE 교체 (사용자 앱 실행 중이라 재빌드 대기)
- [x] Phase 11 보고서 작성

### Phase 9 원칙 준수

- [x] 블랙리스트 없음 — UI chrome 필터(Close/Back/...)는 positive filter
- [x] AutomationId 캐싱 없음 — 매 호출마다 `FindPrimaryPane()` / `FindSidebar()` 재조회
- [x] Container 기반 — 2-anchor("Plan usage limits" + "Weekly limits") 교집합으로 패널 루트 식별
- [x] positive matching — `group/label ` 처럼 클래스 접두사가 아니라 텍스트 anchor로 positive 매치

### Android 측 대응 필요 사항 (별도 이슈)

- [ ] 설정/상태 화면에 UsageInfo 실시간 표시 (하트비트 주기로 업데이트)
- [ ] "종합 사용량" 상세 화면 + pull-to-refresh로 `get_usage_dashboard` 호출
- [ ] UsageDashboard in-flight 가드 (동시 호출 방지)
- [ ] `resetText`를 원문 그대로 표시 + 시각적 progress bar는 `percentUsed`로

---

## 11. 다음 단계 (Phase 12 권고사항)

1. **실기 테스트**: 위 7절 시나리오를 실제 Claude Code에 연결해 수동 검증
   (특히 드롭다운 메뉴 항목의 정확한 Name 확인)
2. **UI 덤프 추가**: 드롭다운이 열린 상태와 패널이 열린 상태의 UI 트리를 각각 캡처하여
   `docs/screenshot/uitree-phase11-dropdown.txt`, `uitree-phase11-panel.txt`로 저장
3. **Monitor loop에서 UsageInfo 자동 push**: 인라인 버튼의 Name이 바뀌면 이벤트로
   Android에 broadcast → Android가 명시적 `get_usage`를 보낼 필요 없음
4. **Settings → Usage 탭 경로 fallback**: 드롭다운 경로 실패 시 Settings 아이콘을 통한
   대체 경로 구현 (Phase 12에서 탭 네비게이션 필요)
5. **하드코딩 대기 시간 제거**: "anchor 텍스트가 DOM에 나타날 때까지 최대 N회 poll"
   패턴으로 교체 — 250/500/150 ms 튜닝 불필요
6. **실패 시 스크린샷 저장**: `GetUsageDashboardAsync`가 null을 반환할 때 Claude 창 스크린샷을
   자동 저장해 진단 편의성 향상

---

# Phase 11 재설계 — 팝업 기반 (간소화)

> **상태**: 위 1~11절(프로필 → Settings → Usage 탭 경로)은 **폐기**됐다.
> 사용자가 "Claude Code 앱 오른쪽 하단의 작은 버튼을 누르면 그 자리에서 팝업이 뜨고
> 거기에 종합 사용량이 표시된다"는 사실을 발견하여, 훨씬 단순한 단일 클릭 경로로
> 다시 구현했다. 아래 12~17절이 **현재 살아 있는 설계와 코드**이다.

## 12. 새 데이터 흐름 (Bottom-Right Popup)

```
[Android] get_usage_dashboard
   │
   ▼
[ClaudeAutomationService.GetUsageDashboardAsync]
   │
   ├─ WithWindowRestoredAsync (Phase 8)
   │
   ├─ FindUsagePopupRoot()              ◀── 이미 열려 있나? (멱등)
   │     │
   │     ├ 열려 있음 → 그대로 스크레이프 (재클릭 X, 닫지 X)
   │     │
   │     └ 닫혀 있음 → 다음 단계
   │
   ├─ FindPrimaryPane() → FindButtonByNamePrefixRaw("Usage:")
   ├─ Invoke (트리거 클릭)
   ├─ Thread.Sleep(220)                  ◀── 팝업 애니메이션 settle
   ├─ FindUsagePopupRoot()               ◀── 이번엔 nullable
   │
   ├─ CollectUsagePopupStrings(popup)    ◀── DOM 순서 유지
   ├─ ParseUsagePopup(lines)             ◀── 새 상태 머신
   │
   ├─ TryCloseUsagePopup(trigger)
   │     ├ 1차: 트리거 재 Invoke (토글 닫기)
   │     └ 2차: SendKeys("{ESC}")  (1차로 안 닫히면)
   │
   └─ UsageDashboard 반환
```

핵심 차이:

| 옛 설계 (Settings 패널) | 새 설계 (Popup) |
|---|---|
| 사이드바 → 프로필 버튼(radix-) → 드롭다운 → "Usage" 메뉴 | 입력창 푸터의 "Usage:" 아이콘 1개 클릭 |
| 클릭 2회 (열기) + 패널 닫기 (ESC) | 클릭 1회 (열기) + 클릭 1회 (닫기) |
| 패널 식별: "Plan usage limits" + "Weekly limits" 2-앵커 | 팝업 식별: "Context window" + ("5-hour" / "Weekly" / "Sonnet") 2-앵커 |
| Tailwind 클래스 의존(`max-w-[75%]`) | 클래스 의존 0 (라벨 텍스트만 사용) |
| 500 ms 대기 | 220 ms 대기 |
| 사용자 화면 큰 패널이 잠깐 떴다 사라짐 | 작은 팝업이 ~200 ms 잠깐 보였다 사라짐 |

---

## 13. 새 모델 스키마

`Models/UsageDashboard.cs` 전면 교체.

```csharp
public class UsageDashboard
{
    public ContextWindow? ContextWindow { get; set; }
    public UsageLimit?    FiveHourLimit    { get; set; }
    public UsageLimit?    WeeklyAllModels  { get; set; }
    public UsageLimit?    WeeklySonnetOnly { get; set; }
    public string?        ModelName { get; set; }   // "Opus 4.6 1M"
    public string?        PlanName  { get; set; }   // "Max"
    public DateTime       FetchedAt { get; set; } = DateTime.UtcNow;
}

public class ContextWindow
{
    public string? UsedText    { get; set; }   // "514.6k"
    public string? TotalText   { get; set; }   // "1.0M"
    public int?    PercentUsed { get; set; }   // 51
}

public class UsageLimit
{
    public string? Label       { get; set; }   // "5-hour limit"
    public int?    PercentUsed { get; set; }   // 18
    public string? ResetText   { get; set; }   // "resets 4h" (verbatim)
}
```

폐기된 클래스/필드:
- `RoutineUsage` — 팝업에는 routine runs가 없음
- `UsageDashboard.CurrentSession` → `FiveHourLimit`로 명칭 변경 (의미 동일)
- `UsageDashboard.LastUpdated` — 팝업에 표시 안 됨
- `UsageDashboard.RoutineRuns` — 팝업에 표시 안 됨

---

## 14. 새 정규식 (4종, 컴파일 캐시)

```csharp
// 인라인 버튼 — UsageInfo 경로 (변경 없음)
InlineUsageRegex   = ^Usage:\s*context\s*([^,]+?),\s*plan\s*(\d+)\s*%\s*$

// 팝업: "514.6k / 1.0M (51%)"
ContextWindowRegex = ([\d.]+\s*[kKmM])\s*/\s*([\d.]+\s*[kKmM])\s*\(\s*(\d+)\s*%\s*\)

// 팝업: "18% · resets 4h"
LimitValueRegex    = (\d+)\s*%\s*[·•∙・]\s*(resets\s+\S+(?:\s+\S+)?)

// 팝업 푸터: "Opus 4.6 1M · Max"
ModelPlanRegex     = ^(.+?)\s+[·•∙・]\s+(.+?)$
```

미들도트 후보 4종(`· • ∙ ・`) 모두 받아들여 폰트/입력 변형에 견고.

폐기된 정규식:
- `PercentUsedRegex` (`(\d+)% used`) — Settings 패널의 "78% used" 형식이었음
- `RoutineUsageRegex` (`(\d+)/(\d+)`) — routine runs가 없음
- `LastUpdatedRegex` — "Last updated:" 라인이 없음

---

## 15. 새 파서 — `ParseUsagePopup`

상태 머신 대신 **pending-slot 패턴**을 사용한다. 한 라인이 라벨만 있고 값이 없으면
다음 라인이 그 슬롯을 채운다.

```
입력: List<string> lines (DOM 순회 순서)
변수:
  pendingContext : bool
  pendingLimit   : UsageLimit?

각 라인 처리:
  1. line = trim, ":" 끝의 콜론 제거
  2. "Context window" 시작 → ContextWindowRegex 시도
     성공 → 슬롯 채움 / 실패 → pendingContext=true
  3. pendingContext 이면 → ContextWindowRegex 재시도
  4. "Plan usage" / "Plan usage limits" → 헤더, pendingLimit reset
  5. "5-hour limit" 시작 → FiveHourLimit 슬롯 + LimitValueRegex
  6. "Weekly" + "all models" → WeeklyAllModels 슬롯 + LimitValueRegex
  7. "Sonnet only" 시작 → WeeklySonnetOnly 슬롯 + LimitValueRegex
  8. LooksLikeModelPlanLine(line) → ModelName + PlanName
  9. pendingLimit 이면 → LimitValueRegex로 값 채우기 시도
```

각 한도 라인은 `"<Label>: <NN>% · resets <T>"` 또는 `"<Label>"\n"<NN>% · resets <T>"`
어느 형태로 들어와도 매칭된다.

`LooksLikeModelPlanLine`:
- 길이 5~80자
- 미들도트 후보 1개 이상 포함
- "Opus" / "Sonnet" / "Haiku" / "Claude" 중 하나로 시작

---

## 16. 새 헬퍼 함수 매핑

| 옛 메서드 (폐기) | 새 메서드 (살아 있음) |
|---|---|
| `FindUsagePanelRoot` | `FindUsagePopupRoot` |
| `OpenUsagePanel` | (삭제됨 — 트리거 클릭으로 충분) |
| `TryCloseUsagePanel` (ESC만) | `TryCloseUsagePopup(trigger)` (재 Invoke + ESC 폴백) |
| `ParseUsagePanel` | `ParseUsagePopup` |
| `TryFillLimit` | `TryFillLimitValue` (인자 시그니처 동일) |
| `LooksLikePlanName` | `LooksLikeModelPlanLine` |
| `CollectUsagePanelStrings` | `CollectUsagePopupStrings` |
| `WalkCollectPanelStrings` | `WalkCollectPopupStrings` |
| `IsUsagePanelChromeButton` | `IsUsagePopupChromeButton` (+ "Usage:" 자기 자신도 필터) |
| — | `TryFillContext` (신규) |

---

## 17. 새 응답 JSON 샘플

요청:
```json
{ "type": "command", "action": "get_usage_dashboard" }
```

응답 (성공):
```json
{
  "type": "response",
  "action": "get_usage_dashboard",
  "mode": "code",
  "payload": {
    "success": true,
    "dashboard": {
      "contextWindow": {
        "usedText": "514.6k",
        "totalText": "1.0M",
        "percentUsed": 51
      },
      "fiveHourLimit": {
        "label": "5-hour limit",
        "percentUsed": 18,
        "resetText": "resets 4h"
      },
      "weeklyAllModels": {
        "label": "Weekly · all models",
        "percentUsed": 56,
        "resetText": "resets 19h"
      },
      "weeklySonnetOnly": {
        "label": "Sonnet only",
        "percentUsed": 0,
        "resetText": "resets 2d"
      },
      "modelName": "Opus 4.6 1M",
      "planName": "Max",
      "fetchedAt": "2026-04-15T14:39:10Z"
    }
  }
}
```

응답 (실패):
```json
{
  "type": "response",
  "action": "get_usage_dashboard",
  "payload": {
    "success": false,
    "error": "ELEMENT_NOT_FOUND"
  }
}
```

---

## 18. 빌드 결과 (재설계 후)

| 시각 | 동작 | 결과 |
|---|---|---|
| 14:39:10 | `dotnet build -c Release` | **Build succeeded — 0 Error(s)** |
| | `obj/Release/.../ClaudeRemote.Windows.dll` | 131,072 B (Phase 11 popup) |
| | `bin/Release/.../ClaudeRemote.Windows.dll` | 131,072 B ✅ (이전 116,736 B에서 갱신) |
| | `bin/Release/.../ClaudeRemote.Windows.exe` | 152,064 B ✅ (재기록 완료) |

`ClaudeRemote.Windows.exe` PID 13372가 빌드 직전에 종료되어 EXE 복사 단계가
정상 완료됐다. C# 컴파일 0 오류, 모든 산출물 갱신.

---

## 19. 변경 파일 요약 (재설계 기준)

### 코드 (5 파일)

| 파일 | 변경 | 비고 |
|---|---|---|
| `Models/UsageDashboard.cs` | **재작성** | 새 스키마: ContextWindow / FiveHourLimit / ModelName / PlanName |
| `Services/ClaudeAutomationService.cs` | **재작성 (Usage observability 섹션)** | 1062~1582 → 1062~1546 (-36 줄). 옛 Settings 헬퍼 9개 삭제, 팝업 헬퍼 8개 신규. `GetUsageInfoAsync`는 그대로 보존. |
| `Services/MessageProcessor.cs` | 수정 | `CreateUsageDashboardMessage` 새 스키마, `SerializeContextWindow` 신규, 로그 라인 갱신 |
| `Models/UsageInfo.cs` | 변경 없음 | 인라인 경로는 동일 |
| `Models/ClaudeMessage.cs` | 변경 없음 | 두 액션 상수 그대로 |

### 문서 (3 파일)

| 파일 | 변경 |
|---|---|
| `docs/ClaudeUI_UsagePopup.md` | **신규** — 팝업 매핑 문서 (트리거 식별, 2-앵커 컨테이너, 정규식, JSON, 검증 체크리스트) |
| `docs/ClaudeUI_UsagePanel.md` | 폐기 (역사적 참조용으로 남김) |
| `docs/reports/Phase11_Windows_WorkReport.md` | **이 문서** — 12~19절(재설계) 추가 |

---

## 20. 검증 체크리스트 (Android 실기 테스트)

- [ ] `get_usage` → 인라인 데이터 즉시 반환 (UI 변화 0)
- [ ] `get_usage_dashboard` → 작은 팝업이 ~200 ms 떴다 사라지면서 dashboard 반환
- [ ] 응답 JSON `contextWindow.{usedText, totalText, percentUsed}` 모두 채워짐
- [ ] `fiveHourLimit.{percentUsed, resetText}` 채워짐
- [ ] `weeklyAllModels` + `weeklySonnetOnly` 모두 채워짐
- [ ] `modelName="Opus 4.6 1M"`, `planName="Max"`
- [ ] 사용자가 미리 팝업을 열어 둔 상태에서 호출 → 데이터 반환 + 팝업 그대로 유지
- [ ] 모델 변경(Opus → Sonnet) 후 다시 호출 → `modelName` 갱신 확인
- [ ] 입력창 포커스 보존 (트리거 재 Invoke 방식이 ESC보다 먼저 시도되므로 가능해야 함)

검증 시 주의: 팝업 DOM이 라벨/값을 한 줄로 합칠지 두 줄로 분리할지 React 빌드에 따라
다를 수 있다. 두 형태 모두 파서가 처리하지만, 만약 둘 다 못 잡으면 `Log.Debug`의
`"Collected N lines from popup"` 출력에서 실제 라인 텍스트를 보고 정규식을 미세 조정.

---

## 21. 다음 단계 (재설계 후)

1. **Android 측 UI**: 종합 사용량 화면을 `ContextWindow + FiveHourLimit + WeeklyAllModels +
   WeeklySonnetOnly + ModelName/PlanName` 6개 카드로 그림
2. **palette 매핑**: `percentUsed`로 progress bar 색상 (≥80% red, ≥50% amber, else green)
3. **자동 새로고침 정책**: pull-to-refresh 외에 5분에 한 번 자동 호출 가능 (팝업이 잠깐
   보였다 사라지는 영향이 작으므로) — Android 설정 토글로
4. **`get_usage_dashboard` in-flight 가드**: 동시 호출이 들어오면 두 번째 요청은 첫 응답을 공유
5. **팝업 라인 캡처 모드**: 진단 빌드에서 `CollectUsagePopupStrings` 결과를 로그/파일로
   덤프하는 옵션 (정규식 튜닝용)
6. **모델 셀렉터 통합**: 팝업 푸터의 `ModelName`이 모델 셀렉터 값과 같으므로 Phase 12의
   "모델 변경" 기능과 연동

