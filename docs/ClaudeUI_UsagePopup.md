# Claude Code v2 — Usage Popup Mapping (Bottom-Right Trigger)

**대상 버전**: Claude Desktop 1.2581.0+
**관련 Phase**: Phase 11 (간소화된 팝업 기반 구현)
**관련 파일**: `ClaudeRemote.Windows/Services/ClaudeAutomationService.cs` (Usage observability 섹션)
**선행 문서**: `docs/ClaudeUI_UsagePanel.md` (폐기 — 이전의 프로필 → Settings 경로)

---

## 1. 사용량 데이터 두 갈래

Claude Code 데스크톱 앱은 동일한 버튼을 통해 **두 개의 사용량 데이터**를 노출한다.

| 항목 | UsageInfo (라이트) | UsageDashboard (헤비) |
|---|---|---|
| **소스** | 인라인 버튼의 `Name` 프로퍼티 | 같은 버튼을 클릭한 뒤 뜨는 팝업 |
| **클릭 필요?** | ❌ 클릭 없이 텍스트만 읽음 | ✅ Invoke → 팝업 → 스크레이프 → 닫기 |
| **사용자 영향** | 0 (UI 변화 없음) | 약 200ms 동안 팝업이 잠깐 보임 |
| **호출 빈도** | 폴링/하트비트 가능 | 사용자 트리거(pull-to-refresh) 권장 |
| **API** | `GetUsageInfoAsync()` | `GetUsageDashboardAsync()` |
| **프로토콜** | `get_usage` | `get_usage_dashboard` |

> **핵심**: 두 데이터 소스가 사실 **같은 하나의 UIA 버튼**이다. 인라인 텍스트는 그 버튼의 `Name`이고, 팝업은 그 버튼을 `Invoke`하면 토글되어 나타나는 것이다.

---

## 2. 트리거 버튼 식별

Phase 9 UI 트리 덤프(`docs/screenshot/uitree-phase9-main.txt`) 598번 행에서 발견:

```
Line 598:
  [Button]
    Name = "Usage: context 106.1k, plan 49%"
    AID  = ""    (없음)
    Class = "shrink-0 flex items-center justify-center h-small aspect-square rounded-r3 hover..."
    Rect = [1213, 1017, 21x20]
```

위치 특징:
- **Primary pane 안**, 하단 입력창 행의 가장 오른쪽 (X=1213, 화면 폭 1280 기준)
- 모델 셀렉터 (`Opus 4.6 · Max`, AID `_r_v62_`, Rect [1115, 1017, 94x20]) **바로 옆**
- 21x20px의 작은 정사각형 아이콘 버튼

### 식별 전략

`AID`가 비어 있고 `Name`은 동적 텍스트(컨텍스트 / 퍼센트가 시시각각 바뀜)이므로 **`Name` 프리픽스 매칭**이 안정적이다:

```csharp
var btn = FindButtonByNamePrefixRaw(primaryPane, "Usage:");
```

- "`Usage:`"로 시작하는 첫 번째 Button을 RawViewWalker로 찾음
- Primary pane (왼쪽 사이드바 제외한 메인 채팅 영역) 안만 탐색하면 충돌 없음
- `FindButtonByNamePrefixRaw`는 Phase 9에서 이미 도입된 헬퍼

---

## 3. 팝업 동작 시퀀스

### 3-1. 정상 흐름 (`GetUsageDashboardAsync`)

```
1. WithWindowRestoredAsync 진입
   └ 창이 최소화돼 있으면 잠시 복원, 메서드 끝나면 원상 복구

2. 이미 열려 있는지 확인 (멱등)
   └ FindUsagePopupRoot() 가 non-null → 이미 열림 (사용자가 직접 클릭한 상태)
   └ 열려 있으면 step 4로 점프 (재클릭 X)

3. 닫혀 있으면 트리거 버튼 클릭
   ├ FindPrimaryPane()
   ├ FindButtonByNamePrefixRaw(pane, "Usage:")
   ├ Invoke (또는 폴백으로 ClickElement)
   ├ Thread.Sleep(220)        // 팝업 애니메이션 settle
   └ FindUsagePopupRoot()      // 팝업 루트 재탐색

4. 팝업 텍스트 수집
   └ CollectUsagePopupStrings(popup)
   └ Text 컨트롤 + 짧은 Button Name (chrome 제외) → DOM 순서 리스트

5. 정규식 파싱
   └ ParseUsagePopup(lines)

6. 우리가 열었으면 닫기
   ├ TryCloseUsagePopup(trigger)
   │   ├ 1차: 트리거 재 Invoke (토글 닫기)
   │   ├ Thread.Sleep(120)
   │   └ 2차 (1차 후 팝업이 살아 있으면): SendKeys("{ESC}")
   └ 사용자가 미리 열어 둔 거였으면 그대로 둠 (UX 보존)

7. UsageDashboard 반환
```

