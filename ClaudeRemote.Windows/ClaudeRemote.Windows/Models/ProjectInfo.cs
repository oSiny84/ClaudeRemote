namespace ClaudeRemote.Windows.Models;

public class ProjectInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsActive { get; set; }

    /// <summary>
    /// Whether the project node is currently expanded in the sidebar (sessions visible).
    /// Detected via ExpandCollapsePattern when available, falling back to structural check
    /// (presence of session rows between this project label and the next).
    /// </summary>
    public bool IsExpanded { get; set; }
}
