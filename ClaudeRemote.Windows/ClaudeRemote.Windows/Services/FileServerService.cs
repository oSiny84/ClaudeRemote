using System.IO;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace ClaudeRemote.Windows.Services;

/// <summary>
/// Phase 12 Hotfix 2: Kestrel-backed HTTP file server.
///
/// Why Kestrel instead of <c>HttpListener</c>:
///   - HttpListener sits on top of HTTP.sys (Windows kernel driver). Non-localhost
///     prefixes like <c>http://+:8766/</c> require a URL ACL that can only be
///     granted via admin (`netsh http add urlacl`). Without it, HttpListener
///     crashes or falls back to localhost-only and is unreachable from Android.
///   - Kestrel uses plain sockets and needs no ACL. Binding to <c>0.0.0.0:8766</c>
///     works out of the box for any non-admin user.
///
/// Endpoint:
///   GET /download?path=&lt;url-encoded filesystem path&gt;
///     → streams file with Content-Disposition: attachment
///     → supports Range requests for resumable downloads
///     → 64KB stream buffer — constant memory even for GB files
/// </summary>
public class FileServerService : IFileServerService, IDisposable
{
    private WebApplication? _app;
    private Task? _runTask;

    private const int BufferSize = 64 * 1024;

    public bool IsRunning => _app != null;
    public int Port { get; private set; }

    public void Start(int port = 8766)
    {
        try
        {
            if (IsRunning) return;
            Port = port;

            // CreateSlimBuilder = minimal hosting, no static-files middleware,
            // no JSON converters registered — just what we need for one route.
            // Disables ASP.NET Core's own console/file loggers so we don't
            // double-log with Serilog.
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            builder.WebHost.ConfigureKestrel(opts =>
            {
                opts.ListenAnyIP(port); // 0.0.0.0:port — reachable from LAN
                opts.AddServerHeader = false;
                // Allow unbounded download size (some log files are hundreds of MB)
                opts.Limits.MaxRequestBodySize = null;
            });

            var app = builder.Build();
            app.MapGet("/download", HandleDownload);

            // Health check so the user can quickly verify the server is up
            app.MapGet("/", () =>
                Results.Text(
                    $"ClaudeRemote file server — GET /download?path=<filesystem path>",
                    "text/plain; charset=utf-8"));

            _app = app;
            _runTask = app.RunAsync();
            Log.Information("[FileServer] Kestrel started on port {Port} (all interfaces)", port);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FileServer] Kestrel start failed on port {Port}", port);
            _app = null;
            _runTask = null;
        }
    }

    public void Stop()
    {
        try
        {
            var app = _app;
            _app = null;
            if (app == null) return;

            // StopAsync fires graceful shutdown; fire-and-forget with timeout
            // so we don't block the UI thread if a download is mid-stream.
            _ = app.StopAsync(TimeSpan.FromSeconds(3))
                .ContinueWith(_ => app.DisposeAsync().AsTask());
            _runTask = null;
            Log.Information("[FileServer] Kestrel stopping");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[FileServer] Stop error");
        }
    }

    public string BuildDownloadUrl(string localFilePath, string localIp)
    {
        var encoded = HttpUtility.UrlEncode(localFilePath);
        return $"http://{localIp}:{Port}/download?path={encoded}";
    }

    public void Dispose() => Stop();

    // ================================================================
    // Download endpoint
    // ================================================================

    private static async Task HandleDownload(HttpContext ctx)
    {
        var path = ctx.Request.Query["path"].ToString();
        if (string.IsNullOrEmpty(path))
        {
            await WriteError(ctx, StatusCodes.Status400BadRequest,
                "Missing 'path' query parameter");
            return;
        }

        // ASP.NET Core auto-decodes query string values, but if a client
        // double-encoded we decode once more defensively.
        if (path.Contains('%'))
        {
            try { path = HttpUtility.UrlDecode(path); } catch { /* keep original */ }
        }

        if (!File.Exists(path))
        {
            await WriteError(ctx, StatusCodes.Status404NotFound,
                $"File not found: {path}");
            return;
        }

        FileInfo fileInfo;
        try { fileInfo = new FileInfo(path); }
        catch (Exception ex)
        {
            await WriteError(ctx, StatusCodes.Status500InternalServerError,
                $"FileInfo error: {ex.Message}");
            return;
        }

        var totalLength = fileInfo.Length;
        var fileName = fileInfo.Name;

        Log.Information("[FileServer] Download request: {File} ({Size})",
            fileName, FormatFileSize(totalLength));

        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.Headers["Content-Disposition"] =
            $"attachment; filename=\"{SanitizeFileName(fileName)}\"";
        ctx.Response.Headers["Accept-Ranges"] = "bytes";

        // Range support for resume
        long rangeStart = 0;
        long rangeEnd = totalLength - 1;
        var rangeHeader = ctx.Request.Headers["Range"].ToString();
        if (!string.IsNullOrEmpty(rangeHeader) &&
            rangeHeader.StartsWith("bytes=", StringComparison.Ordinal))
        {
            var spec = rangeHeader["bytes=".Length..];
            var parts = spec.Split('-');
            if (parts.Length >= 1 && long.TryParse(parts[0], out var start))
            {
                rangeStart = start;
                if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]) &&
                    long.TryParse(parts[1], out var end))
                {
                    rangeEnd = Math.Min(end, totalLength - 1);
                }
            }

            if (rangeStart >= totalLength || rangeStart > rangeEnd)
            {
                ctx.Response.StatusCode = StatusCodes.Status416RequestedRangeNotSatisfiable;
                ctx.Response.Headers["Content-Range"] = $"bytes */{totalLength}";
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
            ctx.Response.Headers["Content-Range"] =
                $"bytes {rangeStart}-{rangeEnd}/{totalLength}";
            ctx.Response.ContentLength = rangeEnd - rangeStart + 1;
        }
        else
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentLength = totalLength;
        }

        try
        {
            await using var fs = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            if (rangeStart > 0)
                fs.Seek(rangeStart, SeekOrigin.Begin);

            var buffer = new byte[BufferSize];
            long remaining = rangeEnd - rangeStart + 1;

            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ctx.RequestAborted);
                if (read == 0) break;
                await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                remaining -= read;
            }
            Log.Information("[FileServer] Served {File} ({Size}, range={Start}-{End})",
                fileName, FormatFileSize(totalLength), rangeStart, rangeEnd);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-download — normal for cancelled transfers
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[FileServer] Stream error for {File}", fileName);
        }
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static async Task WriteError(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync(message);
    }

    private static string SanitizeFileName(string name) =>
        name.Replace("\"", "'").Replace("\r", "").Replace("\n", "");

    public static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024L => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