### 3-2. 팝업 루트 찾기 (`FindUsagePopupRoot`)

팝업은 Radix UI 포털로 렌더되므로 트리거 버튼의 자손이 아닐 수 있다. **2-앵커 컨테이너 감지**를 쓴다:

```
앵커 1: ControlType=Text, Name이 "Context window"로 시작
앵커 2: 같은 부모/조상 안에서 다음 중 하나가 발견되어야 함
        - "5-hour limit..." (Text)
        - "Sonnet only..."  (Text)
        - "Plan usage"      (Text)
        - "Weekly..."       (Text)

알고리즘:
  1. _claudeWindow 전체에서 앵커1을 찾음 (없으면 → null = 팝업 닫혀 있음)
  2. ControlViewWalker로 위로 올라가며 (최대 10단계)
     각 단계의 부모 서브트리에서 앵커2가 발견되는지 확인
  3. 발견되면 그 부모가 팝업 루트
  4. 못 찾으면 폴백: 앵커1의 4-up 조상 반환
```

이 방식의 장점:
- Tailwind 클래스명에 의존하지 않음 (Tailwind 클래스는 빌드마다 깨짐)
- React 컴포넌트 구조 변경에 견고함
- "Context window"라는 라벨 텍스트만 안정적이면 동작

---

## 4. 팝업 콘텐츠 (스크린샷 기준)

```
┌──────────────────────────────────────┐
│ Context window: 514.6k / 1.0M (51%)  │  ← ContextWindow
├──────────────────────────────────────┤
│ Plan usage                           │  ← 섹션 헤더
│ 5-hour limit: 18% · resets 4h        │  ← FiveHourLimit
│ Weekly · all models: 56% · resets 19h│  ← WeeklyAllModels
│ Sonnet only: 0% · resets 2d          │  ← WeeklySonnetOnly
├──────────────────────────────────────┤
│ Opus 4.6 1M · Max                    │  ← ModelName · PlanName
└──────────────────────────────────────┘
```

DOM이 각 행을 단일 Text 노드로 합쳐서 줄 수도 있고, "라벨 노드 + 값 노드"로 쪼개서 줄 수도 있다 (React/Radix 동작에 따라 다름). 파서는 두 형태를 모두 처리한다.

---

## 5. 정규식

`ClaudeAutomationService.cs` 상단의 컴파일된 Regex 4종:

```csharp
// 인라인 버튼 (Phase 11 — UsageInfo 경로)
InlineUsageRegex   = ^Usage:\s*context\s*([^,]+?),\s*plan\s*(\d+)\s*%\s*$

// 팝업 컨텍스트 윈도우
ContextWindowRegex = ([\d.]+\s*[kKmM])\s*/\s*([\d.]+\s*[kKmM])\s*\(\s*(\d+)\s*%\s*\)

// 팝업 한도 라인
LimitValueRegex    = (\d+)\s*%\s*[·•∙・]\s*(resets\s+\S+(?:\s+\S+)?)

// 팝업 푸터 (모델 · 플랜)
ModelPlanRegex     = ^(.+?)\s+[·•∙・]\s+(.+?)$
```

미들도트 후보 4종 `[·•∙・]`을 모두 받아들여 폰트/입력 변형에 견고하다.

`LimitValueRegex` 매칭 예시:
- "18% · resets 4h" → group1="18", group2="resets 4h"
- "56% · resets 19h" → group1="56", group2="resets 19h"
- "0% · resets 2d" → group1="0", group2="resets 2d"

---

## 6. 파서 상태 머신 (`ParseUsagePopup`)

