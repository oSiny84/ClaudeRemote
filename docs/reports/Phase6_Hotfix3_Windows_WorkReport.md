# Phase 6 Hotfix 3 작업 보고서 - AskUserQuestion 버튼 감지 수정

**작성일**: 2026-04-13
**담당**: Windows Developer Agent
**프로젝트**: ClaudeRemote Windows
**유형**: Phase 6 긴급 수정 (Hotfix 3)

---

## 1. 작업 요약

Phase 6 Hotfix 2에서 구현한 위치 기반(turn-form 상대 좌표) 버튼 감지가 **AskUserQuestion 상태에서 완전히 실패**하는 문제를 진단하고 수정했습니다.

**핵심 발견**: AskUserQuestion이 활성화되면 `turn-form`의 `BoundingRectangle`이 **Empty**(0x0)로 변하여 위치 기반 감지의 기준점이 사라짐.

**해결**: turn-form 기반 감지(정상 상태) + RawView 전체 스캔 폴백(AskUserQuestion 상태)의 **이중 전략**으로 전환.

---

## 2. 디버깅 과정 및 발견

### 2-1. 진단 방법

`GetActionButtonsAsync()`에 강화된 로깅을 추가하여 실시간 UI 트리 분석 수행:
- turn-form BoundingRectangle 상태 추적
- main-content 내 전체 Button 요소의 Name, Y좌표, Width 로깅
- RawView 전체 스캔으로 비-Button 요소 타입 조사
- AskUserQuestion 도구를 직접 트리거하여 실제 버튼 표시 상태에서 데이터 수집

### 2-2. 핵심 발견 사항

| 발견 | 상세 |
|------|------|
| **turn-form Empty** | AskUserQuestion 활성 시 turn-form.BoundingRectangle = Empty (숨김 상태) |
| **시점** | turn-form Rect(Top=775) → 1초 후 Empty로 전환 (11:07:10 → 11:07:11) |
| **버튼 위치** | AskUserQuestion 선택 버튼은 Y=562~752, **Width=700** (매우 넓음) |
| **UI 크롬 버튼** | "Preview"(W=81), "Session options"(W=29), "Copy message"(W=16) 등은 좁음 |
| **폴백 필요** | turn-form Empty 시 위치 기반 감지 불가 → 별도 감지 전략 필요 |

### 2-3. AskUserQuestion 버튼 실제 UI 트리 구조

```
AskUserQuestion 활성 상태 (turn-form: EMPTY)

main-content
  ├─ [대화 내용 버튼들] W < 200px
  │   ├─ "Copy message" W=16
  │   ├─ "Ran 3 commands" W=704 (activity toggle)
  │   └─ ...
  │
  ├─ "AskUserQuestion" Y=345 W=704 (도구 상태 버튼 - 선택 버튼 아님)
  │
  ├─ "소 (Cow) 소를 선택합니다 1"      Y=562 W=700  ← 선택 버튼
  ├─ "쥐 (Mouse) 쥐를 선택합니다 2"    Y=625 W=700  ← 선택 버튼
  ├─ "닭 (Chicken) 닭을 선택합니다 3"  Y=688 W=700  ← 선택 버튼
  ├─ "Type something else... 4"         Y=752 W=700  ← 선택 버튼
  └─ "Skip"                             Y=818 W=63   (좁음 → 제외)
```

**핵심 특성**: AskUserQuestion 선택 버튼은 **Width ≥ 300px** (실제 700px), UI 크롬 버튼은 모두 < 200px.

---

## 3. 변경 파일 목록

| # | 파일 | 변경 유형 | 주요 내용 |
|---|------|-----------|-----------|
| 1 | `Services/ClaudeAutomationService.cs` | **핵심 수정** | 이중 감지 전략, 폴백 메서드, 크롬 버튼 필터 |

---

## 4. 상세 변경 내용

### 4-1. 이중 감지 전략 (`GetActionButtonsAsync`)

```
GetActionButtonsAsync()
  ├─ GetTurnForm()
  │   ├─ BoundingRectangle 유효 → 위치 기반 감지 (기존 로직)
  │   └─ BoundingRectangle Empty → DetectAskUserQuestionButtons() 폴백
  └─ 결과 반환
```

### 4-2. 신규: `DetectAskUserQuestionButtons()` 폴백 메서드

| 단계 | 내용 |
|------|------|
| 1 | `CollectAllVisibleRawElements()` — main-content의 모든 가시 RawView 요소 수집 |
| 2 | Button 타입 + 화면 내(Y > 0) + 이름 있는 요소 필터링 |
| 3 | 대화 크롬 버튼 제외 (`IsConversationChromeButton`) |
| 4 | **Width ≥ 300px** 필터 — AskUserQuestion 선택 버튼만 남김 |
| 5 | 2개 이상이면 선택 버튼으로 판정 |

### 4-3. 신규: `IsConversationChromeButton()` 필터

Position-based와 AskUserQ 폴백 양쪽에서 사용하는 통합 크롬 버튼 필터:

