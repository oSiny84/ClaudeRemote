# Claude Code v2 — Usage Panel Mapping

**대상 버전**: Claude Desktop 1.2581.0+
**관련 Phase**: Phase 11
**관련 파일**: `ClaudeRemote.Windows/Services/ClaudeAutomationService.cs` (Usage observability 섹션)

---

## 1. 두 가지 사용량 데이터 소스

Claude Code 데스크탑 앱에는 **서로 다른 두 개의 사용량 표시**가 있다. 둘 다
별도로 스크레이핑해야 한다.

### 1-1. 인라인 사용량 버튼 (항상 표시)

입력창 푸터의 오른쪽 끝에 있는 작은 버튼.

```
UI 위치: Primary pane → 하단 입력 영역 → Button
UI 식별: Name="Usage: context {N.Nk}, plan {NN}%"
예시:    Name="Usage: context 106.1k, plan 49%"
```

- Claude Code 창이 열려 있으면 **항상 DOM에 존재** → 싸고 빠르게 스크레이핑 가능
- 이 값만 쓴다: `ContextText`, `PlanPercent`
- `UsageInfo` 모델에 매핑
- 폴링/하트비트/send_input 직후 등 빈번하게 호출해도 OK
- **API**: `IClaudeAutomationService.GetUsageInfoAsync()`
- **프로토콜 액션**: `get_usage`

### 1-2. 종합 사용량 대시보드 (패널 오픈 필요)

사용자 프로필 드롭다운 메뉴를 열어야 나오는 전용 패널.

```
UI 위치: 사이드바 좌측 하단 "osiny" Button → 클릭 → 드롭다운 메뉴 →
         "Usage" 또는 "Usage & limits" 항목 → 패널 오픈
UI 식별: Text="Plan usage limits" 포함
         Text="Weekly limits" 포함
         Text="Additional features" 포함
```

- 패널을 **열고/닫는 동작이 필요함** → 사용자 작업을 일시적으로 방해
- `UsageDashboard` 모델에 매핑 (plan / current session / weekly / routine runs)
- 호출 빈도는 사용자가 "새로고침" 누를 때 정도
- **API**: `IClaudeAutomationService.GetUsageDashboardAsync()`
- **프로토콜 액션**: `get_usage_dashboard`

---

## 2. 인라인 버튼 매핑 (UsageInfo)

### 2-1. 정확한 UI 경로

`uitree-phase9-main.txt` 라인 598에서 확인:

```
[Group] Class="dframe-main" (AID="dframe-main")
  [Group] Class="dframe-pane-host ..."
    [Group] Name="Primary pane" Class="dframe-pane dframe-pane-primary ..."
      [Group] Class="..."
        ...
        [Group] Class="h-full overflow-y-auto ..."
          ...
          [Button] Name="Accept edits"
          [Button] Name="Transcript view mode"
          [Button] Name="Add"
          [Group] Name="Dictation"
          [Button] Name="Opus 4.6 · Max"
          [Button] Name="Usage: context 106.1k, plan 49%"   ← ★ HERE
```

### 2-2. 식별 규칙

- **Control**: `ControlType.Button`
- **Name 접두사**: `"Usage:"` (콜론 포함)
- **찾기 루트**: `FindPrimaryPane()` 하위 (전체 창이 아님 — 중복 방지)

### 2-3. 파싱 정규식

```csharp
^Usage:\s*context\s*([^,]+?),\s*plan\s*(\d+)\s*%\s*$
```

- Group 1: context 값 (예: `"106.1k"` 또는 `"1.2M"` — 단위 포함 문자열 그대로 보존)
- Group 2: plan 퍼센트 (예: `49`)

### 2-4. 샘플 JSON 응답

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

---

## 3. 종합 대시보드 패널 매핑 (UsageDashboard)

### 3-1. 오픈 경로

두 가지 경로가 이론적으로 존재한다. 현재 구현은 **경로 1** (사용자 프로필 드롭다운)을 사용.

**경로 1: 사용자 프로필 드롭다운 (구현됨)**

```
[Group] Name="" Class="min-w-0 max-w-[75%]"          ← 프로필 칩 컨테이너
  [Button] Name="{username}" AID="radix-_r_*"         ← 클릭!
    → Radix dropdown 메뉴가 포털로 열림
      → [MenuItem] Name="Usage" (or "Usage limits", "Usage & billing")  ← 클릭!
        → 패널 오픈
```

**경로 2: Settings/Customize → Usage 탭 (미구현)**

```
[Button] Name="Customize"
  → 설정 화면
    → 탭/섹션 중 "Usage" 선택
```

