# Phase 7 작업 보고서 - 권한 요청 다이얼로그 감지 및 원격 제어

**작성일**: 2026-04-14
**담당**: Windows Developer Agent
**프로젝트**: ClaudeRemote Windows
**유형**: Phase 7 신규 기능

---

## 1. 작업 요약

Claude Code가 도구 실행 시 사용자에게 표시하는 **권한 요청 다이얼로그**(Permission Dialog)를 감지하고, Android 클라이언트에서 원격으로 응답할 수 있도록 구현했습니다.

| 항목 | 내용 |
|------|------|
| 대상 UI | Claude Code의 도구 실행 권한 요청 (예: `Allow Claude to Run grep -n ...?`) |
| 감지 버튼 | Deny / Allow once / Always allow for project (local) / Always allow for project |
| 프로토콜 확장 | `action_buttons` 메시지에 `category` 필드 추가 (`selection` / `permission`) |
| 재사용 | 기존 `click_button` 명령 그대로 사용 (신규 프로토콜 없음) |

---

## 2. 배경

Phase 6에서 구현한 `AskUserQuestion` 선택 버튼 감지는 Claude가 사용자에게 선택지를 묻는 경우를 다뤘습니다. 그러나 Claude Code가 **도구를 실행하기 전** 표시하는 권한 요청 다이얼로그는 구조가 완전히 달라 별도 감지 로직이 필요했습니다.

| 구분 | AskUserQuestion | Permission Dialog |
|------|-----------------|-------------------|
| 컨테이너 시그니처 | `ClassName~"rounded-xl"` + `Name~"Skip"` | 없음 |
| 버튼 폭 | Width ≈ 700px (매우 넓음) | Width 가변 |
| 버튼 이름 | 선택지 텍스트 그대로 | 단축키 접미사 포함 (`"Deny Esc"`, `"Allow once Enter"`) |
| 프롬프트 | 컨테이너의 `Name` 속성 | 별도 Group 내 다수 Text 요소 |

---

## 3. 변경/추가 파일 목록

| # | 파일 | 변경 유형 | 주요 내용 |
|---|------|-----------|-----------|
| 1 | `Services/ClaudeAutomationService.cs` | 핵심 구현 | 권한 버튼 감지, 이름 정리, 프롬프트 추출 |
| 2 | `Services/IClaudeAutomationService.cs` | 인터페이스 확장 | `LastPermissionPrompt`, `LastButtonCategory` |
| 3 | `Services/IMessageProcessor.cs` | 시그니처 확장 | `CreateActionButtonsMessage(buttons, prompt, category)` |
| 4 | `Services/MessageProcessor.cs` | 프로토콜 확장 | `category` 필드 payload에 추가 |
| 5 | `ViewModels/MainViewModel.cs` | 이벤트 연결 | 카테고리 기반 prompt 선택 후 전송 |

---

## 4. 상세 구현

### 4-1. UI 트리 분석

권한 요청 다이얼로그의 UI 구조 (`uitree-permission.txt` 수집):

```
[Group] Class="pointer-events-auto ..."      ← container (Allow once 기준 2단계 상위)
  ├─ [Group]                                   ← 프롬프트 Group들 (첫 버튼 앞)
  │   ├─ [Text] Name="Allow Claude to "
  │   ├─ [Text] Name="Run"
  │   ├─ [Text] Name="grep -n ... ?"
  │   └─ ...
  ├─ [Group]                                   ← 명령 상세 Group
  │   └─ [Text] Name="{JSON command detail}"
  ├─ [Button] Name="Deny Esc"                  ← 버튼들
  ├─ [Button] Name="Allow once Enter"
  ├─ [Button] Name="Always allow for project (local) Ctrl Enter"
  └─ [Button] Name="Always allow for project Ctrl Enter"
```

### 4-2. 감지 알고리즘 (`DetectPermissionButtons`)

