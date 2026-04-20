namespace ClaudeRemote.Windows.Models;

/// <summary>
/// Comprehensive usage data scraped from Claude Code's bottom-right usage
/// popup (the small popover that opens when you click the
/// "Usage: context …, plan …%" button next to the model selector at the
/// bottom-right of the chat input row).
///
/// This is the <b>heavy</b> snapshot — calling it briefly opens and
/// closes the popup, so it should be user-triggered (pull-to-refresh /
/// explicit dashboard tap), not polled.
/// For the always-available compact data, use <see cref="UsageInfo"/>
/// (parsed from the same button's accessibility Name without clicking it).
/// </summary>
public class UsageDashboard
{
    /// <summary>Context window usage block ("514.6k / 1.0M (51%)").</summary>
    public ContextWindow? ContextWindow { get; set; }

    /// <summary>"5-hour limit: 18% · resets 4h"</summary>
    public UsageLimit? FiveHourLimit { get; set; }

    /// <summary>"Weekly · all models: 56% · resets 19h"</summary>
    public UsageLimit? WeeklyAllModels { get; set; }

    /// <summary>"Sonnet only: 0% · resets 2d"</summary>
    public UsageLimit? WeeklySonnetOnly { get; set; }

    /// <summary>Model name shown in the footer line, e.g. "Opus 4.6 1M".</summary>
    public string? ModelName { get; set; }

    /// <summary>Plan name shown in the footer line, e.g. "Max".</summary>
    public string? PlanName { get; set; }

    /// <summary>When this snapshot was captured (server-side UTC).</summary>
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Context-window usage block. All textual values are preserved verbatim
/// so Android can render Claude's wording without re-formatting.
/// </summary>
public class ContextWindow
{
    /// <summary>Used token budget as text, e.g. "514.6k".</summary>
    public string? UsedText { get; set; }

    /// <summary>Total token budget as text, e.g. "1.0M".</summary>
    public string? TotalText { get; set; }

    /// <summary>Parsed percent (0–100), e.g. 51. Null if not parseable.</summary>
    public int? PercentUsed { get; set; }
}

/// <summary>
/// One limit bucket — 5-hour / weekly all models / weekly sonnet only.
/// </summary>
public class UsageLimit
{
    /// <summary>Section label, e.g. "5-hour limit", "Weekly · all models", "Sonnet only".</summary>
    public string? Label { get; set; }

    /// <summary>Parsed "NN%" → integer percent (0–100). Null if not parseable.</summary>
    public int? PercentUsed { get; set; }

    /// <summary>
    /// Raw reset text including the "resets" prefix as shown in the popup,
    /// e.g. "resets 4h" / "resets 19h" / "resets 2d".
    /// Kept verbatim — Android renders as-is.
    /// </summary>
    public string? ResetText { get; set; }
}
