# Phase 10: Folder-Tree Project UI - Work Report

## Overview
Claude Code의 프로젝트 UI가 폴더 트리 구조로 변경됨에 따라 Android UI 개선.
- 프로젝트 펼침/접힘 상태 시각화
- 프로젝트 선택 시 세션 자동 갱신
- Sessions 섹션을 선택된 프로젝트 컨텍스트로 정리

## Date
2026-04-15

## Problems Addressed
1. **프로젝트 열림/닫힘 상태 불명확** — 기존 UI에서는 어떤 프로젝트가 현재 활성화되어 있는지 확인 어려움
2. **세션 목록 자동 갱신 누락** — 프로젝트 선택 후 세션 목록이 갱신되지 않음

## Changes

### 1. Data Model (`Messages.kt`)
`ProjectInfo`에 `expanded: Boolean` 필드 추가:
```kotlin
@Serializable
data class ProjectInfo(
    val id: String,
    val name: String,
    val path: String = "",
    val active: Boolean = false,
    val expanded: Boolean = false  // 신규
)
```
`expanded`는 서버가 알려주는 프로젝트의 펼침 상태로, `active`(현재 활성)와 독립적으로 사용 가능.

### 2. MainViewModel.kt

#### `selectProject()` 개선
- **Optimistic UI 토글**: 클릭한 프로젝트의 `expanded`를 반전, 다른 프로젝트는 `active=false` 처리
- **자동 세션 갱신**: `requestSessions()` 자동 호출로 클라이언트 측 갱신 보장
```kotlin
fun selectProject(projectId: String) {
    webSocketClient.sendCommand(
        action = MessageAction.SELECT_PROJECT,
        mode = "code",
        payload = mapOf("projectId" to projectId)
    )
    _projects.value = _projects.value.map { project ->
        if (project.id == projectId) {
            project.copy(active = true, expanded = !project.expanded)
        } else {
            project.copy(active = false)
        }
    }
    requestSessions()
}
```

#### `handleResponse` GET_PROJECTS 파서 확장
서버 응답에서 `expanded` 필드 파싱 추가:
```kotlin
ProjectInfo(
    id = obj["id"]?.jsonPrimitive?.content ?: "",
    name = obj["name"]?.jsonPrimitive?.content ?: "",
    path = obj["path"]?.jsonPrimitive?.content ?: "",
    active = obj["active"]?.jsonPrimitive?.boolean ?: false,
    expanded = obj["expanded"]?.jsonPrimitive?.boolean ?: false
)
```

### 3. ManageScreen.kt — 전면 개편

#### 레이아웃 재정렬
- **기존**: Sessions (상단) → Projects (하단)
- **신규**: Projects (상단) → Sessions (하단)
- 이유: 새 로직에서 Sessions는 Projects 선택에 의존. 상→하 흐름이 사용자의 시선 이동과 일치 (Projects 탭 → Sessions 확인).

#### ProjectCard UI
- **접힌 프로젝트**: `Icons.Default.Folder` + `KeyboardArrowRight` (오른쪽), 표준 배경
- **펼쳐진 프로젝트**: `Icons.Default.FolderOpen` + `KeyboardArrowDown` (하단)
  - 배경: `primaryContainer`
  - 보더: `BorderStroke(2.dp, primary)`
  - 이름: `FontWeight.Bold` + `primary` 색상
  - 폴더 아이콘: `primary` 틴트
- 탭 → `selectProject()` 호출 → `expanded` 토글

#### Sessions 섹션 조건부 표시
`projects.firstOrNull { it.expanded }`로 "포커스된" 프로젝트 판별:
- **헤더**:
  - 펼쳐진 프로젝트 없음 → "Sessions"
  - 펼쳐진 프로젝트 있음 → "Sessions - {프로젝트명}"
- **콘텐츠**:
  - 펼쳐진 프로젝트 없음 → `EmptyStateBox("Select a project to view sessions")`
  - 펼쳐진 프로젝트 있음, 세션 비어있음 → "No sessions in this project."
  - 펼쳐진 프로젝트 있음, 세션 존재 → `LazyColumn` with `SessionCard`
- **헤더 액션 버튼** (Refresh, Add):
  - `enabled = expandedProject != null`
  - 프로젝트 펼침 전에는 비활성화 → 잘못된 컨텍스트에서 세션 조작 방지

## Files Modified
| File | Change |
|------|--------|
| `Messages.kt` | `ProjectInfo.expanded` 필드 추가 |
| `MainViewModel.kt` | `selectProject()` 개선, `GET_PROJECTS` 파서 확장 |
| `ManageScreen.kt` | 섹션 순서 재정렬, ProjectCard 펼침 UI, Sessions 조건부 표시 |

## UX Flow (Before → After)

### Before
```
┌──────────────────────┐
│ Sessions             │
│ [7 sessions shown    │
│  — which project?]   │
│                      │
│ Projects             │
│ [proj A] [proj B]... │
│ (no expand state)    │
└──────────────────────┘
```
- 세션이 어떤 프로젝트의 것인지 불명확
- 프로젝트 상태 구분 불가
- 프로젝트 선택 후 수동 Refresh 필요

### After
```
┌──────────────────────┐
│ Projects             │
│ [📁 proj A  ▶]      │
│ [📂 proj B  ▼]      │← 강조
│ [📁 proj C  ▶]      │
│                      │
│ Sessions - proj B    │← 헤더에 프로젝트명
│ [session 1]          │
│ [session 2]          │
└──────────────────────┘
```
- 펼쳐진 프로젝트 즉시 구분 (아이콘 + 배경 + 보더 + 이름 굵기)
- Sessions 헤더에 컨텍스트 명시
- 클릭 → 자동 세션 갱신
- 프로젝트 미선택 시 명확한 안내

## Protocol Compatibility
- Server → Client: `get_projects` 응답에 `expanded` 필드 포함 (서버에서 제공해야 함)
- `expanded` 미포함 시 기본값 `false`로 fallback → 하위 호환성 유지
- `select_project` 명령은 변경 없음 (기존 프로토콜 그대로)

## Status
Complete
