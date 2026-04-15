namespace ClaudeRemote.Windows.Models;

public class SessionInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string LastMessage { get; set; } = string.Empty;
}