| 단계 | 내용 |
|------|------|
| 1 | `FindByNameRaw(mainContent, "Allow once")` — RawView에서 Text 요소 검색 (존재 = 권한 다이얼로그 활성) |
| 2 | 발견한 Text → 부모 Button → 부모 Group (container)으로 2단계 위로 이동 |
| 3 | Container의 children을 RawView 순회 |
| 4 | `ControlType.Group` && 첫 버튼 이전 → `CollectTextFromGroup`으로 프롬프트 텍스트 수집 |
| 5 | `ControlType.Button` → `CleanPermissionButtonName` 으로 단축키 접미사 제거 |
| 6 | 정리된 이름이 허용 prefix로 시작하면 `ButtonInfo` 추가 |

### 4-3. 버튼 이름 정리 (`CleanPermissionButtonName`)

```csharp
string[] suffixes = { " Ctrl Enter", " Enter", " Esc" };
// "Deny Esc"                                 → "Deny"
// "Allow once Enter"                         → "Allow once"
// "Always allow for project (local) Ctrl Enter" → "Always allow for project (local)"
// "Always allow for project Ctrl Enter"       → "Always allow for project"
```

### 4-4. 허용 버튼 prefix (`PermissionButtonPrefixes`)

```csharp
private static readonly string[] PermissionButtonPrefixes = new[]
{
    "Deny",
    "Allow once",
    "Always allow for project"    // "... (local)" 케이스도 prefix로 매칭
};
```

### 4-5. 프롬프트 텍스트 추출 (`CollectTextFromGroup`)

```
프롬프트 Group 구조:
  Group
    ├─ Text "Allow Claude to "
    ├─ Text "Run"
    └─ Text "grep -n ... ?"

→ 재귀 수집 후 string.Join("") = "Allow Claude to Run grep -n ... ?"
```

깊이 제한 `maxDepth=5`로 무한 재귀 방지.

### 4-6. 이중 전략 감지 (`GetActionButtonsAsync`)

```csharp
// 1. 권한 다이얼로그 먼저 체크 (우선순위 높음)
var permButtons = DetectPermissionButtons(mainContent);
if (permButtons.Count > 0)
{
    LastButtonCategory = "permission";
    return permButtons;
}

// 2. AskUserQuestion 체크 (fallback)
var askButtons = DetectAskUserQuestionButtons(mainContent);
LastButtonCategory = askButtons.Count > 0 ? "selection" : null;
return askButtons;
```

### 4-7. 프로토콜 확장 (`CreateActionButtonsMessage`)

기존 시그니처:
```csharp
CreateActionButtonsMessage(List<ButtonInfo> buttons, string? prompt = null)
```

확장 시그니처:
```csharp
CreateActionButtonsMessage(List<ButtonInfo> buttons, string? prompt = null, string? category = null)
```

Payload 구조:
```json
{
  "type": "content",
  "action": "action_buttons",
  "payload": {
    "buttons": [
      { "id": "btn_0", "text": "Deny" },
      { "id": "btn_1", "text": "Allow once" },
      { "id": "btn_2", "text": "Always allow for project (local)" },
      { "id": "btn_3", "text": "Always allow for project" }
    ],
    "prompt": "Allow Claude to Run grep -n ... ?",
    "category": "permission"
  }
}
```

### 4-8. 카테고리 기반 prompt 선택 (`MainViewModel.OnActionButtonsChanged`)

```csharp
var category = _claudeService.LastButtonCategory;
var prompt = category == "permission"
    ? _claudeService.LastPermissionPrompt
    : _claudeService.LastAskUserQuestionPrompt;
var msg = _messageProcessor.CreateActionButtonsMessage(buttons, prompt, category);
```

---

## 5. 해결한 버그

### 5-1. 권한 버튼 클릭 무반응

**증상**: 버튼은 정상 감지되어 Android 앱에 표시되지만, 클릭 시 Claude 앱에서 반응이 없고 Android는 대기 상태가 풀리지 않음.