```csharp
// 제외 대상:
"Copy message", "Show more", "Show less", "Skip"
"Toggle menu*", "Auto accept*", "Settings*", "Press and hold*"
"Ctrl+*", "Alt+*", "C:\*" (프로젝트 경로)
"Ran *", "Read *", "Edited *", "Created *", "Searched *", "Updated *", "Loaded *"
"AskUserQuestion", "TodoWrite", "Write", "Edit", "Bash", "Grep", "Glob", "Read", "NotebookEdit"
"*Opus*", "*Sonnet*", "*Haiku*", "*Claude*"
```

### 4-4. `ClickButtonAsync()` 리팩토링

중복 로직을 `GetActionButtonsSync()` 헬퍼로 통합:
- turn-form 유효 → 위치 기반 감지
- turn-form Empty → AskUserQ 폴백
- 인덱스로 대상 버튼 선택 → InvokePattern / 좌표 클릭

### 4-5. 상태 전환 로깅

```csharp
private bool _lastTurnFormWasEmpty;
// turn-form Empty 전환 시 1회만 로깅 (500ms 폴링마다 반복 안 함)
// "turn-form BECAME EMPTY" / "turn-form RECOVERED" 로그
```

---

## 5. 테스트 결과

### 자동 테스트

| 테스트 그룹 | 결과 | 상세 |
|------------|------|------|
| Phase 2 통합 (8개) | **8/8 PASS** | WebSocket, 메시지 처리, 프로토콜 준수 |
| Phase 5 통합 (5개) | **5/5 PASS** | Heartbeat, 큐잉, 청크 분할, 프로세스 워처 |
| Release 빌드 | **성공** | 0 Error |

### 수동 검증 (AskUserQuestion 트리거 테스트)

| 테스트 | 결과 | 상세 |
|--------|------|------|
| AskUserQuestion 버튼 감지 | **PASS** | 4개 선택 버튼 정확 감지 (소, 쥐, 닭, Other) |
| 버튼 이름 정확성 | **PASS** | "사과 (Apple) 사과를 선택합니다 1" 등 정확 |
| 생성 중 오탐 방지 | **PASS** | turn-form Empty + 선택 버튼 없음 → 0개 반환 |
| 크롬 버튼 제외 | **PASS** | "Preview", "Session options", "Copy message" 등 미포함 |
| 정상 상태 복귀 | **PASS** | turn-form 복귀 시 위치 기반 감지로 자동 전환 |

### 로그 증거

```
11:35:50 [INF] [AskUserQ]   Wide: Name='사과 (Apple) 사과를 선택합니다 1' Y=562 W=700
11:35:50 [INF] [AskUserQ]   Wide: Name='바나나 (Banana) 바나나를 선택합니다 2' Y=625 W=700
11:35:50 [INF] [AskUserQ]   Wide: Name='포도 (Grape) 포도를 선택합니다 3' Y=688 W=700
11:35:50 [INF] [AskUserQ]   Wide: Name='Type something else... 4' Y=752 W=700
11:35:50 [INF] [AskUserQ] Detected 4 choice buttons
11:35:50 [INF] [AskUserQ] RESULT: 4 action buttons detected (AskUserQuestion fallback)
```

---

## 6. 아키텍처

```
GetActionButtonsAsync() — 이중 감지 전략

  GetTurnForm()
    │
    ├─ BoundingRectangle 유효 (정상 대화 상태)
    │   └─ 위치 기반 감지
    │       ├─ zone = [turnFormTop - 200, turnFormTop)
    │       ├─ IsConversationChromeButton 제외
    │       └─ 2개 이상 → 선택 버튼 판정
    │
    └─ BoundingRectangle Empty (AskUserQuestion / 생성 중)
        └─ DetectAskUserQuestionButtons() 폴백
            ├─ CollectAllVisibleRawElements (RawView 전체 스캔)
            ├─ Button 타입 + Y > 0 (화면 내) 필터
            ├─ IsConversationChromeButton 제외
            ├─ Width ≥ 300px 필터 (핵심 판별 기준)
            └─ 2개 이상 → 선택 버튼 판정

  ClickButtonAsync() → GetActionButtonsSync()
    └─ 동일한 이중 전략으로 fresh 버튼 수집 후 인덱스 매칭
```

---

## 7. 근본 원인 및 설계 근거

| 관점 | 설명 |
|------|------|
| **Hotfix 2 실패 원인** | AskUserQuestion 시 turn-form이 숨겨져 BoundingRectangle=Empty → 위치 기준점 소실 |
| **Width 기반 필터** | AskUserQuestion 선택 버튼(W=700)은 UI 크롬 버튼(W<200)과 폭이 확연히 다름 |
| **크롬 버튼 필터** | Activity toggle("Ran X commands"), 도구 상태("AskUserQuestion") 등 오탐 원인 제거 |
| **이중 전략** | 정상 상태(위치 기반)와 AskUserQuestion 상태(폭 기반)를 모두 커버 |
| **상태 전환 로깅** | 500ms 폴링 시 매번 로깅하면 10MB 로그가 수분 내 꽉 참 → 전환 시점만 1회 로깅 |
