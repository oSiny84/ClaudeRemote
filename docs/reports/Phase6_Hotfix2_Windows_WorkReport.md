# Phase 6 Hotfix 2 작업 보고서 - 버튼 감지 위치 기반 전환

**작성일**: 2026-04-13
**담당**: Windows Developer Agent
**프로젝트**: ClaudeRemote Windows
**유형**: Phase 6 긴급 수정 (Hotfix 2)

---

## 1. 작업 요약

Phase 6 액션 버튼 감지의 **블랙리스트 기반 필터링**을 **위치 기반(turn-form 상대 좌표) 감지**로 전면 교체했습니다.

**문제**: 블랙리스트(`KnownUiButtons` 22개)로는 "Copy code", "Preview", "Preview options", "AskUserQuestion", 세션 이름 버튼 등 동적으로 생성되는 UI 버튼을 필터링할 수 없어 오탐(false positive) 발생.

**해결**: Claude가 선택 버튼을 제시할 때 항상 `turn-form`(입력 영역) 바로 위에 배치되는 위치적 특성을 이용한 좌표 기반 감지로 전환.

---

## 2. 변경 파일 목록

| # | 파일 | 변경 유형 | 주요 내용 |
|---|------|-----------|-----------|
| 1 | `Services/ClaudeAutomationService.cs` | **핵심 수정** | 블랙리스트 → 위치 기반 감지 전면 교체 |

---

## 3. 상세 변경 내용

### 3-1. 제거된 코드

| 항목 | 내용 |
|------|------|
| `KnownUiButtons` HashSet | 22개 블랙리스트 제거 |
| `IsKnownUiButton()` 메서드 | 블랙리스트 + 패턴 매칭 함수 전체 삭제 |

### 3-2. 추가된 상수/필드

```csharp
// 입력 영역 버튼 (turn-form 근접 영역 내에서도 제외)
private static readonly HashSet<string> InputAreaButtons = new(StringComparer.OrdinalIgnoreCase)
{
    "Interrupt", "Send", "Submit", "Stop", "Resume", "Cancel"
};

// 위치 기반 감지 상수
private const double ButtonZoneHeight = 200.0;  // turn-form 위 200px 범위
private const int MinActionButtonCount = 2;      // 최소 2개 이상이어야 선택 버튼으로 판정
```

### 3-3. `GetActionButtonsAsync()` 새 알고리즘

```
1. GetTurnForm() → BoundingRectangle.Top 확보 (기준 Y좌표)
2. main-content에서 모든 Button 수집 (ControlView → RawView 폴백)
3. 각 버튼의 BoundingRectangle.Top이 [turnFormTop - 200, turnFormTop) 범위인지 확인
4. InputAreaButtons (Interrupt, Send 등) 제외
5. 후보 2개 이상 → 선택 버튼으로 판정, 1개 이하 → 빈 배열 반환
```

| 항목 | 기존 (블랙리스트) | 변경 (위치 기반) |
|------|------------------|-----------------|
| 필터링 방식 | 22개 이름 블랙리스트 + 패턴 매칭 | Y좌표 범위 `[Top-200, Top)` |
| 오탐 대응 | 수동으로 블랙리스트 추가 필요 | 위치로 자동 판별 |
| 최소 버튼 수 | 없음 (1개도 반환) | 2개 미만이면 빈 배열 |
| 입력 영역 버튼 | 블랙리스트에 포함 | `InputAreaButtons` 6개만 제외 |

### 3-4. `ClickButtonAsync()` 동기화

`GetActionButtonsAsync()`와 동일한 위치 기반 로직을 적용하여 fresh한 버튼 수집 시 동일 필터링 수행:

1. `GetTurnForm()` → Y좌표 기준점 확보
2. Button 수집 → Y좌표 범위 필터
3. `InputAreaButtons` 제외
4. 인덱스 매칭 → `InvokePattern` / 좌표 클릭

---

## 4. 테스트 결과

| 테스트 그룹 | 결과 | 상세 |
|------------|------|------|
| Phase 2 통합 (8개) | **8/8 PASS** | WebSocket, 메시지 처리, 프로토콜 준수 |
| Phase 5 통합 (5개) | **5/5 PASS** | Heartbeat, 큐잉, 청크 분할, 프로세스 워처 |
| Release 빌드 | **성공** | 0 Error, 0 Warning (main project) |

### 수동 검증 필요 항목

| 시나리오 | 기대 결과 |
|----------|-----------|
| Claude 선택 버튼 제시 | turn-form 위 200px 이내 버튼만 감지 |
| "Copy code" 버튼 | turn-form과 거리 멀어 미감지 |
| "Preview" / "Preview options" | 대화 영역 중간에 위치 → 미감지 |
| 세션 이름 버튼 | 사이드바에 위치 → 미감지 |
| 선택 버튼 1개만 존재 | 빈 배열 반환 (MinActionButtonCount=2) |
| "Interrupt" 버튼 | InputAreaButtons로 명시 제외 |

---

## 5. 아키텍처

```
GetActionButtonsAsync() — 위치 기반 감지 흐름

  turn-form
  ├─ BoundingRectangle.Top = Y기준점
  │
  main-content (전체 Button 수집)
  │
  ├─ Button A (Y=300)  ← turn-form 위 200px 밖 → 제외
  ├─ Button B (Y=580)  ← [turnFormTop-200, turnFormTop) 범위 → 후보
  ├─ Button C (Y=600)  ← [turnFormTop-200, turnFormTop) 범위 → 후보
  ├─ "Interrupt" (Y=610) ← InputAreaButtons → 제외
  └─ Button D (Y=750)  ← turn-form 아래 → 제외

  후보 2개 (B, C) ≥ MinActionButtonCount(2) → 선택 버튼 판정
  → action_buttons 메시지로 Android에 push
```

---

## 6. 변경 이유 및 설계 근거

| 관점 | 설명 |
|------|------|
| **블랙리스트 한계** | Claude 앱 업데이트마다 새 UI 버튼 추가 가능 → 유지보수 불가 |
| **위치 기반 장점** | Claude 선택 버튼은 항상 turn-form 직전에 렌더링 → 앱 업데이트에 강건 |
| **최소 2개 규칙** | 단일 버튼은 UI 크롬일 가능성 높음; 선택지는 항상 2개 이상 |
| **InputAreaButtons 최소화** | 6개만 유지 — turn-form 영역 내 실제 입력 관련 버튼만 제외 |
