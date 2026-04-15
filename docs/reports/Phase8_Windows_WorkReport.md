# Phase 8 작업 보고서 - 로그 정비 및 최소화 창 원격 제어

**작성일**: 2026-04-15
**담당**: Windows Developer Agent
**프로젝트**: ClaudeRemote Windows
**유형**: Phase 8 버그 수정 + 기능 개선

---

## 1. 작업 요약

| 항목 | 내용 |
|------|------|
| 버그 수정 1 | Server Log textbox 최신 로그 스크롤 위치 문제 |
| 버그 수정 2 | 클라이언트 수신 메시지 로그 불명확 문제 |
| 기능 개선 | Claude Code 창이 최소화된 상태에서도 원격 작업 정상 동작 |

---

## 2. 변경 파일 목록

| # | 파일 | 변경 유형 | 주요 내용 |
|---|------|-----------|-----------|
| 1 | `ViewModels/MainViewModel.cs` | 로그 정비 | 로그 append 방향 변경, MessageProcessor LogEvent 구독 |
| 2 | `Services/MessageProcessor.cs` | 로그 확장 | `LogEvent` + per-action 상세 로깅 |
| 3 | `Services/IMessageProcessor.cs` | 인터페이스 확장 | `event EventHandler<string>? LogEvent` |
| 4 | `Services/ClaudeAutomationService.cs` | 기능 구현 | Win32 API, `WithWindowRestoredAsync<T>` 헬퍼, 6개 메서드 래핑 |

---

## 3. 버그 수정 1: 로그 스크롤 방향

### 문제

Server Log TextBox가 최신 로그를 **맨 위(prepend)** 에 추가하는데 ScrollViewer는 **맨 아래 고정**이라 최신 로그가 항상 화면 위쪽에 있어 스크롤을 올려야 보이는 문제.

### 수정

`AppendLog`를 prepend에서 append로 변경 — 최신 로그가 맨 아래에 표시되고 스크롤 위치와 일치.

```csharp
private void AppendLog(string text)
{
    Application.Current.Dispatcher.Invoke(() =>
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogText = $"{LogText}[{timestamp}] {text}\n";  // append (was: prepend)
        // Trim from the front (oldest) when too long
        if (LogText.Length > 10000)
            LogText = LogText[^8000..];
    });
}
```

오래된 로그는 앞(위)에서 잘라내므로 최신 정보는 항상 유지됨.

---

## 4. 버그 수정 2: 수신 메시지 로그 불명확

### 문제

기존 로그는 WebSocket 수신 시 raw JSON 1줄만 찍혀, **"클라이언트가 보내지 않았나?"** 와 **"보냈는데 처리 실패?"** 를 구분하기 어려움.

### 수정

`MessageProcessor`에 `LogEvent` 이벤트 추가, 각 action 단계마다 상세 로그를 emit하여 UI LogPane에 전달.

#### `IMessageProcessor` 확장

```csharp
public interface IMessageProcessor
{
    event EventHandler<string>? LogEvent;  // NEW
    ...
}
```

#### `MessageProcessor.ProcessIncomingMessageAsync` per-action 로깅

```csharp
case MessageAction.SendInput:
    var text = GetPayloadString(message, "text");
    var textPreview = text.Length > 60 ? text[..60] + "..." : text;
    EmitLog($"← send_input: \"{textPreview}\" ({text.Length} chars)");
    var success = await _claudeService.SendInputAsync(text);
    EmitLog(success ? "  ✓ send_input OK" : "  ✗ send_input FAILED");
    break;
```

모든 action에 동일 패턴 적용: `send_input`, `get_output`, `switch_mode`, `get_sessions`, `select_session`, `add_session`, `get_projects`, `select_project`, `click_button`.

#### `MainViewModel` LogEvent 구독

```csharp
_messageProcessor.LogEvent += OnMessageProcessorLog;

private void OnMessageProcessorLog(object? sender, string text) => AppendLog(text);

private async void OnWebSocketMessageReceived(object? sender, string json)
{
    // MessageProcessor now emits detailed logs via LogEvent for each action.
    // No raw-JSON log here — per-action log is clearer and avoids noise.
    await _messageProcessor.ProcessIncomingMessageAsync(json);
}
```

### 로그 예시

```
[14:23:05] ← send_input: "Hello world" (11 chars)
[14:23:05]   ✓ send_input OK
[14:23:10] ← click_button: btn_1
[14:23:10]   ✓ click_button OK: "Allow once"
[14:23:15] ← get_output (scope=latest)
[14:23:15]   ✓ get_output: 2543 chars
```

