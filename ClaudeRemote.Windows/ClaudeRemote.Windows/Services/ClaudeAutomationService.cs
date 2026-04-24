using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using ClaudeRemote.Windows.Models;
using Serilog;

namespace ClaudeRemote.Windows.Services;

/// <summary>
/// UIAutomation-based service for controlling the Claude desktop app (Electron/Chromium).
/// UI element mapping is based on docs/ClaudeUI_Map.md (v2 for Claude app 1.2581.0+).
///
/// Phase 9 rewrite principles:
/// - NO cached element references (AutomationIds are volatile; positions shift)
/// - Name-based and class-prefix-based lookups only
/// - Container-based detection for approval/selection cards (no blacklist approach)
/// </summary>
public class ClaudeAutomationService : IClaudeAutomationService, IDisposable
{
    private AutomationElement? _claudeWindow;
    private AutomationElement? _rootDocument;
    private CancellationTokenSource? _monitoringCts;
    private CancellationTokenSource? _processWatcherCts;
    private string _lastOutputHash = string.Empty;
    private readonly int _pollingIntervalMs = 500;
    private const int ProcessCheckIntervalMs = 5000;
    private const int MaxSessionsToCollect = 10;
    private const int ScrollPassCount = 3; // Max scroll attempts to find more sessions
    private bool _wasClaudeRunning;

    // Process name for Claude desktop app
    private const string ClaudeProcessName = "claude";

    public bool IsClaudeRunning => _claudeWindow != null && IsWindowValid();
    public string CurrentMode { get; private set; } = "chat";
    public bool IsGenerating { get; private set; }
    public string? LastAskUserQuestionPrompt { get; private set; }
    public string? LastPermissionPrompt { get; private set; }
    public string? LastButtonCategory { get; private set; }

    // Cached button list for change detection in monitor loop
    private List<string> _lastButtonTexts = new();

    // ------------------------------------------------------------
    // UIA serialization gate
    // ------------------------------------------------------------
    // UIAutomation does cross-process COM calls into the Claude renderer.
    // When multiple tree walks run concurrently (MonitorLoop + several
    // incoming commands), they serialize at the RPC level and each walk
    // slows from <500ms to tens of seconds. A single SemaphoreSlim that
    // both the monitor loop and every public command takes eliminates the
    // contention — commands queue cleanly behind each other and the
    // monitor loop politely waits its turn.
    private readonly SemaphoreSlim _uiaGate = new(1, 1);
    private const int UiaGateWaitMs = 30000;

    // Phase 13: one-shot chat-structure diagnostic flag.
    // First GetOutputAsync call dumps the chat tree so we can design the
    // message-boundary detector against the real UIA shape, then never
    // fires again for the process lifetime.
    private bool _chatStructureDumped = false;

    // ------------------------------------------------------------
    // UI element markers (Claude Desktop v2 — 1.2581.0+)
    // ------------------------------------------------------------

    // Class prefixes / names for structural lookup
    private const string DFrameSidebarClass = "dframe-sidebar";
    private const string DFrameMainAid = "dframe-main";
    private const string PrimaryPaneName = "Primary pane";
    private const string DFrameNavScrollClass = "dframe-nav-scroll";
    private const string ModeGroupName = "Mode";
    private const string ModePillsClass = "df-pills";
    private const string EpitaxyPromptClass = "epitaxy-prompt";
    private const string InputFieldName = "Prompt"; // tiptap ProseMirror
    private const string StopButtonName = "Stop"; // streaming state indicator (v1: Interrupt)
    private const string OutputScrollClass = "h-full overflow-y-auto overflow-x-hidden"; // content area (pre-1.3883)
    private const string ChatColumnClass = "relative epitaxy-chat-column"; // chat column (1.3883+). Distinct from the prompt column which is "epitaxy-chat-column epitaxy-chat-size ...".
    private const string NewSessionPrefix = "New session"; // "New session ⌘N"
    private const string SessionRowClassPrefix = "group relative rounded-"; // session wrapper (pre-1.3883)
    private const string SessionRowDragWrapperPrefix = "relative df-drag-shiftable"; // session wrapper (1.3883+)
    private const string ProjectLabelClassPrefix = "group/label "; // project/section labels in sidebar

    // Approval card (both Permission and AskUserQuestion use this class)
    // Differentiation: presence of "outline-none" class → AskUserQuestion
    private const string ApprovalCardClassMarker = "epitaxy-approval-card";
    private const string AskUserOutlineMarker = "outline-none";

    // Permission button name prefixes (after shortcut suffix is stripped)
    private static readonly string[] PermissionButtonPrefixes = new[]
    {
        "Deny", "Allow once", "Always allow"
    };

    // Shortcut suffixes on permission buttons (new v2 format first, legacy fallback after)
    // Order matters: longer suffixes must be checked first.
    private static readonly string[] PermissionShortcutSuffixes = new[]
    {
        " ⌘⇧⏎", " ⌘⏎", " esc",
        // Legacy v1 formats (fallback if Claude reverts)
        " Ctrl Enter", " Enter", " Esc"
    };

    // AskUserQuestion fixed button names (excluded from option list)
    // Note: "Other N" buttons vary by index ("Other 5", "Other 3") — handled by prefix check
    private static readonly HashSet<string> AskUserFixedButtonNames = new(StringComparer.Ordinal)
    {
        "Skip", "Submit", "Submit ⏎"
    };

    // Sidebar nav buttons to skip when looking for sessions (positive filter —
    // sessions are detected by their group wrapper class, not by blacklist)
    private static readonly HashSet<string> SidebarNavLabels = new(StringComparer.Ordinal)
    {
        "New session", "Routines", "Customize", "More navigation items",
        "Pinned", "Filter", "osiny", "Appearance"
    };

    public event EventHandler<string>? OutputChanged;
    public event EventHandler<bool>? ClaudeStatusChanged;
    public event EventHandler<List<ButtonInfo>>? ActionButtonsChanged;

    // ============================================================
    // Core: Window discovery
    // ============================================================

    public async Task<bool> FindClaudeWindowAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                var processes = Process.GetProcessesByName(ClaudeProcessName);
                if (processes.Length == 0)
                {
                    Log.Warning("Claude process not found");
                    SetDisconnected();
                    return false;
                }

