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
    Task<bool> SendInputAsync(string text);
    Task<bool> SwitchModeAsync(string targetMode);
    Task<List<SessionInfo>> GetSessionsAsync();
    Task<bool> SelectSessionAsync(string sessionId);
    Task<bool> AddSessionAsync();
    Task<List<ProjectInfo>> GetProjectsAsync();
    Task<bool> SelectProjectAsync(string projectId);
    Task<List<ButtonInfo>> GetActionButtonsAsync();
    Task<bool> ClickButtonAsync(string buttonId);
    void StartMonitoring();
    void StopMonitoring();
    void StartProcessWatcher();
    void StopProcessWatcher();
}