**원인**: `ClickButtonAsync`가 호출하는 `GetActionButtonsSync()`가 `DetectAskUserQuestionButtons`만 호출하고 `DetectPermissionButtons`를 호출하지 않음. 결과적으로 re-find 단계에서 권한 버튼을 찾지 못해 `idx out of range` 에러 발생.

**수정**: `GetActionButtonsSync`에도 동일한 이중 전략 적용.

```csharp
private List<ButtonInfo> GetActionButtonsSync()
{
    var mainContent = GetMainContent();
    if (mainContent == null) return new List<ButtonInfo>();

    // 권한 먼저, AskUserQuestion 다음 (비동기 버전과 동일 순서)
    var permButtons = DetectPermissionButtons(mainContent);
    if (permButtons.Count > 0) return permButtons;

    return DetectAskUserQuestionButtons(mainContent);
}
```

### 5-2. UI 트리 덤프 타이밍 이슈

**증상**: 디버그용 UI 트리 덤프가 "Thinking..." 상태에서 실행되어 권한 다이얼로그를 캡처하지 못함.

**해결**: 덤프 트리거를 `FindByNameRaw(mainContent, "Allow once")` 존재 조건으로 변경. 덤프는 권한 다이얼로그가 실제로 표시된 시점에만 실행.

---

## 6. 테스트 결과

### 자동 테스트

| 테스트 | 결과 |
|--------|------|
| Release 빌드 | **성공** (0 Error, 1 Warning) |

### 수동 검증

| 테스트 | 결과 | 상세 |
|--------|------|------|
| 권한 다이얼로그 감지 | **PASS** | 4개 버튼 정확 감지 |
| 버튼 이름 정리 | **PASS** | "Deny Esc" → "Deny", "Allow once Enter" → "Allow once" |
| 프롬프트 추출 | **PASS** | "Allow Claude to Run grep -n ... ?" 정확 추출 |
| category 필드 | **PASS** | `"category": "permission"` 정확 전송 |
| Android 원격 클릭 | **PASS** | InvokePattern 클릭 성공, Claude가 해당 동작 실행 |
| AskUserQuestion 호환 | **PASS** | `"category": "selection"`으로 분리 전송 |

---

## 7. 아키텍처

```
GetActionButtonsAsync()
  └─ GetMainContent()
      ├─ DetectPermissionButtons()       ← 우선
      │   ├─ FindByNameRaw("Allow once")
      │   ├─ parent(Button) → parent(container Group)
      │   ├─ Group 순회 → CollectTextFromGroup → promptParts
      │   ├─ Button 순회 → CleanPermissionButtonName → IsPermissionButton
      │   └─ LastButtonCategory = "permission"
      │       LastPermissionPrompt = string.Join("", promptParts)
      │
      └─ DetectAskUserQuestionButtons()   ← fallback
          └─ LastButtonCategory = "selection"
              LastAskUserQuestionPrompt = container.Name

OnActionButtonsChanged (MainViewModel)
  └─ category == "permission"
      ? LastPermissionPrompt
      : LastAskUserQuestionPrompt
  └─ CreateActionButtonsMessage(buttons, prompt, category)
  └─ WebSocket → Android
```

---

## 8. 설계 근거

| 관점 | 설명 |
|------|------|
| **권한 우선순위** | 권한 다이얼로그는 AskUserQuestion보다 긴급 — 도구 실행 대기 중이므로 먼저 체크 |
| **prefix 매칭** | "Always allow for project"와 "Always allow for project (local)" 양쪽을 단일 prefix로 커버 |
| **RawView 사용** | ControlView는 일부 Chromium 상태에서 Allow once Text를 놓침 — RawView는 안정적 |
| **프로토콜 최소 확장** | 신규 액션 추가 없이 `category` 필드만으로 Android가 UI 분기 가능 (에러 색상 강조, 햅틱 피드백 등) |
| **프롬프트 수집 방식** | 권한 프롬프트는 다수 Text로 분리되어 있어 Join 필요. AskUserQuestion은 단일 Name이므로 경로 다름 |