                // Find the process with a visible main window.
                // Pick the one with largest working set (main renderer), following UITreeDumper's strategy.
                Process? mainProcess = null;
                long maxWs = 0;
                foreach (var p in processes)
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.WorkingSet64 > maxWs)
                    {
                        mainProcess = p;
                        maxWs = p.WorkingSet64;
                    }
                }

                if (mainProcess == null)
                {
                    Log.Warning("Claude process found but no visible window");
                    SetDisconnected();
                    return false;
                }

                _claudeWindow = AutomationElement.FromHandle(mainProcess.MainWindowHandle);
                var className = _claudeWindow.Current.ClassName;
                Log.Information("Claude window found: Title='{Title}' Class='{Class}' PID={Pid}",
                    _claudeWindow.Current.Name, className, mainProcess.Id);

                if (className != "Chrome_WidgetWin_1")
                    Log.Warning("Unexpected window class: {Class}", className);

                // Find the RootWebArea Document Name="Claude"
                _rootDocument = FindRootDocument();
                if (_rootDocument == null)
                {
                    Log.Warning("Main document (RootWebArea) not found");
                    SetDisconnected();
                    return false;
                }

                ClaudeStatusChanged?.Invoke(this, true);
                Log.Information("Claude main document located successfully");

                // Auto-switch to Code mode (async so window lookup doesn't block)
                if (!string.Equals(CurrentMode, "code", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Auto-switching to Code mode (current: {Mode})", CurrentMode);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        await SwitchModeAsync("code");
                    });
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to find Claude window");
                SetDisconnected();
                return false;
            }
        });
    }

    // ============================================================
    // Output extraction
    // ============================================================

    public async Task<string> GetOutputAsync(string scope = "latest")
    {
        // Phase 13: legacy single-string path is now a thin wrapper over the
        // structured extractor — callers get the same unwrapped-prose output
        // the chat bubbles see (paragraph-per-line, blank line between).
        var messages = await GetChatMessagesAsync(scope);
        return RenderMessagesAsString(messages);
    }

    public async Task<List<ChatMessage>> GetChatMessagesAsync(string scope = "latest")
    {
        return await WithUiaGateAsync($"GetChatMessages({scope})", async () =>
        {
            if (!await EnsureConnected()) return new List<ChatMessage>();

            return await Task.Run(() =>
            {
                try
                {
                    var primaryPane = FindPrimaryPane();
                    if (primaryPane == null)
                    {
                        Log.Debug("Primary pane not found");
                        return new List<ChatMessage>();
                    }

                    // Find the message list container.
                    // 1.3883+ layout nests messages under "relative epitaxy-chat-column"
                    // → (inner "absolute top-0 left-0 w-full") → [message children].
                    // The OLD "h-full overflow-y-auto overflow-x-hidden" Group still
                    // exists in the new layout as an ANCESTOR of epitaxy-chat-column,
                    // so matching it first and walking its direct children would hit
                    // the chat-column wrapper (one child only) — everything falls into
                    // the "unknown top-level" branch and gets flattened via
                    // CollectInlineContent. Prefer the chat-column anchor.
                    AutomationElement? scrollContainer = null;
                    var chatColumn = FindByClassPrefixRaw(primaryPane, ChatColumnClass);
                    if (chatColumn != null)
                    {
                        // Descend one level to the actual message list container.
                        var inner = RawWalker.GetFirstChild(chatColumn);
                        scrollContainer = inner ?? chatColumn;
                    }
                    else
                    {
                        // Legacy (pre-1.3883) layout.
                        scrollContainer = FindByClassPrefixRaw(primaryPane, OutputScrollClass);
                    }
                    var searchRoot = scrollContainer ?? primaryPane;

                    // Phase 13 research hook: dump chat structure once per session so
                    // new Claude-Desktop layouts can be re-analyzed quickly.
                    if (!_chatStructureDumped)
                    {
                        _chatStructureDumped = true;
                        try { DumpChatStructureDiagnostic(searchRoot); }
                        catch (Exception dx) { Log.Warning(dx, "[ChatDump] failed (non-fatal)"); }
                    }

                    // Walk the scroll container subtree and build a structured
                    // message list. Uses the same cached-subtree strategy as
                    // CollectTextsCached — one UIA RPC for the whole subtree,
                    // then all per-element property reads hit the local cache.
                    var swCollect = Stopwatch.StartNew();
                    var all = CollectChatMessages(searchRoot);
                    swCollect.Stop();
                    if (swCollect.ElapsedMilliseconds > 1500)
                        Log.Information("[GetChatMessages] took {Ms}ms ({N} msgs)",
                            swCollect.ElapsedMilliseconds, all.Count);

                    // Detect streaming state (Stop button in prompt box)
                    DetectStreamingState();

                    return SliceByScope(all, scope);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to get chat messages");
                    return new List<ChatMessage>();
                }
            });
        }, new List<ChatMessage>());
    }

    // ============================================================
    // Input sending
    // ============================================================

    public async Task<bool> SendInputAsync(string text)
    {
        return await WithUiaGateAsync("SendInput", async () =>
        {
            if (!await EnsureConnected()) return false;

            return await WithWindowRestoredAsync(() => Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    // With the UIA gate the prior operation has fully released,
                    // but the Claude renderer may still be processing the last
                    // click/navigation event. A short settle is enough.
                    Thread.Sleep(150);

                    // Always fresh lookup — no caching. Shallow walk because
                    // the prompt field is always in the lower half of the
                    // primary pane and never buried deep.
                    var swFind = Stopwatch.StartNew();
                    var inputField = FindPromptField();
                    swFind.Stop();
                    if (swFind.ElapsedMilliseconds > 1500)
                        Log.Information("[SendInput] FindPromptField took {Ms}ms", swFind.ElapsedMilliseconds);

                    bool useSubmitButton = false;

                    if (inputField == null)
                    {
                        // Fallback: AskUserQuestion "Other option" Edit field
                        // appears when user clicks "Other". Bounded scan so a
                        // missing field fails fast instead of walking the full
                        // tree looking for something that isn't there.
                        Log.Information("[SendInput] Prompt field not found — searching for 'Other option' Edit");
                        var swOther = Stopwatch.StartNew();
                        var primaryPane = FindPrimaryPane();
                        if (primaryPane != null)
                        {
                            inputField = FindEditByName(primaryPane, "Other option", maxDepth: 20, maxNodes: 2000);
                            if (inputField != null)
                            {
                                useSubmitButton = true;
                                Log.Information("[SendInput] Found 'Other option' Edit — will use Submit button");
                            }
                        }
                        swOther.Stop();

                        if (inputField == null)
                        {
                            Log.Warning("[SendInput] No input field found at all (find={Find}ms, other={Other}ms)",
                                swFind.ElapsedMilliseconds, swOther.ElapsedMilliseconds);
                            return false;
                        }
                    }
                    else
                    {
                        Log.Information("[SendInput] Prompt field found: Name='{Name}' ({Ms}ms)",
                            inputField.Current.Name, swFind.ElapsedMilliseconds);
                    }

                    // Focus the input
                    try
                    {
                        inputField.SetFocus();
                        Thread.Sleep(150);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[SendInput] SetFocus failed, attempting click fallback");
                        try
                        {
                            ClickElement(inputField);
                            Thread.Sleep(150);
                        }
                        catch (Exception ex2)
                        {
                            Log.Error(ex2, "[SendInput] Click fallback also failed");
                            return false;
                        }
                    }

                    // Use clipboard for reliable Unicode/Korean input
                    var thread = new Thread(() => System.Windows.Forms.Clipboard.SetText(text));
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    thread.Join();
                    Log.Verbose("[SendInput] Clipboard set: {Len} chars", text.Length);

                    System.Windows.Forms.SendKeys.SendWait("^a");
                    Thread.Sleep(50);
                    System.Windows.Forms.SendKeys.SendWait("^v");
                    Thread.Sleep(100);

                    if (useSubmitButton)
                    {
                        // Click Submit button (AskUserQuestion Other flow)
                        Thread.Sleep(100);
                        var primaryPane2 = FindPrimaryPane();
                        var submitBtn = primaryPane2 != null
                            ? (FindButtonByNamePrefixRaw(primaryPane2, "Submit") ?? FindButtonByNameRaw(primaryPane2, "Submit"))
                            : null;
                        if (submitBtn != null)
                        {
                            ClickElement(submitBtn);
                            Log.Information("[SendInput] Clicked Submit button");
                        }
                        else
                        {
                            Log.Warning("[SendInput] Submit button not found, falling back to Enter key");
                            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
                        }
                    }
                    else
                    {
                        // Standard prompt: press Enter
                        System.Windows.Forms.SendKeys.SendWait("{ENTER}");
                    }

                    Log.Information("[SendInput] Input sent to Claude: {TextLength} chars ({TotalMs}ms)",
                        text.Length, sw.ElapsedMilliseconds);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[SendInput] Failed to send input to Claude");
                    return false;
                }
            }));
        }, false);
    }

    // ============================================================
    // Mode switching (Chat / Cowork / Code)
    // ============================================================

    public async Task<bool> SwitchModeAsync(string targetMode)
    {
        return await WithUiaGateAsync($"SwitchMode({targetMode})", async () =>
        {
            if (!await EnsureConnected()) return false;

            return await WithWindowRestoredAsync(() => Task.Run(() =>
            {
                try
                {
                    if (_claudeWindow == null) return false;

                // v2: Mode buttons are plain Buttons inside Group Name="Mode" Class="df-pills"
                var sidebar = FindSidebar();
                if (sidebar == null)
                {
                    Log.Warning("[SwitchMode] Sidebar not found");
                    return false;
                }

                var modeGroup = FindModeGroup(sidebar);
                if (modeGroup == null)
                {
                    Log.Warning("[SwitchMode] Mode group not found");
                    return false;
                }

                // Collect Button children of the Mode group
                var child = RawWalker.GetFirstChild(modeGroup);
                while (child != null)
                {
                    try
                    {
                        if (child.Current.ControlType == ControlType.Button &&
                            string.Equals(child.Current.Name, targetMode, StringComparison.OrdinalIgnoreCase))
                        {
                            // Try InvokePattern first
                            if (child.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                            {
                                ((InvokePattern)pattern).Invoke();
                                CurrentMode = targetMode.ToLowerInvariant();
                                Log.Information("Switched to mode: {Mode}", targetMode);
                                return true;
                            }

                            // Fallback: coordinate click
                            ClickElement(child);
                            CurrentMode = targetMode.ToLowerInvariant();
                            Log.Information("Switched to mode: {Mode} (via click)", targetMode);
                            return true;
                        }
                    }
                    catch { }
                    child = RawWalker.GetNextSibling(child);
                }

                    Log.Warning("[SwitchMode] Mode button '{Mode}' not found in Mode group", targetMode);
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to switch mode to {Mode}", targetMode);
                    return false;
                }
            }));
        }, false);
    }

    // ============================================================
    // Sessions
    // ============================================================

    public async Task<List<SessionInfo>> GetSessionsAsync()
    {
        return await WithUiaGateAsync("GetSessions", async () =>
        {
            if (!await EnsureConnected()) return new List<SessionInfo>();

            return await Task.Run(() =>
            {
            var allSessions = new List<SessionInfo>();
            var knownNames = new HashSet<string>();
            string? expandedProjectName = null;

            try
            {
                var navScroll = FindNavScroll();
                if (navScroll == null)
                {
                    Log.Warning("[GetSessions] dframe-nav-scroll not found");
                    return allSessions;
                }

                for (int pass = 0; pass <= ScrollPassCount; pass++)
                {
                    // Re-walk the layout every pass — scroll invalidates AutomationElement refs
                    var layout = CollectProjectLayout(navScroll);
                    var expandedEntry = layout.FirstOrDefault(e => e.SessionGroups.Count > 0);

                    if (expandedEntry == null)
                    {
                        Log.Debug("[GetSessions] Pass {Pass}: no expanded project — returning {Count}",
                            pass, allSessions.Count);
                        break;
                    }

                    expandedProjectName = expandedEntry.Name;

                    foreach (var rowGroup in expandedEntry.SessionGroups)
                    {
                        try
                        {
                            var sessionBtn = FindFirstButtonRaw(rowGroup);
                            if (sessionBtn == null) continue;

                            var rawName = SafeGetName(sessionBtn);
                            if (string.IsNullOrWhiteSpace(rawName)) continue;

                            // Strip "Running " prefix if present (session being generated)
                            var cleanName = rawName.StartsWith("Running ", StringComparison.Ordinal)
                                ? rawName.Substring("Running ".Length)
                                : rawName;

                            if (knownNames.Add(cleanName))
                            {
                                allSessions.Add(new SessionInfo
                                {
                                    Id = $"session_{allSessions.Count}",
                                    Name = cleanName,
                                    IsActive = allSessions.Count == 0
                                });
                            }
                        }
                        catch { }
                    }

                    Log.Debug("[GetSessions] Pass {Pass}: project='{P}', total unique {Total}",
                        pass, expandedProjectName, allSessions.Count);

                    if (allSessions.Count >= MaxSessionsToCollect)
                    {
                        allSessions = allSessions.Take(MaxSessionsToCollect).ToList();
                        break;
                    }

                    if (pass < ScrollPassCount)
                    {
                        if (!ScrollSidebarDown(navScroll)) break;
                        Thread.Sleep(300);
                    }
                }

                if (allSessions.Count > 3)
                    ScrollSidebarToTop(navScroll);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[GetSessions] Failed to get sessions");
            }

            Log.Information("[GetSessions] Returning {Count} sessions (project='{P}', mode={Mode})",
                allSessions.Count, expandedProjectName ?? "(none)", CurrentMode);
                return allSessions;
            });
        }, new List<SessionInfo>());
    }

    /// <summary>
    /// One "project slot" in the flat dframe-nav-scroll list.
    /// A project label Button is followed (as sibling) by its session row Groups
    /// when the project is expanded. Collapsed projects have no session rows between
    /// their label and the next project label / end of nav scroll.
    /// </summary>
    private sealed class ProjectLayoutEntry
    {
        public AutomationElement Label { get; set; } = null!;
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
        public List<AutomationElement> SessionGroups { get; } = new();
    }

    /// <summary>
    /// Walks nav-scroll children once and returns a list of project entries with
    /// their associated session row Groups. Section headers (Pinned/Recent/Filter)
    /// reset the current project owner so their siblings aren't mis-assigned.
    /// </summary>
    private List<ProjectLayoutEntry> CollectProjectLayout(AutomationElement navScroll)
    {
        var entries = new List<ProjectLayoutEntry>();
        ProjectLayoutEntry? current = null;
        int projectIdx = 0;

        var child = RawWalker.GetFirstChild(navScroll);
        while (child != null)
        {
            try
            {
                var cls = SafeGetClassName(child);
                var ct = child.Current.ControlType;

                if (ct == ControlType.Button &&
                    cls.StartsWith(ProjectLabelClassPrefix, StringComparison.Ordinal))
                {
                    var name = SafeGetName(child);
                    if (string.IsNullOrWhiteSpace(name) || IsSectionHeader(name))
                    {
                        // Section header or unnamed — subsequent sessions don't belong to a project
                        current = null;
                    }
                    else
                    {
                        current = new ProjectLayoutEntry
                        {
                            Label = child,
                            Name = name,
                            Index = projectIdx++
                        };
                        entries.Add(current);
                    }
                }
                else if (cls.StartsWith(SessionRowClassPrefix, StringComparison.Ordinal) ||
                         cls.StartsWith(SessionRowDragWrapperPrefix, StringComparison.Ordinal))
                {
                    // Session row — belongs to most recent project label (if any).
                    // 1.3883+ wraps the session Group in an extra "relative df-drag-shiftable" Group;
                    // FindFirstButtonRaw walks descendants so either wrapper resolves to the same Button.
                    if (current != null)
                        current.SessionGroups.Add(child);
                }
            }
            catch { }
            child = RawWalker.GetNextSibling(child);
        }

        return entries;
    }

    /// <summary>
    /// Returns true if the project label represents an expanded project.
    ///
    /// Priority order (most reliable → least):
    ///   1. <c>ExpandCollapsePattern</c> — maps directly to <c>aria-expanded</c>
    ///      on the Chromium button.
    ///   2. Visibility check — collapsed session rows in Chromium get either
    ///      <c>IsOffscreen = true</c> or a zero-height bounding rect; only
    ///      count session rows that are actually visible.
    ///   3. Raw session-group count — last resort, unreliable when Chromium
    ///      keeps collapsed children in the DOM.
    /// </summary>
    private static bool DetectProjectExpanded(ProjectLayoutEntry entry)
    {
        // Signal 1: ExpandCollapsePattern (aria-expanded on the button)
        try
        {
            if (entry.Label.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern))
            {
                var state = ((ExpandCollapsePattern)pattern).Current.ExpandCollapseState;
                return state == ExpandCollapseState.Expanded;
            }
        }
        catch { }

        // Signal 2: Visible session rows (skip offscreen / zero-height rows)
        if (entry.SessionGroups.Count > 0)
        {
            foreach (var row in entry.SessionGroups)
            {
                try
                {
                    if (row.Current.IsOffscreen) continue;
                    var rect = row.Current.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Height > 2) return true;
                }
                catch { }
            }
            // All session rows are offscreen/zero-height → project is collapsed
            // despite having DOM children.
            return false;
        }

        return false;
    }

    public async Task<bool> SelectSessionAsync(string sessionId)
    {
        return await WithUiaGateAsync($"SelectSession({sessionId})", async () =>
        {
            if (!await EnsureConnected()) return false;

            return await WithWindowRestoredAsync(() => Task.Run(() =>
            {
                try
                {
                    var idx = int.Parse(sessionId.Replace("session_", ""));
                var navScroll = FindNavScroll();
                if (navScroll == null) return false;

                // Session indices are scoped to the currently-expanded project
                var layout = CollectProjectLayout(navScroll);
                var expandedEntry = layout.FirstOrDefault(e => e.SessionGroups.Count > 0);

                if (expandedEntry == null)
                {
                    Log.Warning("[SelectSession] No expanded project — cannot resolve session '{Id}'", sessionId);
                    return false;
                }

                if (idx < 0 || idx >= expandedEntry.SessionGroups.Count)
                {
                    Log.Warning("[SelectSession] Index {Idx} out of range (0..{Max}) for project '{P}'",
                        idx, expandedEntry.SessionGroups.Count - 1, expandedEntry.Name);
                    return false;
                }

                var sessionBtn = FindFirstButtonRaw(expandedEntry.SessionGroups[idx]);
                if (sessionBtn == null)
                {
                    Log.Warning("[SelectSession] No Button inside session row idx {Idx}", idx);
                    return false;
                }

                if (sessionBtn.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                    ((InvokePattern)pattern).Invoke();
                else
                    ClickElement(sessionBtn);

                    Log.Information("[SelectSession] {Id} clicked (Name='{Name}', project='{P}')",
                        sessionId, SafeGetName(sessionBtn), expandedEntry.Name);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to select session {Id}", sessionId);
                    return false;
                }
            }));
        }, false);
    }

    public async Task<bool> AddSessionAsync()
    {
        return await WithUiaGateAsync("AddSession", async () =>
        {
            if (!await EnsureConnected()) return false;

            return await WithWindowRestoredAsync(() => Task.Run(() =>
            {
                try
                {
                    if (_claudeWindow == null) return false;

                    // v2: Button Name starts with "New session" (e.g. "New session ⌘N")
                    var navScroll = FindNavScroll();
                if (navScroll != null)
                {
                    var btn = FindButtonByNamePrefixRaw(navScroll, NewSessionPrefix);
                    if (btn != null)
                    {
                        if (btn.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                        {
                            ((InvokePattern)pattern).Invoke();
                            Log.Information("New session created (Name='{Name}')", SafeGetName(btn));
                            return true;
                        }

                        ClickElement(btn);
                        Log.Information("New session created via click (Name='{Name}')", SafeGetName(btn));
                        return true;
                    }
                }

                    // Fallback: keyboard shortcut — try Ctrl+N first, then legacy Ctrl+Shift+O
                    System.Windows.Forms.SendKeys.SendWait("^n");
                    Log.Information("New session created (via shortcut Ctrl+N)");
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to add session");
                    return false;
                }
            }));
        }, false);
    }

    // ============================================================
    // Projects
    // ============================================================

    public async Task<List<ProjectInfo>> GetProjectsAsync()
    {
        return await WithUiaGateAsync("GetProjects", async () =>
        {
            if (!await EnsureConnected()) return new List<ProjectInfo>();

            return await Task.Run(() =>
            {
            var projects = new List<ProjectInfo>();
            try
            {
                var navScroll = FindNavScroll();
                if (navScroll == null) return projects;

                // v2: project label Buttons have Class starting "group/label ".
                // Flat sibling layout: when a project is expanded, its session row Groups
                // appear as siblings between its label and the next project label.
                var layout = CollectProjectLayout(navScroll);

                foreach (var entry in layout)
                {
                    projects.Add(new ProjectInfo
                    {
                        Id = $"project_{entry.Index}",
                        Name = entry.Name,
                        Path = string.Empty, // Path not exposed in v2 UI
                        IsActive = false, // set below via header match / fallback
                        IsExpanded = DetectProjectExpanded(entry)
                    });
                }

                // Active project: match by Primary pane header text (most reliable)
                var primaryPane = FindPrimaryPane();
                string? headerProjectName = primaryPane != null
                    ? FindHeaderProjectName(primaryPane)
                    : null;

                if (!string.IsNullOrEmpty(headerProjectName))
                {
                    for (int i = 0; i < projects.Count; i++)
                        projects[i].IsActive = projects[i].Name == headerProjectName;
                }
                else if (projects.Count > 0)
                {
                    // Fallback 1: prefer the first expanded project
                    var firstExpanded = projects.FindIndex(p => p.IsExpanded);
                    if (firstExpanded >= 0)
                        projects[firstExpanded].IsActive = true;
                    else
                        projects[0].IsActive = true; // Fallback 2: first label
                }

                    Log.Information(
                        "[GetProjects] {Count} projects — expanded=[{Expanded}], active='{Active}'",
                        projects.Count,
                        string.Join(",", projects.Where(p => p.IsExpanded).Select(p => p.Name)),
                        projects.FirstOrDefault(p => p.IsActive)?.Name ?? "(none)");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to get projects");
                }
                return projects;
            });
        }, new List<ProjectInfo>());
    }

    public async Task<bool> SelectProjectAsync(string projectId)
    {
        return await WithUiaGateAsync($"SelectProject({projectId})", async () =>
        {
            if (!await EnsureConnected()) return false;

            return await WithWindowRestoredAsync(() => Task.Run(() =>
            {
                try
                {
                    var navScroll = FindNavScroll();
                    if (navScroll == null) return false;

                    var idx = int.Parse(projectId.Replace("project_", ""));
                    var layout = CollectProjectLayout(navScroll);

                    if (idx < 0 || idx >= layout.Count)
                    {
                        Log.Warning("[SelectProject] Index {Idx} out of range (0..{Max})",
                            idx, layout.Count - 1);
                        return false;
                    }

                    var targetEntry = layout[idx];
                    var targetName = targetEntry.Name;

                    // EXCLUSIVE SELECTION semantics: after this call, ONLY the
                    // target project is expanded. Claude Desktop allows multiple
                    // projects to be expanded simultaneously, but Android UX
                    // needs a single "current project" — otherwise:
                    //   - GetSessions always returns the first expanded project's
                    //     sessions, regardless of which project the user tapped
                    //   - SelectSession always operates within the first expanded
                    //     project, so tapping "session_2" in Project B actually
                    //     selects Project A's 3rd session
                    //   - Tapping an already-expanded project appears to do
                    //     nothing (no-op)
                    //
                    // Flow:
                    //   1. Expand target if collapsed
                    //   2. Collapse every OTHER expanded project (click each once)
                    //   3. Re-fetch layout once to confirm final state
                    //
                    // Note: clicks invalidate element refs, so we re-fetch layout
                    // before clicking other projects.

                    // Step 1: ensure target is expanded
                    bool targetWasExpanded = DetectProjectExpanded(targetEntry);
                    if (!targetWasExpanded)
                    {
                        Log.Information("[SelectProject] Expanding target '{Name}' (idx={Idx})",
                            targetName, idx);
                        if (targetEntry.Label.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
                            ((InvokePattern)p).Invoke();
                        else
                            ClickElement(targetEntry.Label);
                        Thread.Sleep(300);
                    }
                    else
                    {
                        Log.Debug("[SelectProject] Target '{Name}' already expanded", targetName);
                    }

                    // Step 2: collapse every OTHER expanded project
                    // Re-fetch layout: the expand click above may have moved elements
                    var freshLayout = CollectProjectLayout(navScroll);
                    int collapsed = 0;
                    foreach (var other in freshLayout)
                    {
                        if (string.Equals(other.Name, targetName, StringComparison.Ordinal))
                            continue;
                        if (!DetectProjectExpanded(other))
                            continue;

                        try
                        {
                            Log.Information("[SelectProject] Collapsing '{Name}' (non-target)",
                                other.Name);
                            if (other.Label.TryGetCurrentPattern(InvokePattern.Pattern, out var p2))
                                ((InvokePattern)p2).Invoke();
                            else
                                ClickElement(other.Label);
                            Thread.Sleep(200); // shorter settle per collapse
                            collapsed++;
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "[SelectProject] Collapse '{Name}' failed (non-fatal)",
                                other.Name);
                        }
                    }

                    if (collapsed > 0) Thread.Sleep(200); // final settle

                    Log.Information(
                        "[SelectProject] '{Name}' exclusive — targetExpanded={WasExp}, collapsed={C} others",
                        targetName, targetWasExpanded, collapsed);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to select project {Id}", projectId);
                    return false;
                }
            }));
        }, false);
    }

    // ============================================================
    // Action buttons (Permission / AskUserQuestion)
    // ============================================================

    public async Task<List<ButtonInfo>> GetActionButtonsAsync()
    {
        return await WithUiaGateAsync("GetActionButtons", async () =>
        {
            if (!await EnsureConnected()) return new List<ButtonInfo>();

            return await Task.Run(() =>
            {
                try
                {
                    return DetectApprovalButtons();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to get action buttons");
                    return new List<ButtonInfo>();
                }
            });
        }, new List<ButtonInfo>());
    }

    /// <summary>
    /// Detects action buttons by finding the approval card container in the Primary pane.
    /// - Class contains "outline-none" → AskUserQuestion (category=askuser)
    /// - Class without "outline-none" → Permission dialog (category=permission)
    /// - No approval card → empty list
    /// </summary>
    private List<ButtonInfo> DetectApprovalButtons()
    {
        var primaryPane = FindPrimaryPane();
        if (primaryPane == null)
        {
            LastButtonCategory = null;
            LastPermissionPrompt = null;
            LastAskUserQuestionPrompt = null;
            return new List<ButtonInfo>();
        }

        var card = FindApprovalCard(primaryPane);
        if (card == null)
        {
            LastButtonCategory = null;
            LastPermissionPrompt = null;
            LastAskUserQuestionPrompt = null;
            return new List<ButtonInfo>();
        }

        var cardClass = SafeGetClassName(card);
        bool isAskUser = cardClass.Contains(AskUserOutlineMarker, StringComparison.Ordinal);

        if (isAskUser)
        {
            LastButtonCategory = "askuser";
            return CollectAskUserQuestionButtons(card);
        }
        else
        {
            LastButtonCategory = "permission";
            return CollectPermissionButtons(card);
        }
    }

    /// <summary>
    /// Collects option buttons from an AskUserQuestion card.
    /// Structure: first Text child = prompt; Buttons = option buttons (numbered) + Other/Skip/Submit.
    /// Excludes Skip/Submit/Submit⏎; includes "Other N" so user can trigger it.
    /// </summary>
    private List<ButtonInfo> CollectAskUserQuestionButtons(AutomationElement card)
    {
        var buttons = new List<ButtonInfo>();
        string? prompt = null;
        int index = 0;

        var child = RawWalker.GetFirstChild(card);
        while (child != null)
        {
            try
            {
                var ct = child.Current.ControlType;
                if (ct == ControlType.Text && prompt == null)
                {
                    var text = SafeGetName(child);
                    if (!string.IsNullOrWhiteSpace(text))
                        prompt = text;
                }
                else if (ct == ControlType.Button)
                {
                    var name = SafeGetName(child);
                    if (!string.IsNullOrWhiteSpace(name) && !AskUserFixedButtonNames.Contains(name))
                    {
                        buttons.Add(new ButtonInfo
                        {
                            Id = $"btn_{index}",
                            Text = name,
                            Element = child
                        });
                        index++;
                    }
                }
            }
            catch { }
            child = RawWalker.GetNextSibling(child);
        }

        LastAskUserQuestionPrompt = prompt;
        LastPermissionPrompt = null;
        if (buttons.Count > 0)
        {
            var names = string.Join(", ", buttons.Select(b => $"'{b.Text}'"));
            Log.Information("[AskUserQ] Prompt='{Prompt}', {Count} buttons: [{Names}]",
                prompt ?? "(none)", buttons.Count, names);
        }
        return buttons;
    }

    /// <summary>
    /// Collects action buttons from a Permission dialog card.
    /// Structure: first Button child is the "Allow Claude to ..." header (skipped);
    /// Groups with text = command detail + "This command requires approval";
    /// remaining Buttons = Deny / Allow once / Always allow (with shortcut suffix).
    /// </summary>
    private List<ButtonInfo> CollectPermissionButtons(AutomationElement card)
    {
        var buttons = new List<ButtonInfo>();
        var promptParts = new List<string>();
        int index = 0;
        bool headerSeen = false;

        var child = RawWalker.GetFirstChild(card);
        while (child != null)
        {
            try
            {
                var ct = child.Current.ControlType;
                if (ct == ControlType.Button)
                {
                    var rawName = SafeGetName(child);
                    var cleanName = CleanPermissionButtonName(rawName);

                    if (!headerSeen && !IsPermissionActionButton(cleanName))
                    {
                        // First non-action Button = "Allow Claude to ..." header
                        if (!string.IsNullOrWhiteSpace(rawName))
                            promptParts.Add(rawName);
                        headerSeen = true;
                    }
                    else if (IsPermissionActionButton(cleanName))
                    {
                        buttons.Add(new ButtonInfo
                        {
                            Id = $"btn_{index}",
                            Text = cleanName,
                            Element = child
                        });
                        index++;
                    }
                }
                else if (ct == ControlType.Group && buttons.Count == 0)
                {
                    // Before any action button: this is command detail or footnote
                    var groupTexts = CollectTextFromGroup(child);
                    if (groupTexts.Count > 0)
                        promptParts.AddRange(groupTexts);
                }
            }
            catch { }
            child = RawWalker.GetNextSibling(child);
        }

        LastPermissionPrompt = promptParts.Count > 0 ? string.Join(" | ", promptParts) : null;
        LastAskUserQuestionPrompt = null;
        if (buttons.Count > 0)
        {
            var names = string.Join(", ", buttons.Select(b => $"'{b.Text}'"));
            Log.Information("[Permission] Prompt='{Prompt}', {Count} buttons: [{Names}]",
                LastPermissionPrompt ?? "(none)", buttons.Count, names);
        }
        return buttons;
    }

    /// <summary>
    /// Strips v2 shortcut suffixes from permission button names.
    /// "Deny esc" → "Deny", "Allow once ⌘⇧⏎" → "Allow once", "Always allow ⌘⏎" → "Always allow"
    /// Also handles legacy v1 suffixes as fallback.
    /// </summary>
    private static string CleanPermissionButtonName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return rawName;
        foreach (var suffix in PermissionShortcutSuffixes)
        {
            if (rawName.EndsWith(suffix, StringComparison.Ordinal))
                return rawName.Substring(0, rawName.Length - suffix.Length);
        }
        return rawName;
    }

    private static bool IsPermissionActionButton(string cleanName)
    {
        if (string.IsNullOrEmpty(cleanName)) return false;
        return Array.Exists(PermissionButtonPrefixes, prefix =>
            cleanName.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            cleanName.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> ClickButtonAsync(string buttonId)
    {
        return await WithUiaGateAsync($"ClickButton({buttonId})", async () =>
        {
            if (!await EnsureConnected()) return false;

            return await WithWindowRestoredAsync(() => Task.Run(() =>
            {
                try
                {
                    var freshButtons = DetectApprovalButtons();
                    var idx = int.Parse(buttonId.Replace("btn_", ""));

                if (idx < 0 || idx >= freshButtons.Count)
                {
                    Log.Warning("Action button index {Idx} out of range (have {Count})",
                        idx, freshButtons.Count);
                    return false;
                }

                var target = freshButtons[idx];
                var targetElement = target.Element;

                if (targetElement != null)
                {
                    if (targetElement.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                    {
                        ((InvokePattern)pattern).Invoke();
                        Log.Information("Clicked action button: '{Name}' (InvokePattern)", target.Text);
                        return true;
                    }

                    ClickElement(targetElement);
                    Log.Information("Clicked action button: '{Name}' (coordinate click)", target.Text);
                    return true;
                }

                    Log.Warning("Action button not found: {Id}", buttonId);
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to click button {Id}", buttonId);
                    return false;
                }
            }));
        }, false);
    }

    // ============================================================
    // Usage observability (Phase 11)
    // ============================================================

    // ----- Regex patterns for inline button + bottom-right popup dashboard -----
    // Inline (always-available): "Usage: context 514.6k, plan 51%"
    private static readonly System.Text.RegularExpressions.Regex InlineUsageRegex =
        new(@"^Usage:\s*context\s*([^,]+?),\s*plan\s*(\d+)\s*%\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Popup: "514.6k / 1.0M (51%)" or "514.6k/1.0M(51%)"
    private static readonly System.Text.RegularExpressions.Regex ContextWindowRegex =
        new(@"([\d.]+\s*[kKmM])\s*/\s*([\d.]+\s*[kKmM])\s*\(\s*(\d+)\s*%\s*\)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Popup: "18% · resets 4h" / "56% • resets 19h" — middle-dot variants
    private static readonly System.Text.RegularExpressions.Regex LimitValueRegex =
        new(@"(\d+)\s*%\s*[·•∙・]\s*(resets\s+\S+(?:\s+\S+)?)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Three-way split variant #1: just a percentage like "18%"
    // Newer Claude builds render the limit row as three separate Text nodes:
    //   "5-hour limit"   "18%"   " · resets 4h"
    private static readonly System.Text.RegularExpressions.Regex PercentOnlyRegex =
        new(@"^\s*(\d+)\s*%\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Three-way split variant #2: reset info, possibly with a leading middle-dot
    //   " · resets 4h" / "· resets 19h" / "resets 2d"
    private static readonly System.Text.RegularExpressions.Regex ResetOnlyRegex =
        new(@"^\s*[·•∙・]?\s*(resets\s+\S+(?:\s+\S+)?)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Context window value as a separate node: "514.6k / 1.0M"
    // (new layout emits the percent in a different node, which we also pick up)
    private static readonly System.Text.RegularExpressions.Regex ContextWindowValuesRegex =
        new(@"^\s*([\d.]+\s*[kKmM])\s*/\s*([\d.]+\s*[kKmM])\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Footer line: "Opus 4.6 1M · Max"
    private static readonly System.Text.RegularExpressions.Regex ModelPlanRegex =
        new(@"^(.+?)\s+[·•∙・]\s+(.+?)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Lightweight: the inline Usage button always present in the chat footer
    public async Task<UsageInfo?> GetUsageInfoAsync()
    {
        return await WithUiaGateAsync<UsageInfo?>("GetUsageInfo", async () =>
        {
            if (!await EnsureConnected()) return null;

            return await Task.Run(() =>
            {
                try
                {
                    var primaryPane = FindPrimaryPane();
                    if (primaryPane == null)
                    {
                        Log.Warning("[GetUsageInfo] Primary pane not found");
                        return (UsageInfo?)null;
                    }

                    var btn = FindUsageTriggerButton(primaryPane);
                    if (btn == null)
                    {
                        Log.Warning("[GetUsageInfo] Inline usage button not found");
                        return null;
                    }

                    var raw = SafeGetName(btn);
                    var info = new UsageInfo { RawText = raw };

                    var m = InlineUsageRegex.Match(raw);
                    if (m.Success)
                    {
                        info.ContextText = m.Groups[1].Value.Trim();
                        if (int.TryParse(m.Groups[2].Value, out var pct))
                            info.PlanPercent = pct;
                    }
                    else
                    {
                        Log.Warning("[GetUsageInfo] Regex miss on '{Raw}' — returning raw text only", raw);
                    }

                    Log.Information("[GetUsageInfo] context={Ctx}, plan={Pct}%",
                        info.ContextText ?? "?", info.PlanPercent?.ToString() ?? "?");
                    return info;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[GetUsageInfo] Failed");
                    return null;
                }
            });
        }, null);
    }

    // Heavy: click the bottom-right "Usage:" button, scrape the popup, close it.
    // The same button used by GetUsageInfoAsync is also the popup trigger —
    // its accessibility Name carries the inline summary, while clicking it
    // (Invoke) toggles the comprehensive popup that contains Context window /
    // 5-hour / Weekly / model+plan footer.
    public async Task<UsageDashboard?> GetUsageDashboardAsync()
    {
        return await WithUiaGateAsync<UsageDashboard?>("GetUsageDashboard", async () =>
        {
            if (!await EnsureConnected()) return null;

            return await WithWindowRestoredAsync(() => Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                bool openedByUs = false;
                AutomationElement? trigger = null;
                try
                {
                    // Step 1: Idempotent — is the popup already open?
                    // (User may have clicked the button manually before pulling.)
                    var popup = FindUsagePopupRoot();

                    // Step 2: If not open, find the bottom-right Usage button and click it
                    if (popup == null)
                    {
                        var primaryPane = FindPrimaryPane();
                        if (primaryPane == null)
                        {
                            Log.Warning("[GetUsageDashboard] Primary pane not found");
                            return (UsageDashboard?)null;
                        }

                        trigger = FindUsageTriggerButton(primaryPane);
                        if (trigger == null)
                        {
                            Log.Warning("[GetUsageDashboard] Bottom-right Usage button not found");
                            return null;
                        }

                        Log.Information("[GetUsageDashboard] Clicking trigger '{Name}'", SafeGetName(trigger));

                        // Radix UI onClick handlers live on an inner div — UIA
                        // InvokePattern sometimes wires to the wrong level and
                        // doesn't fire React's handler. Try Invoke first (no
                        // cursor movement) and if the popup doesn't appear
                        // within 500ms, fall back to a real coordinate click
                        // which always fires the real click pipeline.
                        bool invoked = false;
                        if (trigger.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                        {
                            try { ((InvokePattern)ip).Invoke(); invoked = true; } catch { }
                        }
                        if (!invoked)
                        {
                            ClickElement(trigger);
                        }

                        openedByUs = true;

                        // Phase A: short poll after Invoke (happy path ~100-300ms)
                        for (int i = 0; i < 5; i++)
                        {
                            Thread.Sleep(100);
                            popup = FindUsagePopupRoot();
                            if (popup != null) break;
                        }

                        // Phase B: if Invoke silently failed, try a real click
                        if (popup == null && invoked)
                        {
                            Log.Debug("[GetUsageDashboard] Invoke produced no popup — trying coordinate click");
                            try { ClickElement(trigger); } catch { }
                            for (int i = 0; i < 8; i++)
                            {
                                Thread.Sleep(100);
                                popup = FindUsagePopupRoot();
                                if (popup != null) break;
                            }
                        }

                        if (popup == null)
                        {
                            Log.Warning("[GetUsageDashboard] Popup did not appear after click ({Ms}ms)",
                                sw.ElapsedMilliseconds);
                            // One-shot diagnostic: show what Text/Window nodes
                            // actually exist so we can tell whether the popup
                            // opened with a different label, or didn't open at
                            // all, or rendered in a separate top-level window.
                            DumpUsagePopupDiagnostic();
                            // Try to clean up anyway (popup might exist but anchor missed)
                            TryCloseUsagePopup(trigger);
                            return null;
                        }
                    }

                    // Step 3: Collect text lines from popup subtree, then parse
                    var lines = CollectUsagePopupStrings(popup);
                    Log.Debug("[GetUsageDashboard] Collected {Count} lines from popup", lines.Count);
                    // Dump raw lines while we're debugging the parser — each
                    // Claude build tweaks the popup layout, so seeing the
                    // exact strings is far cheaper than guessing.
                    for (int li = 0; li < lines.Count; li++)
                        Log.Information("[GetUsageDashboard]   line[{I}]: '{Line}'", li, lines[li]);
                    var dashboard = ParseUsagePopup(lines);

                    // Step 4: Close popup if we opened it (minimize disruption)
                    if (openedByUs)
                    {
                        TryCloseUsagePopup(trigger);
                    }

                    Log.Information(
                        "[GetUsageDashboard] context={Ctx}, 5h={F}%, weekAll={WA}%, weekSonnet={WS}%, model='{Model}', plan='{Plan}' ({Ms}ms)",
                        dashboard.ContextWindow?.PercentUsed?.ToString() ?? "?",
                        dashboard.FiveHourLimit?.PercentUsed?.ToString() ?? "?",
                        dashboard.WeeklyAllModels?.PercentUsed?.ToString() ?? "?",
                        dashboard.WeeklySonnetOnly?.PercentUsed?.ToString() ?? "?",
                        dashboard.ModelName ?? "?",
                        dashboard.PlanName ?? "?",
                        sw.ElapsedMilliseconds);
                    return dashboard;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[GetUsageDashboard] Failed");
                    if (openedByUs) { try { TryCloseUsagePopup(trigger); } catch { } }
                    return null;
                }
            }));
        }, null);
    }

    /// <summary>
    /// Finds the bottom-right usage popup root by anchoring on the
    /// "Context window" Text element and walking up to the smallest
    /// ancestor that also contains a second popup landmark
    /// ("5-hour limit" / "Sonnet only" / "Plan usage" / "Weekly").
    /// Returns null if the popup is not currently visible.
    ///
    /// Two historic pitfalls we have to avoid:
    ///   1) <b>Walker budget</b> — the popup Text nodes sit deeper than 30
    ///      levels inside the Chromium tree, so the old <c>maxDepth: 30</c>
    ///      shortcut silently cut the anchor off. The diagnostic walker
    ///      proved the anchor IS reachable within 35 levels / 15k nodes.
    ///   2) <b>Walker view mixing</b> — a RawView-discovered element cannot
    ///      be reliably ascended with <c>ControlViewWalker.GetParent()</c>;
    ///      it may return null on the first call because the raw node is
    ///      not in the control view. Use <see cref="RawWalker"/> for the
    ///      whole walk so the parent chain is self-consistent.
    /// </summary>
    private AutomationElement? FindUsagePopupRoot()
    {
        if (_claudeWindow == null) return null;

        // Collect multiple "Context window" anchor candidates via reverse
        // DFS. Reverse DFS visits children last-first so the Radix portal
        // (which attaches at the end of the sibling order) is hit before
        // the chat subtree — but we can't trust the first match blindly:
        // the user's chat transcript may itself mention "Context window"
        // inside paragraphs that are rendered as Text nodes, and those
        // match our StartsWith predicate just as well as the real label.
        var candidates = new List<AutomationElement>(12);
        WalkCollectReverse(_claudeWindow, el =>
        {
            try
            {
                if (el.Current.ControlType != ControlType.Text) return false;
                var name = SafeGetName(el);
                return name.StartsWith("Context window", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("Context:", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("Current context", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }, candidates, maxCount: 12, maxDepth: 40, maxNodes: 15000);

        if (candidates.Count == 0)
        {
            Log.Debug("[FindUsagePopupRoot] no Context window anchors found");
            return null;
        }

        // For each candidate, walk up a few ancestors (RawWalker, NOT
        // ControlViewWalker — parent-chain must match the walker type
        // used to discover the anchor) and check whether that ancestor
        // looks like a real popup container. The structural validation
        // requires THREE signals together:
        //   a) another landmark (5-hour limit / Weekly / Sonnet only)
        //   b) a standalone "resets" text node
        //   c) a small total Text-node count (popup is ~15 texts; chat
        //      subtrees are hundreds)
        // Chat paragraphs lack (b) and violate (c), so this rejects the
        // false-positive anchors inside the conversation body.
        for (int ci = 0; ci < candidates.Count; ci++)
        {
            var anchor = candidates[ci];
            var parent = RawWalker.GetParent(anchor);
            for (int i = 0; i < 8 && parent != null; i++)
            {
                if (LooksLikePopupContainer(parent))
                {
                    if (ci > 0)
                        Log.Debug("[FindUsagePopupRoot] accepted candidate #{I} at ancestor +{L}", ci, i);
                    return parent;
                }
                parent = RawWalker.GetParent(parent);
            }
        }

        Log.Debug("[FindUsagePopupRoot] {N} candidate(s) tried, none validated as popup",
            candidates.Count);
        return null;
    }

    /// <summary>
    /// Validates that <paramref name="container"/> looks like the real
    /// usage popup rather than a chat paragraph that happens to mention
    /// "Context window". A real popup subtree has all three of:
    ///   1. another landmark label (5-hour / Weekly / Sonnet only)
    ///   2. a standalone "resets ..." text node
    ///   3. a small total Text-node count (popup is tight; chat is not)
    /// </summary>
    private static bool LooksLikePopupContainer(AutomationElement container)
    {
        try
        {
            bool hasSecondLandmark = false;
            bool hasResetsText = false;
            int textCount = 0;

            int visited = 0;
            WalkPopupValidationInner(container, 0, 8, ref visited, 600,
                ref hasSecondLandmark, ref hasResetsText, ref textCount);

            // Chat subtrees easily have 100s of Text nodes; real popup
            // has roughly 12-25. Cap at 80 to be generous to future
            // layout changes while still rejecting chat.
            return hasSecondLandmark && hasResetsText && textCount > 0 && textCount <= 80;
        }
        catch { return false; }
    }

    private static void WalkPopupValidationInner(
        AutomationElement element,
        int depth,
        int maxDepth,
        ref int visited,
        int maxNodes,
        ref bool hasSecondLandmark,
        ref bool hasResetsText,
        ref int textCount)
    {
        if (depth > maxDepth || visited > maxNodes) return;
        // Once the subtree is proven too large, stop counting — save
        // node budget for other candidates.
        if (textCount > 80) return;
        visited++;

        try
        {
            if (element.Current.ControlType == ControlType.Text)
            {
                var name = SafeGetName(element);
                if (!string.IsNullOrEmpty(name))
                {
                    textCount++;
                    if (!hasSecondLandmark)
                    {
                        if (name.StartsWith("5-hour limit", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("Weekly", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("Sonnet only", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Plan usage", StringComparison.OrdinalIgnoreCase))
                            hasSecondLandmark = true;
                    }
                    if (!hasResetsText)
                    {
                        // Popup has a standalone "resets X hours" / "resets in Xd"
                        // text node. Chat prose almost never has "resets" as a
                        // standalone Text node; it would be embedded in a longer
                        // sentence.
                        if (name.Length <= 40 &&
                            name.IndexOf("resets", StringComparison.OrdinalIgnoreCase) >= 0)
                            hasResetsText = true;
                    }
                }
            }
        }
        catch { return; }

        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            WalkPopupValidationInner(child, depth + 1, maxDepth, ref visited, maxNodes,
                ref hasSecondLandmark, ref hasResetsText, ref textCount);
            if (textCount > 80) return;
            child = RawWalker.GetNextSibling(child);
        }
    }

    /// <summary>
    /// One-shot diagnostic for when <see cref="FindUsagePopupRoot"/> keeps
    /// returning null after clicking the trigger. Dumps Text nodes whose
    /// names look like they could be part of the usage popup, plus any
    /// top-level windows owned by the Claude process (in case the popup
    /// rendered as a separate HWND). Called at most once per failed
    /// <c>GetUsageDashboard</c> attempt — not on every poll — so it's fine
    /// to use a bigger node budget here.
    /// </summary>
    private void DumpUsagePopupDiagnostic()
    {
        try
        {
            if (_claudeWindow == null)
            {
                Log.Warning("[UsageDiag] _claudeWindow is null");
                return;
            }

            // 1) Walk the main Claude window for anything that smells like popup content.
            var hits = new List<string>();
            string[] keywords =
            {
                "context", "window", "hour", "weekly", "plan", "sonnet",
                "opus", "haiku", "limit", "resets", "claude", "max"
            };

            int visited = 0;
            WalkForDiagnosticTexts(_claudeWindow, keywords, hits, 0, 35, ref visited, 15000);

            Log.Warning("[UsageDiag] Walked {Visited} nodes in claude window, matched {N} text candidates",
                visited, hits.Count);
            foreach (var h in hits.Take(60))
                Log.Warning("[UsageDiag]   text: {Text}", h);
            if (hits.Count > 60)
                Log.Warning("[UsageDiag]   ... ({More} more omitted)", hits.Count - 60);

            // 2) Enumerate top-level windows of the Claude process — the
            //    popup may render as a separate HWND (tooltip/menu window).
            try
            {
                int claudePid;
                try { claudePid = _claudeWindow.Current.ProcessId; }
                catch { claudePid = -1; }

                if (claudePid > 0)
                {
                    var desktop = AutomationElement.RootElement;
                    var topWindows = desktop.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ProcessIdProperty, claudePid));
                    Log.Warning("[UsageDiag] Top-level windows of PID {Pid}: {Count}", claudePid, topWindows.Count);
                    for (int i = 0; i < topWindows.Count && i < 20; i++)
                    {
                        try
                        {
                            var w = topWindows[i];
                            var name = w.Current.Name ?? "";
                            var cls = w.Current.ClassName ?? "";
                            var ct = w.Current.ControlType.ProgrammaticName ?? "";
                            Log.Warning("[UsageDiag]   window[{I}]: name='{Name}' class='{Cls}' type={Ct}",
                                i, name, cls, ct);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[UsageDiag] Top-level window enumeration failed");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[UsageDiag] Diagnostic dump failed");
        }
    }

    private static void WalkForDiagnosticTexts(
        AutomationElement element,
        string[] keywords,
        List<string> results,
        int depth,
        int maxDepth,
        ref int visited,
        int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return;
        visited++;

        try
        {
            if (element.Current.ControlType == ControlType.Text)
            {
                var name = element.Current.Name ?? "";
                if (!string.IsNullOrWhiteSpace(name) && name.Length < 200)
                {
                    var lower = name.ToLowerInvariant();
                    foreach (var kw in keywords)
                    {
                        if (lower.Contains(kw))
                        {
                            results.Add(name);
                            break;
                        }
                    }
                }
                return; // Text = leaf
            }
        }
        catch { return; }

        // Reverse child order — popup/portal content usually lives late.
        var children = new List<AutomationElement>(8);
        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            children.Add(child);
            child = RawWalker.GetNextSibling(child);
        }
        for (int i = children.Count - 1; i >= 0; i--)
        {
            WalkForDiagnosticTexts(children[i], keywords, results, depth + 1, maxDepth, ref visited, maxNodes);
        }
    }

    /// <summary>
    /// Closes the usage popup. Preferred path: re-click the trigger button
    /// (toggles the popover). Fallback: send ESC. ESC is reserved as the
    /// secondary path because it can dismiss other floating UI on the page.
    /// </summary>
    private void TryCloseUsagePopup(AutomationElement? trigger)
    {
        try
        {
            if (trigger != null)
            {
                if (trigger.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                {
                    ((InvokePattern)ip).Invoke();
                    Thread.Sleep(120);
                    Log.Information("[TryCloseUsagePopup] re-invoked trigger");
                    // If the popup is still detectable, fall through to ESC
                    if (FindUsagePopupRoot() == null) return;
                }
            }

            System.Windows.Forms.SendKeys.SendWait("{ESC}");
            Thread.Sleep(120);
            Log.Information("[TryCloseUsagePopup] ESC sent (fallback)");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TryCloseUsagePopup] Failed to close popup");
        }
    }

    /// <summary>
    /// Parses the usage popup's flat string list into a UsageDashboard.
    ///
    /// The popup contents (per current Claude Code v2 layout):
    /// <code>
    /// Context window: 514.6k / 1.0M (51%)
    /// Plan usage
    /// 5-hour limit: 18% · resets 4h
    /// Weekly · all models: 56% · resets 19h
    /// Sonnet only: 0% · resets 2d
    /// Opus 4.6 1M · Max
    /// </code>
    /// The DOM may either deliver each row as one combined Text node or as
    /// (label, value) split nodes — this parser handles both shapes by
    /// keeping a "pending slot" pointer that the next line fills if the
    /// current line was a label without a value.
    /// </summary>
    private UsageDashboard ParseUsagePopup(List<string> lines)
    {
        var dashboard = new UsageDashboard();

        // Pending slot: when we see a known label without an inline value,
        // the next line is expected to carry the value.
        UsageLimit? pendingLimit = null;
        bool pendingContext = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            // Strip a trailing ":" so "5-hour limit:" matches the same path as "5-hour limit"
            var stripped = line.TrimEnd(':').Trim();

            // --- Context window ---
            if (stripped.StartsWith("Context window", StringComparison.OrdinalIgnoreCase))
            {
                // Combined: "Context window: 514.6k / 1.0M (51%)"
                if (TryFillContext(dashboard, line))
                {
                    pendingContext = false;
                    pendingLimit = null;
                }
                else
                {
                    pendingContext = true;
                    pendingLimit = null;
                }
                continue;
            }
            if (pendingContext && TryFillContext(dashboard, line))
            {
                pendingContext = false;
                continue;
            }

            // --- Section header ---
            if (stripped.Equals("Plan usage", StringComparison.OrdinalIgnoreCase) ||
                stripped.Equals("Plan usage limits", StringComparison.OrdinalIgnoreCase))
            {
                pendingLimit = null;
                continue;
            }

            // --- 5-hour limit ---
            if (stripped.StartsWith("5-hour limit", StringComparison.OrdinalIgnoreCase))
            {
                var slot = new UsageLimit { Label = "5-hour limit" };
                dashboard.FiveHourLimit = slot;
                TryFillLimitValue(slot, line);
                pendingLimit = LimitIsComplete(slot) ? null : slot;
                continue;
            }

            // --- Weekly · all models ---
            // Matches "Weekly · all models", "Weekly all models", "Weekly • all models"
            if (stripped.StartsWith("Weekly", StringComparison.OrdinalIgnoreCase) &&
                stripped.IndexOf("all models", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var slot = new UsageLimit { Label = "Weekly · all models" };
                dashboard.WeeklyAllModels = slot;
                TryFillLimitValue(slot, line);
                pendingLimit = LimitIsComplete(slot) ? null : slot;
                continue;
            }

            // --- Sonnet only ---
            if (stripped.StartsWith("Sonnet only", StringComparison.OrdinalIgnoreCase))
            {
                var slot = new UsageLimit { Label = "Sonnet only" };
                dashboard.WeeklySonnetOnly = slot;
                TryFillLimitValue(slot, line);
                pendingLimit = LimitIsComplete(slot) ? null : slot;
                continue;
            }

            // --- Footer: model · plan ---
            if (dashboard.ModelName == null && LooksLikeModelPlanLine(stripped))
            {
                var m = ModelPlanRegex.Match(stripped);
                if (m.Success)
                {
                    dashboard.ModelName = m.Groups[1].Value.Trim();
                    dashboard.PlanName = m.Groups[2].Value.Trim();
                    pendingLimit = null;
                    continue;
                }
            }

            // --- Value for a pending limit slot ---
            // New Claude builds split each row into up to THREE nodes:
            //   "5-hour limit"   "18%"   " · resets 4h"
            // Accept the combined form, or fill percent/reset independently
            // and only drop the pending pointer once both fields are set.
            if (pendingLimit != null)
            {
                if (TryFillLimitValue(pendingLimit, line))
                {
                    pendingLimit = null;
                    continue;
                }
                bool filled = TryFillLimitPercent(pendingLimit, line)
                            | TryFillLimitReset(pendingLimit, line);
                if (filled)
                {
                    if (LimitIsComplete(pendingLimit)) pendingLimit = null;
                    continue;
                }
            }
        }

        return dashboard;
    }

    private static bool LimitIsComplete(UsageLimit limit)
        => limit.PercentUsed != null && !string.IsNullOrEmpty(limit.ResetText);

    /// <summary>Tries to fill just the percent field of a UsageLimit from a "18%" line.</summary>
    private static bool TryFillLimitPercent(UsageLimit limit, string line)
    {
        var m = PercentOnlyRegex.Match(line);
        if (!m.Success) return false;
        if (int.TryParse(m.Groups[1].Value, out var pct))
        {
            limit.PercentUsed = pct;
            return true;
        }
        return false;
    }

    /// <summary>Tries to fill just the reset field from a " · resets 4h" line.</summary>
    private static bool TryFillLimitReset(UsageLimit limit, string line)
    {
        var m = ResetOnlyRegex.Match(line);
        if (!m.Success) return false;
        limit.ResetText = m.Groups[1].Value.Trim();
        return true;
    }

    /// <summary>
    /// Tries to fill the dashboard's ContextWindow slot from a line that may
    /// look like "Context window: 514.6k / 1.0M (51%)" or just "514.6k / 1.0M (51%)".
    /// </summary>
    private static bool TryFillContext(UsageDashboard dashboard, string line)
    {
        var m = ContextWindowRegex.Match(line);
        if (!m.Success) return false;
        var ctx = new ContextWindow
        {
            UsedText = m.Groups[1].Value.Replace(" ", string.Empty),
            TotalText = m.Groups[2].Value.Replace(" ", string.Empty),
        };
        if (int.TryParse(m.Groups[3].Value, out var pct))
            ctx.PercentUsed = pct;
        dashboard.ContextWindow = ctx;
        return true;
    }

    /// <summary>
    /// Fills a UsageLimit slot from a line of the form
    /// "[label[:]] 18% · resets 4h" — returns true on success.
    /// </summary>
    private static bool TryFillLimitValue(UsageLimit limit, string line)
    {
        var m = LimitValueRegex.Match(line);
        if (!m.Success) return false;
        if (int.TryParse(m.Groups[1].Value, out var pct))
            limit.PercentUsed = pct;
        limit.ResetText = m.Groups[2].Value.Trim();
        return true;
    }

    /// <summary>
    /// Heuristic for the popup's footer "Opus 4.6 1M · Max" line:
    /// must contain a middle-dot separator and start with a known model
    /// family prefix (Opus / Sonnet / Haiku / Claude).
    /// </summary>
    private static bool LooksLikeModelPlanLine(string line)
    {
        if (line.Length is < 5 or > 80) return false;
        if (line.IndexOfAny(new[] { '·', '•', '∙', '・' }) < 0) return false;
        return line.StartsWith("Opus", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Sonnet", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Haiku", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Claude", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collects strings from the usage popup in document order. Includes Text
    /// control names and short Button names (chrome buttons filtered out).
    /// Preserves DOM traversal order so the state-machine parser can read
    /// labels before their values.
    /// </summary>
    private List<string> CollectUsagePopupStrings(AutomationElement popup)
    {
        var results = new List<string>();
        int visited = 0;
        WalkCollectPopupStrings(popup, results, 0, 20, ref visited, 4000);
        return results;
    }

    private void WalkCollectPopupStrings(
        AutomationElement element,
        List<string> results,
        int depth,
        int maxDepth,
        ref int visited,
        int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return;
        visited++;

        string? self = null;
        bool recurse = true;

        try
        {
            var ct = element.Current.ControlType;
            var name = SafeGetName(element);

            if (ct == ControlType.Text)
            {
                if (!string.IsNullOrWhiteSpace(name)) self = name;
                recurse = false; // Text is leaf
            }
            else if (ct == ControlType.Button && !string.IsNullOrWhiteSpace(name))
            {
                // Short button names only — filters out long tooltips/aria-labels
                if (name.Length <= 80 && !IsUsagePopupChromeButton(name))
                    self = name;
                // Still recurse — some buttons contain nested Text nodes too
            }
        }
        catch { return; }

        if (self != null && (results.Count == 0 || results[^1] != self))
            results.Add(self);

        if (!recurse) return;

        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            WalkCollectPopupStrings(child, results, depth + 1, maxDepth, ref visited, maxNodes);
            child = RawWalker.GetNextSibling(child);
        }
    }

    private static bool IsUsagePopupChromeButton(string name)
    {
        // Popup chrome / re-entry — not popup data
        return name == "Close" || name == "Back" || name == "Share" ||
               name == "Copy" || name == "Dismiss" ||
               name.StartsWith("Close ", StringComparison.Ordinal) ||
               // The popup trigger itself (re-collected when scanning the whole window)
               name.StartsWith("Usage:", StringComparison.Ordinal);
    }

    // ============================================================
    // Monitoring loops
    // ============================================================

    public void StartMonitoring()
    {
        StopMonitoring();
        _monitoringCts = new CancellationTokenSource();
        _ = MonitorLoopAsync(_monitoringCts.Token);
        Log.Information("Output monitoring started (interval: {Interval}ms)", _pollingIntervalMs);
    }

    public void StopMonitoring()
    {
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
        _monitoringCts = null;
    }

    public void StartProcessWatcher()
    {
        StopProcessWatcher();
        _processWatcherCts = new CancellationTokenSource();
        _wasClaudeRunning = IsClaudeRunning;
        _ = ProcessWatchLoopAsync(_processWatcherCts.Token);
        Log.Information("Claude process watcher started (interval: {Interval}ms)", ProcessCheckIntervalMs);
    }

    public void StopProcessWatcher()
    {
        _processWatcherCts?.Cancel();
        _processWatcherCts?.Dispose();
        _processWatcherCts = null;
    }

    private async Task ProcessWatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ProcessCheckIntervalMs, ct);

                var hasProcess = Process.GetProcessesByName(ClaudeProcessName)
                    .Any(p => p.MainWindowHandle != IntPtr.Zero);

                if (_wasClaudeRunning && !hasProcess)
                {
                    Log.Warning("Claude process terminated - setting disconnected");
                    SetDisconnected();
                    _wasClaudeRunning = false;
                }
                else if (!_wasClaudeRunning && hasProcess)
                {
                    Log.Information("Claude process detected - attempting reconnect");
                    await Task.Delay(2000, ct);
                    if (await FindClaudeWindowAsync())
                    {
                        _wasClaudeRunning = true;
                        StartMonitoring();
                        Log.Information("Auto-reconnected to Claude after restart");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error(ex, "Process watcher error");
            }
        }
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IsClaudeRunning)
                    await FindClaudeWindowAsync();

                if (IsClaudeRunning)
                {
                    var output = await GetOutputAsync("latest");
                    if (!string.IsNullOrEmpty(output))
                    {
                        var hash = ComputeHash(output);
                        if (hash != _lastOutputHash)
                        {
                            _lastOutputHash = hash;
                            OutputChanged?.Invoke(this, output);
                        }
                    }

                    var currentButtons = await GetActionButtonsAsync();
                    var currentTexts = currentButtons.Select(b => b.Text).ToList();
                    if (!_lastButtonTexts.SequenceEqual(currentTexts))
                    {
                        _lastButtonTexts = currentTexts;
                        ActionButtonsChanged?.Invoke(this, currentButtons);
                        Log.Debug("[Monitor] Action buttons changed: {Count} buttons", currentButtons.Count);
                    }
                }

                await Task.Delay(_pollingIntervalMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error(ex, "Monitor loop error");
                await Task.Delay(2000, ct);
            }
        }
    }

    // ============================================================
    // Structural element finders (all fresh lookups — no caching)
    // ============================================================

    private static readonly TreeWalker RawWalker = TreeWalker.RawViewWalker;

    private AutomationElement? FindRootDocument()
    {
        if (_claudeWindow == null) return null;

        var allDocs = _claudeWindow.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea"));

        foreach (AutomationElement doc in allDocs)
        {
            if (doc.Current.Name == "Claude")
                return doc;
        }
        return allDocs.Count > 0 ? allDocs[allDocs.Count - 1] : null;
    }

    /// <summary>
    /// Find the Sidebar group (Class starts with "dframe-sidebar"). Always fresh.
    /// </summary>
    private AutomationElement? FindSidebar()
    {
        var root = _rootDocument ?? _claudeWindow;
        if (root == null) return null;
        return FindByClassPrefixRaw(root, DFrameSidebarClass);
    }

    /// <summary>
    /// Find the dframe-nav-scroll group inside the sidebar (main session/project/new-session container).
    /// </summary>
    private AutomationElement? FindNavScroll()
    {
        var sidebar = FindSidebar();
        if (sidebar == null) return null;
        return FindByClassPrefixRaw(sidebar, DFrameNavScrollClass);
    }

    /// <summary>
    /// Find Group Name="Mode" Class="df-pills" inside sidebar.
    /// </summary>
    private AutomationElement? FindModeGroup(AutomationElement sidebar)
    {
        return WalkFindFirst(sidebar, el =>
        {
            try
            {
                return el.Current.ControlType == ControlType.Group &&
                       el.Current.Name == ModeGroupName &&
                       SafeGetClassName(el).StartsWith(ModePillsClass, StringComparison.Ordinal);
            }
            catch { return false; }
        }, maxDepth: 10);
    }

    /// <summary>
    /// Find the Primary pane (Group Name="Primary pane" inside dframe-main).
    /// </summary>
    private AutomationElement? FindPrimaryPane()
    {
        var root = _rootDocument ?? _claudeWindow;
        if (root == null) return null;

        // Try dframe-main first
        var dframeMain = FindByAutomationIdRaw(root, DFrameMainAid);
        var searchRoot = dframeMain ?? root;

        // Name="Primary pane" is in ControlView
        var found = searchRoot.FindFirst(TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
                new PropertyCondition(AutomationElement.NameProperty, PrimaryPaneName)));
        if (found != null) return found;

        // Fallback: raw walk
        return WalkFindFirst(searchRoot, el =>
        {
            try
            {
                return el.Current.ControlType == ControlType.Group && el.Current.Name == PrimaryPaneName;
            }
            catch { return false; }
        }, maxDepth: 25);
    }

    /// <summary>
    /// Find the tiptap Prompt input field (Name="Prompt" inside epitaxy-prompt).
    /// The prompt field lives in a shallow, fixed location in the bottom of
    /// the primary pane — we keep the walk tight so a missing field fails
    /// fast instead of exhaustively scanning the whole tree.
    /// </summary>
    private AutomationElement? FindPromptField()
    {
        var primaryPane = FindPrimaryPane();
        if (primaryPane == null) return null;

        var promptBox = FindByClassPrefixRaw(primaryPane, EpitaxyPromptClass, maxDepth: 20, maxNodes: 1500);
        var searchRoot = promptBox ?? primaryPane;

        // Look for Group Name="Prompt" — bounded so a missing field returns
        // null quickly instead of walking all 5000 nodes of the primary pane.
        return WalkFindFirst(searchRoot, el =>
        {
            try
            {
                return el.Current.Name == InputFieldName &&
                       SafeGetClassName(el).Contains("ProseMirror", StringComparison.Ordinal);
            }
            catch { return false; }
        }, maxDepth: 10, maxNodes: 1500);
    }

    /// <summary>
    /// Find approval card container (shared class: "epitaxy-approval-card").
    /// Permission: class starts with "epitaxy-approval-card" but does NOT contain "outline-none".
    /// AskUserQuestion: class contains both "epitaxy-approval-card" and "outline-none".
    /// </summary>
    private AutomationElement? FindApprovalCard(AutomationElement root)
    {
        return WalkFindFirst(root, el =>
        {
            try
            {
                var cls = SafeGetClassName(el);
                // Must start with epitaxy-approval-card (not be a descendant that happens to mention it)
                return el.Current.ControlType == ControlType.Group &&
                       cls.StartsWith(ApprovalCardClassMarker, StringComparison.Ordinal);
            }
            catch { return false; }
        }, maxDepth: 25);
    }

    /// <summary>
    /// Walk the Primary pane session header to find the active project Button name.
    /// Session header: Button Name="Local" ... Button Name="<project>" ... Text "<session>".
    /// </summary>
    private string? FindHeaderProjectName(AutomationElement primaryPane)
    {
        // Look for first Button in the top of primary pane that is not "Local" or "Session actions"
        var found = WalkFindFirst(primaryPane, el =>
        {
            try
            {
                if (el.Current.ControlType != ControlType.Button) return false;
                var name = el.Current.Name ?? "";
                if (name == "Local" || name == "Session actions") return false;
                // Class hint: the project button has " inline-flex items-center h-base px-p3 rounded-base border-0 cursor-def..."
                var cls = SafeGetClassName(el);
                return cls.Contains("cursor-def", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(name);
            }
            catch { return false; }
        }, maxDepth: 15);
        return found != null ? SafeGetName(found) : null;
    }

    // --- Generic raw-walk helpers ---

    private static AutomationElement? FindByAutomationIdRaw(AutomationElement? root, string automationId, int maxDepth = 25, int maxNodes = 5000)
    {
        if (root == null) return null;

        // Try ControlView first (faster)
        var result = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        if (result != null) return result;

        // Fallback: raw walk
        int visited = 0;
        return WalkForAutomationId(root, automationId, 0, maxDepth, ref visited, maxNodes);
    }

    private static AutomationElement? WalkForAutomationId(AutomationElement element, string targetId, int depth, int maxDepth, ref int visited, int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return null;
        visited++;

        try
        {
            if (element.Current.AutomationId == targetId)
                return element;
        }
        catch { return null; }

        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            var found = WalkForAutomationId(child, targetId, depth + 1, maxDepth, ref visited, maxNodes);
            if (found != null) return found;
            child = RawWalker.GetNextSibling(child);
        }
        return null;
    }

    private static AutomationElement? FindByClassPrefixRaw(AutomationElement root, string classPrefix, int maxDepth = 25, int maxNodes = 5000)
    {
        int visited = 0;
        return WalkForClassPrefix(root, classPrefix, 0, maxDepth, ref visited, maxNodes);
    }

    private static AutomationElement? WalkForClassPrefix(AutomationElement element, string classPrefix, int depth, int maxDepth, ref int visited, int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return null;
        visited++;

        try
        {
            var cls = element.Current.ClassName ?? "";
            if (cls.StartsWith(classPrefix, StringComparison.Ordinal))
                return element;
        }
        catch { return null; }

        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            var found = WalkForClassPrefix(child, classPrefix, depth + 1, maxDepth, ref visited, maxNodes);
            if (found != null) return found;
            child = RawWalker.GetNextSibling(child);
        }
        return null;
    }

    private static AutomationElement? FindButtonByNameRaw(AutomationElement root, string buttonName, int maxDepth = 25)
    {
        return WalkFindFirst(root, el =>
        {
            try
            {
                return el.Current.ControlType == ControlType.Button && el.Current.Name == buttonName;
            }
            catch { return false; }
        }, maxDepth);
    }

    private static AutomationElement? FindButtonByNamePrefixRaw(AutomationElement root, string prefix, int maxDepth = 25, int maxNodes = 5000)
    {
        return WalkFindFirst(root, el =>
        {
            try
            {
                return el.Current.ControlType == ControlType.Button &&
                       (el.Current.Name ?? "").StartsWith(prefix, StringComparison.Ordinal);
            }
            catch { return false; }
        }, maxDepth, maxNodes);
    }

    /// <summary>
    /// Same as <see cref="WalkFindFirst"/> but walks each level's children in
    /// reverse order (last sibling first). The bottom-right Usage button lives
    /// in the prompt footer, which is among the LAST children of the primary
    /// pane. A forward DFS dives into the chat content first and exhausts the
    /// node budget long before it ever reaches the footer. Reverse DFS hits
    /// the footer almost immediately.
    /// </summary>
    private static AutomationElement? WalkFindFirstReverse(AutomationElement root, Func<AutomationElement, bool> predicate, int maxDepth = 25, int maxNodes = 5000)
    {
        int visited = 0;
        return WalkFindFirstReverseInner(root, predicate, 0, maxDepth, ref visited, maxNodes);
    }

    private static AutomationElement? WalkFindFirstReverseInner(AutomationElement element, Func<AutomationElement, bool> predicate, int depth, int maxDepth, ref int visited, int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return null;
        visited++;

        try
        {
            if (predicate(element)) return element;
        }
        catch { /* Skip stale elements */ }

        // Collect children then walk them last-first.
        var children = new List<AutomationElement>(8);
        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            children.Add(child);
            child = RawWalker.GetNextSibling(child);
        }
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var found = WalkFindFirstReverseInner(children[i], predicate, depth + 1, maxDepth, ref visited, maxNodes);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Reverse-order DFS that collects up to <paramref name="maxCount"/>
    /// matching elements (instead of returning the first). Used when the
    /// first match may be a false positive and we need to try subsequent
    /// candidates.
    /// </summary>
    private static void WalkCollectReverse(
        AutomationElement root,
        Func<AutomationElement, bool> predicate,
        List<AutomationElement> results,
        int maxCount = 10,
        int maxDepth = 25,
        int maxNodes = 5000)
    {
        int visited = 0;
        WalkCollectReverseInner(root, predicate, results, maxCount, 0, maxDepth, ref visited, maxNodes);
    }

    private static void WalkCollectReverseInner(
        AutomationElement element,
        Func<AutomationElement, bool> predicate,
        List<AutomationElement> results,
        int maxCount,
        int depth,
        int maxDepth,
        ref int visited,
        int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes || results.Count >= maxCount) return;
        visited++;

        try
        {
            if (predicate(element))
            {
                results.Add(element);
                if (results.Count >= maxCount) return;
                // Don't recurse into a matched element — its descendants
                // are unlikely to be distinct anchors we care about.
                return;
            }
        }
        catch { /* Skip stale elements */ }

        var children = new List<AutomationElement>(8);
        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            children.Add(child);
            child = RawWalker.GetNextSibling(child);
        }
        for (int i = children.Count - 1; i >= 0; i--)
        {
            WalkCollectReverseInner(children[i], predicate, results, maxCount,
                depth + 1, maxDepth, ref visited, maxNodes);
            if (results.Count >= maxCount) return;
        }
    }

    /// <summary>
    /// Locate the bottom-right "Usage:" button. Tries two strategies so that a
    /// huge chat tree doesn't swallow the node budget:
    ///   1. Narrow to the <c>epitaxy-prompt</c> box (a small subtree at the
    ///      bottom of the primary pane) and do a tight forward walk there.
    ///   2. Fall back to a reverse-order DFS of the whole primary pane — the
    ///      Usage button is a late sibling of the chat content, so reverse DFS
    ///      hits it before the budget runs out.
    /// </summary>
    private static AutomationElement? FindUsageTriggerButton(AutomationElement primaryPane)
    {
        // Strategy 1: prompt box subtree (typically <300 nodes)
        var promptBox = WalkFindFirstReverse(primaryPane, el =>
        {
            try
            {
                return SafeGetClassName(el).StartsWith(EpitaxyPromptClass, StringComparison.Ordinal);
            }
            catch { return false; }
        }, maxDepth: 20, maxNodes: 2000);

        if (promptBox != null)
        {
            var btn = FindButtonByNamePrefixRaw(promptBox, "Usage:", maxDepth: 15, maxNodes: 1500);
            if (btn != null) return btn;
        }

        // Strategy 2: reverse DFS over primary pane (footer-first)
        return WalkFindFirstReverse(primaryPane, el =>
        {
            try
            {
                return el.Current.ControlType == ControlType.Button &&
                       (el.Current.Name ?? "").StartsWith("Usage:", StringComparison.Ordinal);
            }
            catch { return false; }
        }, maxDepth: 25, maxNodes: 4000);
    }

    private static AutomationElement? FindEditByName(AutomationElement root, string name, int maxDepth = 25, int maxNodes = 5000)
    {
        return WalkFindFirst(root, el =>
        {
            try
            {
                return el.Current.ControlType == ControlType.Edit && el.Current.Name == name;
            }
            catch { return false; }
        }, maxDepth, maxNodes);
    }

    private static AutomationElement? FindFirstButtonRaw(AutomationElement root, int maxDepth = 6)
    {
        return WalkFindFirst(root, el =>
        {
            try { return el.Current.ControlType == ControlType.Button; }
            catch { return false; }
        }, maxDepth);
    }

    private static AutomationElement? WalkFindFirst(AutomationElement root, Func<AutomationElement, bool> predicate, int maxDepth = 25, int maxNodes = 5000)
    {
        int visited = 0;
        return WalkFindFirstInner(root, predicate, 0, maxDepth, ref visited, maxNodes);
    }

    private static AutomationElement? WalkFindFirstInner(AutomationElement element, Func<AutomationElement, bool> predicate, int depth, int maxDepth, ref int visited, int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return null;
        visited++;

        try
        {
            if (predicate(element)) return element;
        }
        catch { /* Skip stale elements */ }

        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            var found = WalkFindFirstInner(child, predicate, depth + 1, maxDepth, ref visited, maxNodes);
            if (found != null) return found;
            child = RawWalker.GetNextSibling(child);
        }
        return null;
    }

    private static List<string> CollectTextsRaw(AutomationElement root, int maxDepth = 25, int maxNodes = 5000)
    {
        var results = new List<string>();
        int visited = 0;
        WalkCollectTexts(root, results, 0, maxDepth, ref visited, maxNodes);
        return results;
    }

    /// <summary>
    /// Fast text collection using a single pre-fetched UIA cache snapshot.
    ///
    /// The raw DFS walker (<see cref="WalkCollectTexts"/>) crosses the UIA
    /// process boundary for every <c>GetFirstChild</c>/<c>GetNextSibling</c> and
    /// every property read, which adds up to 15-25 seconds for a 4000-node
    /// chat tree and holds the UIA serialization gate long enough to starve
    /// user commands (<c>GetUsageDashboard timed out waiting for gate</c>).
    ///
    /// <see cref="CacheRequest"/> with <c>TreeScope.Subtree</c> + Raw filter
    /// fetches the whole subtree and the requested properties in a SINGLE
    /// RPC. After <c>GetUpdatedCache</c> all subsequent access goes through
    /// <see cref="AutomationElement.Cached"/> and <c>CachedChildren</c> —
    /// purely in-process — so the walk drops to well under a second.
    /// </summary>
    private static List<string> CollectTextsCached(AutomationElement root, int maxDepth = 25)
    {
        var results = new List<string>();
        try
        {
            var cacheReq = new CacheRequest();
            cacheReq.Add(AutomationElement.NameProperty);
            cacheReq.Add(AutomationElement.ControlTypeProperty);
            cacheReq.TreeScope = TreeScope.Element | TreeScope.Subtree;
            cacheReq.TreeFilter = Automation.RawViewCondition;
            cacheReq.AutomationElementMode = AutomationElementMode.None;

            using (cacheReq.Activate())
            {
                var cachedRoot = root.GetUpdatedCache(cacheReq);
                WalkCollectTextsCached(cachedRoot, results, 0, maxDepth);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CollectTextsCached] Cached walk failed — falling back to raw walk");
            results.Clear();
            int visited = 0;
            WalkCollectTexts(root, results, 0, maxDepth, ref visited, 4000);
        }
        return results;
    }

    private static void WalkCollectTextsCached(AutomationElement element, List<string> results, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        try
        {
            if (element.Cached.ControlType == ControlType.Text)
            {
                var name = element.Cached.Name;
                if (!string.IsNullOrWhiteSpace(name) && !IsUiChromeText(name))
                    results.Add(name);
                return; // Text = leaf
            }
        }
        catch { return; }

        AutomationElementCollection? children;
        try { children = element.CachedChildren; }
        catch { return; }
        if (children == null) return;

        foreach (AutomationElement child in children)
        {
            WalkCollectTextsCached(child, results, depth + 1, maxDepth);
        }
    }

    private static void WalkCollectTexts(AutomationElement element, List<string> results, int depth, int maxDepth, ref int visited, int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return;
        visited++;

        try
        {
            if (element.Current.ControlType == ControlType.Text)
            {
                var name = element.Current.Name;
                if (!string.IsNullOrWhiteSpace(name) && !IsUiChromeText(name))
                    results.Add(name);
                return; // Text = leaf
            }
        }
        catch { return; }

        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            WalkCollectTexts(child, results, depth + 1, maxDepth, ref visited, maxNodes);
            child = RawWalker.GetNextSibling(child);
        }
    }

    // ============================================================
    // Phase 13 research: chat-structure diagnostic
    // ============================================================

    /// <summary>
    /// One-shot dump of the conversation scroll container. Logs three
    /// perspectives so we can see how to split the flat text stream back
    /// into <see cref="ChatMessage"/>-style user/assistant/tool messages:
    ///
    ///   [1] TOP-LEVEL CHILDREN — direct children of the scroll container.
    ///       If each child is one message (likely), this is the message boundary.
    ///
    ///   [2] BUTTONS IN TREE — name + class of every Button. "Copy" / "Copy message"
    ///       buttons typically sit inside assistant bubbles; their ancestor div is
    ///       another boundary candidate.
    ///
    ///   [3] TEXT NODES WITH ANCESTOR CHAIN — each Text leaf along with class names
    ///       of its 4 nearest ancestors. Patterns in the ancestor class usually
    ///       reveal user vs assistant (e.g. bubbles often have different class
    ///       prefixes like "font-claude-response" vs "prose" vs a user wrapper).
    ///
    /// Everything is written at INFO level so it shows up in the normal log.
    /// Walked with RawWalker to stay consistent with CollectTextsCached's view.
    /// </summary>
    private void DumpChatStructureDiagnostic(AutomationElement scrollContainer)
    {
        try
        {
            Log.Information("[ChatDump] ============== BEGIN ==============");
            Log.Information("[ChatDump] root class='{C}' name='{N}'",
                SafeGetClassName(scrollContainer), SafeGetName(scrollContainer));

            // [1] Top-level children
            Log.Information("[ChatDump] --- [1] top-level children ---");
            int topIdx = 0;
            var top = RawWalker.GetFirstChild(scrollContainer);
            while (top != null && topIdx < 40)
            {
                try
                {
                    int textCount = CountTextsWithin(top, 12);
                    var firstText = FirstTextSnippetWithin(top, 12, 80);
                    Log.Information("[ChatDump]   top[{I}] ct={CT} class='{C}' texts={TC} snippet='{S}'",
                        topIdx, top.Current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
                        Trunc(SafeGetClassName(top), 80), textCount, firstText);
                }
                catch (Exception ex) { Log.Debug(ex, "[ChatDump] top[{I}] error", topIdx); }
                topIdx++;
                top = RawWalker.GetNextSibling(top);
            }

            // [2] Buttons
            Log.Information("[ChatDump] --- [2] buttons (name + class) ---");
            var buttons = new List<AutomationElement>(50);
            CollectButtons(scrollContainer, buttons, 0, 25, 50);
            for (int i = 0; i < buttons.Count; i++)
            {
                try
                {
                    Log.Information("[ChatDump]   btn[{I}] name='{N}' class='{C}'",
                        i, Trunc(SafeGetName(buttons[i]), 60),
                        Trunc(SafeGetClassName(buttons[i]), 80));
                }
                catch { }
            }

            // [3] Text nodes with ancestor classes
            Log.Information("[ChatDump] --- [3] text nodes with ancestor chain ---");
            var texts = new List<AutomationElement>(100);
            CollectTextElements(scrollContainer, texts, 0, 25, 150);
            for (int i = 0; i < texts.Count; i++)
            {
                try
                {
                    var name = SafeGetName(texts[i]);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var ancestors = GetAncestorClassesUp(texts[i], 4);
                    Log.Information("[ChatDump]   txt[{I}] '{T}' anc=[{A}]",
                        i, Trunc(name, 70), string.Join(" ← ", ancestors));
                }
                catch { }
            }

            Log.Information("[ChatDump] ============== END ({Top} top, {B} btn, {T} txt) ==============",
                topIdx, buttons.Count, texts.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ChatDump] unexpected error");
        }
    }

    private static int CountTextsWithin(AutomationElement root, int maxDepth)
    {
        int count = 0;
        CountTextsInner(root, 0, maxDepth, ref count);
        return count;
    }

    private static void CountTextsInner(AutomationElement el, int depth, int maxDepth, ref int count)
    {
        if (depth > maxDepth || count > 500) return;
        try
        {
            if (el.Current.ControlType == ControlType.Text)
            {
                if (!string.IsNullOrWhiteSpace(SafeGetName(el))) count++;
                return;
            }
        }
        catch { return; }
        var c = RawWalker.GetFirstChild(el);
        while (c != null) { CountTextsInner(c, depth + 1, maxDepth, ref count); c = RawWalker.GetNextSibling(c); }
    }

    private static string FirstTextSnippetWithin(AutomationElement root, int maxDepth, int maxLen)
    {
        string? hit = null;
        FirstTextInner(root, 0, maxDepth, ref hit);
        return Trunc(hit ?? "", maxLen);
    }

    private static void FirstTextInner(AutomationElement el, int depth, int maxDepth, ref string? hit)
    {
        if (hit != null || depth > maxDepth) return;
        try
        {
            if (el.Current.ControlType == ControlType.Text)
            {
                var n = SafeGetName(el);
                if (!string.IsNullOrWhiteSpace(n)) { hit = n; return; }
                return;
            }
        }
        catch { return; }
        var c = RawWalker.GetFirstChild(el);
        while (c != null && hit == null) { FirstTextInner(c, depth + 1, maxDepth, ref hit); c = RawWalker.GetNextSibling(c); }
    }

    private static void CollectButtons(AutomationElement el, List<AutomationElement> results,
        int depth, int maxDepth, int maxCount)
    {
        if (depth > maxDepth || results.Count >= maxCount) return;
        try
        {
            if (el.Current.ControlType == ControlType.Button) results.Add(el);
        }
        catch { }
        var c = RawWalker.GetFirstChild(el);
        while (c != null && results.Count < maxCount)
        {
            CollectButtons(c, results, depth + 1, maxDepth, maxCount);
            c = RawWalker.GetNextSibling(c);
        }
    }

    private static void CollectTextElements(AutomationElement el, List<AutomationElement> results,
        int depth, int maxDepth, int maxCount)
    {
        if (depth > maxDepth || results.Count >= maxCount) return;
        try
        {
            if (el.Current.ControlType == ControlType.Text)
            {
                if (!string.IsNullOrWhiteSpace(SafeGetName(el))) results.Add(el);
                return;
            }
        }
        catch { return; }
        var c = RawWalker.GetFirstChild(el);
        while (c != null && results.Count < maxCount)
        {
            CollectTextElements(c, results, depth + 1, maxDepth, maxCount);
            c = RawWalker.GetNextSibling(c);
        }
    }

    private static List<string> GetAncestorClassesUp(AutomationElement el, int levels)
    {
        var chain = new List<string>(levels);
        var cur = RawWalker.GetParent(el);
        for (int i = 0; i < levels && cur != null; i++)
        {
            chain.Add(Trunc(SafeGetClassName(cur), 40));
            try { cur = RawWalker.GetParent(cur); } catch { break; }
        }
        return chain;
    }

    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    // ============================================================
    // Phase 13: structured chat-message extraction
    // ============================================================
    //
    // DOM shape of the scroll container (verified by ChatDump 2026-04-17):
    //
    //   scroll container children (top-level blocks, in visual order):
    //     • user bubble   : Group, class contains "rounded-r7" + "rounded-bl"
    //     • assistant text: Group, class contains "text-assistant-primary"
    //     • assistant list: List,  class contains "text-assistant-primary"
    //                       + "list-disc" (unordered) or "list-decimal" (ordered)
    //     • tool row      : Button, class contains "group/tool"
    //                       → Name holds the full label ("Ran 2 commands" etc.)
    //     • ui chrome     : Button, class contains "group/btn"
    //                       → Copy message / Pin / Rewind / Fork / Show more — SKIP
    //     • inline code   : Button, class contains "text-code"
    //                       → found INSIDE paragraphs/list items, wrap in backticks
    //
    // Walking top-level left-to-right we can emit one ChatMessage per turn.
    // Consecutive assistant blocks (paragraph + list + paragraph ...) belong
    // to one assistant turn — we buffer them and flush on the next user
    // message (or end of tree), joining with "\n\n" so the output is the
    // "unwrapped prose" format the Android client expects: one line per
    // paragraph, blank line between paragraphs, no mid-paragraph breaks.
    //
    // Tool rows interrupt an assistant turn: each tool row becomes its own
    // ChatMessage with role="tool" so the client can render it as a
    // collapsible card.

    // Class-marker substrings for top-level-child classification
    private const string AssistantTextMarker = "text-assistant-primary";
    private const string UserBubbleMarkerA = "rounded-r7";
    private const string UserBubbleMarkerB = "rounded-bl";
    private const string ToolButtonMarker = "group/tool";
    private const string ChromeButtonMarker = "group/btn";
    private const string InlineCodeMarker = "text-code";
    private const string ListDiscMarker = "list-disc";
    private const string ListDecimalMarker = "list-decimal";

    /// <summary>
    /// Walk the conversation scroll container and extract a structured
    /// message list. Uses the same cached-subtree pattern as
    /// <see cref="CollectTextsCached"/> so a full walk of a long chat stays
    /// sub-second even with ~2000 nodes.
    /// </summary>
    private List<ChatMessage> CollectChatMessages(AutomationElement scrollContainer)
    {
        var messages = new List<ChatMessage>();
        var assistantBuffer = new List<string>();

        try
        {
            var cacheReq = new CacheRequest();
            cacheReq.Add(AutomationElement.NameProperty);
            cacheReq.Add(AutomationElement.ControlTypeProperty);
            cacheReq.Add(AutomationElement.ClassNameProperty);
            cacheReq.TreeScope = TreeScope.Element | TreeScope.Subtree;
            cacheReq.TreeFilter = Automation.RawViewCondition;
            cacheReq.AutomationElementMode = AutomationElementMode.None;

            using (cacheReq.Activate())
            {
                var cachedRoot = scrollContainer.GetUpdatedCache(cacheReq);
                var children = cachedRoot.CachedChildren;
                if (children != null)
                {
                    foreach (AutomationElement top in children)
                        ProcessTopLevelChild(top, messages, assistantBuffer);
                }
                FlushAssistantBuffer(messages, assistantBuffer);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[CollectChatMessages] walk failed — returning partial ({N} msgs)",
                messages.Count);
            FlushAssistantBuffer(messages, assistantBuffer);
        }

        return messages;
    }

    private void ProcessTopLevelChild(
        AutomationElement top,
        List<ChatMessage> messages,
        List<string> assistantBuffer)
    {
        ControlType ct;
        string cls, name;
        try
        {
            ct = top.Cached.ControlType;
            cls = top.Cached.ClassName ?? string.Empty;
            name = top.Cached.Name ?? string.Empty;
        }
        catch { return; }

        // User bubble
        if (IsUserBubbleClass(cls))
        {
            FlushAssistantBuffer(messages, assistantBuffer);
            var content = CollectInlineContent(top).Trim();
            if (!string.IsNullOrEmpty(content))
                messages.Add(new ChatMessage { Role = "user", Content = content });
            return;
        }

        // Tool row — Button with class "group/tool", Name has full label
        if (ct == ControlType.Button && cls.Contains(ToolButtonMarker, StringComparison.Ordinal))
        {
            FlushAssistantBuffer(messages, assistantBuffer);
            if (!string.IsNullOrWhiteSpace(name))
                messages.Add(new ChatMessage { Role = "tool", Content = name.Trim() });
            return;
        }

        // UI chrome button (Copy message / Pin / Rewind / Fork / Show more) — skip entirely
        if (ct == ControlType.Button && cls.Contains(ChromeButtonMarker, StringComparison.Ordinal))
            return;

        // Assistant list (bulleted or numbered)
        if (ct == ControlType.List && cls.Contains(AssistantTextMarker, StringComparison.Ordinal))
        {
            var listMarkdown = CollectListContent(top,
                ordered: cls.Contains(ListDecimalMarker, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(listMarkdown))
                assistantBuffer.Add(listMarkdown);
            return;
        }

        // Assistant paragraph (Group with text-assistant-primary), or unknown
        // container under assistant styling — treat as inline paragraph.
        if (cls.Contains(AssistantTextMarker, StringComparison.Ordinal))
        {
            var para = CollectInlineContent(top).Trim();
            if (!string.IsNullOrEmpty(para))
                assistantBuffer.Add(para);
            return;
        }

        // Unknown top-level element: try as paragraph (safety net so new
        // Claude-Desktop layouts don't silently drop content).
        var fallback = CollectInlineContent(top).Trim();
        if (!string.IsNullOrEmpty(fallback))
            assistantBuffer.Add(fallback);
    }

    private static void FlushAssistantBuffer(List<ChatMessage> messages, List<string> buffer)
    {
        if (buffer.Count == 0) return;
        var content = string.Join("\n\n", buffer).Trim();
        if (!string.IsNullOrEmpty(content))
            messages.Add(new ChatMessage { Role = "assistant", Content = content });
        buffer.Clear();
    }

    private static bool IsUserBubbleClass(string cls)
    {
        // User bubble has the distinct rounded-corner pattern where one corner
        // is squared (rounded-bl-[var(--r1)]) — visual tail of the bubble.
        return cls.Contains(UserBubbleMarkerA, StringComparison.Ordinal)
            && cls.Contains(UserBubbleMarkerB, StringComparison.Ordinal);
    }

    /// <summary>
    /// Collects visible text from a paragraph-like container, walking in
    /// tree order so Text fragments and inline-code Buttons are merged in
    /// the same order they visually appear. Spurious whitespace from the
    /// boundary between adjacent spans is collapsed.
    /// </summary>
    private string CollectInlineContent(AutomationElement element)
    {
        var sb = new StringBuilder(256);
        CollectInlineInner(element, sb, 0, 15);
        return CollapseWhitespace(sb.ToString());
    }

    private void CollectInlineInner(AutomationElement element, StringBuilder sb, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        AutomationElementCollection? children;
        try { children = element.CachedChildren; }
        catch { return; }
        if (children == null) return;

        foreach (AutomationElement child in children)
        {
            ControlType ct;
            string cls, name;
            try
            {
                ct = child.Cached.ControlType;
                cls = child.Cached.ClassName ?? string.Empty;
                name = child.Cached.Name ?? string.Empty;
            }
            catch { continue; }

            if (ct == ControlType.Text)
            {
                if (!string.IsNullOrWhiteSpace(name) && !IsUiChromeText(name))
                    sb.Append(name);
                // Text is a leaf — no recurse.
                continue;
            }

            if (ct == ControlType.Button)
            {
                // Inline code / file-path chip: wrap Name in backticks to
                // preserve the "code span" semantic when rendered as prose.
                if (cls.Contains(InlineCodeMarker, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        sb.Append('`').Append(name.Trim()).Append('`');
                    continue;
                }
                // UI chrome inside a paragraph — skip.
                if (cls.Contains(ChromeButtonMarker, StringComparison.Ordinal))
                    continue;
                // Unknown button — fall through to recurse so we don't miss content.
            }

            // Recurse into other containers (nested Group/Hyperlink/etc.)
            CollectInlineInner(child, sb, depth + 1, maxDepth);
        }
    }

    /// <summary>
    /// Renders a List element as one-item-per-line markdown. Each direct
    /// child is one list item (which may itself contain a nested List —
    /// handled by recursion through <see cref="CollectInlineContent"/>).
    /// </summary>
    private string CollectListContent(AutomationElement list, bool ordered)
    {
        var items = new List<string>();
        AutomationElementCollection? children;
        try { children = list.CachedChildren; }
        catch { return string.Empty; }
        if (children == null) return string.Empty;

        int idx = 1;
        foreach (AutomationElement item in children)
        {
            string itemText = CollectInlineContent(item).Trim();
            if (string.IsNullOrEmpty(itemText)) { idx++; continue; }

            // Chromium already emits a leading "• " for bullet items or
            // "N. " for numbered. Normalize:
            //   - bullet: keep "• " prefix (strip if duplicated, re-add once)
            //   - ordered: prefer explicit "N. " using our counter so nested
            //              structure looks right even if the inline prefix
            //              was a "•" (Claude uses "•" under numbered parents)
            if (ordered)
            {
                // Strip any leading marker ("• " or "1. ") already in the text
                itemText = StripLeadingListMarker(itemText);
                items.Add($"{idx}. {itemText}");
            }
            else
            {
                // Ensure exactly one "• " prefix
                if (!itemText.StartsWith("• ", StringComparison.Ordinal))
                    itemText = "• " + StripLeadingListMarker(itemText);
                items.Add(itemText);
            }
            idx++;
        }

        return string.Join("\n", items);
    }

    private static string StripLeadingListMarker(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // "• " or "◦ " or "∙ "
        if (text.Length > 2 && (text[0] == '•' || text[0] == '◦' || text[0] == '∙') && char.IsWhiteSpace(text[1]))
            return text[2..].TrimStart();
        // "1. " / "23. "
        int i = 0;
        while (i < text.Length && char.IsDigit(text[i])) i++;
        if (i > 0 && i + 1 < text.Length && text[i] == '.' && char.IsWhiteSpace(text[i + 1]))
            return text[(i + 2)..].TrimStart();
        return text;
    }

    /// <summary>
    /// Collapse runs of 2+ ASCII spaces into one (they come from adjacent
    /// Text spans each carrying a trailing/leading space around an inline
    /// code chip that we wrap with backticks). Preserves newlines and
    /// other whitespace — only the spurious double-space is removed.
    /// </summary>
    private static string CollapseWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (var c in s)
        {
            if (c == ' ')
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Slice the full message list by scope:
    ///   full    → all messages
    ///   latest  → last user + following assistant (and any tool rows between)
    ///   summary → last assistant message, truncated to 500 chars
    /// Matches the Phase 13 spec agreed with the Android client.
    /// </summary>
    private static List<ChatMessage> SliceByScope(List<ChatMessage> all, string scope)
    {
        if (all.Count == 0) return all;

        switch (scope)
        {
            case "full":
                return all;

            case "summary":
            case "latest":
            {
                // Find the last "user" message; take everything from it to end.
                int lastUserIdx = -1;
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    if (all[i].Role == "user") { lastUserIdx = i; break; }
                }

                if (lastUserIdx < 0)
                {
                    // No user message yet — return the whole assistant stream
                    // (first message(s) of the conversation).
                    return scope == "summary" ? SummarizeLast(all) : all;
                }

                var slice = all.GetRange(lastUserIdx, all.Count - lastUserIdx);
                return scope == "summary" ? SummarizeLast(slice) : slice;
            }

            default:
                return all;
        }
    }

    private static List<ChatMessage> SummarizeLast(List<ChatMessage> src)
    {
        // summary = last assistant message (truncated) if present, else last message
        var last = src.LastOrDefault(m => m.Role == "assistant") ?? src.LastOrDefault();
        if (last == null) return new List<ChatMessage>();
        var c = last.Content;
        if (c.Length > 500) c = c[..500] + "...";
        return new List<ChatMessage>
        {
            new ChatMessage { Role = last.Role, Content = c }
        };
    }

    /// <summary>
    /// Render a <see cref="ChatMessage"/> list into the legacy single-string
    /// output format: messages joined with "\n\n" (unwrapped prose with
    /// paragraph breaks between turns). Used both as the legacy
    /// <c>content</c> payload field and as the fallback return value of
    /// <see cref="GetOutputAsync"/> for callers that still want a string.
    /// </summary>
    internal static string RenderMessagesAsString(List<ChatMessage> messages)
    {
        if (messages.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        for (int i = 0; i < messages.Count; i++)
        {
            if (i > 0) sb.Append("\n\n");
            sb.Append(messages[i].Content);
        }
        return sb.ToString();
    }

    private List<string> CollectTextFromGroup(AutomationElement group, int depth = 0, int maxDepth = 5)
    {
        var texts = new List<string>();
        if (depth > maxDepth) return texts;

        var child = RawWalker.GetFirstChild(group);
        while (child != null)
        {
            try
            {
                if (child.Current.ControlType == ControlType.Text)
                {
                    var text = child.Current.Name ?? "";
                    if (!string.IsNullOrWhiteSpace(text))
                        texts.Add(text);
                }
                else if (child.Current.ControlType == ControlType.Group)
                {
                    texts.AddRange(CollectTextFromGroup(child, depth + 1, maxDepth));
                }
            }
            catch { }
            child = RawWalker.GetNextSibling(child);
        }
        return texts;
    }

    // --- Sidebar scroll ---

    private static bool ScrollSidebarDown(AutomationElement sidebarOrScroll)
    {
        try
        {
            if (sidebarOrScroll.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
            {
                var scrollPattern = (ScrollPattern)pattern;
                if (scrollPattern.Current.VerticallyScrollable)
                {
                    scrollPattern.ScrollVertical(ScrollAmount.LargeIncrement);
                    return true;
                }
            }

            var scrollable = sidebarOrScroll.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
            if (scrollable != null &&
                scrollable.TryGetCurrentPattern(ScrollPattern.Pattern, out var childPattern))
            {
                var sp = (ScrollPattern)childPattern;
                if (sp.Current.VerticallyScrollable)
                {
                    sp.ScrollVertical(ScrollAmount.LargeIncrement);
                    return true;
                }
            }

            // Last resort: mouse wheel on sidebar center
            var rect = sidebarOrScroll.Current.BoundingRectangle;
            if (!rect.IsEmpty)
            {
                var x = (int)(rect.X + rect.Width / 2);
                var y = (int)(rect.Y + rect.Height / 2);
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
                Thread.Sleep(50);
                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)-360), 0);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[GetSessions] Scroll down failed: {Error}", ex.Message);
        }
        return false;
    }

    private static void ScrollSidebarToTop(AutomationElement sidebarOrScroll)
    {
        try
        {
            if (sidebarOrScroll.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
            {
                var sp = (ScrollPattern)pattern;
                if (sp.Current.VerticallyScrollable)
                {
                    sp.SetScrollPercent(ScrollPattern.NoScroll, 0);
                    return;
                }
            }

            var scrollable = sidebarOrScroll.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
            if (scrollable != null &&
                scrollable.TryGetCurrentPattern(ScrollPattern.Pattern, out var childPattern))
            {
                ((ScrollPattern)childPattern).SetScrollPercent(ScrollPattern.NoScroll, 0);
                return;
            }

            var rect = sidebarOrScroll.Current.BoundingRectangle;
            if (!rect.IsEmpty)
            {
                var x = (int)(rect.X + rect.Width / 2);
                var y = (int)(rect.Y + rect.Height / 2);
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
                Thread.Sleep(50);
                for (int i = 0; i < 5; i++)
                {
                    mouse_event(MOUSEEVENTF_WHEEL, 0, 0, 360, 0);
                    Thread.Sleep(50);
                }
            }
        }
        catch { }
    }

    // ============================================================
    // State detection + text filtering
    // ============================================================

    private void DetectStreamingState()
    {
        try
        {
            var primaryPane = FindPrimaryPane();
            if (primaryPane == null) return;

            var promptBox = FindByClassPrefixRaw(primaryPane, EpitaxyPromptClass);
            if (promptBox == null) return;

            var stopBtn = FindButtonByNameRaw(promptBox, StopButtonName, maxDepth: 5);
            var wasGenerating = IsGenerating;
            IsGenerating = stopBtn != null;

            if (wasGenerating != IsGenerating)
                Log.Debug("Streaming state changed: {IsGenerating}", IsGenerating);
        }
        catch { }
    }

    private static bool IsUiChromeText(string text)
    {
        // Filter out known UI chrome text from output collection.
        return text == "Reply..." ||
               text == "Copy message" ||
               text.StartsWith("Ctrl+") ||
               text.StartsWith("Alt+") ||
               text.StartsWith("⌘") ||
               text == "Forging..." ||
               text == "Thinking..." ||
               text.EndsWith(" tokens") ||
               text == "Session chapters";
    }

    private static bool IsSectionHeader(string text)
    {
        // Section headers appearing as group/label Buttons that are not projects
        return text == "Pinned" ||
               text == "Recent" ||
               text == "Favorites" ||
               text.StartsWith("Filter", StringComparison.Ordinal);
    }

    // ExtractLatestResponse / ExtractSummary were replaced by SliceByScope
    // in Phase 13 — the flat 50-item cap they used truncated long responses
    // in the middle (inline code / list items / headers would be dropped).

    // ============================================================
    // Utility
    // ============================================================

    private static string SafeGetName(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; } catch { return ""; }
    }

    private static string SafeGetClassName(AutomationElement el)
    {
        try { return el.Current.ClassName ?? ""; } catch { return ""; }
    }

    private bool IsWindowValid()
    {
        try
        {
            _ = _claudeWindow!.Current.Name;
            return true;
        }
        catch
        {
            SetDisconnected();
            return false;
        }
    }

    private async Task<bool> EnsureConnected()
    {
        if (IsClaudeRunning && _rootDocument != null) return true;
        return await FindClaudeWindowAsync();
    }

    private void SetDisconnected()
    {
        _claudeWindow = null;
        _rootDocument = null;
        ClaudeStatusChanged?.Invoke(this, false);
    }

    private static void ClickElement(AutomationElement element)
    {
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty) return;
        SetCursorAndClick((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
    }

    private static void SetCursorAndClick(int x, int y)
    {
        System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
        Thread.Sleep(50);

        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
    }

    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, int dwExtraInfo);

    // --- Window state management (minimize/restore for remote operations) ---
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);

    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const uint ASFW_ANY = unchecked((uint)-1);

    private IntPtr GetClaudeWindowHandle()
    {
        try
        {
            if (_claudeWindow == null) return IntPtr.Zero;
            return new IntPtr(_claudeWindow.Current.NativeWindowHandle);
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// Serializes UIA work across the monitor loop and command handlers.
    /// Prevents cross-process COM contention — see _uiaGate comment.
    /// Logs wait and work time when either exceeds a threshold so slow
    /// paths become visible in the log.
    /// </summary>
    private async Task<T> WithUiaGateAsync<T>(string opName, Func<Task<T>> work, T timeoutResult)
    {
        var swWait = Stopwatch.StartNew();
        if (!await _uiaGate.WaitAsync(UiaGateWaitMs))
        {
            Log.Warning("[UIA] {Op} timed out waiting for gate after {Ms}ms",
                opName, swWait.ElapsedMilliseconds);
            return timeoutResult;
        }
        var waited = swWait.ElapsedMilliseconds;
        var swWork = Stopwatch.StartNew();
        try
        {
            return await work();
        }
        finally
        {
            swWork.Stop();
            _uiaGate.Release();
            // Only log if anything interesting happened (avoid spam for
            // the dozens of <50ms monitor-loop ticks that make up the norm)
            if (waited > 500 || swWork.ElapsedMilliseconds > 1500)
            {
                Log.Information("[UIA] {Op} wait={Wait}ms work={Work}ms",
                    opName, waited, swWork.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>
    /// Temporarily restores the Claude window if minimized, runs the action, re-minimizes.
    /// Used for any operation requiring window focus (SendKeys, coordinate clicks).
    /// </summary>
    private async Task<T> WithWindowRestoredAsync<T>(Func<Task<T>> action)
    {
        var hwnd = GetClaudeWindowHandle();
        if (hwnd == IntPtr.Zero) return await action();

        bool wasMinimized;
        IntPtr previousForeground;
        try
        {
            wasMinimized = IsIconic(hwnd);
            previousForeground = GetForegroundWindow();
        }
        catch { return await action(); }

        if (wasMinimized)
        {
            try
            {
                AllowSetForegroundWindow(ASFW_ANY);
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
                await Task.Delay(300);
                Log.Verbose("[Window] Restored Claude window for remote operation");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Window] Failed to restore Claude window");
            }
        }

        try
        {
            return await action();
        }
        finally
        {
            if (wasMinimized)
            {
                try
                {
                    await Task.Delay(100);
                    ShowWindow(hwnd, SW_MINIMIZE);
                    if (previousForeground != IntPtr.Zero && previousForeground != hwnd)
                        SetForegroundWindow(previousForeground);
                    Log.Verbose("[Window] Re-minimized Claude window, focus returned");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Window] Failed to re-minimize Claude window");
                }
            }
        }
    }

    private static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        StopMonitoring();
        StopProcessWatcher();
    }
}