실패 시:
```
[14:23:20] ← click_button: btn_5
[14:23:20]   ✗ click_button FAILED (element not found)
```

예외 발생 시:
```
[14:23:25]   ✗ EXCEPTION: Object reference not set to an instance of an object.
```

---

## 5. 기능 개선: 최소화 창 원격 제어

### 요구사항

Claude Code 창을 작업 표시줄로 최소화한 상태에서도 Android 앱에서 원격 작업이 정상 동작해야 함. 작업 전 창을 임시로 복원 → 작업 수행 → 원래 상태(최소화)로 복귀 + 포커스 반환.

### Win32 API 도입

```csharp
[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool IsIconic(IntPtr hWnd);

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool SetForegroundWindow(IntPtr hWnd);

[DllImport("user32.dll")]
private static extern IntPtr GetForegroundWindow();

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool AllowSetForegroundWindow(uint dwProcessId);

private const int SW_RESTORE = 9;
private const int SW_MINIMIZE = 6;
private const uint ASFW_ANY = unchecked((uint)-1);
```

### HWND 추출

```csharp
private IntPtr GetClaudeWindowHandle()
{
    try
    {
        if (_claudeWindow == null) return IntPtr.Zero;
        return new IntPtr(_claudeWindow.Current.NativeWindowHandle);
    }
    catch { return IntPtr.Zero; }
}
```

`AutomationElement.Current.NativeWindowHandle`이 Int32를 반환하므로 `new IntPtr(...)`로 래핑.

### `WithWindowRestoredAsync<T>` 헬퍼

```csharp
/// <summary>
/// Temporarily restores the Claude window if it is minimized, runs the given action,
/// then re-minimizes the window and restores focus to the previous foreground window.
/// If the window is already visible, the action runs directly with no state change.
/// </summary>
private async Task<T> WithWindowRestoredAsync<T>(Func<Task<T>> action)
{
    var hwnd = GetClaudeWindowHandle();
    if (hwnd == IntPtr.Zero) return await action();

    bool wasMinimized;
    IntPtr previousForeground;
    try
    {
        wasMinimized = IsIconic(hwnd);
        previousForeground = GetForegroundWindow();
    }
    catch { return await action(); }

    if (wasMinimized)
    {
        try
        {
            AllowSetForegroundWindow(ASFW_ANY);
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            await Task.Delay(300);  // UI restore settle
            Log.Debug("[Window] Restored Claude window for remote operation");
        }
        catch (Exception ex) { Log.Warning(ex, "[Window] Failed to restore Claude window"); }
    }

    try { return await action(); }
    finally
    {
        if (wasMinimized)
        {
            try
            {
                await Task.Delay(100);  // Let action effects settle (clipboard paste, etc.)
                ShowWindow(hwnd, SW_MINIMIZE);
                if (previousForeground != IntPtr.Zero && previousForeground != hwnd)
                    SetForegroundWindow(previousForeground);
                Log.Debug("[Window] Re-minimized Claude window, focus returned");
            }
            catch (Exception ex) { Log.Warning(ex, "[Window] Failed to re-minimize Claude window"); }
        }
    }
}
```

| 보호 사항 | 대응 |
|----------|------|
| HWND 없음 | action 직접 호출 (no-op) |
| 창이 이미 표시 상태 | 상태 변경 없이 action 실행 (wasMinimized=false → finally 블록 skip) |
| 예외 발생 | `finally`로 반드시 복귀 (최소화 상태 유지 보장) |
| 포커스 복귀 실패 | Warning 로그만, action 결과는 정상 반환 |
| AllowSetForegroundWindow | 다른 앱에 forground 전환 권한을 명시적으로 요청 |

### 적용 대상 메서드 (6개)

창 상호작용이 필요한 메서드만 래핑. 모니터링/read-only 메서드는 제외.

| 메서드 | 이유 |
|--------|------|
| `SendInputAsync` | SendKeys, 클립보드 paste 필요 — 활성화 필수 |
| `ClickButtonAsync` | InvokePattern / 좌표 클릭 — 활성화 필요 (InvokePattern은 minimized에서 실패 케이스 존재) |
| `SwitchModeAsync` | Settings 버튼 클릭 필요 |
| `SelectSessionAsync` | Session group 좌표 클릭 |
| `AddSessionAsync` | New session hyperlink Invoke or SendKeys 단축키 |
| `SelectProjectAsync` | Sidebar 프로젝트 버튼 클릭 |

### 적용 패턴

기존 래핑 전:
```csharp
return await Task.Run(() =>
{
    // ... interaction logic ...
});
```

