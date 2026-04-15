using System.Windows.Automation;

namespace ClaudeRemote.Windows.Models;

public class ButtonInfo
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Reference to the UIAutomation element for clicking. Not serialized.
    /// </summary>
    public AutomationElement? Element { get; set; }
}
