namespace ClaudeRemote.Windows.Services;

public interface IMessageProcessor
{
    /// <summary>
    /// Fired for high-level log messages intended for the UI log pane (e.g. "→ send_input: ..." / "✓ send_input OK").
    /// </summary>
    event EventHandler<string>? LogEvent;

    Task ProcessIncomingMessageAsync(string json);
    string CreateOutputMessage(string content, string scope, string mode);
    string CreateStatusMessage(bool claudeRunning, string currentMode, bool isGenerating);
    string CreateResponseMessage(string action, bool success, string? error = null, Dictionary<string, object>? data = null);
    string CreateSessionListMessage(string mode, List<Models.SessionInfo> sessions);
    string CreateProjectListMessage(List<Models.ProjectInfo> projects);
    string CreateActionButtonsMessage(List<Models.ButtonInfo> buttons, string? prompt = null, string? category = null);
    Task SendChunkedAsync(string content);
}
