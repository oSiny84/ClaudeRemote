# Phase 12: File Transfer — Windows HTTP File Server

## Summary

Added HTTP file server to ClaudeRemote Windows host, enabling Android clients to remotely browse directories and download files from the Windows PC over the local network.

## Architecture

```
Android                          Windows Host
  |                                |
  |--- browse_files (WS) -------->| WebSocket (8765)
  |<-- entries + fileServerPort ---|
  |                                |
  |--- request_download (WS) ---->| WebSocket (8765)
  |<-- download URL --------------|
  |                                |
  |--- GET /download?path=... --->| HTTP File Server (8766)
  |<-- file stream (Range OK) ----|
```

- **WebSocket (8765)**: message-only, handles `browse_files` and `request_download`
- **HTTP Server (8766)**: binary file streaming with Range header support

## New Files

| File | Description |
|------|-------------|
| `Services/IFileServerService.cs` | Interface: Start/Stop/Port/IsRunning/BuildDownloadUrl |
| `Services/FileServerService.cs` | HttpListener-based HTTP file server |

## Modified Files

| File | Changes |
|------|---------|
| `Models/ClaudeMessage.cs` | Added `MessageAction.BrowseFiles`, `MessageAction.RequestDownload` |
| `Services/MessageProcessor.cs` | Added `IFileServerService` dependency, `HandleBrowseFiles()`, `HandleRequestDownload()` handlers |
| `Services/IMessageProcessor.cs` | (no changes — handlers are internal) |
| `App.xaml.cs` | Registered `IFileServerService` as singleton |
| `ViewModels/MainViewModel.cs` | Added `IFileServerService` injection, Start/Stop lifecycle alongside WebSocket |

## Protocol

### browse_files

**Request** (empty path = drive list):
```json
{
  "type": "command",
  "action": "browse_files",
  "payload": { "path": "" }
}
```

**Response (drives)**:
```json
{
  "type": "response",
  "action": "browse_files",
  "payload": {
    "success": true,
    "currentPath": "",
    "parentPath": "",
    "entries": [
      { "name": "C:\\ (Windows)", "type": "drive", "size": 512110190592, "freeSpace": 102400000000, "modified": "" }
    ],
    "fileServerPort": 8766
  }
}
```

**Response (directory)**:
```json
{
  "type": "response",
  "action": "browse_files",
  "payload": {
    "success": true,
    "currentPath": "C:\\D_Develop\\output",
    "parentPath": "C:\\D_Develop",
    "entries": [
      { "name": "bin", "type": "directory", "size": 0, "modified": "2026-04-15T10:00:00" },
      { "name": "app-debug.apk", "type": "file", "size": 17100294, "modified": "2026-04-14T13:16:00" }
    ],
    "fileServerPort": 8766
  }
}
```

### request_download

**Request**:
```json
{
  "type": "command",
  "action": "request_download",
  "payload": { "path": "C:\\D_Develop\\output\\app-debug.apk" }
}
```

**Response**:
```json
{
  "type": "response",
  "action": "request_download",
  "payload": {
    "success": true,
    "url": "http://192.168.0.10:8766/download?path=C%3A%5CD_Develop%5Coutput%5Capp-debug.apk",
    "fileName": "app-debug.apk",
    "fileSize": 17100294,
    "fileSizeText": "16.3 MB"
  }
}
```

## FileServerService Features

- **HttpListener** on port 8766 (WebSocket port + 1)
- Single endpoint: `GET /download?path=<url-encoded-path>`
- `Content-Disposition: attachment` for safe browser downloads
- **Range header support** (`bytes=start-end`) for resumable downloads
- 64KB streaming buffer for low memory footprint
- Graceful fallback if admin prefix binding fails (tries localhost + wildcard)
- `FormatFileSize()` utility: bytes to KB/MB/GB

## Lifecycle

- File server starts/stops in lockstep with WebSocket server via `ToggleServer()`
- File server port = WebSocket port + 1 (configurable)
- `IDisposable` cleanup on app exit

## Sorting Convention

Directory entries are sorted: **directories first, then files**, both alphabetical (case-insensitive). Inaccessible entries are silently skipped.

## Error Handling

| Error Code | Condition |
|---|---|
| `DIRECTORY_NOT_FOUND` | browse_files: path doesn't exist |
| `EMPTY_PATH` | request_download: no path provided |
| `FILE_NOT_FOUND` | request_download: file doesn't exist |
| `FILE_SERVER_OFFLINE` | request_download: HTTP server not running |
| `INTERNAL_ERROR` | Unexpected exception |
| HTTP 400 | Missing path query parameter |
| HTTP 404 | File not found / wrong endpoint |
| HTTP 416 | Range not satisfiable |
| HTTP 500 | Internal server error |
