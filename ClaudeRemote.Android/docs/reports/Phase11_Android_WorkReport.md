# Phase 11: Usage Dashboard - Work Report

## Overview
Claude Code 사용량(Context window / 5-hour limit / Weekly limits / Model / Plan) 종합 대시보드를 Android에서 원격 조회 가능한 별도 다이얼로그로 구현.

## Date
2026-04-15

## Scope
Phase 11은 두 축으로 구성:
1. **실시간 토큰 표시 (ManageScreen Usage Card)** — 기존 계획대로 병행 진행 중 (이 보고서 범위 외)
2. **종합 사용량 대시보드 다이얼로그** — 본 보고서 범위 ✅

## Changes

### 1. Data Model (`Messages.kt`)
사용량 대시보드 전용 @Serializable 데이터 클래스 3종 추가 (C# 서버 스펙 일치):

```kotlin
@Serializable
data class UsageDashboard(
    val contextWindow: ContextWindow? = null,
    val fiveHourLimit: UsageLimit? = null,
    val weeklyAllModels: UsageLimit? = null,
    val weeklySonnetOnly: UsageLimit? = null,
    val modelName: String? = null,
    val planName: String? = null,
    val fetchedAt: String? = null
)

@Serializable
data class ContextWindow(
    val usedText: String? = null,
    val totalText: String? = null,
    val percentUsed: Int? = null
)

@Serializable
data class UsageLimit(
    val label: String? = null,
    val percentUsed: Int? = null,
    val resetText: String? = null
)
```

- 4개 핵심 사용량 필드: `contextWindow` / `fiveHourLimit` / `weeklyAllModels` / `weeklySonnetOnly`
- 메타데이터: `modelName` (현재 모델) / `planName` (플랜) / `fetchedAt` (수집 시각)
- `ContextWindow`는 `usedText` + `totalText` (토큰 수 문자열) 제공 — `UsageLimit`의 `resetText`와 구분
- 모든 필드 nullable — 서버가 일부 항목만 반환해도 UI는 제공된 섹션만 렌더
- `@Serializable` 적용하여 향후 `decodeFromJsonElement` 전환 가능

`MessageAction`에 신규 상수 추가:
```kotlin
const val GET_USAGE_DASHBOARD = "get_usage_dashboard"
```

### 2. MainViewModel.kt

#### State Flows
```kotlin
private val _usageDashboard = MutableStateFlow<UsageDashboard?>(null)
val usageDashboard: StateFlow<UsageDashboard?> = _usageDashboard

private val _usageDashboardLoading = MutableStateFlow(false)
val usageDashboardLoading: StateFlow<Boolean> = _usageDashboardLoading

private val _showUsageDashboard = MutableStateFlow(false)
val showUsageDashboard: StateFlow<Boolean> = _showUsageDashboard
```

`_showUsageDashboard`는 다이얼로그 오픈/클로즈 제어 — 데이터/로딩 상태와 분리되어 관리.

#### Methods
```kotlin
fun requestUsageDashboard() {
    _usageDashboardLoading.value = true
    webSocketClient.sendCommand(action = MessageAction.GET_USAGE_DASHBOARD)
}

fun openUsageDashboard() {
    _showUsageDashboard.value = true
    requestUsageDashboard()
}

fun closeUsageDashboard() {
    _showUsageDashboard.value = false
}
```

- `openUsageDashboard()`: 다이얼로그 열면서 즉시 요청 (첫 오픈 시 로딩 표시 → 데이터 도착)
- `requestUsageDashboard()`: Refresh 버튼에서도 재사용 (다이얼로그 이미 열려있는 상태에서 호출)

#### Response Handler
```kotlin
MessageAction.GET_USAGE_DASHBOARD -> {
    _usageDashboardLoading.value = false
    if (success) {
        val dashObj = payload?.get("dashboard") as? JsonObject ?: payload
        if (dashObj != null) {
            _usageDashboard.value = parseUsageDashboard(dashObj)
        } else {
            _snackbarEvent.tryEmit("Usage data missing")
        }
    } else {
        val error = payload?.get("error")?.jsonPrimitive?.content
        _snackbarEvent.tryEmit("Failed to load usage: ${error ?: "unknown"}")
    }
}
```

- **이중 파싱 경로**: `payload.dashboard` 중첩 객체 우선, 없으면 payload 자체를 파싱 — 서버 구현의 응답 shape 차이를 유연하게 흡수
- **Null-safe 파서**: 전용 헬퍼 `parseUsageDashboard` / `parseContextWindow` / `parseUsageLimit` 사용, `as?` 안전 캐스트 + `contentOrNull` / `intOrNull`

### 3. UI Components

#### `ui/components/UsageDashboardDialog.kt` (신규)
Material3 `AlertDialog` 기반. 주요 특징:

**구조**:
- **Title**: "Usage Dashboard"
- **Body**: `Column` + `verticalScroll` + `heightIn(max = 500.dp)` — 장문 콘텐츠 스크롤 지원
- **Buttons**: Refresh (confirmButton) + Close (dismissButton)

**상태별 표시**:
- `isLoading && dashboard == null` → 중앙 `CircularProgressIndicator` (첫 로드)
- `dashboard == null` → "No usage data available" 안내
- 정상 → `DashboardContent` 표시

**섹션 구성** (모두 nullable — 서버 응답에 있는 것만 렌더):
1. **Current usage**:
   - `ContextWindowRow` — `contextWindow` (usedText / totalText + progress bar + %)
   - `UsageLimitRow("5-hour limit")` — `fiveHourLimit` (label + resetText + progress bar + %)
2. **Weekly limits**:
   - `UsageLimitRow("All models")` — `weeklyAllModels`
   - `UsageLimitRow("Sonnet only")` — `weeklySonnetOnly`
3. **Footer** (divider 아래):
   - `Model: {modelName}`
   - `Plan: {planName}`
   - `Fetched: {fetchedAt}` + 재로딩 시 인라인 `CircularProgressIndicator` (12.dp)

`ContextWindowRow`는 `UsageLimitRow`와 별도 컴포저블 — Context window는 reset 시간이 아닌 사용/전체 토큰 문자열(`usedText / totalText`)을 표시하기 때문.

**Progress Bar 색상 분기** (`UsageLimitRow`):
```kotlin
val progressColor = when {
    percent >= 80 -> MaterialTheme.colorScheme.error        // 빨강
    percent >= 50 -> Color(0xFFF59E0B)                      // 주황 (Amber 500)
    else -> MaterialTheme.colorScheme.primary               // 기본 primary
}
LinearProgressIndicator(
    progress = { percent / 100f },
    modifier = Modifier.fillMaxWidth().height(6.dp),
    color = progressColor,
    trackColor = MaterialTheme.colorScheme.surfaceVariant
)
```

- 0~49%: primary (안전)
- 50~79%: Amber (주의)
- 80~100%: error (위험)
- `percent` 값은 `coerceIn(0, 100)` 적용 → 서버 오류값 방어
- `label`이 제공되면 우선 사용, 없으면 `fallbackTitle` ("Current session" / "All models" / "Sonnet only")

**Refresh 버튼**:
- `enabled = !isLoading` → 요청 중 연타 방지
- 클릭 → `onRefresh()` → ViewModel의 `requestUsageDashboard()` 재호출

### 4. ManageScreen.kt — Integration

#### Button 추가 (상단)
```kotlin
OutlinedButton(
    onClick = { viewModel.openUsageDashboard() },
    modifier = Modifier.fillMaxWidth()
) {
    Icon(Icons.Default.Analytics, ...)
    Spacer(Modifier.width(8.dp))
    Text("View detailed usage")
}
```

- ManageScreen 최상단 — Projects/Sessions 섹션 위
- `OutlinedButton` + `Analytics` 아이콘 → "데이터 조회" 성격을 시각적으로 전달
- 별도 `Usage Card` (Phase 11 실시간 섹션) 구현 시 해당 카드 옆 또는 아래로 이동 가능

#### Dialog Mount
```kotlin
if (showUsageDashboard) {
    UsageDashboardDialog(
        dashboard = usageDashboard,
        isLoading = usageDashboardLoading,
        onRefresh = { viewModel.requestUsageDashboard() },
        onDismiss = { viewModel.closeUsageDashboard() }
    )
}
```

- `showUsageDashboard` StateFlow를 관찰 → true일 때만 다이얼로그 합성
- 다이얼로그 내부 Refresh/Close가 ViewModel 상태 변경 → 재합성 → 업데이트 반영
- 다이얼로그는 Popup으로 렌더링되므로 Column 레이아웃 공간 점유 없음

## Files Modified / Created
| File | Type | Change |
|------|------|--------|
| `Messages.kt` | Modified | 3개 데이터 클래스 + `GET_USAGE_DASHBOARD` 상수 |
| `MainViewModel.kt` | Modified | 3개 StateFlow + 3개 method + response handler + 3개 parser helper |
| `ManageScreen.kt` | Modified | "View detailed usage" 버튼 + 다이얼로그 마운트 |
| `ui/components/UsageDashboardDialog.kt` | **Created** | 대시보드 다이얼로그 컴포저블 (~270 lines) |

## Protocol
### Request
```json
{
  "type": "command",
  "action": "get_usage_dashboard"
}
```

### Response (expected shape)
```json
{
  "type": "response",
  "action": "get_usage_dashboard",
  "payload": {
    "success": true,
    "dashboard": {
      "contextWindow": {
        "usedText": "142k",
        "totalText": "200k tokens",
        "percentUsed": 71
      },
      "fiveHourLimit": {
        "label": "5-hour limit",
        "percentUsed": 78,
        "resetText": "Resets in 1 hr 26 min"
      },
      "weeklyAllModels": {
        "label": "All models",
        "percentUsed": 53,
        "resetText": "Resets in 21 hr 26 min"
      },
      "weeklySonnetOnly": {
        "label": "Sonnet only",
        "percentUsed": 0,
        "resetText": "Resets Fri 5:00 PM"
      },
      "modelName": "Sonnet 4.5",
      "planName": "Max (5x)",
      "fetchedAt": "2026-04-15T10:00:00Z"
    }
  }
}
```

- 모든 `dashboard.*` 필드 optional — 서버는 플랜/상태에 따라 일부만 채워 반환 가능
- 파싱 시 누락된 필드는 `null`로 처리되고 UI는 해당 섹션을 렌더하지 않음
- 서버가 `payload` 루트에 직접 필드를 넣는 구조도 지원 (`payload.dashboard` 우선, fallback)

## Architecture Flow
```
User taps "View detailed usage"
  → ManageScreen → viewModel.openUsageDashboard()
    → _showUsageDashboard = true + requestUsageDashboard()
      → WebSocketClient.sendCommand(get_usage_dashboard)
        → Server collects Claude Code usage (IPC/scraping)
  → UsageDashboardDialog mounted with isLoading=true, dashboard=null
    → CircularProgressIndicator shown

Server response arrives
  → rawMessages.collect → processMessage → handleResponse(GET_USAGE_DASHBOARD)
    → parseUsageDashboard() → _usageDashboard.value = ...
    → _usageDashboardLoading.value = false
  → Dialog recomposes with DashboardContent
    → Current usage (context window + 5-hour) / Weekly limits / Footer(model, plan) 표시
    → Progress bars 색상 분기 (primary/amber/error)

User taps Refresh
  → viewModel.requestUsageDashboard()
    → _usageDashboardLoading.value = true
  → Dialog footer shows 12dp inline spinner, Refresh 버튼 비활성화
  → 응답 도착 시 갱신

User taps Close or taps outside
  → viewModel.closeUsageDashboard()
    → _showUsageDashboard = false
  → Dialog unmounted; _usageDashboard 값은 유지 (다음 오픈 시 이전 데이터 즉시 표시 + 재로딩)
```

## UX Details
- **첫 오픈**: 센터 스피너만 표시 (데이터 없음)
- **재오픈**: 이전 데이터 즉시 표시 + 백그라운드 리프레시 (footer 인라인 스피너)
- **Refresh 연타 방지**: `enabled = !isLoading`
- **Null 필드 유연 처리**: 서버가 Sonnet 없는 플랜이면 Weekly 섹션에 All models만 표시 등
- **색상 접근성**: 경고(주황)는 #F59E0B (Amber 500, WCAG AA on surface) 사용
- **스크롤 가능**: `heightIn(max = 500.dp)` + `verticalScroll` — 작은 화면에서도 모든 섹션 접근

## Open Items (Phase 11 별개 작업)
- **ManageScreen Usage Card** (실시간 토큰 표시): 별도 작업으로 병행 진행. 완료 시 "View detailed usage" 버튼을 해당 Card 내부 또는 옆으로 재배치 권장.
- **서버 측 구현**: `get_usage_dashboard` 핸들러 — Claude Code의 `/usage` 커맨드 출력 파싱 또는 내부 API 호출 필요.

## Status
Complete (Dashboard Dialog 파트만 해당)
