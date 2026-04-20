# Phase 12 Hotfix 1: File Browser — Drive Entry Bug

## Symptom

Tapping a drive entry (e.g., `C:`) in the Android file browser showed a **download confirmation dialog** instead of navigating INTO the drive.

## Root Cause

Two defects in the `browse_files` response shape produced by `HandleBrowseFiles` in `MessageProcessor.cs`:

### 1. Drive entries used `type: "drive"`

The Android client only branches on `"directory"` (tap → navigate) vs `"file"` (tap → download). An unknown value like `"drive"` fell through to the default branch, which the client treats as a downloadable file. Hence the download dialog.

### 2. No explicit `path` field

Entries carried only a `name` field. The Android client had to reconstruct a full path by concatenating `currentPath + name`. For drives, `currentPath` was empty AND `name` was a display string like `"C: (Windows)"` — not a valid filesystem path. Even if Android re-mapped `"drive"` to `"directory"`, the follow-up `browse_files` request would send `"C: (Windows)"` as the path, fail directory existence checks, and silently return nothing.

## Fix

### `Services/MessageProcessor.cs :: HandleBrowseFiles`

**Drive entries**:

```csharp
new Dictionary<string, object>
{
    ["name"] = $"{d.Name.TrimEnd('\\')} ({label})",  // display: "C: (Windows)"
    ["path"] = d.Name,                                 // real path: "C:\\"
    ["type"] = "directory",                            // navigable (was "drive")
    ["size"] = d.TotalSize,
    ["freeSpace"] = d.AvailableFreeSpace,
    ["modified"] = ""
}
```

**Directory entries** — added explicit `path`:

```csharp
["name"] = di.Name,
["path"] = di.FullName,       // NEW
["type"] = "directory",
["size"] = 0,
["modified"] = di.LastWriteTime.ToString("o")
```

**File entries** — added explicit `path`:

```csharp
["name"] = fi.Name,
["path"] = fi.FullName,       // NEW
["type"] = "file",
["size"] = fi.Length,
["modified"] = fi.LastWriteTime.ToString("o")
```

**Protocol contract documented** in the handler header so future changes preserve the invariant:

- Every entry carries `path` with the full filesystem path.
- Android uses `path` directly for navigation and download requests — no string concatenation.
- `type` is only `"directory"` or `"file"`.
- `name` is a display string; `path` is the real path.

### Volume label fallback

Drives with empty `VolumeLabel` now get a human-readable label based on drive type:

```csharp
private static string DriveTypeLabel(DriveType type) => type switch
{
    DriveType.Fixed => "Local Disk",
    DriveType.Removable => "Removable",
    DriveType.Network => "Network",
    DriveType.CDRom => "CD/DVD",
    DriveType.Ram => "RAM Disk",
    _ => "Drive"
};
```

### Diagnostic logging

Debug-level logging of each entry on `browse_files` response so the exact `name`/`path`/`type` values sent to Android are visible in `logs/clauderemote-*.log`:

```csharp
Log.Debug("[BrowseFiles]   drive: name='{N}' path='{P}' type={T}",
    d["name"], d["path"], d["type"]);
Log.Debug("[BrowseFiles] currentPath='{CP}' parentPath='{PP}' dirs={D} files={F}",
    path, parentPath, dirCount, fileCount);
```

User-facing log line now splits the count into directories vs files for faster triage:

```
✓ browse_files: 3 dirs, 17 files in output
```

## Response Format (reference)

```json
{
  "type": "response",
  "action": "browse_files",
  "payload": {
    "success": true,
    "currentPath": "",
    "parentPath": "",
    "entries": [
      {
        "name": "C: (Windows)",
        "path": "C:\\",
        "type": "directory",
        "size": 512110190592,
        "freeSpace": 102400000000,
        "modified": ""
      }
    ],
    "fileServerPort": 8766
  }
}
```

```json
{
  "entries": [
    {
      "name": "bin",
      "path": "C:\\D_Develop\\output\\bin",
      "type": "directory",
      "size": 0,
      "modified": "2026-04-15T10:00:00"
    },
    {
      "name": "app-debug.apk",
      "path": "C:\\D_Develop\\output\\app-debug.apk",
      "type": "file",
      "size": 17100294,
      "modified": "2026-04-14T13:16:00"
    }
  ]
}
```

## Android-side expectation

The Android client should:
1. Use `entry.path` (NOT `currentPath + entry.name`) when the user taps an entry.
2. Branch on `entry.type`:
   - `"directory"` → send `browse_files` with `{ path: entry.path }`.
   - `"file"` → send `request_download` with `{ path: entry.path }`.

No Android code changes are required by this hotfix if the client was already reading `path` (or once updated to do so).

## Files Changed

| File | Change |
|------|--------|
| `Services/MessageProcessor.cs` | `HandleBrowseFiles`: drive `type` → "directory", added `path` to every entry, added `DriveTypeLabel` helper, added debug logging |

## Build

```
Build succeeded. 0 Error(s), 1 Warning(s) (pre-existing ModeChanged CS0067).
```
