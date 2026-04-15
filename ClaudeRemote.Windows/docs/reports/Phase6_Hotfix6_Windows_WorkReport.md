# Phase 6 Hotfix 6 - 버튼 클릭 후 SendInputAsync 실패 수정

**작업일**: 2026-04-13
**상태**: 완료

---

## 1. 문제

AskUserQuestion 선택 버튼 클릭은 정상 동작하나, 이후 `SendInputAsync()` 호출 시 텍스트 입력이 실패.

- 입력해도 반응 없음
- 원인: 버튼 클릭 후 UI가 변경되면서 캐싱된 입력 필드 참조가 stale 됨

## 2. 수정 내용

### 2-1. ClickButtonAsync — 클릭 후 캐시 무효화

```csharp
// InvokePattern 또는 좌표 클릭 성공 후:
_cachedTurnForm = null;
_cachedInputField = null;
```

버튼 클릭 후 Claude UI가 변경되므로, 모든 캐시된 요소 참조를 즉시 무효화.

### 2-2. SendInputAsync — 항상 fresh 탐색 + 대기 + 로깅

변경 전:
```csharp
var inputField = GetInputField();  // 캐시된 요소 반환 가능
inputField.SetFocus();
```

변경 후:
```csharp
Thread.Sleep(500);  // UI 갱신 대기

// 캐시 강제 무효화 후 fresh 탐색
_cachedTurnForm = null;
_cachedInputField = null;
var inputField = GetInputField();

// SetFocus 실패 시 좌표 클릭 fallback
try { inputField.SetFocus(); }
catch { ClickElement(inputField); }
```

### 2-3. 단계별 로깅 추가

| 단계 | 로그 |
|---|---|
| 입력 필드 탐색 | `[SendInput] Input field found: Name='Enter your turn'` 또는 `not found after fresh search` |
| SetFocus | `SetFocus succeeded` 또는 `SetFocus failed, attempting click fallback` |
| 클립보드 | `Clipboard set: N chars` |
| 완료 | `Input sent to Claude: N chars` |

## 3. 빌드 결과

- Release 빌드: **0 Error, 0 Warning**

## 4. 변경 파일

- `ClaudeRemote.Windows/Services/ClaudeAutomationService.cs`
  - `SendInputAsync()` — fresh 탐색 + 500ms 대기 + 로깅
  - `ClickButtonAsync()` — 클릭 후 캐시 무효화
