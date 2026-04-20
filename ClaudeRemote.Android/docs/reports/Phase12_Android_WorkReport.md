# Phase 12: Remote File Browser + Download - Work Report

## Overview
Windows PC의 파일 시스템을 Android에서 원격 탐색하고 파일을 HTTP로 다운로드하는 기능 구현.

## Date
2026-04-16

## Architecture
| Operation | Transport | Mechanism |
|-----------|-----------|-----------|
| File browsing | WebSocket | `browse_files` command/response |
| Download request | WebSocket | `request_download` → server returns HTTP URL |
| File transfer | HTTP GET | Android `DownloadManager` (별도 포트, 서버 제공 URL) |

- 파일 목록 탐색은 기존 WebSocket 채널 활용 (지연 최소, 양방향)
- 실제 파일 전송은 HTTP로 분리 (대용량 바이너리에 적합, DownloadManager가 중단/재시도/알림 처리)

## Changes

### 1. Data Model (`Messages.kt`)

```kotlin
data class FileEntry(
    val name: String,
    val type: String,   // "file" or "directory"
    val size: Long = 0,
    val modified: String? = null
)
```

- `@Serializable` 미적용 — ViewModel에서 수동 파싱 (일관된 패턴)
- `size: Long` — 대용량 파일 지원 (2GB+ 가능)
- `modified` — 선택 필드 (서버가 제공하면 표시, 현재 UI에서는 미사용)

`MessageAction` 상수:
```kotlin
const val BROWSE_FILES = "browse_files"
const val REQUEST_DOWNLOAD = "request_download"
```

### 2. MainViewModel.kt

#### State Flows (5개)
```kotlin
_fileEntries: List<FileEntry>     // 현재 디렉토리의 파일/폴더 목록
_currentPath: String              // 현재 탐색 경로 (서버가 반환)
_parentPath: String?              // 상위 경로 (null이면 최상위/드라이브)
_showFileBrowser: Boolean         // BottomSheet 표시 여부
_fileBrowserLoading: Boolean      // 탐색 중 로딩 상태
```

#### Methods (6개)
```kotlin
openFileBrowser()                 // show=true + browseFiles("")
closeFileBrowser()                // show=false
browseFiles(path: String)         // WebSocket browse_files 전송
browseDirectory(entryName: String)// currentPath + entryName 조합 → browseFiles
browseParent()                    // parentPath로 browseFiles
requestDownload(entryName: String)// currentPath + entryName → request_download 전송
```

**Path 구성 로직** (`browseDirectory` / `requestDownload` 공유):
```kotlin
val fullPath = when {
    current.isEmpty() -> entryName              // 드라이브 목록에서 선택
    current.endsWith("\\") -> "$current$entryName"  // 드라이브 루트 (C:\)
    else -> "$current\\$entryName"              // 일반 경로
}
```

