# Phase 6 Hotfix 5 - AskUserQuestion 감지 근본 재구현 (실측 기반)

**작업일**: 2026-04-13
**상태**: 완료

---

## 1. 문제

Hotfix 3~4의 감지 로직이 완전 실패:
- 실제 선택 버튼 (소/쥐/닭) → **감지 안됨**
- "Used a tool", "AskUserQuestion" 등 → **오탐 감지됨**

근본 원인: 실제 UI 트리 구조를 분석하지 않고 추측에 기반한 필터링을 계속 시도했기 때문.

## 2. UI 트리 실측 분석

진단 덤프 기능을 추가하여 AskUserQuestion 활성 상태의 전체 RawView 트리를 캡처 (8000+ elements).

### 핵심 발견

| 항목 | 실측 결과 |
|---|---|
| turn-form | AskUserQuestion 활성 시 **완전히 사라짐** (null, Empty 아님) |
| 선택 버튼 ControlType | **Button** (맞았음) |
| 선택 버튼 부모 컨테이너 | **Group** (ClassName에 `"rounded-xl"` 포함, Name에 질문+선택지+`"Skip"` 전체 포함) |
| "Used a tool" | ControlType.**Button** (W=704) — 대화 영역 내 활동 토글 (선택 컨테이너 외부) |
| "AskUserQuestion" | ControlType.**Text** — 도구 상태 라벨 |

### 선택 컨테이너 구조

```
[Group] Name="어떤 동물이 좋으세요? 소 음메~ 1 쥐 찍찍~ 2 닭 꼬끼오~ 3 Type something else... 4 Skip"
        Class="rounded-xl outline-none border border-border-300 border-0.5 p-4 my-4 bg-bg-000"
  ├─ [Text]   "어떤 동물이 좋으세요?"        ← 질문
  ├─ [Button] "소 음메~ 1"     W=700 H=64   ← 선택지
  ├─ [Button] "쥐 찍찍~ 2"     W=700 H=64   ← 선택지
  ├─ [Button] "닭 꼬끼오~ 3"   W=700 H=64   ← 선택지
  ├─ [Button] "Type something else... 4"     ← 직접 입력
  └─ [Button] "Skip"           W=63  H=38   ← 건너뛰기
```

### 식별 특징 (100% 확정)
- **ClassName에 `"rounded-xl"` 포함**
- **Name에 `"Skip"` 포함** (Skip 버튼이 항상 존재)
- 이 두 조건을 동시에 만족하는 Group은 AskUserQuestion 컨테이너뿐

## 3. 이전 실패 원인

| 접근 방식 | 실패 이유 |
|---|---|
| Hotfix 3: Width≥300 필터 | "Used a tool" (W=704)도 통과 |
| Hotfix 4: 블랙리스트 | 새 UI 요소 추가 시 끝없이 재발 |
| Hotfix 4-2: 구조 기반 (부모 3+ Button) | "Used a tool" 버튼들이 같은 부모에 3+ 존재하여 오탐 |
| 캐시 stale 문제 | 캐시된 turn-form이 Empty 반환 → 항상 폴백 경로 진입 |

## 4. 새 구현

### 삭제된 코드
- `DetectChoiceButtonsNearTurnForm()` — 형제 순회 방식
- `DetectChoiceButtons()` — 구조 기반 그룹 탐색
- `FindButtonGroupsWalk()` — 부모별 Button 그룹 수집
- `IsValidChoiceButton()` — 화이트리스트 필터
- 모든 상수/regex: `MinChoiceGroupSize`, `MinChoiceNameLength`, `MaxSiblingsToSearch`, `NumericPatternRegex`, `UiKeywords`

### 추가된 코드

**`FindAskUserQuestionContainer()`** — RawView 트리를 순회하며 컨테이너 탐색:
```csharp
// ClassName에 "rounded-xl" AND Name에 "Skip" 포함된 Group을 찾음
var cls = root.Current.ClassName ?? "";
var name = root.Current.Name ?? "";
if (cls.Contains("rounded-xl") && name.Contains("Skip"))
    return root;
```

**`DetectAskUserQuestionButtons()`** — 컨테이너의 직계 Button 자식 수집:
```csharp
// 컨테이너 찾기
var container = FindAskUserQuestionContainer(searchRoot);
// 직계 Button 자식만 수집 ("Skip"과 "Type something else..." 제외)
var child = RawWalker.GetFirstChild(container);
while (child != null) {
    if (child is Button && name != "Skip" && !name.StartsWith("Type something else"))
        buttons.Add(...)
}
```

**turn-form 캐시 수정**: BoundingRect Empty 시 캐시 무효화 후 재탐색

### 감지 흐름 (단순화)

```
GetActionButtonsAsync()
  └─ main-content에서 FindAskUserQuestionContainer() 호출
       └─ ClassName="rounded-xl" + Name에 "Skip" 포함된 Group 찾기
            └─ 직계 Button 자식 수집 (Skip, Type something else... 제외)
                 └─ 반환
```

## 5. 빌드 결과

- Release 빌드: **0 Error, 0 Warning** (SessionManager CS0067 무시)

## 6. 변경 파일

- `ClaudeRemote.Windows/Services/ClaudeAutomationService.cs` — 감지 로직 전면 교체
- `docs/ClaudeUI_AskUserQuestion.md` — UI 트리 실측 문서 (신규)

## 7. 남은 작업

- 실제 AskUserQuestion 상태에서 감지 테스트 필요
- 버튼 클릭(ClickButtonAsync) 동작 검증 필요
- 진단 덤프 코드(`DumpElementTree`, `_lastDumpTime`) 제거 필요 (테스트 완료 후)
