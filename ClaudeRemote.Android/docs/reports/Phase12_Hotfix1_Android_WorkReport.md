# Phase 12 Hotfix 1: File Browser — Drive Entry Bug Fix

## Overview
파일 브라우저 최상위(드라이브 목록)에서 `C:`, `D:` 항목을 탭하면 디렉토리로 진입해야 하는데 다운로드 확인 다이얼로그가 뜨던 버그 수정.

## Date
2026-04-17

## Bug Reproduction
1. ManageScreen에서 "Browse files" 탭
2. 최상위(드라이브 리스트) 표시
3. `C:` 또는 `D:` 항목 탭
4. **기대**: 해당 드라이브 루트로 진입 (`C:\` 내용 표시)
5. **실제**: "Download C: (0 B)?" 다운로드 확인 다이얼로그 표시

## Root Cause 가설
### (a) `type` 필드 불일치
서버가 드라이브 항목에 `type="directory"`가 아닌 다른 값(`"drive"`, `"dir"`, `"folder"` 등)을 보내서 클라이언트의 `entry.type == "directory"` 비교가 실패.

### (b) 경로 정규화 누락
드라이브명 `"C:"`만 서버에 전송하면 Windows는 "현재 프로세스의 `C:` 드라이브 작업 디렉토리"로 해석 → 서버가 디렉토리 리스팅 실패 → 하위 항목 안 뜸. 드라이브 루트로 진입하려면 `"C:\"`처럼 백슬래시가 필요.

두 가설 모두 가능성이 있어 양쪽 모두 대응.

## Changes

### 1. `FileBrowserDialog.kt` — 유연한 타입 판정

```kotlin
private val DRIVE_REGEX = Regex("^[A-Za-z]:\\\\?$")   // "C:" 또는 "C:\"

private fun FileEntry.isDirectoryLike(): Boolean {
    val t = type.lowercase()
    return t == "directory" || t == "dir" || t == "drive" || t == "folder"
        || DRIVE_REGEX.matches(name)
}
```

- 서버가 어떤 type 문자열을 보내든 디렉토리 성격이면 디렉토리로 취급
- 최후의 방어선: name이 드라이브 패턴이면 type과 무관하게 디렉토리
- 탭 처리/아이콘 선택 모두 이 함수 사용 → 단일 진실 원천

탭 핸들러 수정:
```kotlin
onClick = {
    val isDir = entry.isDirectoryLike()
    Log.d(TAG,
        "tap: name='${entry.name}' type='${entry.type}' " +
            "size=${entry.size} isDir=$isDir currentPath='$currentPath'"
    )
    if (isDir) onBrowseDirectory(entry.name)
    else fileToDownload = entry
}
```

`fileIconFor(name, type)` → `fileIconFor(name, isDirectory: Boolean)`로 시그니처 변경 — 호출자(`FileEntryRow`)가 이미 `isDirectoryLike()` 판정한 결과를 그대로 전달.

### 2. `MainViewModel.kt` — 드라이브 경로 정규화

```kotlin
private val DRIVE_LETTER_REGEX = Regex("^[A-Za-z]:$")

fun browseDirectory(entryName: String) {
    val current = _currentPath.value
    // "C:" → "C:\" 변환. Windows는 "C:"를 드라이브 루트로 해석하지 않고
    // 해당 드라이브의 현재 디렉토리(cwd)로 해석하므로 반드시 trailing \ 필요.
    val normalizedName = if (DRIVE_LETTER_REGEX.matches(entryName)) {
        "$entryName\\"
    } else {
        entryName
    }
    val fullPath = when {
        current.isEmpty() -> normalizedName
        current.endsWith("\\") -> "$current$normalizedName"
        else -> "$current\\$normalizedName"
    }
    Log.d(TAG_FILE,
        "browseDirectory: entry='$entryName' normalized='$normalizedName' " +
            "current='$current' → fullPath='$fullPath'"
    )
    browseFiles(fullPath)
}
```

- `entryName == "C:"` → `"C:\"`로 변환 후 경로 결합
- 일반 디렉토리명(예: `"bin"`)은 변환 없이 그대로 사용

### 3. 진단 로그 (Log.d) 일괄 추가

| 위치 | 로그 내용 | Tag |
|------|-----------|-----|
| `FileBrowserDialog.onClick` | 탭한 항목의 `name`, `type`, `size`, `isDir`, `currentPath` | `FileBrowser` |
| `ViewModel.browseFiles` | 서버로 보내는 최종 `path` | `FileBrowserVM` |
| `ViewModel.browseDirectory` | `entry`, `normalized`, `current`, `fullPath` | `FileBrowserVM` |
| `ViewModel.browseParent` | `parentPath` 값 | `FileBrowserVM` |
| `ViewModel.requestDownload` | `entry`, `fullPath` | `FileBrowserVM` |
| `BROWSE_FILES` 응답 핸들러 | `path`, `parent`, `entries 수`, 최초 5개 항목의 `name:type` | `FileBrowserVM` |
| `BROWSE_FILES` 실패 | 서버 error 메시지 (`Log.w`) | `FileBrowserVM` |

로그캣 필터링: `adb logcat -s FileBrowser FileBrowserVM` 로 파일 브라우저만 필터 가능.

## Verification Flow
`adb logcat -s FileBrowser FileBrowserVM`로 스트리밍하면서 앱에서 "Browse files" → `C:` 탭:

### 정상 동작 예상 로그 순서
```
FileBrowserVM  browseFiles → sending path=''
FileBrowserVM  browse_files response: path='' parent='null' entries=2
                 types=[C::drive, D::drive]
FileBrowser    tap: name='C:' type='drive' size=0 isDir=true currentPath=''
FileBrowserVM  browseDirectory: entry='C:' normalized='C:\' current='' → fullPath='C:\'
FileBrowserVM  browseFiles → sending path='C:\'
FileBrowserVM  browse_files response: path='C:\' parent='' entries=N
                 types=[Windows:directory, Users:directory, ...]
```

만약 `tap` 로그의 `type`이 예상과 다르거나 `isDir=false`이면 `isDirectoryLike()`의 허용 목록 추가 판단 근거로 사용.

## Files Modified
| File | Change |
|------|--------|
| `ui/components/FileBrowserDialog.kt` | `isDirectoryLike()` 확장 함수, `DRIVE_REGEX`, 탭 로그, `fileIconFor` 시그니처 변경 |
| `viewmodel/MainViewModel.kt` | 드라이브 경로 정규화, 6개 지점 Log.d/Log.w, companion object (`TAG_FILE`, `DRIVE_LETTER_REGEX`) |

## Build
- `./gradlew assembleDebug` — **BUILD SUCCESSFUL in 51s**
- APK: `app/build/outputs/apk/debug/app-debug.apk` (17 MB, 2026-04-17 12:35)
- 에러 0, deprecation 경고 5개 (모두 `AutoMirrored` 아이콘 권장 — 기능 무관)

## Status
Complete — 빌드 산출물 배포 준비 완료. 실기기에서 logcat으로 드라이브 `type` 값 확인 후, 필요 시 `isDirectoryLike()`에 새 키워드 추가하는 것으로 2차 대응 가능.
