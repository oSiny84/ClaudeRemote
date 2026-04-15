using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace UITreeDumper;

/// <summary>
/// Standalone UI tree dumper for Claude Desktop.
/// Finds the Claude main window by process name and dumps the full RawView tree to a text file.
/// Does not depend on ClaudeRemote.Windows internals so it works regardless of UI changes.
///
/// Usage:
///   UITreeDumper.exe [output_path] [max_depth] [max_nodes]
///   Default: output="uitree.txt" max_depth=30 max_nodes=5000
/// </summary>
public static class Program
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    private static readonly TreeWalker RawWalker = TreeWalker.RawViewWalker;

    public static int Main(string[] args)
    {
        var outputPath = args.Length > 0 ? args[0] : "uitree.txt";
        var maxDepth = args.Length > 1 ? int.Parse(args[1]) : 30;
        var maxNodes = args.Length > 2 ? int.Parse(args[2]) : 5000;

        Console.WriteLine($"UITreeDumper: output={outputPath} maxDepth={maxDepth} maxNodes={maxNodes}");

        // Find Claude process (case-insensitive match for "claude")
        var claudeProcesses = Process.GetProcesses()
            .Where(p => p.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase))
            .Where(p => p.MainWindowHandle != IntPtr.Zero)
            .ToList();

        if (claudeProcesses.Count == 0)
        {
            Console.WriteLine("ERROR: No Claude process with a main window found.");
            Console.WriteLine("Currently running 'claude' processes:");
            foreach (var p in Process.GetProcessesByName("claude"))
            {
                Console.WriteLine($"  PID={p.Id} Name={p.ProcessName} MainHWND={p.MainWindowHandle} Title='{p.MainWindowTitle}'");
            }
            return 1;
        }

        // Pick the process with the largest working set (main UI process typically)
        var mainProc = claudeProcesses.OrderByDescending(p => p.WorkingSet64).First();
        Console.WriteLine($"Target: PID={mainProc.Id} Title='{mainProc.MainWindowTitle}' HWND={mainProc.MainWindowHandle}");
        Console.WriteLine($"ProductVersion: {mainProc.MainModule?.FileVersionInfo.ProductVersion ?? "(unknown)"}");
        Console.WriteLine($"FileVersion: {mainProc.MainModule?.FileVersionInfo.FileVersion ?? "(unknown)"}");
        Console.WriteLine($"ExePath: {mainProc.MainModule?.FileName ?? "(unknown)"}");

        // Restore window if minimized
        if (IsIconic(mainProc.MainWindowHandle))
        {
            Console.WriteLine("Window is minimized — restoring...");
            ShowWindow(mainProc.MainWindowHandle, SW_RESTORE);
            SetForegroundWindow(mainProc.MainWindowHandle);
            System.Threading.Thread.Sleep(500);
        }

        // Find the AutomationElement from HWND
        AutomationElement? claudeWindow;
        try
        {
            claudeWindow = AutomationElement.FromHandle(mainProc.MainWindowHandle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: AutomationElement.FromHandle failed: {ex.Message}");
            return 2;
        }

        if (claudeWindow == null)
        {
            Console.WriteLine("ERROR: Could not obtain AutomationElement for Claude window.");
            return 3;
        }

        // Dump the full tree
        var sb = new StringBuilder();
        sb.AppendLine($"# Claude Desktop UI Tree Dump");
        sb.AppendLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# PID: {mainProc.Id}");
        sb.AppendLine($"# Title: {mainProc.MainWindowTitle}");
        sb.AppendLine($"# ProductVersion: {mainProc.MainModule?.FileVersionInfo.ProductVersion ?? "(unknown)"}");
        sb.AppendLine($"# FileVersion: {mainProc.MainModule?.FileVersionInfo.FileVersion ?? "(unknown)"}");
        sb.AppendLine($"# ExePath: {mainProc.MainModule?.FileName ?? "(unknown)"}");
        sb.AppendLine($"# MaxDepth: {maxDepth}  MaxNodes: {maxNodes}");
        sb.AppendLine($"#");
        sb.AppendLine($"# Format: [ControlType] Name=\"...\" AID=\"...\" Class=\"...\" Rect=[X,Y,WxH]");
        sb.AppendLine($"#");

        int visited = 0;
        DumpElementTree(claudeWindow, sb, 0, maxDepth, ref visited, maxNodes);

        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"Dumped {visited} nodes to {outputPath}");
        Console.WriteLine($"File size: {new FileInfo(outputPath).Length} bytes");
        return 0;
    }

    private static void DumpElementTree(AutomationElement element, StringBuilder sb, int depth, int maxDepth, ref int visited, int maxNodes)
    {
        if (depth > maxDepth || visited > maxNodes) return;
        visited++;

        string indent;
        string ct;
        string name;
        string aid;
        string cls;
        System.Windows.Rect rect;

        try
        {
            indent = new string(' ', depth * 2);
            ct = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            name = element.Current.Name ?? "";
            aid = element.Current.AutomationId ?? "";
            cls = element.Current.ClassName ?? "";
            rect = element.Current.BoundingRectangle;
        }
        catch
        {
            return;
        }

        // Truncate long names for readability
        var nameStr = name.Length > 120 ? name.Substring(0, 120) + "..." : name;
        // Escape control chars for line safety
        nameStr = nameStr.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        var clsStr = cls.Length > 80 ? cls.Substring(0, 80) + "..." : cls;

        sb.AppendLine($"{indent}[{ct}] Name=\"{nameStr}\" AID=\"{aid}\" Class=\"{clsStr}\" Rect=[{rect.X:F0},{rect.Y:F0},{rect.Width:F0}x{rect.Height:F0}]");

        try
        {
            var child = RawWalker.GetFirstChild(element);
            while (child != null && visited <= maxNodes)
            {
                DumpElementTree(child, sb, depth + 1, maxDepth, ref visited, maxNodes);
                try { child = RawWalker.GetNextSibling(child); }
                catch { break; }
            }
        }
        catch { }
    }
}