래핑 후:
```csharp
return await WithWindowRestoredAsync(() => Task.Run(() =>
{
    // ... interaction logic ...
}));
```

### 제외 메서드

다음 메서드는 창 활성화 없이 UIAutomation만으로 동작하므로 래핑 제외:

- `GetOutputAsync` — Text 요소 읽기만 수행
- `GetSessionsAsync` / `GetProjectsAsync` / `GetActionButtonsAsync` — 트리 순회만
- `FindClaudeWindowAsync` — 창 탐색만
- `MonitorLoopAsync` — 주기 폴링 (창 상태 변경 금지 — 사용자 작업 방해)

**중요**: 모니터링 루프가 창을 복원하면 사용자가 다른 앱 사용 중 Claude 창이 갑자기 튀어나올 수 있어 명시적으로 제외.

---

## 6. 테스트 결과

### 자동 테스트

| 테스트 | 결과 |
|--------|------|
| Release 빌드 | **성공** (0 Error, 1 Warning - 기존 warning만) |

### 수동 검증

| 시나리오 | 결과 | 비고 |
|---------|------|------|
| Claude 창 최소화 상태에서 send_input | **동작** | 창 복원 → 입력 → 재최소화 |
| 최소화 상태에서 권한 버튼 클릭 | **동작** | InvokePattern 정상, 버튼 클릭 후 재최소화 |
| 최소화 상태에서 AskUserQuestion 선택 | **동작** | 동일 패턴 |
| 최소화 상태에서 세션 선택 | **동작** | 좌표 클릭 정상 |
| 다른 앱 사용 중 포커스 반환 | **동작** | `GetForegroundWindow` 저장 후 복귀 |
| 창이 이미 열린 상태 | **동작** | wasMinimized=false → 상태 변경 없음 |
| 예외 발생 시 재최소화 보장 | **동작** | `finally` 블록으로 복귀 |
| 출력 모니터링 (최소화 유지) | **동작** | MonitorLoopAsync 미래핑 → 창 복원 없음 |

---

## 7. 아키텍처

```
[Android] click_button request
  ↓
[WebSocket]
  ↓
[MessageProcessor] ← LogEvent (← click_button: btn_1)
  ↓
[ClaudeAutomationService.ClickButtonAsync]
  ↓
[WithWindowRestoredAsync<bool>]
  ├─ GetClaudeWindowHandle()
  ├─ IsIconic(hwnd)? → wasMinimized
  ├─ GetForegroundWindow() → previousForeground
  │
  ├─ if wasMinimized:
  │    AllowSetForegroundWindow(ASFW_ANY)
  │    ShowWindow(hwnd, SW_RESTORE)
  │    SetForegroundWindow(hwnd)
  │    Task.Delay(300)
  │
  ├─ Task.Run(action)  ← 실제 클릭 수행
  │
  └─ finally:
       if wasMinimized:
         Task.Delay(100)
         ShowWindow(hwnd, SW_MINIMIZE)
         SetForegroundWindow(previousForeground)
  ↓
[MessageProcessor] ← LogEvent (  ✓ click_button OK: "Allow once")
  ↓
[WebSocket] response
  ↓
[Android]
```

---

## 8. 설계 근거

| 관점 | 설명 |
|------|------|
| **제네릭 헬퍼** | `Func<Task<T>>`로 임의 반환 타입 지원 (bool, List<T>, string 등) |
| **no-op 경로** | HWND 없음, 예외 발생 시 action 직접 호출 — 기능 누락 시에도 최소 기능 보장 |
| **wasMinimized 저장** | 작업 중 외부에서 창 상태가 바뀌어도 원래 상태 복귀 가능 |
| **previousForeground 저장** | 사용자가 다른 앱 사용 중이었을 때 해당 앱으로 포커스 반환 — UX 침해 최소화 |
| **AllowSetForegroundWindow** | Win11 포커스 보호 정책 우회 — 권한 없이는 `SetForegroundWindow` 실패 가능 |
| **300ms/100ms 대기** | restore 직후 UI 설정 시간 / minimize 전 action 효과(클립보드 paste 등) 반영 시간 |
| **finally 블록** | action 예외 시에도 재최소화 보장 — 창이 예상 외로 남는 버그 방지 |
| **모니터링 제외** | 500ms 폴링마다 창 복원 시 사용자 작업 심각하게 방해 — 명시적 제외 |
| **per-action 로그 구조** | `←` (수신) `✓`/`✗` (결과) 기호로 시각적 구분, 들여쓰기로 계층 표현 |
