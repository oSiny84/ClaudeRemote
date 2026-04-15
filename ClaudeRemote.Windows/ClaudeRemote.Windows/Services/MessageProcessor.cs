using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeRemote.Windows.Models;
using Serilog;

namespace ClaudeRemote.Windows.Services;

public class MessageProcessor : IMessageProcessor
{
    private readonly IClaudeAutomationService _claudeService;
    private readonly ISessionManager _sessionManager;
    private readonly IWebSocketServerService _webSocketServer;

    public event EventHandler<string>? LogEvent;

    private const int ChunkThresholdBytes = 10 * 1024; // 10KB
    private const int ChunkSizeBytes = 8 * 1024;       // 8KB per chunk

    private void EmitLog(string text) => LogEvent?.Invoke(this, text);

    public MessageProcessor(
        IClaudeAutomationService claudeService,
        ISessionManager sessionManager,
        IWebSocketServerService webSocketServer)
    {
        _claudeService = claudeService;
        _sessionManager = sessionManager;
        _webSocketServer = webSocketServer;
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

            Log.Debug("Processing message: type={Type} action={Action}", message.Type, message.Action);

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
                    var output = await _claudeService.GetOutputAsync(scope);
                    var outputMsg = CreateOutputMessage(output, scope, _sessionManager.CurrentMode);
                    if (System.Text.Encoding.UTF8.GetByteCount(outputMsg) > ChunkThresholdBytes)
                    {
                        EmitLog($"  ✓ get_output: {output.Length} chars (chunked)");
                        await SendChunkedAsync(output);
                        return;
                    }
                    EmitLog($"  ✓ get_output: {output.Length} chars");
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
                    await _sessionManager.SelectProjectAsync(projectId);
                    EmitLog("  ✓ select_project OK");
                    response = CreateResponseMessage(message.Action, true);
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

    public string CreateOutputMessage(string content, string scope, string mode)
    {
        var message = new ClaudeMessage
        {
            Type = MessageType.Content,
            Action = scope == "full" ? MessageAction.OutputFull : MessageAction.OutputUpdate,
            Mode = mode,
            Payload = new Dictionary<string, object>
            {
                ["scope"] = scope,
                ["content"] = content,
                ["isStreaming"] = false
            }
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
                    ["active"] = p.IsActive
                }).ToList()
            }
        };
        return JsonSerializer.Serialize(message);
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
}
