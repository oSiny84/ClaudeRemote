namespace ClaudeRemote.Windows.Services;

public interface IWebSocketServerService
{
    bool IsRunning { get; }
    int ConnectedClients { get; }
    event EventHandler<string>? MessageReceived;
    event EventHandler<int>? ClientCountChanged;

    void Start(int port = 8765);
    void Stop();
    Task SendToAllAsync(string message);
    Task SendAsync(string message);
}