경로 2는 Settings 화면 자체의 UI 구조를 알아야 하고, 탭 네비게이션까지 자동화해야 해서
현재 Phase 11에서는 구현하지 않았다. 경로 1이 실패할 때의 fallback으로 Phase 12에서
고려.

### 3-2. 프로필 버튼 식별 (Phase 11 구현 기준)

Radix UI 드롭다운 트리거는 AID가 `radix-` 접두사로 시작한다. 하지만 사이드바 안에
여러 radix 버튼이 있을 수 있으므로 **부모 Group의 클래스**로 추가 식별:

```
parent.Class contains "max-w-[75%]"
child.ControlType == Button
child.AutomationId starts with "radix-"
```

이 조합은 현재 UI 트리에서 유일하다. 코드:

```csharp
var profileBtn = WalkFindFirst(sidebar, el =>
{
    if (el.Current.ControlType != ControlType.Button) return false;
    var aid = el.Current.AutomationId ?? "";
    if (!aid.StartsWith("radix-")) return false;
    var par = TreeWalker.ControlViewWalker.GetParent(el);
    if (par == null) return false;
    var parCls = SafeGetClassName(par);
    return parCls.Contains("max-w-[75%]");
});
```

### 3-3. 메뉴 항목 식별

`osiny` 버튼 Invoke 후 **250ms 대기** → 창 전체에서 Name에 "Usage"가 포함된
MenuItem/Button을 찾는다. 단 Name이 `Usage:`로 시작하면 **인라인 버튼**이므로 제외
(이건 패널을 여는 메뉴가 아님).

```csharp
var usageItem = WalkFindFirst(_claudeWindow, el =>
{
    var ct = el.Current.ControlType;
    if (ct != ControlType.MenuItem && ct != ControlType.Button) return false;
    var name = SafeGetName(el);
    if (string.IsNullOrWhiteSpace(name)) return false;
    if (name.StartsWith("Usage:")) return false;  // 인라인 버튼 제외
    return name.Contains("Usage", StringComparison.OrdinalIgnoreCase);
});
```

### 3-4. 패널 루트 식별

패널이 열린 후, "Plan usage limits" 텍스트를 anchor로 사용해 **세 앵커를 모두 포함하는
가장 작은 조상**을 패널 루트로 간주:

1. `Text` 컨트롤 중 Name == `"Plan usage limits"` 찾기 → anchor
2. `ControlViewWalker.GetParent()`로 한 단계씩 올라가며, 그 ancestor 밑에
   `"Weekly limits"` Text도 존재하는지 확인
3. 첫 번째로 만족하는 ancestor를 패널 루트로 사용
4. 10단계까지 못 찾으면 anchor의 5-up ancestor를 fallback으로 사용

### 3-5. 패널 내부 텍스트 순회

`CollectUsagePanelStrings(panel)`가 **DOM 순서대로** Text와 짧은 Button 이름을 수집한다:

- `ControlType.Text` → Name 수집
- `ControlType.Button`이면서 Name 길이 ≤ 80 → 수집 (툴팁/긴 aria-label 탈락)
- `Close`, `Back`, `Share`, `Copy`, `Dismiss`는 Panel chrome으로 간주해 제외

### 3-6. 섹션 상태 머신

수집된 라인 배열을 순차 순회하며 **section state**를 유지한다:

| 라인 | 동작 |
|---|---|
| `"Plan usage limits"` | `section = PLAN` |
| `"Weekly limits"` | `section = WEEKLY` |
| `"Additional features"` | `section = ADDITIONAL` |
| `"Last updated: …"` | `dashboard.LastUpdated = …` (section과 무관) |
| PLAN 상태 + `"Max (…)"`/`"Pro"`/`"Team"`/`"Free"` | `PlanName` 설정 |
| PLAN 상태 + `"Current session"` | session slot 시작 |
| WEEKLY 상태 + `"All models"` / `"Sonnet only"` | 해당 bucket slot 시작 |
| ADDITIONAL 상태 + `"Daily included routine runs"` | routine slot 시작 |
| 현재 slot이 열려있고 line이 `"Resets"`로 시작 | `ResetText` 채움 |
| 현재 slot이 열려있고 line이 `"NN% used"` 패턴 | `PercentUsed` 채움 |
| routine slot이 열려있고 line이 `"N / M"` 패턴 | `Used`/`Total` 채움 |

**예외 처리**: `"Resets in 1 hr 26 min, 78% used"`처럼 reset과 percent가 한 라인에
같이 있으면 한 번에 채운다 (`TryFillLimit`가 양쪽 regex를 다 돌린다).

### 3-7. 정규식 목록

```csharp
// Inline button
InlineUsageRegex   = ^Usage:\s*context\s*([^,]+?),\s*plan\s*(\d+)\s*%\s*$

// Dashboard panel
PercentUsedRegex   = (\d+)\s*%\s*used
RoutineUsageRegex  = (\d+)\s*/\s*(\d+)
LastUpdatedRegex   = ^Last updated:\s*(.+)$
```

