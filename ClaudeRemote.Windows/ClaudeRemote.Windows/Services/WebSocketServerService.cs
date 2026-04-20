using System.Collections.Concurrent;
using Fleck;
using Serilog;

namespace ClaudeRemote.Windows.Services;

public class WebSocketServerService : IWebSocketServerService, IDisposable
{
    private WebSocketServer? _server;
    private readonly ConcurrentDictionary<Guid, IWebSocketConnection> _clients = new();
    private readonly ConcurrentQueue<string> _pendingMessages = new();
    private Timer? _heartbeatTimer;
    private const int HeartbeatIntervalMs = 30_000;
    private const int MaxQueuedMessages = 100;
    private DateTime _startTime;

    public bool IsRunning => _server != null;
    public int ConnectedClients => _clients.Count;

    public event EventHandler<string>? MessageReceived;
    public event EventHandler<int>? ClientCountChanged;

    public void Start(int port = 8765)
    {
        if (_server != null) return;

        _server = new WebSocketServer($"ws://0.0.0.0:{port}");
        _server.RestartAfterListenError = true;
        _startTime = DateTime.UtcNow;

        _server.Start(socket =>
        {
            socket.OnOpen = () =>
            {
                _clients.TryAdd(socket.ConnectionInfo.Id, socket);
                Log.Information("Client connected: {Id} from {Ip}",
                    socket.ConnectionInfo.Id,
                    socket.ConnectionInfo.ClientIpAddress);
                ClientCountChanged?.Invoke(this, _clients.Count);

                // Flush queued messages to the newly connected client
                _ = FlushQueueAsync(socket);
            };

            socket.OnClose = () =>
            {
                _clients.TryRemove(socket.ConnectionInfo.Id, out _);
                Log.Information("Client disconnected: {Id}", socket.ConnectionInfo.Id);
                ClientCountChanged?.Invoke(this, _clients.Count);
            };

            socket.OnMessage = message =>
            {
                // Full JSON is noisy on hot paths — keep it available at
                // Verbose for debugging but don't pollute the default log.
                Log.Verbose("Message received: {Message}", message);
                MessageReceived?.Invoke(this, message);
            };

            socket.OnError = ex =>
            {
                Log.Error(ex, "WebSocket error for client {Id}", socket.ConnectionInfo.Id);
                _clients.TryRemove(socket.ConnectionInfo.Id, out _);
                ClientCountChanged?.Invoke(this, _clients.Count);
            };
        });

        StartHeartbeat();
        Log.Information("WebSocket server started on port {Port}", port);
    }

    public void Stop()
    {
        StopHeartbeat();

        foreach (var client in _clients.Values)
        {
            try { client.Close(); } catch { }
        }
        _clients.Clear();

        _server?.Dispose();
        _server = null;

        Log.Information("WebSocket server stopped");
        ClientCountChanged?.Invoke(this, 0);
    }

    public async Task SendToAllAsync(string message)
    {
        if (_clients.IsEmpty)
        {
            EnqueueMessage(message);
            return;
        }

        var tasks = _clients.Values.Select(client =>
        {
            try
            {
                return client.Send(message);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to send to client {Id}", client.ConnectionInfo.Id);
                return Task.CompletedTask;
            }
        });
        await Task.WhenAll(tasks);
    }

    public async Task SendAsync(string message)
    {
        await SendToAllAsync(message);
    }

    // --- Heartbeat ---

    private void StartHeartbeat()
    {
        StopHeartbeat();
        _heartbeatTimer = new Timer(HeartbeatCallback, null, HeartbeatIntervalMs, HeartbeatIntervalMs);
        Log.Debug("Heartbeat timer started ({Interval}s interval)", HeartbeatIntervalMs / 1000);
    }

    private void StopHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    private void HeartbeatCallback(object? state)
    {
        if (_clients.IsEmpty) return;

        try
        {
            var uptime = (long)(DateTime.UtcNow - _startTime).TotalSeconds;
            var heartbeat = System.Text.Json.JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid().ToString(),
                type = "status",
                action = "heartbeat",
                payload = new
                {
                    uptime,
                    clientCount = _clients.Count
                },
                timestamp = DateTime.UtcNow.ToString("o")
            });

            _ = SendToAllAsync(heartbeat);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Heartbeat send error");
        }
    }

    // --- Message Queuing ---

    private void EnqueueMessage(string message)
    {
        // Limit queue size to prevent memory growth
        while (_pendingMessages.Count >= MaxQueuedMessages)
        {
            _pendingMessages.TryDequeue(out _);
        }
        _pendingMessages.Enqueue(message);
        Log.Debug("Message queued (queue size: {Count})", _pendingMessages.Count);
    }

    private async Task FlushQueueAsync(IWebSocketConnection client)
    {
        var flushed = 0;
        while (_pendingMessages.TryDequeue(out var message))
        {
            try
            {
                await client.Send(message);
                flushed++;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to flush queued message to client");
                break;
            }
        }

        if (flushed > 0)
        {
            Log.Information("Flushed {Count} queued messages to client {Id}",
                flushed, client.ConnectionInfo.Id);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
