using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using ClaudeRemote.Windows.Models;
using Serilog;

namespace ClaudeRemote.Windows.Services;

public class MessageProcessor : IMessageProcessor
{
    private readonly IClaudeAutomationService _claudeService;
    private readonly ISessionManager _sessionManager;
    private readonly IWebSocketServerService _webSocketServer;
    private readonly IFileServerService _fileServer;

    public event EventHandler<string>? LogEvent;

    private const int ChunkThresholdBytes = 10 * 1024; // 10KB
    private const int ChunkSizeBytes = 8 * 1024;       // 8KB per chunk

    private void EmitLog(string text) => LogEvent?.Invoke(this, text);

    public MessageProcessor(
        IClaudeAutomationService claudeService,
        ISessionManager sessionManager,
        IWebSocketServerService webSocketServer,
        IFileServerService fileServer)
    {
        _claudeService = claudeService;
        _sessionManager = sessionManager;
        _webSocketServer = webSocketServer;
        _fileServer = fileServer;
    }

    public async Task ProcessIncomingMessageAsync(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ClaudeMessage>(json);
            if (message == null)
            {
                EmitLog("← [parse failed] empty message");
                return;
            }

            Log.Verbose("Processing message: type={Type} action={Action}", message.Type, message.Action);

            if (message.Type != MessageType.Command)
            {
                EmitLog($"← [ignored] type={message.Type}");
                return;
            }

            string response;
            switch (message.Action)
            {
                case MessageAction.SendInput:
                    var text = GetPayloadString(message, "text");
                    var textPreview = text.Length > 60 ? text[..60] + "..." : text;
                    EmitLog($"← send_input: \"{textPreview}\" ({text.Length} chars)");
                    var success = await _claudeService.SendInputAsync(text);
                    EmitLog(success ? "  ✓ send_input OK" : "  ✗ send_input FAILED");
                    response = CreateResponseMessage(message.Action, success,
                        success ? null : ErrorCode.InputFailed);
                    break;

                case MessageAction.GetOutput:
                    var scope = GetPayloadString(message, "scope", "latest");
                    EmitLog($"← get_output (scope={scope})");
                    // Phase 13: structured output — fetch messages then serialize
                    // both the new `messages[]` array AND the legacy `content`
                    // field so older clients keep working. Android uses messages
                    // when present, content as fallback.
                    var chatMessages = await _claudeService.GetChatMessagesAsync(scope);
                    var legacyContent = ClaudeAutomationService.RenderMessagesAsString(chatMessages);
                    var outputMsg = CreateOutputMessage(
                        legacyContent, scope, _sessionManager.CurrentMode, chatMessages);
                    if (System.Text.Encoding.UTF8.GetByteCount(outputMsg) > ChunkThresholdBytes)
                    {
                        EmitLog(
                            $"  ✓ get_output: {chatMessages.Count} msgs / {legacyContent.Length} chars (chunked)");
                        await SendChunkedAsync(legacyContent);
                        return;
                    }
                    EmitLog($"  ✓ get_output: {chatMessages.Count} msgs / {legacyContent.Length} chars");
                    response = outputMsg;
                    break;

                case MessageAction.SwitchMode:
                    EmitLog("← switch_mode (ignored: locked to Code)");
                    Log.Information("switch_mode command ignored: locked to Code mode");
                    response = CreateResponseMessage(message.Action, true, data: new Dictionary<string, object>
                    {
                        ["mode"] = "code"
                    });
                    break;

                case MessageAction.GetSessions:
                    EmitLog("← get_sessions");
                    await _sessionManager.RefreshSessionsAsync();
                    Log.Information("[MessageProcessor] get_sessions: mode={Mode} sessionCount={Count}",
                        _sessionManager.CurrentMode, _sessionManager.Sessions.Count);
                    EmitLog($"  ✓ get_sessions: {_sessionManager.Sessions.Count} sessions");
                    response = CreateSessionListMessage(
                        message.Mode ?? _sessionManager.CurrentMode,
                        _sessionManager.Sessions);
                    break;

                case MessageAction.SelectSession:
                    var sessionId = GetPayloadString(message, "sessionId");
                    EmitLog($"← select_session: {sessionId}");
                    await _sessionManager.SelectSessionAsync(sessionId);
                    EmitLog("  ✓ select_session OK");
                    response = CreateResponseMessage(message.Action, true);
                    break;

                case MessageAction.AddSession:
                    EmitLog("← add_session");
                    await _sessionManager.AddSessionAsync();
                    EmitLog("  ✓ add_session OK");
                    response = CreateResponseMessage(message.Action, true);
                    break;

                case MessageAction.GetProjects:
                    EmitLog("← get_projects");
                    await _sessionManager.RefreshProjectsAsync();
                    EmitLog($"  ✓ get_projects: {_sessionManager.Projects.Count} projects");
                    response = CreateProjectListMessage(_sessionManager.Projects);
                    break;

                case MessageAction.SelectProject:
                    var projectId = GetPayloadString(message, "projectId");
                    EmitLog($"← select_project: {projectId}");
                    // SessionManager.SelectProjectAsync already refreshes both
                    // projects (IsExpanded state) and sessions (expanded project's
                    // session list) after clicking the project label.
                    await _sessionManager.SelectProjectAsync(projectId);
                    var selectedProject = _sessionManager.Projects
                        .FirstOrDefault(p => p.Id == projectId);
                    EmitLog(
                        $"  ✓ select_project OK — projects={_sessionManager.Projects.Count}, " +
                        $"sessions={_sessionManager.Sessions.Count}, " +
                        $"expanded={selectedProject?.IsExpanded.ToString() ?? "?"}");
                    response = CreateSelectProjectResponseMessage(
                        _sessionManager.Projects,
                        _sessionManager.Sessions);
                    break;

                case MessageAction.GetUsage:
                    EmitLog("← get_usage");
                    var usage = await _claudeService.GetUsageInfoAsync();
                    if (usage != null)
                    {
                        EmitLog($"  ✓ get_usage: context={usage.ContextText ?? "?"}, plan={usage.PlanPercent?.ToString() ?? "?"}%");
                        response = CreateUsageInfoMessage(usage);
                    }
                    else
                    {
                        EmitLog("  ✗ get_usage: inline usage button not found");
                        response = CreateResponseMessage(message.Action, false, ErrorCode.ElementNotFound);
                    }
                    break;

                case MessageAction.GetUsageDashboard:
                    EmitLog("← get_usage_dashboard");
                    var dashboard = await _claudeService.GetUsageDashboardAsync();
                    if (dashboard != null)
                    {
                        EmitLog(
                            $"  ✓ get_usage_dashboard: ctx={dashboard.ContextWindow?.PercentUsed?.ToString() ?? "?"}%, " +
                            $"5h={dashboard.FiveHourLimit?.PercentUsed?.ToString() ?? "?"}%, " +
                            $"weekly={dashboard.WeeklyAllModels?.PercentUsed?.ToString() ?? "?"}%, " +
                            $"model={dashboard.ModelName ?? "?"}, plan={dashboard.PlanName ?? "?"}");
                        response = CreateUsageDashboardMessage(dashboard);
                    }
                    else
                    {
                        EmitLog("  ✗ get_usage_dashboard: popup unreachable");
                        response = CreateResponseMessage(message.Action, false, ErrorCode.ElementNotFound);
                    }
                    break;

                case MessageAction.ClickButton:
                    var buttonId = GetPayloadString(message, "buttonId");
                    EmitLog($"← click_button: {buttonId}");
                    var clicked = await _claudeService.ClickButtonAsync(buttonId);
                    if (clicked)
                    {
                        var btns = await _claudeService.GetActionButtonsAsync();
                        if (int.TryParse(buttonId.Replace("btn_", ""), out var idx) && idx < btns.Count)
                        {
                            EmitLog($"  ✓ click_button OK: \"{btns[idx].Text}\"");
                            response = CreateResponseMessage(message.Action, true, data: new Dictionary<string, object>
                            {
                                ["buttonText"] = btns[idx].Text
                            });
                        }
                        else
                        {
                            EmitLog("  ✓ click_button OK");
                            response = CreateResponseMessage(message.Action, true);
                        }
                    }
                    else
                    {
                        EmitLog("  ✗ click_button FAILED (element not found)");
                        response = CreateResponseMessage(message.Action, false, ErrorCode.ElementNotFound);
                    }
                    break;

                // Phase 12: File transfer
                case MessageAction.BrowseFiles:
                    var browsePath = GetPayloadString(message, "path");
                    EmitLog($"← browse_files: {(string.IsNullOrEmpty(browsePath) ? "(drives)" : browsePath)}");
                    response = HandleBrowseFiles(browsePath);
                    break;

                case MessageAction.RequestDownload:
                    var downloadPath = GetPayloadString(message, "path");
                    EmitLog($"← request_download: {downloadPath}");
                    response = HandleRequestDownload(downloadPath);
                    break;

                default:
                    EmitLog($"← [unknown action] {message.Action}");
                    response = CreateResponseMessage(message.Action, false, ErrorCode.InternalError);
                    break;
            }

            await _webSocketServer.SendAsync(response);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing message");
            EmitLog($"  ✗ EXCEPTION: {ex.Message}");
            var errorResponse = CreateResponseMessage("unknown", false, ErrorCode.InternalError);
            await _webSocketServer.SendAsync(errorResponse);
        }
    }

    public string CreateOutputMessage(string content, string scope, string mode,
        List<ChatMessage>? messages = null)
    {
        // Phase 13: payload carries BOTH the structured `messages[]` AND the
        // legacy `content` string. New Android clients render bubbles from
        // `messages`; older clients fall back to `content` (which is the
        // same text in unwrapped-prose form, joined with \n\n).
        var payload = new Dictionary<string, object>
        {
            ["scope"] = scope,
            ["content"] = content,
            ["isStreaming"] = false
        };
        if (messages != null)
        {
            payload["messages"] = messages
                .Select(m => new Dictionary<string, object>
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content
                })
                .ToList();
        }

        var message = new ClaudeMessage
        {
            Type = MessageType.Content,
            Action = scope == "full" ? MessageAction.OutputFull : MessageAction.OutputUpdate,
            Mode = mode,
            Payload = payload
        };
        return JsonSerializer.Serialize(message);
    }

    public string CreateStatusMessage(bool claudeRunning, string currentMode, bool isGenerating)
    {
        var message = new ClaudeMessage
        {
            Type = MessageType.Status,
            Action = MessageAction.ClaudeStatus,
            Payload = new Dictionary<string, object>
            {
                ["claudeRunning"] = claudeRunning,
                ["currentMode"] = currentMode,
                ["isGenerating"] = isGenerating
            }
        };
        return JsonSerializer.Serialize(message);
    }

    public string CreateResponseMessage(string action, bool success, string? error = null, Dictionary<string, object>? data = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["success"] = success
        };
        if (error != null) payload["error"] = error;
        if (data != null)
        {
            foreach (var kvp in data)
                payload[kvp.Key] = kvp.Value;
        }

        var message = new ClaudeMessage
        {
            Type = MessageType.Response,
            Action = action,
            Payload = payload
        };
        return JsonSerializer.Serialize(message);
    }

    public string CreateSessionListMessage(string mode, List<SessionInfo> sessions)
    {
        var message = new ClaudeMessage
        {
            Type = MessageType.Response,
            Action = MessageAction.GetSessions,
            Mode = mode,
            Payload = new Dictionary<string, object>
            {
                ["success"] = true,
                ["sessions"] = sessions.Select(s => new Dictionary<string, object>
                {
                    ["id"] = s.Id,
                    ["name"] = s.Name,
                    ["active"] = s.IsActive,
                    ["lastMessage"] = s.LastMessage
                }).ToList()
            }
        };
        return JsonSerializer.Serialize(message);
    }

    public string CreateProjectListMessage(List<ProjectInfo> projects)
    {
        var message = new ClaudeMessage
        {
            Type = MessageType.Response,
            Action = MessageAction.GetProjects,
            Mode = "code",
            Payload = new Dictionary<string, object>
            {
                ["success"] = true,
                ["projects"] = projects.Select(p => new Dictionary<string, object>
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["path"] = p.Path,
                    ["active"] = p.IsActive,
                    ["expanded"] = p.IsExpanded  // Phase 10: sidebar expand/collapse state
                }).ToList()
            }
        };
        return JsonSerializer.Serialize(message);
    }

    /// <summary>
    /// Phase 10: Response to <c>select_project</c> carries the refreshed project
    /// list (with updated expand/collapse state) AND the session list of whichever
    /// project is now expanded, so Android updates both panels from a single RTT.
    /// </summary>
    public string CreateSelectProjectResponseMessage(
        List<ProjectInfo> projects,
        List<SessionInfo> sessions)
    {
        var message = new ClaudeMessage
        {
            Type = MessageType.Response,
            Action = MessageAction.SelectProject,
            Mode = "code",
            Payload = new Dictionary<string, object>
            {
                ["success"] = true,
                ["projects"] = projects.Select(p => new Dictionary<string, object>
                {
                    ["id"] = p.Id,
                    ["name"] = p.Name,
                    ["path"] = p.Path,
                    ["active"] = p.IsActive,
                    ["expanded"] = p.IsExpanded
                }).ToList(),
                ["sessions"] = sessions.Select(s => new Dictionary<string, object>
                {
                    ["id"] = s.Id,
                    ["name"] = s.Name,
                    ["active"] = s.IsActive,
                    ["lastMessage"] = s.LastMessage
                }).ToList()
            }
        };
        return JsonSerializer.Serialize(message);
    }

    public string CreateUsageInfoMessage(UsageInfo usage)
    {
        var message = new ClaudeMessage
        {
            Type = MessageType.Response,
            Action = MessageAction.GetUsage,
            Mode = "code",
            Payload = new Dictionary<string, object>
            {
                ["success"] = true,
                ["usage"] = new Dictionary<string, object?>
                {
                    ["rawText"] = usage.RawText,
                    ["contextText"] = usage.ContextText,
                    ["planPercent"] = usage.PlanPercent,
                    ["fetchedAt"] = usage.FetchedAt.ToString("o")
                }
            }
        };
        return JsonSerializer.Serialize(message);
    }

    public string CreateUsageDashboardMessage(UsageDashboard dashboard)
    {
        var payload = new Dictionary<string, object?>
        {
            ["contextWindow"] = SerializeContextWindow(dashboard.ContextWindow),
            ["fiveHourLimit"] = SerializeLimit(dashboard.FiveHourLimit),
            ["weeklyAllModels"] = SerializeLimit(dashboard.WeeklyAllModels),
            ["weeklySonnetOnly"] = SerializeLimit(dashboard.WeeklySonnetOnly),
            ["modelName"] = dashboard.ModelName,
            ["planName"] = dashboard.PlanName,
            ["fetchedAt"] = dashboard.FetchedAt.ToString("o")
        };

        var message = new ClaudeMessage
        {
            Type = MessageType.Response,
            Action = MessageAction.GetUsageDashboard,
            Mode = "code",
            Payload = new Dictionary<string, object>
            {
                ["success"] = true,
                ["dashboard"] = payload
            }
        };
        return JsonSerializer.Serialize(message);
    }

    private static Dictionary<string, object?>? SerializeContextWindow(ContextWindow? ctx)
    {
        if (ctx == null) return null;
        return new Dictionary<string, object?>
        {
            ["usedText"] = ctx.UsedText,
            ["totalText"] = ctx.TotalText,
            ["percentUsed"] = ctx.PercentUsed
        };
    }

    private static Dictionary<string, object?>? SerializeLimit(UsageLimit? limit)
    {
        if (limit == null) return null;
        return new Dictionary<string, object?>
        {
            ["label"] = limit.Label,
            ["percentUsed"] = limit.PercentUsed,
            ["resetText"] = limit.ResetText
        };
    }

    public string CreateActionButtonsMessage(List<ButtonInfo> buttons, string? prompt = null, string? category = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["buttons"] = buttons.Select(b => new Dictionary<string, object>
            {
                ["id"] = b.Id,
                ["text"] = b.Text
            }).ToList()
        };
        if (!string.IsNullOrEmpty(prompt))
            payload["prompt"] = prompt;
        if (!string.IsNullOrEmpty(category))
            payload["category"] = category;

        var message = new ClaudeMessage
        {
            Type = MessageType.Content,
            Action = MessageAction.ActionButtons,
            Payload = payload
        };
        return JsonSerializer.Serialize(message);
    }

    // --- Chunk Splitting ---

    public async Task SendChunkedAsync(string content)
    {
        var chunkId = Guid.NewGuid().ToString();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var totalChunks = (int)Math.Ceiling((double)bytes.Length / ChunkSizeBytes);

        Log.Information("Sending chunked output: {TotalBytes} bytes in {Chunks} chunks",
            bytes.Length, totalChunks);

        for (int i = 0; i < totalChunks; i++)
        {
            var offset = i * ChunkSizeBytes;
            var length = Math.Min(ChunkSizeBytes, bytes.Length - offset);
            var chunkData = System.Text.Encoding.UTF8.GetString(bytes, offset, length);

            var chunkMessage = new ClaudeMessage
            {
                Type = MessageType.Content,
                Action = MessageAction.OutputChunk,
                Payload = new Dictionary<string, object>
                {
                    ["chunkId"] = chunkId,
                    ["chunkIndex"] = i,
                    ["totalChunks"] = totalChunks,
                    ["data"] = chunkData
                }
            };

            var json = JsonSerializer.Serialize(chunkMessage);
            await _webSocketServer.SendAsync(json);
        }
    }

    private string GetPayloadString(ClaudeMessage message, string key, string defaultValue = "")
    {
        if (message.Payload == null) return defaultValue;
        if (message.Payload.TryGetValue(key, out var value))
        {
            return value?.ToString() ?? defaultValue;
        }
        return defaultValue;
    }

    // ============================================================
    // Phase 12: File transfer handlers
    // ============================================================

    /// <summary>
    /// Handles <c>browse_files</c>: returns directory entries for the given path,
    /// or a list of available drives when <paramref name="path"/> is empty.
    /// Entries are sorted: directories first, then files, both alphabetical.
    /// </summary>
    private string HandleBrowseFiles(string path)
    {
        try
        {
            // Empty or null → list available drives.
            //
            // IMPORTANT CONTRACT with Android:
            //   - Every entry has a `path` field with the FULL filesystem path.
            //     Android uses this directly for both navigation (on tap) and
            //     download requests. It does NOT concatenate currentPath + name.
            //   - Drives use type="directory" (not "drive"): they are navigable
            //     containers, and Android only branches on "directory" vs "file".
            //     Returning "drive" caused fallthrough to the file-tap handler
            //     which showed a download dialog.
            //   - `name` is a display string (can include volume label); `path`
            //     is the actual filesystem path. Keep them separate.
            if (string.IsNullOrWhiteSpace(path))
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d =>
                    {
                        var label = string.IsNullOrWhiteSpace(d.VolumeLabel)
                            ? DriveTypeLabel(d.DriveType)
                            : d.VolumeLabel;
                        return new Dictionary<string, object>
                        {
                            ["name"] = $"{d.Name.TrimEnd('\\')} ({label})", // display only: "C: (Windows)"
                            ["path"] = d.Name,                              // actual path: "C:\\"
                            ["type"] = "directory",                         // navigable, not downloadable
                            ["size"] = d.TotalSize,
                            ["freeSpace"] = d.AvailableFreeSpace,
                            ["modified"] = ""
                        };
                    })
                    .ToList();

                EmitLog($"  ✓ browse_files: {drives.Count} drives");
                foreach (var d in drives)
                    Log.Debug("[BrowseFiles]   drive: name='{N}' path='{P}' type={T}",
                        d["name"], d["path"], d["type"]);

                return SerializeMessage(new ClaudeMessage
                {
                    Type = MessageType.Response,
                    Action = MessageAction.BrowseFiles,
                    Payload = new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["currentPath"] = "",
                        ["parentPath"] = "",
                        ["entries"] = drives,
                        ["fileServerPort"] = _fileServer.Port
                    }
                });
            }

            // Normalize path
            path = Path.GetFullPath(path);

            if (!Directory.Exists(path))
            {
                EmitLog($"  ✗ browse_files: directory not found");
                return CreateResponseMessage(MessageAction.BrowseFiles, false, "DIRECTORY_NOT_FOUND");
            }

            var entries = new List<Dictionary<string, object>>();

            // Directories first
            try
            {
                foreach (var dir in Directory.GetDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var di = new DirectoryInfo(dir);
                        entries.Add(new Dictionary<string, object>
                        {
                            ["name"] = di.Name,
                            ["path"] = di.FullName,
                            ["type"] = "directory",
                            ["size"] = 0,
                            ["modified"] = di.LastWriteTime.ToString("o")
                        });
                    }
                    catch { /* Skip inaccessible directories */ }
                }
            }
            catch (UnauthorizedAccessException)
            {
                EmitLog($"  ⚠ browse_files: partial access denied for subdirectories");
            }

            // Then files
            try
            {
                foreach (var file in Directory.GetFiles(path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        entries.Add(new Dictionary<string, object>
                        {
                            ["name"] = fi.Name,
                            ["path"] = fi.FullName,
                            ["type"] = "file",
                            ["size"] = fi.Length,
                            ["modified"] = fi.LastWriteTime.ToString("o")
                        });
                    }
                    catch { /* Skip inaccessible files */ }
                }
            }
            catch (UnauthorizedAccessException)
            {
                EmitLog($"  ⚠ browse_files: partial access denied for files");
            }

            var parentPath = Directory.GetParent(path)?.FullName ?? "";

            int dirCount = entries.Count(e => (e["type"] as string) == "directory");
            int fileCount = entries.Count - dirCount;
            EmitLog($"  ✓ browse_files: {dirCount} dirs, {fileCount} files in {Path.GetFileName(path)}");
            Log.Debug("[BrowseFiles] currentPath='{CP}' parentPath='{PP}' dirs={D} files={F}",
                path, parentPath, dirCount, fileCount);

            return SerializeMessage(new ClaudeMessage
            {
                Type = MessageType.Response,
                Action = MessageAction.BrowseFiles,
                Payload = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["currentPath"] = path,
                    ["parentPath"] = parentPath,
                    ["entries"] = entries,
                    ["fileServerPort"] = _fileServer.Port
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[BrowseFiles] Error browsing {Path}", path);
            EmitLog($"  ✗ browse_files: {ex.Message}");
            return CreateResponseMessage(MessageAction.BrowseFiles, false, ErrorCode.InternalError);
        }
    }

    /// <summary>
    /// Human-friendly label fallback when a drive has no volume label.
    /// </summary>
    private static string DriveTypeLabel(DriveType type) => type switch
    {
        DriveType.Fixed => "Local Disk",
        DriveType.Removable => "Removable",
        DriveType.Network => "Network",
        DriveType.CDRom => "CD/DVD",
        DriveType.Ram => "RAM Disk",
        _ => "Drive"
    };

    /// <summary>
    /// Handles <c>request_download</c>: validates the file exists and returns
    /// an HTTP download URL pointing to the <see cref="FileServerService"/>.
    /// </summary>
    private string HandleRequestDownload(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                EmitLog("  ✗ request_download: empty path");
                return CreateResponseMessage(MessageAction.RequestDownload, false, "EMPTY_PATH");
            }

            path = Path.GetFullPath(path);

            if (!File.Exists(path))
            {
                EmitLog("  ✗ request_download: file not found");
                return CreateResponseMessage(MessageAction.RequestDownload, false, "FILE_NOT_FOUND");
            }

            if (!_fileServer.IsRunning)
            {
                EmitLog("  ✗ request_download: file server not running");
                return CreateResponseMessage(MessageAction.RequestDownload, false, "FILE_SERVER_OFFLINE");
            }

            var fileInfo = new FileInfo(path);
            var localIp = GetLocalIpAddress();
            var downloadUrl = _fileServer.BuildDownloadUrl(path, localIp);

            EmitLog($"  ✓ request_download: {fileInfo.Name} ({FileServerService.FormatFileSize(fileInfo.Length)})");
            EmitLog($"    url: {downloadUrl}");
            Log.Information("[RequestDownload] {File} → {Url}", fileInfo.Name, downloadUrl);
            return SerializeMessage(new ClaudeMessage
            {
                Type = MessageType.Response,
                Action = MessageAction.RequestDownload,
                Payload = new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["url"] = downloadUrl,
                    ["fileName"] = fileInfo.Name,
                    ["fileSize"] = fileInfo.Length,
                    ["fileSizeText"] = FileServerService.FormatFileSize(fileInfo.Length)
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RequestDownload] Error for {Path}", path);
            EmitLog($"  ✗ request_download: {ex.Message}");
            return CreateResponseMessage(MessageAction.RequestDownload, false, ErrorCode.InternalError);
        }
    }

    /// <summary>
    /// Detects the local network IP address (same logic as MainViewModel).
    /// </summary>
    private static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
                return endPoint.Address.ToString();
        }
        catch { }

        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    return ip.ToString();
            }
        }
        catch { }

        return "127.0.0.1";
    }

    private static string SerializeMessage(ClaudeMessage message)
        => JsonSerializer.Serialize(message);
}
