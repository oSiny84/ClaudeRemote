using ClaudeRemote.Windows.Models;

namespace ClaudeRemote.Windows.Services;

public interface ISessionManager
{
    string CurrentMode { get; }
    List<SessionInfo> Sessions { get; }
    List<ProjectInfo> Projects { get; }
    event EventHandler<string>? ModeChanged;
    event EventHandler<List<SessionInfo>>? SessionsUpdated;
    event EventHandler<List<ProjectInfo>>? ProjectsUpdated;

    Task SwitchModeAsync(string mode);
    Task RefreshSessionsAsync();
    Task RefreshProjectsAsync();
    Task SelectSessionAsync(string sessionId);
    Task AddSessionAsync();
    Task SelectProjectAsync(string projectId);
}
