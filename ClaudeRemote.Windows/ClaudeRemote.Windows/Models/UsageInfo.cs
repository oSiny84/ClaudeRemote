namespace ClaudeRemote.Windows.Models;

/// <summary>
/// Lightweight realtime usage scraped from the inline "Usage:" button that
/// Claude Code always shows in the input footer. Example Name:
///
///   "Usage: context 106.1k, plan 49%"
///
/// This is a single UI element read — cheap to call (every poll, heartbeat,
/// or send_input response). For the big picture, see <see cref="UsageDashboard"/>.
/// </summary>
public class UsageInfo
{
    /// <summary>
    /// Raw Name string from the inline button — kept verbatim so Android can
    /// display it if the structured fields fail to parse.
    /// </summary>
    public string? RawText { get; set; }

    /// <summary>Context window usage, e.g. "106.1k" (formatted string).</summary>
    public string? ContextText { get; set; }

    /// <summary>Plan percent (0–100) parsed from the "plan NN%" fragment.</summary>
    public int? PlanPercent { get; set; }

    /// <summary>When this snapshot was captured (server-side UTC).</summary>
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}
