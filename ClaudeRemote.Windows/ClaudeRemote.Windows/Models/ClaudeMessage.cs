using System.Text.Json.Serialization;

namespace ClaudeRemote.Windows.Models;

public class ClaudeMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("payload")]
    public Dictionary<string, object>? Payload { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
}

public static class MessageType
{
    public const string Command = "command";
    public const string Response = "response";
    public const string Content = "content";
    public const string Status = "status";
}

public static class MessageAction
{
    public const string SendInput = "send_input";
    public const string GetOutput = "get_output";
    public const string SwitchMode = "switch_mode";
    public const string GetSessions = "get_sessions";
    public const string SelectSession = "select_session";
    public const string AddSession = "add_session";
    public const string GetProjects = "get_projects";
    public const string SelectProject = "select_project";
    public const string OutputUpdate = "output_update";
    public const string OutputFull = "output_full";
    public const string OutputChunk = "output_chunk";
    public const string ActionButtons = "action_buttons";
    public const string ClickButton = "click_button";
    public const string ClaudeStatus = "claude_status";
    public const string Heartbeat = "heartbeat";

    // Phase 11: usage observability
    public const string GetUsage = "get_usage";                       // Lightweight inline button scrape
    public const string GetUsageDashboard = "get_usage_dashboard";    // Open panel → scrape → close

    // Phase 12: file transfer
    public const string BrowseFiles = "browse_files";                 // Directory listing via WebSocket
    public const string RequestDownload = "request_download";         // Get HTTP download URL for a file
}

public static class ErrorCode
{
    public const string ClaudeNotFound = "CLAUDE_NOT_FOUND";
    public const string ClaudeNotResponding = "CLAUDE_NOT_RESPONDING";
    public const string ElementNotFound = "ELEMENT_NOT_FOUND";
    public const string ModeSwitchFailed = "MODE_SWITCH_FAILED";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string ProjectNotFound = "PROJECT_NOT_FOUND";
    public const string InputFailed = "INPUT_FAILED";
    public const string Timeout = "TIMEOUT";
    public const string InternalError = "INTERNAL_ERROR";
}
