namespace ClaudeRemote.Windows.Services;

/// <summary>
/// Phase 12: Lightweight HTTP file server for Android file downloads.
/// Runs on a separate port (WebSocket port + 1) and serves files via
/// GET /download?path=... with Range header support for resume.
/// </summary>
public interface IFileServerService
{
    bool IsRunning { get; }
    int Port { get; }

    void Start(int port = 8766);
    void Stop();

    /// <summary>
    /// Builds an HTTP download URL for the given local file path,
    /// using the host machine's local network IP.
    /// </summary>
    string BuildDownloadUrl(string localFilePath, string localIp);
}