```
입력: List<string> lines (DOM 순회 순서)
출력: UsageDashboard

상태 변수:
  pendingContext : bool    — "Context window" 라벨만 보고 값 대기 중
  pendingLimit   : UsageLimit?  — 한도 라벨만 보고 값 대기 중

각 라인에 대해:
  ├ "Context window" 시작 → ContextWindowRegex 시도
  │     성공 → 슬롯 채움 / 실패 → pendingContext=true
  ├ "Plan usage" 또는 "Plan usage limits" → 헤더, pendingLimit 초기화
  ├ "5-hour limit" 시작
  │     → FiveHourLimit 슬롯 생성, LimitValueRegex 시도
  │     성공 → 그대로 / 실패 → pendingLimit = 슬롯
  ├ "Weekly" 시작 + "all models" 포함
  │     → WeeklyAllModels 슬롯
  ├ "Sonnet only" 시작
  │     → WeeklySonnetOnly 슬롯
  ├ LooksLikeModelPlanLine(line) (Opus/Sonnet/Haiku/Claude로 시작 + 미들도트 포함)
  │     → ModelPlanRegex → ModelName / PlanName 채움
  └ pendingContext or pendingLimit이 살아 있으면 → 그 슬롯에 채워 시도
```

각 라벨은 콜론을 trim해서 `"5-hour limit:"` ↔ `"5-hour limit"` 어느 형태로 들어와도 매칭된다.

---

## 7. JSON 응답 샘플

### 7-1. `get_usage` (인라인 — UsageInfo)

요청:
```json
{ "type": "command", "action": "get_usage" }
```

응답:
```json
{
  "type": "response",
  "action": "get_usage",
  "mode": "code",
  "payload": {
    "success": true,
    "usage": {
      "rawText": "Usage: context 514.6k, plan 51%",
      "contextText": "514.6k",
      "planPercent": 51,
      "fetchedAt": "2026-04-15T10:00:00Z"
    }
  }
}
```

### 7-2. `get_usage_dashboard` (팝업 — UsageDashboard)

요청:
```json
{ "type": "command", "action": "get_usage_dashboard" }
```

응답:
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
      "fetchedAt": "2026-04-15T10:00:00Z"
    }
  }
}
```

실패 응답:
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

## 8. 사용자 경험 고려사항

| 시나리오 | 처리 |
|---|---|
| 사용자가 이미 팝업을 열어 둔 상태에서 호출 | `FindUsagePopupRoot`가 non-null 반환 → 재클릭 없이 그대로 스크레이프, 닫지 않음 |
| 우리가 클릭해서 열었음 | 스크레이프 후 트리거 재클릭(토글 닫기) → 팝업 사라짐, 사용자에게 ~200ms 잠깐 보였다 사라짐 |
| 트리거 재클릭 후에도 팝업이 살아 있음 | ESC 폴백 |
| 트리거 버튼을 못 찾음 (창이 최소화/숨김 등) | `WithWindowRestoredAsync`로 임시 복원 → 그래도 못 찾으면 `null` 반환, 응답 `success=false` |
| 팝업이 나타나지 않음 (애니메이션 지연/외부 포커스) | 220ms 후에도 anchor가 없으면 `null` 반환 + 안전을 위해 `TryCloseUsagePopup` 시도 |

---

## 9. 검증 체크리스트

빌드 후 Android에서 직접 호출해 확인:

- [ ] `get_usage` → 인라인 데이터 즉시 반환 (UI 변화 0)
- [ ] `get_usage_dashboard` → 팝업이 잠깐 떴다 사라지면서 dashboard 반환
- [ ] 팝업 미리 열어 둔 상태에서 `get_usage_dashboard` → 팝업이 닫히지 않고 데이터만 반환
- [ ] 응답 JSON의 `contextWindow.usedText` / `totalText` / `percentUsed` 채워짐
- [ ] `fiveHourLimit.percentUsed` + `resetText` 채워짐
- [ ] `weeklyAllModels` + `weeklySonnetOnly` 모두 채워짐
- [ ] `modelName="Opus 4.6 1M"`, `planName="Max"` (또는 현재 선택된 모델/플랜)
- [ ] 모델 변경(Opus → Sonnet) 후 다시 호출 → `modelName` 갱신됨
- [ ] 팝업 호출 후 입력창 포커스가 보존됨 (트리거 재클릭 방식이 ESC보다 우선이라 가능해야 함)