Windows 백슬래시(`\`) 기반 — 서버가 Windows이므로 서버 네이티브 경로 사용.

#### Download 실행 (`startDownload`)
```kotlin
private fun startDownload(downloadUrl: String, fileName: String) {
    val dm = context.getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager
    val request = DownloadManager.Request(Uri.parse(downloadUrl)).apply {
        setDestinationInExternalPublicDir(DIRECTORY_DOWNLOADS, fileName)
        setNotificationVisibility(VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
        setTitle(fileName)
        setDescription("ClaudeRemote file download")
    }
    dm.enqueue(request)
    _snackbarEvent.tryEmit("Downloading $fileName...")
}
```

- `DownloadManager` 시스템 서비스 활용 → 백그라운드 다운로드 + 진행률 알림 + 완료 알림
- `VISIBILITY_VISIBLE_NOTIFY_COMPLETED` → 진행 중 + 완료 후 알림 표시
- `DIRECTORY_DOWNLOADS` → 공용 Downloads 디렉토리 (API 29+ 스코프드 스토리지 호환)
- 에러 처리: try-catch → 실패 시 snackbar

#### Response Handlers
- **`BROWSE_FILES`**: `_currentPath`, `_parentPath`, `_fileEntries` 갱신
- **`REQUEST_DOWNLOAD`**: `downloadUrl` + `fileName` 수신 → `startDownload()` 호출

### 3. FileBrowserDialog (`ui/components/FileBrowserDialog.kt`) — 신규

`ModalBottomSheet` 기반 (85% 높이, skipPartiallyExpanded).

#### 구조
```
┌─ Files ─────────────────────────────┐
│ 📁 Files                            │← 헤더
│ ┌ C:\D_Develop\output ──────────┐   │← 현재 경로 (Surface)
│ └───────────────────────────────┘   │
│ [← Back]                            │← parentPath 있을 때만
│ ─────────────────────────────────── │
│ 📁 bin/                              │← 폴더 → browseDirectory
│ 📁 obj/                              │
│ 📄 app-debug.apk     16.3 MB    ⬇  │← 파일 → 확인 다이얼로그
│ 📄 build.log          4.4 KB    ⬇  │
│ ─────────────────────────────────── │
│              [Close]                 │
└──────────────────────────────────────┘
```

#### 파일 아이콘 분기 (`fileIconFor`)
```kotlin
"directory"  → Icons.Default.Folder
"apk"        → Icons.Default.Android
"zip/rar/7z" → Icons.Default.Archive
"jpg/png/gif"→ Icons.Default.Image
"mp4/avi/mkv"→ Icons.Default.VideoFile
"mp3/wav"    → Icons.Default.AudioFile
"pdf"        → Icons.Default.PictureAsPdf
else         → Icons.Default.InsertDriveFile
```

#### 파일 크기 포맷팅 (`formatFileSize`)
```kotlin
fun formatFileSize(bytes: Long): String = when {
    bytes >= 1 GB → "X.X GB"
    bytes >= 1 MB → "X.X MB"
    bytes >= 1 KB → "X.X KB"
    else          → "X B"
}
```

#### 인터랙션
- **폴더 탭** → `onBrowseDirectory(entry.name)` → 하위 디렉토리 탐색
- **파일 탭** → `AlertDialog` 확인 ("Download {name} ({size})?")
  - 확인 → `onDownload(entry.name)` → `requestDownload`
  - 취소 → 다이얼로그 닫기
- **Back** → `onBrowseParent()` → 상위 디렉토리 (parentPath null이면 비표시)
- **Close** → `onDismiss()` → 시트 닫기
- **로딩 중** → 중앙 `CircularProgressIndicator`
- **빈 디렉토리** → "Empty directory" 표시

### 4. ManageScreen.kt — Integration

#### "Browse files" 버튼
"View detailed usage" 아래에 OutlinedButton 추가:
```kotlin
OutlinedButton(onClick = { viewModel.openFileBrowser() }) {
    Icon(Icons.Default.FolderOpen, ...)
    Text("Browse files")
}
```

#### Dialog Mount
```kotlin
if (showFileBrowser) {
    FileBrowserDialog(
        currentPath, parentPath, entries, isLoading,
        onBrowseDirectory, onBrowseParent, onDownload, onDismiss
    )
}
```

## Files Modified / Created
| File | Type | Change |
|------|------|--------|
| `Messages.kt` | Modified | `FileEntry` 데이터 클래스 + `BROWSE_FILES`/`REQUEST_DOWNLOAD` 상수 |
| `MainViewModel.kt` | Modified | 5 StateFlow + 6 method + 2 response handler + `startDownload` |
| `ui/components/FileBrowserDialog.kt` | **Created** | ModalBottomSheet 파일 탐색 UI (~270 lines) |
| `ManageScreen.kt` | Modified | "Browse files" 버튼 + FileBrowserDialog 마운트 |

## Protocol

### Browse Files
**Request:**
```json
{ "type": "command", "action": "browse_files",
  "payload": { "path": "C:\\D_Develop" } }
```
**Response:**
```json
{ "type": "response", "action": "browse_files",
  "payload": {
    "success": true,
    "path": "C:\\D_Develop",
    "parentPath": "C:\\",
    "entries": [
      { "name": "output", "type": "directory", "size": 0 },
      { "name": "app.apk", "type": "file", "size": 17104896, "modified": "2026-04-15" }
    ]
  }
}
```

### Request Download
**Request:**
```json
{ "type": "command", "action": "request_download",
  "payload": { "path": "C:\\D_Develop\\output\\app.apk" } }
```
**Response:**
```json
{ "type": "response", "action": "request_download",
  "payload": {
    "success": true,
    "downloadUrl": "http://192.168.0.1:8766/download/C%3A%5CD_Develop%5Coutput%5Capp.apk",
    "fileName": "app.apk"
  }
}
```

## Architecture Flow
```
[ManageScreen] "Browse files" 버튼
  → openFileBrowser()
    → showFileBrowser = true
    → browseFiles("") ───WebSocket──→ Server (drive listing)
                                        ↓
  ← _fileEntries update ←──────────── Response (entries[])
    → FileBrowserDialog renders

[FileBrowserDialog] 폴더 탭
  → browseDirectory("output")
    → browseFiles("C:\\D_Develop\\output") ──→ Server
                                                ↓
  ← _fileEntries update ←───────────────── Response

[FileBrowserDialog] 파일 탭
  → AlertDialog "Download app.apk (16.3 MB)?"
    → OK → requestDownload("app.apk")
      → request_download ──WebSocket──→ Server
                                          ↓ starts HTTP server
      ← downloadUrl ←───────────────── Response
        → DownloadManager.enqueue(url)
          → HTTP GET ──────────────→ Server:8766
          → 시스템 다운로드 알림 + 진행률
          → 완료 알림 → Downloads 디렉토리에 저장
```

## Server-side Requirements
1. **`browse_files` handler**: 경로 파싱, `Directory.GetFiles()` / `Directory.GetDirectories()`, parentPath 계산
2. **`request_download` handler**: 경로 검증, HTTP 파일 서버 URL 생성 (별도 포트)
3. **HTTP file server**: `HttpListener` 또는 Kestrel 기반, 파일 스트리밍 + Content-Disposition 헤더

## Status
Complete
