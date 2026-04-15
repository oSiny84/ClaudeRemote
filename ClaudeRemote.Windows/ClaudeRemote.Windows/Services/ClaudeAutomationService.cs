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
/// UI element mapping is based on docs/ClaudeUI_Map.md analysis.
/// </summary>
public class ClaudeAutomationService : IClaudeAutomationService, IDisposable
{
    private AutomationElement? _claudeWindow;
    private AutomationElement? _mainDocument;
    private CancellationTokenSource? _monitoringCts;
    private CancellationTokenSource? _processWatcherCts;
    private string _lastOutputHash = string.Empty;
    private readonly int _pollingIntervalMs = 500;
    private const int ProcessCheckIntervalMs = 5000;
    private const int MaxSessionsToCollect = 10;
    private const int ScrollPassCount = 3; // Max scroll attempts to find more sessions
    private bool _wasClaudeRunning;

    // Cached element references (invalidated on window change)
    private AutomationElement? _cachedMainContent;
    private AutomationElement? _cachedSidebar;
    private AutomationElement? _cachedTurnForm;
    private AutomationElement? _cachedInputField;

    // Process name for Claude desktop app
    private const string ClaudeProcessName = "claude";

    public bool IsClaudeRunning => _claudeWindow != null && IsWindowValid();
    public string CurrentMode { get; private set; } = "chat";
    public bool IsGenerating { get; private set; }
    public string? LastAskUserQuestionPrompt { get; private set; }
    public string? LastPermissionPrompt { get; private set; }
    public string? LastButtonCategory { get; private set; }

    // Cached action buttons for change detection
    private List<string> _lastButtonTexts = new();
    private bool _lastTurnFormWasEmpty;
    private DateTime _lastDumpTime = DateTime.MinValue;

    // Known permission button name prefixes (button Name includes keyboard shortcut suffix)
    private static readonly string[] PermissionButtonPrefixes = new[]
    {
        "Deny", "Allow once", "Always allow for project"
    };