reset 텍스트는 원문 보존 (파싱하지 않음 — Android에 그대로 표시). 예시:

- `Resets in 1 hr 26 min`
- `Resets in 21 hr 26 min`
- `Resets Fri 5:00 PM`

### 3-8. 패널 닫기

`System.Windows.Forms.SendKeys.SendWait("{ESC}")` + 150ms 대기. 우리가 열었으면
닫고, 사용자가 이미 열어둔 경우(`openedByUs = false`)는 건드리지 않는다.

### 3-9. 샘플 JSON 응답

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

---

## 4. 검증 체크리스트 (실기 테스트 대상)

### 4-1. 인라인 UsageInfo (`get_usage`)

- [ ] Claude 앱이 실행 중이고 세션이 열려 있으면 응답 성공
- [ ] `rawText`가 `"Usage: context NN.Nk, plan NN%"` 형태 그대로 전달
- [ ] `contextText`가 단위 포함 문자열 (예: `"106.1k"`, `"1.2M"`)
- [ ] `planPercent`가 0~100 정수
- [ ] 세션이 없을 때(primary pane 빈 상태)는 실패 응답

### 4-2. 종합 UsageDashboard (`get_usage_dashboard`)

- [ ] 패널이 닫혀있을 때: 자동으로 열렸다가 데이터 스크레이핑 후 ESC로 닫힘
- [ ] 패널이 이미 열려 있을 때: 닫지 않고 그대로 데이터만 반환
- [ ] plan name 파싱: `Max (5x)` / `Pro` / `Team` / `Free` 감지
- [ ] Current session: reset text + percent 모두 채워짐
- [ ] Weekly All models: reset text + percent 모두 채워짐
- [ ] Weekly Sonnet only: reset text + percent 모두 채워짐 (0%도 정확히 전달)
- [ ] `"Resets Fri 5:00 PM"` 같은 요일 표기도 reset text에 그대로 들어감
- [ ] Last updated 텍스트 캡처됨
- [ ] Routine runs: `0 / 15` → `used=0 total=15`
- [ ] Routine runs가 없는 경우 (`"You haven't run any routines yet"`) → `routineRuns`가
      label만 있거나 null로 처리됨

### 4-3. Edge cases

- [ ] 사용자 프로필 드롭다운 메뉴에 "Usage" 항목이 없는 빌드 → 실패 응답 (error=ELEMENT_NOT_FOUND)
- [ ] 패널 열리기 전에 창이 최소화되어 있었음 → WithWindowRestoredAsync가 복원
- [ ] 스크레이핑 중 사용자가 다른 창을 클릭해 포커스 뺏김 → 데이터는 이미 긁혀서 응답됨

---

## 5. 알려진 제약

1. **드롭다운 메뉴 항목 이름 불확실성**
   현재 Claude 앱의 정확한 메뉴 항목 이름이 캡처되지 않아 `"Usage"`, `"Usage limits"`,
   `"Usage & billing"` 등을 포괄하는 부분 매치를 사용. Anthropic이 이 텍스트를 변경하면
   fallback으로 떨어질 수 있음.

2. **Radix 포털**
   Radix 드롭다운은 일반적으로 document body에 포털로 렌더링되므로 사이드바 subtree가
   아니라 **창 전체**를 스캔해야 메뉴 항목이 잡힌다 (구현됨).

3. **300ms / 500ms / 250ms 하드코딩 대기**
   React 트랜지션이 느린 머신에서는 부족할 수 있음. Phase 12에서 "최대 N번 retry"
   패턴으로 교체 고려.

4. **Plan name 휴리스틱**
   Claude가 새 플랜 이름(e.g. `"Enterprise"`, `"Custom"`)을 도입하면 `LooksLikePlanName`
   heuristic을 업데이트해야 함.

5. **CollectUsagePanelStrings의 중복 제거**
   바로 이전 라인과 동일하면 스킵하는 간단한 dedup만 적용. 비-인접 중복은 허용 —
   state machine이 정상 처리함.

---

## 6. 참고

- 인라인 버튼 위치: `docs/screenshot/uitree-phase9-main.txt` 라인 598
- 프로필 버튼 위치: `docs/screenshot/uitree-phase9-main.txt` 라인 142~151
- Phase 11 구현: `ClaudeAutomationService.cs` 섹션 "Usage observability"
- 모델: `Models/UsageInfo.cs`, `Models/UsageDashboard.cs`
- 메시지 프로토콜: `MessageAction.GetUsage`, `MessageAction.GetUsageDashboard`
