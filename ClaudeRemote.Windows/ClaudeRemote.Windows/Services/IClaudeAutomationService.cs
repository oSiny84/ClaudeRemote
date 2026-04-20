using ClaudeRemote.Windows.Models;

namespace ClaudeRemote.Windows.Services;

public interface IClaudeAutomationService
{
    bool IsClaudeRunning { get; }
    string CurrentMode { get; }
    string? LastAskUserQuestionPrompt { get; }
    string? LastPermissionPrompt { get; }
    string? LastButtonCategory { get; }
    event EventHandler<string>? OutputChanged;
    event EventHandler<bool>? ClaudeStatusChanged;
    event EventHandler<List<ButtonInfo>>? ActionButtonsChanged;

    Task<bool> FindClaudeWindowAsync();
    Task<string> GetOutputAsync(string scope = "latest");

    // Phase 13: structured chat-message output (user/assistant/tool + content).
    // Android uses this to render chat bubbles instead of parsing a flat string.
    Task<List<ChatMessage>> GetChatMessagesAsync(string scope = "latest");
    Task<bool> SendInputAsync(string text);
    Task<bool> SwitchModeAsync(string targetMode);
    Task<List<SessionInfo>> GetSessionsAsync();
    Task<bool> SelectSessionAsync(string sessionId);
    Task<bool> AddSessionAsync();
    Task<List<ProjectInfo>> GetProjectsAsync();
    Task<bool> SelectProjectAsync(string projectId);
    Task<List<ButtonInfo>> GetActionButtonsAsync();
    Task<bool> ClickButtonAsync(string buttonId);

    // Phase 11: usage observability
    Task<UsageInfo?> GetUsageInfoAsync();
    Task<UsageDashboard?> GetUsageDashboardAsync();

    void StartMonitoring();
    void StopMonitoring();
    void StartProcessWatcher();
    void StopProcessWatcher();
}
