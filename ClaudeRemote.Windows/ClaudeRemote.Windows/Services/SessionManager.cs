using ClaudeRemote.Windows.Models;
using Serilog;

namespace ClaudeRemote.Windows.Services;

public class SessionManager : ISessionManager
{
    private readonly IClaudeAutomationService _claudeService;

    public string CurrentMode { get; private set; } = "code";
    public List<SessionInfo> Sessions { get; private set; } = new();
    public List<ProjectInfo> Projects { get; private set; } = new();

    public event EventHandler<string>? ModeChanged;
    public event EventHandler<List<SessionInfo>>? SessionsUpdated;
    public event EventHandler<List<ProjectInfo>>? ProjectsUpdated;

    public SessionManager(IClaudeAutomationService claudeService)
    {
        _claudeService = claudeService;
    }

    public Task SwitchModeAsync(string mode)
    {
        // Code mode only - ignore non-code requests
        if (!string.Equals(mode, "code", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("SwitchModeAsync ignored: only Code mode is supported (requested: {Mode})", mode);
            return Task.CompletedTask;
        }
        return Task.CompletedTask;
    }

    public async Task RefreshSessionsAsync()
    {
        Sessions = await _claudeService.GetSessionsAsync();
        SessionsUpdated?.Invoke(this, Sessions);
    }

    public async Task RefreshProjectsAsync()
    {
        Projects = await _claudeService.GetProjectsAsync();
        ProjectsUpdated?.Invoke(this, Projects);
    }

    public async Task SelectSessionAsync(string sessionId)
    {
        if (await _claudeService.SelectSessionAsync(sessionId))
        {
            await RefreshSessionsAsync();
        }
    }

    public async Task AddSessionAsync()
    {
        if (await _claudeService.AddSessionAsync())
        {
            await RefreshSessionsAsync();
        }
    }

    public async Task SelectProjectAsync(string projectId)
    {
        if (await _claudeService.SelectProjectAsync(projectId))
        {
            // v2 UI: toggling a project label expands/collapses its session list.
            // Refresh projects (to pick up IsExpanded) and sessions (new expanded set)
            // so Android receives both lists in a single response.
            await RefreshProjectsAsync();
            await RefreshSessionsAsync();
        }
    }
}
