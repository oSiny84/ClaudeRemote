# Phase 6 Hotfix 4 - 버튼 감지 근본 전환: 블랙리스트 → 구조 기반 화이트리스트

**작업일**: 2026-04-13
**상태**: 완료

---

## 1. 문제

블랙리스트 방식의 한계. 아래 UI 버튼들이 action_buttons로 오탐됨:

| 버튼 Name | 실체 |
|---|---|
| `main` | git 브랜치 이름 |
| `+407 -150` | git diff 통계 |
| `Create PR` | PR 생성 버튼 |
| `More options` | 옵션 메뉴 |
| `Used a tool` | 활동 요약 |

블랙리스트를 계속 추가하는 방식은 새로운 UI 요소가 추가될 때마다 오탐이 재발하는 구조적 한계가 있음.

## 2. 해결 방향

**"모든 Button 수집 → 블랙리스트 제외"** 를 버리고,
**"turn-form 근처 특정 컨테이너의 Button 구조 분석"** 으로 근본 전환.

### 핵심 관찰
AskUserQuestion 선택 버튼은 특정 구조를 가짐:
- 같은 부모 컨테이너 안에 3개 이상 연속 Button으로 존재
- 버튼 Name이 4글자 이상
- 숫자/기호 패턴이 아님
- UI 키워드(`PR`, `Create`, `More`, `Copy` 등) 미포함

## 3. 구현

### 삭제된 코드 (블랙리스트 방식)
- `IsConversationChromeButton()` — 30줄+ 블랙리스트 필터
- `DetectAskUserQuestionButtons()` — Width 기반 폴백
- `CollectAllVisibleRawElements()` — 전체 RawView 덤프
- `CollectButtonElementsRaw()` / `CollectButtonElementsRawWalk()` — RawView 버튼 수집
- 위치 기반 Y좌표 필터링 (`ButtonZoneHeight = 200px`)

### 추가된 코드 (구조 기반)

**새 상수:**
```csharp
private const int MinChoiceGroupSize = 3;      // 부모 그룹 내 최소 버튼 수
private const int MinChoiceNameLength = 4;     // 최소 버튼 이름 길이
private const int MaxSiblingsToSearch = 10;    // turn-form 이전 형제 탐색 수

private static readonly Regex NumericPatternRegex = new(@"^[+\-\d\s]+$");
private static readonly string[] UiKeywords = { "PR", "options", "Create", "More", "Collapse", "Expand", "Copy" };
```

**새 메서드:**

| 메서드 | 역할 |
|---|---|
| `DetectChoiceButtonsNearTurnForm()` | turn-form의 이전 형제들을 순회하며 선택 버튼 그룹 탐색 |
| `DetectChoiceButtons()` | 주어진 서브트리에서 구조 기반 선택 버튼 그룹 감지 |
| `FindButtonGroupsWalk()` | RawView 트리를 순회하며 같은 부모의 3+ Button 자식 그룹 수집 |
| `IsValidChoiceButton()` | 화이트리스트 기준 (길이, 숫자패턴, UI키워드) 검증 |

### 감지 흐름

```
GetActionButtonsAsync()
  ├── turn-form 찾기
  ├── [turn-form visible] → DetectChoiceButtonsNearTurnForm(turnForm)
  │     └── 이전 형제 10개까지 순회 → DetectChoiceButtons(sibling)
  └── [turn-form empty]  → DetectChoiceButtons(mainContent)
        └── FindButtonGroupsWalk() → 부모별 Button 그룹 수집
              └── IsValidChoiceButton()으로 화이트리스트 필터링
                    └── 그룹 내 유효 버튼 3개 이상 → 반환
```

### 오탐 방어 시나리오

| 버튼 그룹 | 왜 걸러지는가 |
|---|---|
| `main`, `+407 -150`, `Create PR`, `More options` | `main` (4자) 통과 → `+407 -150` 숫자패턴 제외 → `Create PR` "Create"+"PR" 키워드 → `More options` "More"+"options" 키워드 → 유효 1개 < 3개 최소 → **탈락** |
| `소 음메~ 1`, `쥐 찍찍~ 2`, `닭 꼬끼오~ 3` | 모두 4자+, 숫자패턴 아님, UI키워드 없음 → 유효 3개 ≥ 3개 최소 → **통과** |

## 4. 빌드 결과

- Release 빌드: **0 Error, 0 Warning**

## 5. 변경 파일

- `ClaudeRemote.Windows/Services/ClaudeAutomationService.cs`
  - 감지 로직 전면 교체
  - 순 삭감: 블랙리스트 관련 ~100줄 삭제, 구조 기반 ~120줄 추가