    // Input-area buttons to exclude even within turn-form proximity zone
    private static readonly HashSet<string> InputAreaButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        "Interrupt", "Send", "Submit", "Stop", "Resume", "Cancel"
    };

    // AskUserQuestion container identification
    // The container Group has ClassName containing "rounded-xl" and Name containing "Skip"
    private const string AskUserQuestionClassMarker = "rounded-xl";
    private const string AskUserQuestionNameMarker = "Skip";

    public event EventHandler<string>? OutputChanged;
    public event EventHandler<bool>? ClaudeStatusChanged;
    public event EventHandler<List<ButtonInfo>>? ActionButtonsChanged;

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

                // Find the process with a visible main window
                Process? mainProcess = null;
                foreach (var p in processes)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        mainProcess = p;
                        break;
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

                // Verify it's the Electron app
                if (className != "Chrome_WidgetWin_1")
                {
                    Log.Warning("Unexpected window class: {Class}", className);
                }

                // Find the main content Document (Name='Claude', AID='RootWebArea')
                _mainDocument = FindMainDocument();
                if (_mainDocument == null)
                {
                    Log.Warning("Main document (RootWebArea) not found");
                    SetDisconnected();
                    return false;
                }

                InvalidateCachedElements();
                ClaudeStatusChanged?.Invoke(this, true);
                Log.Information("Claude main document located successfully");

                // Auto-switch to Code mode if not already
                if (!string.Equals(CurrentMode, "code", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Auto-switching to Code mode (current: {Mode})", CurrentMode);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500); // Let UI settle
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

    public async Task<string> GetOutputAsync(string scope = "latest")
    {
        if (!await EnsureConnected()) return string.Empty;

        return await Task.Run(() =>
        {
            try
            {
                var mainContent = GetMainContent();
                if (mainContent == null)
                {
                    Log.Debug("main-content element not found");
                    return string.Empty;
                }

                // Find cli-button-container inside main-content
                var cliContainer = FindByAutomationIdRaw(mainContent, "cli-button-container");
                var searchRoot = cliContainer ?? mainContent;

                // Collect all Text elements
                var textCondition = new PropertyCondition(
                    AutomationElement.ControlTypeProperty, ControlType.Text);
                var textElements = searchRoot.FindAll(TreeScope.Descendants, textCondition);

                var allTexts = new List<string>();
                var messageTexts = new List<string>();

                foreach (AutomationElement el in textElements)
                {
                    try
                    {
                        var name = el.Current.Name;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        // Filter out UI chrome text
                        if (IsUiChromeText(name)) continue;

                        allTexts.Add(name);
                    }
                    catch { }
                }

                // Detect streaming state
                DetectStreamingState(mainContent);

                return scope switch
                {
                    "full" => string.Join("\n", allTexts),
                    "latest" => ExtractLatestResponse(allTexts),
                    "summary" => ExtractSummary(allTexts),
                    _ => string.Join("\n", allTexts)
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to get Claude output");
                return string.Empty;
            }
        });
    }

    public async Task<bool> SendInputAsync(string text)
    {
        if (!await EnsureConnected()) return false;

        return await WithWindowRestoredAsync(() => Task.Run(() =>
        {
            try
            {
                // Wait for UI to settle after button click or state change
                Thread.Sleep(500);

                // Always fresh-find the input field (never use cache)
                _cachedTurnForm = null;
                _cachedInputField = null;
                var inputField = GetInputField();
                bool useSubmitButton = false;

                if (inputField == null)
                {
                    // Fallback: look for "Other option" Edit field directly in main-content
                    // This appears after clicking "Type something else..." button
                    // Don't rely on FindAskUserQuestionContainer — container Name changes after click
                    Log.Information("[SendInput] 'Enter your turn' not found — searching for 'Other option' Edit field");
                    var mainContent = GetMainContent();
                    if (mainContent != null)
                    {
                        inputField = FindEditByName(mainContent, "Other option");
                        if (inputField != null)
                        {
                            useSubmitButton = true;
                            Log.Information("[SendInput] Found 'Other option' Edit field — will use Submit button");
                        }
                    }

                    if (inputField == null)
                    {
                        Log.Warning("[SendInput] No input field found at all");
                        return false;
                    }
                }
                else
                {
                    Log.Information("[SendInput] Input field found: Name='{Name}'", inputField.Current.Name);
                }

                // Focus the input field
                try
                {
                    inputField.SetFocus();
                    Thread.Sleep(150);
                    Log.Debug("[SendInput] SetFocus succeeded");
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

                // Use clipboard for reliable Unicode/Korean text input
                var thread = new Thread(() =>
                {
                    System.Windows.Forms.Clipboard.SetText(text);
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
                Log.Debug("[SendInput] Clipboard set: {Len} chars", text.Length);

                // Select all existing text and paste
                System.Windows.Forms.SendKeys.SendWait("^a");
                Thread.Sleep(50);
                System.Windows.Forms.SendKeys.SendWait("^v");
                Thread.Sleep(100);

                if (useSubmitButton)
                {
                    // Find and click the Submit button near the Edit field
                    Thread.Sleep(100);
                    var mainContent2 = GetMainContent();
                    var submitBtn = mainContent2 != null
                        ? FindButtonByNameRaw(mainContent2, "Submit")
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
                    // Press Enter to submit (standard input)
                    System.Windows.Forms.SendKeys.SendWait("{ENTER}");
                }

                Log.Information("[SendInput] Input sent to Claude: {TextLength} chars", text.Length);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SendInput] Failed to send input to Claude");
                return false;
            }
        }));
    }

    public async Task<bool> SwitchModeAsync(string targetMode)
    {
        if (!await EnsureConnected()) return false;

        return await WithWindowRestoredAsync(() => Task.Run(() =>
        {
            try
            {
                if (_claudeWindow == null) return false;

                // Mode tabs are RadioButtons with Name = 'Chat', 'Cowork', 'Code'
                var condition = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
                    new PropertyCondition(AutomationElement.NameProperty, targetMode, PropertyConditionFlags.IgnoreCase));

                var tabElement = _claudeWindow.FindFirst(TreeScope.Descendants, condition);
                if (tabElement == null)
                {
                    Log.Warning("Mode tab '{Mode}' not found", targetMode);
                    return false;
                }

                // Try SelectionItemPattern first (RadioButton)
                if (tabElement.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selPattern))
                {
                    ((SelectionItemPattern)selPattern).Select();
                    CurrentMode = targetMode.ToLowerInvariant();
                    InvalidateCachedElements();
                    Log.Information("Switched to mode: {Mode}", targetMode);
                    return true;
                }

                // Fallback: InvokePattern
                if (tabElement.TryGetCurrentPattern(InvokePattern.Pattern, out var invPattern))
                {
                    ((InvokePattern)invPattern).Invoke();
                    CurrentMode = targetMode.ToLowerInvariant();
                    InvalidateCachedElements();
                    Log.Information("Switched to mode: {Mode} (via Invoke)", targetMode);
                    return true;
                }

                // Last resort: click
                var rect = tabElement.Current.BoundingRectangle;
                if (!rect.IsEmpty)
                {
                    var x = (int)(rect.X + rect.Width / 2);
                    var y = (int)(rect.Y + rect.Height / 2);
                    SetCursorAndClick(x, y);
                    CurrentMode = targetMode.ToLowerInvariant();
                    InvalidateCachedElements();
                    Log.Information("Switched to mode: {Mode} (via click)", targetMode);
                    return true;
                }

                Log.Warning("No interaction pattern available for mode tab '{Mode}'", targetMode);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to switch mode to {Mode}", targetMode);
                return false;
            }
        }));
    }

    public async Task<List<SessionInfo>> GetSessionsAsync()
    {
        if (!await EnsureConnected()) return new List<SessionInfo>();

        return await Task.Run(() =>
        {
            var allSessions = new List<SessionInfo>();
            var knownNames = new HashSet<string>();

            try
            {
                var sidebar = GetSidebar();
                if (sidebar == null)
                {
                    Log.Warning("[GetSessions] Sidebar element not found");
                    return allSessions;
                }

                Log.Debug("[GetSessions] Sidebar found: Name='{Name}' ControlType={Type}",
                    sidebar.Current.Name, sidebar.Current.ControlType.ProgrammaticName);

                // Collect sessions with scrolling to discover more beyond the viewport
                for (int pass = 0; pass <= ScrollPassCount; pass++)
                {
                    var newSessions = CollectVisibleSessions(sidebar);

                    // Merge: deduplicate by name, preserve order
                    foreach (var s in newSessions)
                    {
                        if (knownNames.Add(s.Name))
                        {
                            s.Id = $"session_{allSessions.Count}";
                            s.IsActive = allSessions.Count == 0;
                            allSessions.Add(s);
                        }
                    }

                    Log.Information("[GetSessions] Pass {Pass}: found {New} visible, total unique {Total}",
                        pass, newSessions.Count, allSessions.Count);

                    if (allSessions.Count >= MaxSessionsToCollect)
                    {
                        allSessions = allSessions.Take(MaxSessionsToCollect).ToList();
                        break;
                    }

                    // If we haven't reached the max, scroll down to reveal more
                    if (pass < ScrollPassCount)
                    {
                        if (!ScrollSidebarDown(sidebar))
                            break; // Scroll failed or not possible
                        Thread.Sleep(300); // Wait for Chromium to render new items
                    }
                }

                // Scroll back to top after collection
                if (allSessions.Count > 3)
                    ScrollSidebarToTop(sidebar);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[GetSessions] Failed to get sessions");
            }

            Log.Information("[GetSessions] Returning {Count} sessions (mode={Mode})",
                allSessions.Count, CurrentMode);
            return allSessions;
        });
    }

    /// <summary>
    /// Collect sessions currently visible in the sidebar viewport.
    /// </summary>
    private List<SessionInfo> CollectVisibleSessions(AutomationElement sidebar)
    {
        // Try ControlView first, fall back to RawView
        var controlViewTexts = sidebar.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));

        List<AutomationElement> textElements;
        if (controlViewTexts.Count > 0)
        {
            textElements = new List<AutomationElement>();
            foreach (AutomationElement el in controlViewTexts)
                textElements.Add(el);
        }
        else
        {
            textElements = CollectTextElementsRaw(sidebar);
        }

        var settingsRect = FindSettingsButtonRect(sidebar);
        var sessions = new List<SessionInfo>();
        bool foundSessionsHeader = false;

        foreach (var textEl in textElements)
        {
            try
            {
                var name = textEl.Current.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (!settingsRect.IsEmpty)
                {
                    var textRect = textEl.Current.BoundingRectangle;
                    if (!textRect.IsEmpty && settingsRect.Contains(textRect))
                        continue;
                }

                if (name == "Sessions")
                {
                    foundSessionsHeader = true;
                    continue;
                }

                if (!foundSessionsHeader) continue;

                if (IsDateHeader(name) || IsUiLabel(name)) continue;
                if (name.StartsWith("Ctrl+") || name.StartsWith("Alt+")) continue;

                sessions.Add(new SessionInfo { Name = name });
            }
            catch { }
        }

        // Fallback: if no "Sessions" header found
        if (!foundSessionsHeader && sessions.Count == 0)
        {
            foreach (var textEl in textElements)
            {
                try
                {
                    var name = textEl.Current.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!settingsRect.IsEmpty)
                    {
                        var textRect = textEl.Current.BoundingRectangle;
                        if (!textRect.IsEmpty && settingsRect.Contains(textRect))
                            continue;
                    }
                    if (IsDateHeader(name) || IsUiLabel(name)) continue;
                    if (name.StartsWith("Ctrl+") || name.StartsWith("Alt+")) continue;
                    if (IsUiChromeText(name)) continue;
                    sessions.Add(new SessionInfo { Name = name });
                }
                catch { }
            }
        }

        return sessions;
    }

    /// <summary>
    /// Scroll the sidebar down to reveal more session items.
    /// Uses ScrollPattern if available, otherwise sends mouse wheel events.
    /// </summary>
    private static bool ScrollSidebarDown(AutomationElement sidebar)
    {
        try
        {
            // Try ScrollPattern first
            if (sidebar.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
            {
                var scrollPattern = (ScrollPattern)pattern;
                if (scrollPattern.Current.VerticallyScrollable)
                {
                    scrollPattern.ScrollVertical(ScrollAmount.LargeIncrement);
                    return true;
                }
            }

            // Fallback: find a scrollable child
            var scrollable = sidebar.FindFirst(TreeScope.Descendants,
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
            var rect = sidebar.Current.BoundingRectangle;
            if (!rect.IsEmpty)
            {
                var x = (int)(rect.X + rect.Width / 2);
                var y = (int)(rect.Y + rect.Height / 2);
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
                Thread.Sleep(50);
                // Scroll down: negative delta
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

    /// <summary>
    /// Scroll sidebar back to top after session collection.
    /// </summary>
    private static void ScrollSidebarToTop(AutomationElement sidebar)
    {
        try
        {
            if (sidebar.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern))
            {
                var sp = (ScrollPattern)pattern;
                if (sp.Current.VerticallyScrollable)
                {
                    sp.SetScrollPercent(ScrollPattern.NoScroll, 0);
                    return;
                }
            }

            var scrollable = sidebar.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
            if (scrollable != null &&
                scrollable.TryGetCurrentPattern(ScrollPattern.Pattern, out var childPattern))
            {
                ((ScrollPattern)childPattern).SetScrollPercent(ScrollPattern.NoScroll, 0);
                return;
            }

            // Last resort: mouse wheel up several times
            var rect = sidebar.Current.BoundingRectangle;
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

    /// <summary>
    /// Walk the RawView tree under root, collecting all elements that are text-like
    /// (ControlType.Text, or non-empty Name on leaf nodes with no children).
    /// This handles Chromium elements with IsControlElement=false that FindAll skips.
    /// </summary>
    private static List<AutomationElement> CollectTextElementsRaw(AutomationElement root, int maxDepth = 20, int maxNodes = 3000)
    {
        var results = new List<AutomationElement>();
        int visited = 0;
        CollectTextElementsRawWalk(root, results, 0, maxDepth, ref visited, maxNodes);
        return results;
    }

    private static void CollectTextElementsRawWalk(AutomationElement element, List<AutomationElement> results, int depth, int maxDepth, ref int visited, int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return;
        visited++;

        try
        {
            var controlType = element.Current.ControlType;
            var name = element.Current.Name;

            // Collect if it's a Text element, or a named leaf with no children
            if (controlType == ControlType.Text && !string.IsNullOrWhiteSpace(name))
            {
                results.Add(element);
                return; // Text nodes are leaf nodes
            }
        }
        catch { return; }

        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            CollectTextElementsRawWalk(child, results, depth + 1, maxDepth, ref visited, maxNodes);
            child = RawWalker.GetNextSibling(child);
        }
    }

    private static System.Windows.Rect FindSettingsButtonRect(AutomationElement sidebar)
    {
        // Try ControlView first
        var settingsBtn = sidebar.FindFirst(TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "radix-_r_34_")));

        // Fallback: find by name pattern in ControlView
        if (settingsBtn == null)
        {
            var allButtons = sidebar.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement btn in allButtons)
            {
                if (btn.Current.Name?.Contains("Settings") == true)
                {
                    settingsBtn = btn;
                    break;
                }
            }
        }

        // Fallback: RawView walk for Settings button
        if (settingsBtn == null)
        {
            Log.Debug("[GetSessions] Settings button not in ControlView, trying RawView");
            settingsBtn = FindSettingsButtonRaw(sidebar);
        }

        var rect = settingsBtn?.Current.BoundingRectangle ?? System.Windows.Rect.Empty;
        Log.Debug("[GetSessions] Settings button found={Found}", settingsBtn != null);
        return rect;
    }

    private static AutomationElement? FindSettingsButtonRaw(AutomationElement root, int maxDepth = 15, int maxNodes = 2000)
    {
        int visited = 0;
        return WalkForSettingsButton(root, 0, maxDepth, ref visited, maxNodes);
    }

    private static AutomationElement? WalkForSettingsButton(AutomationElement element, int depth, int maxDepth, ref int visited, int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return null;
        visited++;

        try
        {
            if (element.Current.ControlType == ControlType.Button &&
                element.Current.Name?.Contains("Settings") == true)
                return element;
        }
        catch { return null; }

        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            var found = WalkForSettingsButton(child, depth + 1, maxDepth, ref visited, maxNodes);
            if (found != null) return found;
            child = RawWalker.GetNextSibling(child);
        }
        return null;
    }

    public async Task<bool> SelectSessionAsync(string sessionId)
    {
        if (!await EnsureConnected()) return false;

        return await WithWindowRestoredAsync(() => Task.Run(() =>
        {
            try
            {
                var idx = int.Parse(sessionId.Replace("session_", ""));
                var sessions = GetSessionTextElements();
                if (idx < 0 || idx >= sessions.Count) return false;

                var sessionEl = sessions[idx];
                // Click on the session text's parent group to select it
                var walker = TreeWalker.RawViewWalker;
                var parent = walker.GetParent(sessionEl);
                if (parent != null)
                {
                    ClickElement(parent);
                    InvalidateCachedElements();
                    Log.Information("Selected session: {Id}", sessionId);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to select session {Id}", sessionId);
                return false;
            }
        }));
    }

    public async Task<bool> AddSessionAsync()
    {
        if (!await EnsureConnected()) return false;

        return await WithWindowRestoredAsync(() => Task.Run(() =>
        {
            try
            {
                if (_claudeWindow == null) return false;

                // Find "New session" hyperlink
                var condition = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                    new PropertyCondition(AutomationElement.NameProperty, "New session"));

                var newSessionLink = _claudeWindow.FindFirst(TreeScope.Descendants, condition);
                if (newSessionLink != null)
                {
                    if (newSessionLink.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                    {
                        ((InvokePattern)pattern).Invoke();
                        InvalidateCachedElements();
                        Log.Information("New session created");
                        return true;
                    }

                    // Fallback: click
                    ClickElement(newSessionLink);
                    InvalidateCachedElements();
                    Log.Information("New session created (via click)");
                    return true;
                }

                // Fallback: use keyboard shortcut Ctrl+Shift+O
                System.Windows.Forms.SendKeys.SendWait("^+o");
                InvalidateCachedElements();
                Log.Information("New session created (via shortcut)");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to add session");
                return false;
            }
        }));
    }

    public async Task<List<ProjectInfo>> GetProjectsAsync()
    {
        if (!await EnsureConnected()) return new List<ProjectInfo>();

        return await Task.Run(() =>
        {
            var projects = new List<ProjectInfo>();
            try
            {
                var sidebar = GetSidebar();
                if (sidebar == null) return projects;

                // In Code mode, projects appear as Buttons in the sidebar
                // The project button has the folder name as its Name
                var buttons = sidebar.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                int index = 0;
                foreach (AutomationElement btn in buttons)
                {
                    try
                    {
                        var name = btn.Current.Name;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        // Project buttons have radix-* AutomationIds and folder-like names
                        var aid = btn.Current.AutomationId;
                        if (!string.IsNullOrEmpty(aid) && aid.StartsWith("radix-") &&
                            !name.Contains("Settings") && !name.Contains(","))
                        {
                            projects.Add(new ProjectInfo
                            {
                                Id = $"project_{index}",
                                Name = name,
                                Path = "", // Path not directly available from UI
                                IsActive = false
                            });
                            index++;
                        }
                    }
                    catch { }
                }

                // Also check main-content bottom for current project button
                var mainContent = GetMainContent();
                if (mainContent != null)
                {
                    var turnForm = GetTurnForm();
                    if (turnForm != null)
                    {
                        var formButtons = turnForm.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                        foreach (AutomationElement btn in formButtons)
                        {
                            try
                            {
                                var name = btn.Current.Name;
                                // Project path buttons look like "C:\path\to\project"
                                if (!string.IsNullOrEmpty(name) && name.Contains(":\\"))
                                {
                                    // Mark the active project
                                    var existingIdx = projects.FindIndex(p =>
                                        name.EndsWith(p.Name) || name.EndsWith(p.Name + "\\"));
                                    if (existingIdx >= 0)
                                    {
                                        projects[existingIdx].IsActive = true;
                                        projects[existingIdx].Path = name;
                                    }
                                    else
                                    {
                                        var folderName = System.IO.Path.GetFileName(name.TrimEnd('\\'));
                                        projects.Insert(0, new ProjectInfo
                                        {
                                            Id = $"project_{index}",
                                            Name = folderName,
                                            Path = name,
                                            IsActive = true
                                        });
                                        index++;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to get projects");
            }
            return projects;
        });
    }

    public async Task<bool> SelectProjectAsync(string projectId)
    {
        if (!await EnsureConnected()) return false;

        return await WithWindowRestoredAsync(() => Task.Run(() =>
        {
            try
            {
                var sidebar = GetSidebar();
                if (sidebar == null) return false;

                var idx = int.Parse(projectId.Replace("project_", ""));
                var buttons = sidebar.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

                int projectIndex = 0;
                foreach (AutomationElement btn in buttons)
                {
                    var name = btn.Current.Name;
                    var aid = btn.Current.AutomationId;
                    if (!string.IsNullOrEmpty(aid) && aid.StartsWith("radix-") &&
                        !string.IsNullOrWhiteSpace(name) &&
                        !name.Contains("Settings") && !name.Contains(","))
                    {
                        if (projectIndex == idx)
                        {
                            ClickElement(btn);
                            InvalidateCachedElements();
                            Log.Information("Selected project: {Name}", name);
                            return true;
                        }
                        projectIndex++;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to select project {Id}", projectId);
                return false;
            }
        }));
    }

    // --- Action Button Detection (Phase 6) ---

    public async Task<List<ButtonInfo>> GetActionButtonsAsync()
    {
        if (!await EnsureConnected()) return new List<ButtonInfo>();

        return await Task.Run(() =>
        {
            try
            {
                // Invalidate stale turn-form cache
                var turnForm = GetTurnForm();
                if (turnForm != null)
                {
                    try
                    {
                        var rect = turnForm.Current.BoundingRectangle;
                        if (rect.IsEmpty) { _cachedTurnForm = null; turnForm = GetTurnForm(); }
                    }
                    catch { _cachedTurnForm = null; turnForm = null; }
                }

                // When turn-form is absent, AskUserQuestion may be active
                bool turnFormAbsent = turnForm == null;
                if (turnFormAbsent && !_lastTurnFormWasEmpty)
                {
                    Log.Information("[ActionButtons] turn-form absent — scanning for AskUserQuestion container");
                    _lastTurnFormWasEmpty = true;
                }
                else if (!turnFormAbsent && _lastTurnFormWasEmpty)
                {
                    Log.Information("[ActionButtons] turn-form restored");
                    _lastTurnFormWasEmpty = false;
                }

                // Scan main-content for buttons (permission dialog OR AskUserQuestion)
                var mainContent = GetMainContent();
                if (mainContent == null) return new List<ButtonInfo>();

                // Check permission dialog first (Deny / Allow once / Always allow)
                var permButtons = DetectPermissionButtons(mainContent);
                if (permButtons.Count > 0)
                {
                    LastButtonCategory = "permission";
                    return permButtons;
                }

                // Then check AskUserQuestion buttons
                var askButtons = DetectAskUserQuestionButtons(mainContent);
                LastButtonCategory = askButtons.Count > 0 ? "selection" : null;
                return askButtons;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to get action buttons");
                return new List<ButtonInfo>();
            }
        });
    }

    /// <summary>
    /// Finds the AskUserQuestion container by its structural signature:
    /// a Group element whose ClassName contains "rounded-xl" and Name contains "Skip".
    /// Then collects its direct Button children, excluding "Skip" and "Type something else...".
    /// </summary>
    private List<ButtonInfo> DetectAskUserQuestionButtons(AutomationElement searchRoot)
    {
        var container = FindAskUserQuestionContainer(searchRoot);
        if (container == null)
        {
            LastAskUserQuestionPrompt = null;
            return new List<ButtonInfo>();
        }

        // Collect direct children: first Text = prompt, Buttons = choices
        var buttons = new List<ButtonInfo>();
        string? prompt = null;
        int index = 0;
        var child = RawWalker.GetFirstChild(container);
        while (child != null)
        {
            try
            {
                var ct = child.Current.ControlType;
                if (ct == ControlType.Text && prompt == null)
                {
                    // First Text child is the question prompt
                    var text = child.Current.Name ?? "";
                    if (!string.IsNullOrWhiteSpace(text))
                        prompt = text;
                }
                else if (ct == ControlType.Button)
                {
                    var name = child.Current.Name ?? "";
                    if (!string.IsNullOrWhiteSpace(name))
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
        if (buttons.Count > 0)
        {
            var names = string.Join(", ", buttons.Select(b => $"'{b.Text}'"));
            Log.Information("[AskUserQ] Prompt='{Prompt}', {Count} buttons: [{Names}]", prompt ?? "(none)", buttons.Count, names);
        }
        return buttons;
    }

    /// <summary>
    /// Detects the permission dialog buttons (Deny / Allow once / Always allow).
    /// The permission dialog appears in the bottom area with buttons whose names include
    /// keyboard shortcuts (e.g. "Deny Esc", "Allow once Enter").
    /// Returns cleaned button names (without shortcut suffixes) and extracts the prompt text.
    /// </summary>
    private List<ButtonInfo> DetectPermissionButtons(AutomationElement searchRoot)
    {
        // Quick check: search for "Allow once" text in RawView to confirm permission dialog exists
        var allowOnce = FindByNameRaw(searchRoot, "Allow once");
        if (allowOnce == null)
        {
            LastPermissionPrompt = null;
            return new List<ButtonInfo>();
        }

        // Find the permission dialog container:
        // Walk up from "Allow once" text → parent Button → parent container Group
        var allowOnceButton = RawWalker.GetParent(allowOnce); // Button "Allow once Enter"
        if (allowOnceButton == null) { LastPermissionPrompt = null; return new List<ButtonInfo>(); }
        var container = RawWalker.GetParent(allowOnceButton);  // Container Group
        if (container == null) { LastPermissionPrompt = null; return new List<ButtonInfo>(); }

        // Collect prompt text and buttons from container's children
        var buttons = new List<ButtonInfo>();
        var promptParts = new List<string>();
        int index = 0;
        var child = RawWalker.GetFirstChild(container);
        while (child != null)
        {
            try
            {
                var ct = child.Current.ControlType;
                if (ct == ControlType.Group)
                {
                    // Groups before buttons contain prompt text (e.g. "Allow Claude to Fetch url?")
                    // and command detail JSON
                    var groupTexts = CollectTextFromGroup(child);
                    if (groupTexts.Count > 0 && buttons.Count == 0)
                        promptParts.AddRange(groupTexts);
                }
                else if (ct == ControlType.Button)
                {
                    var rawName = child.Current.Name ?? "";
                    // Strip keyboard shortcut suffixes: "Deny Esc" → "Deny", "Allow once Enter" → "Allow once"
                    var cleanName = CleanPermissionButtonName(rawName);
                    if (!string.IsNullOrWhiteSpace(cleanName) && IsPermissionButton(cleanName))
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
            }
            catch { }
            child = RawWalker.GetNextSibling(child);
        }

        LastPermissionPrompt = promptParts.Count > 0 ? string.Join("", promptParts) : null;

        if (buttons.Count > 0)
        {
            var names = string.Join(", ", buttons.Select(b => $"'{b.Text}'"));
            Log.Information("[Permission] Prompt='{Prompt}', {Count} buttons: [{Names}]",
                LastPermissionPrompt ?? "(none)", buttons.Count, names);
        }
        return buttons;
    }

    /// <summary>
    /// Collects all Text element values from a Group and its descendants (shallow).
    /// Used to extract permission prompt text like "Allow Claude to Fetch url?"
    /// </summary>
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

    /// <summary>
    /// Strips keyboard shortcut suffixes from permission button names.
    /// "Deny Esc" → "Deny", "Allow once Enter" → "Allow once",
    /// "Always allow for project (local) Ctrl Enter" → "Always allow for project (local)"
    /// </summary>
    private static string CleanPermissionButtonName(string rawName)
    {
        // Known shortcut suffixes to strip
        string[] suffixes = { " Ctrl Enter", " Enter", " Esc" };
        foreach (var suffix in suffixes)
        {
            if (rawName.EndsWith(suffix, StringComparison.Ordinal))
                return rawName.Substring(0, rawName.Length - suffix.Length);
        }
        return rawName;
    }

    /// <summary>
    /// Checks if a cleaned button name is a known permission button.
    /// </summary>
    private static bool IsPermissionButton(string cleanName)
    {
        return Array.Exists(PermissionButtonPrefixes, prefix =>
            cleanName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Walks the RawView tree to find the AskUserQuestion container Group.
    /// Identified by: ClassName contains "rounded-xl" AND Name contains "Skip".
    /// </summary>
    private AutomationElement? FindAskUserQuestionContainer(AutomationElement root, int depth = 0, int maxDepth = 20)
    {
        if (depth > maxDepth) return null;

        try
        {
            var cls = root.Current.ClassName ?? "";
            var name = root.Current.Name ?? "";
            if (cls.Contains(AskUserQuestionClassMarker) && name.Contains(AskUserQuestionNameMarker))
                return root;
        }
        catch { return null; }

        var child = RawWalker.GetFirstChild(root);
        while (child != null)
        {
            var found = FindAskUserQuestionContainer(child, depth + 1, maxDepth);
            if (found != null) return found;
            child = RawWalker.GetNextSibling(child);
        }
        return null;
    }

    /// <summary>
    /// Find an Edit control by Name anywhere in the raw tree.
    /// Used to find "Other option" input after "Type something else..." click.
    /// </summary>
    private AutomationElement? FindEditByName(AutomationElement root, string name, int depth = 0, int maxDepth = 25)
    {
        if (depth > maxDepth) return null;

        try
        {
            if (root.Current.ControlType == ControlType.Edit && root.Current.Name == name)
                return root;
        }
        catch { return null; }

        var child = RawWalker.GetFirstChild(root);
        while (child != null)
        {
            var found = FindEditByName(child, name, depth + 1, maxDepth);
            if (found != null) return found;
            child = RawWalker.GetNextSibling(child);
        }
        return null;
    }

    /// <summary>
    /// Find a Button by exact Name anywhere in the raw tree.
    /// Used to find Submit button for "Type something else..." flow.
    /// </summary>
    private AutomationElement? FindButtonByNameRaw(AutomationElement root, string buttonName, int depth = 0, int maxDepth = 25)
    {
        if (depth > maxDepth) return null;

        try
        {
            if (root.Current.ControlType == ControlType.Button && root.Current.Name == buttonName)
                return root;
        }
        catch { return null; }

        var child = RawWalker.GetFirstChild(root);
        while (child != null)
        {
            var found = FindButtonByNameRaw(child, buttonName, depth + 1, maxDepth);
            if (found != null) return found;
            child = RawWalker.GetNextSibling(child);
        }
        return null;
    }

    /// <summary>
    /// Diagnostic: dumps the full RawView element tree to a StringBuilder.
    /// Each line shows depth indentation, ControlType, Name, AutomationId, ClassName, BoundingRectangle.
    /// </summary>
    private void DumpElementTree(AutomationElement element, StringBuilder sb, int depth, int maxDepth, ref int visited, int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return;
        visited++;

        try
        {
            var indent = new string(' ', depth * 2);
            var ct = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            var name = element.Current.Name ?? "";
            var aid = element.Current.AutomationId ?? "";
            var cls = element.Current.ClassName ?? "";
            var rect = element.Current.BoundingRectangle;

            var nameStr = name.Length > 80 ? name.Substring(0, 80) + "..." : name;
            sb.AppendLine($"{indent}[{ct}] Name=\"{nameStr}\" AID=\"{aid}\" Class=\"{cls}\" Rect=[{rect.X:F0},{rect.Y:F0},{rect.Width:F0}x{rect.Height:F0}]");
        }
        catch { return; }

        var child = RawWalker.GetFirstChild(element);
        while (child != null && visited <= maxNodes)
        {
            DumpElementTree(child, sb, depth + 1, maxDepth, ref visited, maxNodes);
            child = RawWalker.GetNextSibling(child);
        }
    }

    public async Task<bool> ClickButtonAsync(string buttonId)
    {
        if (!await EnsureConnected()) return false;

        return await WithWindowRestoredAsync(() => Task.Run(() =>
        {
            try
            {
                // Re-collect fresh buttons using same detection logic
                var freshButtons = GetActionButtonsSync();
                var idx = int.Parse(buttonId.Replace("btn_", ""));

                if (idx < 0 || idx >= freshButtons.Count)
                {
                    Log.Warning("Action button index {Idx} out of range (have {Count})", idx, freshButtons.Count);
                    return false;
                }

                var target = freshButtons[idx];
                var targetName = target.Text;
                var targetElement = target.Element;

                if (targetElement != null)
                {
                    // Try InvokePattern first
                    if (targetElement.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                    {
                        ((InvokePattern)pattern).Invoke();
                        Log.Information("Clicked action button: '{Name}' (InvokePattern)", targetName);
                        // Invalidate all cached elements — UI will change after click
                        _cachedTurnForm = null;
                        _cachedInputField = null;
                        return true;
                    }

                    // Fallback: coordinate click
                    ClickElement(targetElement);
                    Log.Information("Clicked action button: '{Name}' (coordinate click)", targetName);
                    // Invalidate all cached elements — UI will change after click
                    _cachedTurnForm = null;
                    _cachedInputField = null;
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
    }

    /// <summary>
    /// Synchronous version of button detection logic (used inside ClickButtonAsync's Task.Run).
    /// Mirrors the same structural detection strategy as GetActionButtonsAsync.
    /// </summary>
    private List<ButtonInfo> GetActionButtonsSync()
    {
        var mainContent = GetMainContent();
        if (mainContent == null) return new List<ButtonInfo>();

        // Check permission buttons first, then AskUserQuestion (same order as GetActionButtonsAsync)
        var permButtons = DetectPermissionButtons(mainContent);
        if (permButtons.Count > 0) return permButtons;

        return DetectAskUserQuestionButtons(mainContent);
    }


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
                    // Claude was terminated
                    Log.Warning("Claude process terminated - setting disconnected");
                    SetDisconnected();
                    _wasClaudeRunning = false;
                }
                else if (!_wasClaudeRunning && hasProcess)
                {
                    // Claude was (re)started
                    Log.Information("Claude process detected - attempting reconnect");
                    await Task.Delay(2000, ct); // Give the app time to initialize
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
                {
                    await FindClaudeWindowAsync();
                }

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

                    // Detect action button changes
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Monitor loop error");
                InvalidateCachedElements();
                await Task.Delay(2000, ct);
            }
        }
    }

    // --- Element Finding Helpers ---
    // IMPORTANT: Electron/Chromium renders web content as elements with
    // IsControlElement=false / IsContentElement=false. The standard
    // FindFirst/FindAll only search the ControlView and skip these elements.
    // We must use TreeWalker.RawViewWalker to locate elements by AutomationId,
    // then use FindAll(Descendants, ...) FROM the found element to get its
    // children (which does work once we have a direct reference).

    private static readonly TreeWalker RawWalker = TreeWalker.RawViewWalker;

    private AutomationElement? FindMainDocument()
    {
        if (_claudeWindow == null) return null;

        // RootWebArea is findable because it IS in the control view
        var allDocs = _claudeWindow.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea"));

        foreach (AutomationElement doc in allDocs)
        {
            if (doc.Current.Name == "Claude")
                return doc;
        }

        return allDocs.Count > 0 ? allDocs[allDocs.Count - 1] : null;
    }

    private AutomationElement? GetMainContent()
    {
        if (_cachedMainContent != null && IsCachedElementValid(_cachedMainContent))
            return _cachedMainContent;

        _cachedMainContent = FindByAutomationIdRaw(_claudeWindow, "main-content");
        return _cachedMainContent;
    }

    private AutomationElement? GetSidebar()
    {
        if (_cachedSidebar != null && IsCachedElementValid(_cachedSidebar))
            return _cachedSidebar;

        if (_claudeWindow == null) return null;

        // Sidebar has Name='Sidebar' and is in the control view
        _cachedSidebar = _claudeWindow.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Sidebar"));
        Log.Debug("[GetSidebar] ControlView FindFirst: {Result}",
            _cachedSidebar != null ? "found" : "not found");

        // Fallback: raw walk
        if (_cachedSidebar == null)
        {
            _cachedSidebar = FindByNameRaw(_claudeWindow, "Sidebar");
            Log.Debug("[GetSidebar] RawView FindByName: {Result}",
                _cachedSidebar != null ? "found" : "not found");
        }

        if (_cachedSidebar != null)
        {
            try
            {
                Log.Debug("[GetSidebar] Sidebar element: ControlType={Type} AID='{AID}' Rect={Rect}",
                    _cachedSidebar.Current.ControlType.ProgrammaticName,
                    _cachedSidebar.Current.AutomationId,
                    _cachedSidebar.Current.BoundingRectangle);
            }
            catch { }
        }

        return _cachedSidebar;
    }

    private AutomationElement? GetTurnForm()
    {
        if (_cachedTurnForm != null && IsCachedElementValid(_cachedTurnForm))
            return _cachedTurnForm;

        _cachedTurnForm = FindByAutomationIdRaw(_claudeWindow, "turn-form");
        return _cachedTurnForm;
    }

    private AutomationElement? GetInputField()
    {
        if (_cachedInputField != null && IsCachedElementValid(_cachedInputField))
            return _cachedInputField;

        // 'Enter your turn' is in the control view (IsKeyboardFocusable=true)
        _cachedInputField = _claudeWindow?.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Enter your turn"));

        if (_cachedInputField == null)
        {
            var turnForm = GetTurnForm();
            if (turnForm != null)
                _cachedInputField = FindByNameRaw(turnForm, "Enter your turn");
        }

        return _cachedInputField;
    }

    /// <summary>
    /// Walk the raw view tree to find an element by AutomationId.
    /// Required because Chromium renders web elements with IsControlElement=false.
    /// </summary>
    private static AutomationElement? FindByAutomationIdRaw(AutomationElement? root, string automationId, int maxDepth = 25, int maxNodes = 5000)
    {
        if (root == null) return null;

        // Try ControlView first (faster)
        var result = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        if (result != null) return result;

        // Fall back to RawView walk
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

    private static AutomationElement? FindByNameRaw(AutomationElement? root, string name, int maxDepth = 25, int maxNodes = 5000)
    {
        if (root == null) return null;

        int visited = 0;
        return WalkForName(root, name, 0, maxDepth, ref visited, maxNodes);
    }

    private static AutomationElement? WalkForName(AutomationElement element, string targetName, int depth, int maxDepth, ref int visited, int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return null;
        visited++;

        try
        {
            if (element.Current.Name == targetName)
                return element;
        }
        catch { return null; }

        var child = RawWalker.GetFirstChild(element);
        while (child != null)
        {
            var found = WalkForName(child, targetName, depth + 1, maxDepth, ref visited, maxNodes);
            if (found != null) return found;
            child = RawWalker.GetNextSibling(child);
        }
        return null;
    }

    private List<AutomationElement> GetSessionTextElements()
    {
        var result = new List<AutomationElement>();
        var sidebar = GetSidebar();
        if (sidebar == null) return result;

        // Try ControlView first, fall back to RawView
        var controlViewTexts = sidebar.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));

        List<AutomationElement> textElements;
        if (controlViewTexts.Count > 0)
        {
            textElements = new List<AutomationElement>();
            foreach (AutomationElement el in controlViewTexts)
                textElements.Add(el);
        }
        else
        {
            textElements = CollectTextElementsRaw(sidebar);
        }

        var settingsRect = FindSettingsButtonRect(sidebar);
        bool foundHeader = false;

        foreach (var el in textElements)
        {
            try
            {
                var name = el.Current.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (!settingsRect.IsEmpty)
                {
                    var textRect = el.Current.BoundingRectangle;
                    if (!textRect.IsEmpty && settingsRect.Contains(textRect))
                        continue;
                }

                if (name == "Sessions") { foundHeader = true; continue; }
                if (!foundHeader) continue;
                if (IsDateHeader(name) || IsUiLabel(name)) continue;
                if (name.StartsWith("Ctrl+") || name.StartsWith("Alt+")) continue;
                result.Add(el);
            }
            catch { }
        }

        // Fallback: if no "Sessions" header found, collect all session-like texts
        if (!foundHeader && result.Count == 0)
        {
            foreach (var el in textElements)
            {
                try
                {
                    var name = el.Current.Name;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!settingsRect.IsEmpty)
                    {
                        var textRect = el.Current.BoundingRectangle;
                        if (!textRect.IsEmpty && settingsRect.Contains(textRect))
                            continue;
                    }
                    if (IsDateHeader(name) || IsUiLabel(name)) continue;
                    if (name.StartsWith("Ctrl+") || name.StartsWith("Alt+")) continue;
                    if (IsUiChromeText(name)) continue;
                    result.Add(el);
                }
                catch { }
            }
        }

        return result;
    }

    // --- State Detection ---

    private void DetectStreamingState(AutomationElement mainContent)
    {
        try
        {
            // Check for Interrupt button presence (visible only during generation)
            var turnForm = GetTurnForm();
            if (turnForm == null) return;

            var interruptBtn = turnForm.FindFirst(TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Interrupt")));

            var wasGenerating = IsGenerating;
            IsGenerating = interruptBtn != null;

            if (wasGenerating != IsGenerating)
            {
                Log.Debug("Streaming state changed: {IsGenerating}", IsGenerating);
            }
        }
        catch { }
    }

    // --- Text Filtering ---

    private static bool IsUiChromeText(string text)
    {
        // Filter out known UI elements
        return text == "Reply..." ||
               text == "Copy message" ||
               text.StartsWith("Ctrl+") ||
               text.StartsWith("Alt+") ||
               text == "Sessions" ||
               text == "Today" ||
               text == "Yesterday" ||
               text == "Previous 7 Days" ||
               text == "Previous 30 Days" ||
               text == "Forging..." ||
               text == "Thinking..." ||
               text.EndsWith(" tokens") ||
               text == "New session" ||
               text == "Search" ||
               text == "Scheduled" ||
               text == "Customize" ||
               text == "Dispatch";
    }

    private static bool IsDateHeader(string text)
    {
        return text == "Today" || text == "Yesterday" ||
               text == "Previous 7 Days" || text == "Previous 30 Days" ||
               text.StartsWith("20") && text.Length <= 10; // Date patterns
    }

    private static bool IsUiLabel(string text)
    {
        return text == "Sessions" || text == "New session" ||
               text == "Search" || text == "Scheduled" ||
               text == "Customize" || text == "Dispatch" ||
               text.StartsWith("Ctrl+") || text.StartsWith("Alt+") ||
               text == "No pull request" ||
               // User profile area labels
               text.EndsWith(" plan") || // "Max plan", "Pro plan" etc.
               text.Length <= 1; // Single-letter avatar initials
    }

    // --- Output Extraction ---

    private static string ExtractLatestResponse(List<string> allTexts)
    {
        if (allTexts.Count == 0) return string.Empty;

        // Take the last meaningful chunk of text
        var result = new List<string>();
        for (int i = allTexts.Count - 1; i >= 0 && result.Count < 50; i--)
        {
            if (!string.IsNullOrWhiteSpace(allTexts[i]))
                result.Insert(0, allTexts[i]);
        }
        return string.Join("\n", result);
    }

    private static string ExtractSummary(List<string> allTexts)
    {
        var latest = ExtractLatestResponse(allTexts);
        return latest.Length > 500 ? latest[..500] + "..." : latest;
    }

    // --- Utility ---

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

    private static bool IsCachedElementValid(AutomationElement element)
    {
        try
        {
            _ = element.Current.BoundingRectangle;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> EnsureConnected()
    {
        if (IsClaudeRunning && _mainDocument != null) return true;
        return await FindClaudeWindowAsync();
    }

    private void SetDisconnected()
    {
        _claudeWindow = null;
        _mainDocument = null;
        InvalidateCachedElements();
        ClaudeStatusChanged?.Invoke(this, false);
    }

    private void InvalidateCachedElements()
    {
        _cachedMainContent = null;
        _cachedSidebar = null;
        _cachedTurnForm = null;
        _cachedInputField = null;
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

        // Simulate mouse click using Win32 API
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

    /// <summary>
    /// Gets the HWND of the Claude Code main window, or IntPtr.Zero if not available.
    /// </summary>
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
    /// Temporarily restores the Claude window if it is minimized, runs the given action,
    /// then re-minimizes the window and restores focus to the previous foreground window.
    /// If the window is already visible, the action runs directly with no state change.
    /// Use this for any operation that requires the window to be active (SendKeys, coordinate clicks).
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
                await Task.Delay(300); // UI restore settle
                Log.Debug("[Window] Restored Claude window for remote operation");
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
                    await Task.Delay(100); // Let the action's effects settle (e.g. clipboard paste)
                    ShowWindow(hwnd, SW_MINIMIZE);
                    if (previousForeground != IntPtr.Zero && previousForeground != hwnd)
                        SetForegroundWindow(previousForeground);
                    Log.Debug("[Window] Re-minimized Claude window, focus returned");
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
