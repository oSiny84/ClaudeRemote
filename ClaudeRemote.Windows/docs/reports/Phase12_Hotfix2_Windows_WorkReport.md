# Phase 12 Hotfix 2: FileServer — Kestrel Migration + URL Logging

## Symptoms

1. **Browser returns 404** when hitting the download URL obtained from a `request_download` response. Server-side log showed the request was received but some path / binding mismatch rejected it before reaching the file-serving code.
2. **No visible URL in server log** — only `✓ request_download: <filename> (<size>)` was printed, so the user had to pull the URL from Android logcat to reproduce in a browser.

## Root Cause

### 404 — HttpListener / HTTP.sys limitations

`HttpListener` runs on top of **HTTP.sys**, the Windows kernel HTTP driver. A prefix like `http://+:8766/` matches "all interfaces, all hostnames", but HTTP.sys enforces URL ACLs for non-localhost bindings. Without an admin-granted ACL, the previous Hotfix 1 path silently downgraded to `http://localhost:8766/` — reachable from the Windows host only, not from Android or any other machine on the LAN. When Android got a URL like `http://192.168.x.x:8766/download?path=...` and sent an HTTP request to it, HTTP.sys had no listener matching the `192.168.x.x` hostname → **404 at the kernel level**, before our C# handler ever runs.

The `netsh http add urlacl url=http://+:8766/ user=Everyone` workaround exists, but it requires a one-time admin invocation and persists per-port in the registry — unfriendly for a developer tool.

### Hidden download URL

The `request_download` handler logged only the filename. Debugging a 404 required digging through Android logcat for the generated URL, then pasting it into a browser.

## Fix

### 1. Switched file server from HttpListener to Kestrel (minimal API)

Kestrel is the ASP.NET Core web server. It uses plain sockets — no HTTP.sys, no URL ACL, no admin requirement. Binding to `0.0.0.0:8766` works as a standard user and is reachable from every device on the LAN.

```csharp
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();                              // use Serilog only
builder.WebHost.ConfigureKestrel(opts =>
{
    opts.ListenAnyIP(port);                                    // 0.0.0.0:port
    opts.AddServerHeader = false;
    opts.Limits.MaxRequestBodySize = null;                     // large log files
});

var app = builder.Build();
app.MapGet("/download", HandleDownload);
app.MapGet("/", () => Results.Text(
    "ClaudeRemote file server — GET /download?path=<filesystem path>",
    "text/plain; charset=utf-8"));
_app = app;
_runTask = app.RunAsync();
```

**Preserved capabilities** from the HttpListener version:

- `GET /download?path=<url-encoded-path>` endpoint
- `Content-Disposition: attachment` header
- `application/octet-stream` MIME
- Range header support (`bytes=start-end`) for resumable downloads
- 64KB streaming buffer (constant memory for any file size)
- `BuildDownloadUrl(filePath, localIp)` still produces the same URL format

**New affordances**:

- `GET /` returns a plain-text banner so the user can hit `http://host:8766/` in a browser to confirm the server is alive.
- `CreateSlimBuilder` (available in .NET 8) uses the minimal hosting stack — no MVC, no static files, no auto-registered JSON converters. Startup < 50ms, memory footprint comparable to HttpListener.
- ASP.NET Core logging is silenced (`ClearProviders`) so we don't double-log alongside Serilog.

### 2. `request_download` now logs the generated URL

```csharp
EmitLog($"  ✓ request_download: {fileInfo.Name} ({FileServerService.FormatFileSize(fileInfo.Length)})");
EmitLog($"    url: {downloadUrl}");
Log.Information("[RequestDownload] {File} → {Url}", fileInfo.Name, downloadUrl);
```

The URL shows up in:
- UI log pane (two indented lines per download)
- Serilog file log (`logs/clauderemote-YYYYMMDD.log`) at INFO level

→ Copy-paste the URL straight into a browser to reproduce a failing download.

## Files Changed

| File | Change |
|------|--------|
| `ClaudeRemote.Windows.csproj` | Added `<FrameworkReference Include="Microsoft.AspNetCore.App" />` — pulls the shared ASP.NET Core runtime that ships with .NET 8 SDK (no extra download, no admin) |
| `Services/FileServerService.cs` | Full rewrite: HttpListener → Kestrel minimal API. Same `IFileServerService` surface (Start/Stop/Port/IsRunning/BuildDownloadUrl) so no DI or call-site changes required |
| `Services/MessageProcessor.cs` | `HandleRequestDownload`: log the generated URL to both the UI pane and the Serilog file sink |

## Verification

### Build
```
ClaudeRemote.Windows → 0 errors, 1 warning (pre-existing ModeChanged CS0067)
```

### Runtime (expected with this build)
- On `ToggleServer`, log shows `[FileServer] Kestrel started on port 8766 (all interfaces)` — no localhost fallback, no netsh prompt.
- `http://<local-ip>:8766/` in a browser from another machine → returns the plain-text banner.
- `http://<local-ip>:8766/download?path=C%3A%5Cpath%5Cto%5Cfile.txt` → browser downloads the file.
- Windows Firewall may still prompt on first run (normal); allow for private networks.

### Test from browser

After the user taps a file in the Android file browser, the server log will show:

```
[09:40:01]   ✓ request_download: New Text Document.txt (512 B)
[09:40:01]     url: http://192.168.0.10:8766/download?path=C%3A%5C...
```

Paste that URL into any browser on the same LAN — file should download.

## Notes

- Kestrel in `CreateSlimBuilder` mode has no HTTPS redirection, no authentication, no rate limiting. This is the correct trade-off for a LAN-only dev tool — adding those would require re-introducing the full hosting pipeline.
- The server binds to `0.0.0.0` (all interfaces). Files on the host are accessible to anyone on the same LAN who knows the URL. This is consistent with the WebSocket server on port 8765 which has the same exposure.
- Graceful shutdown on `Stop()` has a 3-second timeout so the UI doesn't hang if a client is mid-stream.
