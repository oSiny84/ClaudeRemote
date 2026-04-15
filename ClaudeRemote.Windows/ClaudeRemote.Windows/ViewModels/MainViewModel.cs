using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClaudeRemote.Windows.Models;
using ClaudeRemote.Windows.Services;
using Serilog;

namespace ClaudeRemote.Windows.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IClaudeAutomationService _claudeService;
    private readonly IWebSocketServerService _webSocketServer;
    private readonly ISessionManager _sessionManager;
    private readonly IMessageProcessor _messageProcessor;

    [ObservableProperty] private bool _isClaudeConnected;
    [ObservableProperty] private bool _isServerRunning;
    [ObservableProperty] private int _connectedClients;
    [ObservableProperty] private string _currentMode = "code";
    [ObservableProperty] private string _claudeOutput = "";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private int _serverPort = 8765;
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _localIpAddress = "Detecting...";

    public ObservableCollection<SessionInfo> Sessions { get; } = new();
    public ObservableCollection<ProjectInfo> Projects { get; } = new();

    public MainViewModel(
        IClaudeAutomationService claudeService,
        IWebSocketServerService webSocketServer,
        ISessionManager sessionManager,
        IMessageProcessor messageProcessor)
    {
        _claudeService = claudeService;
        _webSocketServer = webSocketServer;
        _sessionManager = sessionManager;
        _messageProcessor = messageProcessor;

        // Wire up events
        _claudeService.OutputChanged += OnOutputChanged;
        _claudeService.ClaudeStatusChanged += OnClaudeStatusChanged;
        _claudeService.ActionButtonsChanged += OnActionButtonsChanged;
        _webSocketServer.MessageReceived += OnWebSocketMessageReceived;
        _webSocketServer.ClientCountChanged += OnClientCountChanged;
        _sessionManager.SessionsUpdated += OnSessionsUpdated;
        _sessionManager.ProjectsUpdated += OnProjectsUpdated;
        _messageProcessor.LogEvent += OnMessageProcessorLog;

        // Start process watcher immediately
        _claudeService.StartProcessWatcher();

        // Detect local IP address
        LocalIpAddress = GetLocalIpAddress();
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
                return endPoint.Address.ToString();
        }
        catch { }

        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                    return ip.ToString();
            }
        }
        catch { }

        return "Unknown";
    }

    [RelayCommand]
    private async Task ConnectClaudeAsync()
    {
        AppendLog("Searching for Claude app...");
        var found = await _claudeService.FindClaudeWindowAsync();
        if (found)
        {
            AppendLog("Claude app found! Starting monitor...");
            _claudeService.StartMonitoring();
            StatusText = "Claude connected";
        }
        else
        {
            AppendLog("Claude app not found. Is it running?");
            StatusText = "Claude not found";
        }
    }

    [RelayCommand]
    private void ToggleServer()
    {
        if (IsServerRunning)
        {
            _webSocketServer.Stop();
            IsServerRunning = false;
            AppendLog($"WebSocket server stopped");
            StatusText = "Server stopped";
        }
        else
        {
            _webSocketServer.Start(ServerPort);
            IsServerRunning = true;
            AppendLog($"WebSocket server started on port {ServerPort}");
            StatusText = $"Server running on port {ServerPort}";
        }
    }

    [RelayCommand]
    private async Task RefreshSessionsAsync()
    {
        await _sessionManager.RefreshSessionsAsync();
    }

    [RelayCommand]
    private async Task AddSessionAsync()
    {
        await _sessionManager.AddSessionAsync();
    }

    [RelayCommand]
    private async Task RefreshProjectsAsync()
    {
        await _sessionManager.RefreshProjectsAsync();
    }

    // --- Event Handlers ---

    private async void OnOutputChanged(object? sender, string output)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ClaudeOutput = output;
        });

        // Forward to Android client - use chunked send for large output
        var outputBytes = System.Text.Encoding.UTF8.GetByteCount(output);
        if (outputBytes > 10 * 1024)
        {
            await _messageProcessor.SendChunkedAsync(output);
        }
        else
        {
            var message = _messageProcessor.CreateOutputMessage(output, "latest", CurrentMode);
            await _webSocketServer.SendToAllAsync(message);
        }
    }

    private void OnClaudeStatusChanged(object? sender, bool isRunning)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var wasConnected = IsClaudeConnected;
            IsClaudeConnected = isRunning;

            if (isRunning)
            {
                StatusText = "Claude connected";
                if (!wasConnected) AppendLog("Claude app reconnected");
            }
            else
            {
                StatusText = "Claude disconnected";
                if (wasConnected) AppendLog("Claude app disconnected - watching for restart...");
            }
        });

        var status = _messageProcessor.CreateStatusMessage(isRunning, CurrentMode, false);
        _ = _webSocketServer.SendToAllAsync(status);
    }

    private async void OnWebSocketMessageReceived(object? sender, string json)
    {
        // MessageProcessor now emits detailed logs via LogEvent for each action.
        // No raw-JSON log here — per-action log is clearer and avoids noise.
        await _messageProcessor.ProcessIncomingMessageAsync(json);
    }

    private void OnMessageProcessorLog(object? sender, string text) => AppendLog(text);

    private void OnClientCountChanged(object? sender, int count)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            ConnectedClients = count;
            AppendLog($"Connected clients: {count}");
        });
    }

    private async void OnActionButtonsChanged(object? sender, List<ButtonInfo> buttons)
    {
        var category = _claudeService.LastButtonCategory;
        var prompt = category == "permission"
            ? _claudeService.LastPermissionPrompt
            : _claudeService.LastAskUserQuestionPrompt;
        var msg = _messageProcessor.CreateActionButtonsMessage(buttons, prompt, category);
        await _webSocketServer.SendToAllAsync(msg);
        AppendLog($"Action buttons [{category ?? "none"}]: {buttons.Count} buttons" + (prompt != null ? $" (prompt: {prompt})" : ""));
    }

    private void OnSessionsUpdated(object? sender, List<SessionInfo> sessions)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Sessions.Clear();
            foreach (var s in sessions) Sessions.Add(s);
        });
    }

    private void OnProjectsUpdated(object? sender, List<ProjectInfo> projects)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            Projects.Clear();
            foreach (var p in projects) Projects.Add(p);
        });
    }

    private void AppendLog(string text)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogText = $"{LogText}[{timestamp}] {text}\n";
            // Trim from the front (oldest) when too long
            if (LogText.Length > 10000)
                LogText = LogText[^8000..];
        });
    }

    public void Dispose()
    {
        _claudeService.StopMonitoring();
        _claudeService.StopProcessWatcher();
        _webSocketServer.Stop();
    }
}
