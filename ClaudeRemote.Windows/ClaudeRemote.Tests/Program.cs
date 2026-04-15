using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ClaudeRemote.Windows.Models;
using ClaudeRemote.Windows.Services;
using ClaudeRemote.Tests;

// Route to specific phase test if requested
if (args.Length > 0 && args[0] == "phase3")
{
    await Phase3Test.RunAsync();
    return;
}
if (args.Length > 0 && args[0] == "phase5")
{
    await Phase5Test.RunAsync();
    return;
}

Console.WriteLine("=== ClaudeRemote Phase 2 Integration Test ===\n");

var passed = 0;
var failed = 0;

// --- Test 1: WebSocket Server Start/Stop ---
Console.WriteLine("[Test 1] WebSocket Server Start/Stop");
try
{
    var server = new WebSocketServerService();
    server.Start(18765);
    Assert(server.IsRunning, "Server should be running after Start()");
    Assert(server.ConnectedClients == 0, "No clients initially");

    server.Stop();
    Assert(!server.IsRunning, "Server should not be running after Stop()");
    server.Dispose();
    Pass("WebSocket Server Start/Stop");
}
catch (Exception ex) { Fail("WebSocket Server Start/Stop", ex.Message); }

// --- Test 2: Client Connection ---
Console.WriteLine("\n[Test 2] Client Connection and Disconnection");
try
{
    var server = new WebSocketServerService();
    var clientCountChanged = new TaskCompletionSource<int>();
    server.ClientCountChanged += (_, count) => clientCountChanged.TrySetResult(count);
    server.Start(18766);

    using var client = new ClientWebSocket();
    await client.ConnectAsync(new Uri("ws://localhost:18766"), CancellationToken.None);

    var count = await WaitWithTimeout(clientCountChanged.Task, 3000);
    Assert(count == 1, $"Expected 1 client, got {count}");
    Assert(server.ConnectedClients == 1, "Server should report 1 connected client");

    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    await Task.Delay(500);
    Assert(server.ConnectedClients == 0, $"Expected 0 clients after close, got {server.ConnectedClients}");

    server.Stop();
    server.Dispose();
    Pass("Client Connection and Disconnection");
}
catch (Exception ex) { Fail("Client Connection and Disconnection", ex.Message); }

// --- Test 3: Message Receive ---
Console.WriteLine("\n[Test 3] Message Receive from Client");
try
{
    var server = new WebSocketServerService();
    var messageReceived = new TaskCompletionSource<string>();
    server.MessageReceived += (_, msg) => messageReceived.TrySetResult(msg);
    server.Start(18767);

    using var client = new ClientWebSocket();
    await client.ConnectAsync(new Uri("ws://localhost:18767"), CancellationToken.None);
    await Task.Delay(300);

    var testMsg = "{\"type\":\"command\",\"action\":\"get_output\",\"payload\":{\"scope\":\"latest\"}}";
    var bytes = Encoding.UTF8.GetBytes(testMsg);
    await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

    var received = await WaitWithTimeout(messageReceived.Task, 3000);
    Assert(received == testMsg, "Received message should match sent message");

    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    server.Stop();
    server.Dispose();
    Pass("Message Receive from Client");
}
catch (Exception ex) { Fail("Message Receive from Client", ex.Message); }

// --- Test 4: Server Send to Client ---
Console.WriteLine("\n[Test 4] Server Send to Client");
try
{
    var server = new WebSocketServerService();
    server.Start(18768);

    using var client = new ClientWebSocket();
    await client.ConnectAsync(new Uri("ws://localhost:18768"), CancellationToken.None);
    await Task.Delay(300);

    var responseMsg = "{\"type\":\"response\",\"action\":\"get_output\",\"payload\":{\"success\":true,\"content\":\"Hello\"}}";
    await server.SendToAllAsync(responseMsg);

    var buffer = new byte[4096];
    var result = await WaitWithTimeout(
        client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None),
        3000);
    var receivedText = Encoding.UTF8.GetString(buffer, 0, result.Count);
    Assert(receivedText == responseMsg, "Client should receive server's message");

    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    server.Stop();
    server.Dispose();
    Pass("Server Send to Client");
}
catch (Exception ex) { Fail("Server Send to Client", ex.Message); }

// --- Test 5: MessageProcessor - get_output command ---
Console.WriteLine("\n[Test 5] MessageProcessor - get_output command");
try
{
    var server = new WebSocketServerService();
    server.Start(18769);

    var mockClaude = new MockClaudeAutomationService();
    var sessionMgr = new SessionManager(mockClaude);
    var processor = new MessageProcessor(mockClaude, sessionMgr, server);
    server.MessageReceived += async (_, msg) => await processor.ProcessIncomingMessageAsync(msg);

    using var client = new ClientWebSocket();
    await client.ConnectAsync(new Uri("ws://localhost:18769"), CancellationToken.None);
    await Task.Delay(300);

    var cmd = JsonSerializer.Serialize(new ClaudeMessage
    {
        Type = MessageType.Command,
        Action = MessageAction.GetOutput,
        Payload = new Dictionary<string, object> { ["scope"] = "latest" }
    });
    await client.SendAsync(Encoding.UTF8.GetBytes(cmd), WebSocketMessageType.Text, true, CancellationToken.None);

    var buffer = new byte[4096];
    var result = await WaitWithTimeout(
        client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None),
        3000);
    var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
    var response = JsonSerializer.Deserialize<ClaudeMessage>(responseJson);

    Assert(response != null, "Response should not be null");
    Assert(response!.Type == MessageType.Content, $"Expected type 'content', got '{response.Type}'");
    Assert(response.Action == MessageAction.OutputUpdate, $"Expected action 'output_update', got '{response.Action}'");
    Assert(response.Payload != null, "Payload should not be null");

    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    server.Stop();
    server.Dispose();
    Pass("MessageProcessor - get_output command");
}
catch (Exception ex) { Fail("MessageProcessor - get_output command", ex.Message); }

// --- Test 6: MessageProcessor - send_input command ---
Console.WriteLine("\n[Test 6] MessageProcessor - send_input command");
try
{
    var server = new WebSocketServerService();
    server.Start(18770);

    var mockClaude = new MockClaudeAutomationService();
    var sessionMgr = new SessionManager(mockClaude);
    var processor = new MessageProcessor(mockClaude, sessionMgr, server);
    server.MessageReceived += async (_, msg) => await processor.ProcessIncomingMessageAsync(msg);

    using var client = new ClientWebSocket();
    await client.ConnectAsync(new Uri("ws://localhost:18770"), CancellationToken.None);
    await Task.Delay(300);

    var cmd = JsonSerializer.Serialize(new ClaudeMessage
    {
        Type = MessageType.Command,
        Action = MessageAction.SendInput,
        Mode = "chat",
        Payload = new Dictionary<string, object> { ["text"] = "Hello Claude!" }
    });
    await client.SendAsync(Encoding.UTF8.GetBytes(cmd), WebSocketMessageType.Text, true, CancellationToken.None);

    var buffer = new byte[4096];
    var result = await WaitWithTimeout(
        client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None),
        3000);
    var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
    var response = JsonSerializer.Deserialize<ClaudeMessage>(responseJson);

    Assert(response != null, "Response should not be null");
    Assert(response!.Type == MessageType.Response, $"Expected type 'response', got '{response.Type}'");
    Assert(response.Action == MessageAction.SendInput, $"Expected action 'send_input', got '{response.Action}'");

    if (response.Payload != null && response.Payload.TryGetValue("success", out var successVal))
    {
        var success = successVal is JsonElement je ? je.GetBoolean() : (bool)successVal;
        Assert(success, "send_input should succeed with mock service");
    }

    Assert(mockClaude.LastInputText == "Hello Claude!", $"Mock should have received the input text, got '{mockClaude.LastInputText}'");

    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    server.Stop();
    server.Dispose();
    Pass("MessageProcessor - send_input command");
}
catch (Exception ex) { Fail("MessageProcessor - send_input command", ex.Message); }

// --- Test 7: Invalid JSON handling ---
Console.WriteLine("\n[Test 7] Invalid JSON handling");
try
{
    var server = new WebSocketServerService();
    server.Start(18771);

    var mockClaude = new MockClaudeAutomationService();
    var sessionMgr = new SessionManager(mockClaude);
    var processor = new MessageProcessor(mockClaude, sessionMgr, server);
    server.MessageReceived += async (_, msg) => await processor.ProcessIncomingMessageAsync(msg);

    using var client = new ClientWebSocket();
    await client.ConnectAsync(new Uri("ws://localhost:18771"), CancellationToken.None);
    await Task.Delay(300);

    await client.SendAsync(Encoding.UTF8.GetBytes("not-json{{{"), WebSocketMessageType.Text, true, CancellationToken.None);

    var buffer = new byte[4096];
    var receiveTask = client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
    var result = await WaitWithTimeout(receiveTask, 3000);
    var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);
    var response = JsonSerializer.Deserialize<ClaudeMessage>(responseJson);

    Assert(response != null, "Should get error response for invalid JSON");
    Assert(response!.Type == MessageType.Response, "Error response should be 'response' type");
    if (response.Payload != null && response.Payload.TryGetValue("success", out var sVal))
    {
        var success = sVal is JsonElement je ? je.GetBoolean() : (bool)sVal;
        Assert(!success, "Should report failure for invalid JSON");
    }

    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    server.Stop();
    server.Dispose();
    Pass("Invalid JSON handling");
}
catch (Exception ex) { Fail("Invalid JSON handling", ex.Message); }

// --- Test 8: Protocol compliance ---
Console.WriteLine("\n[Test 8] Protocol message format compliance");
try
{
    var server = new WebSocketServerService();
    server.Start(18772);

    var mockClaude = new MockClaudeAutomationService();
    var sessionMgr = new SessionManager(mockClaude);
    var processor = new MessageProcessor(mockClaude, sessionMgr, server);
    server.MessageReceived += async (_, msg) => await processor.ProcessIncomingMessageAsync(msg);

    using var client = new ClientWebSocket();
    await client.ConnectAsync(new Uri("ws://localhost:18772"), CancellationToken.None);
    await Task.Delay(300);

    var cmd = JsonSerializer.Serialize(new ClaudeMessage
    {
        Type = MessageType.Command,
        Action = MessageAction.GetSessions,
        Mode = "chat"
    });
    await client.SendAsync(Encoding.UTF8.GetBytes(cmd), WebSocketMessageType.Text, true, CancellationToken.None);

    var buffer = new byte[8192];
    var result = await WaitWithTimeout(
        client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None),
        3000);
    var responseJson = Encoding.UTF8.GetString(buffer, 0, result.Count);

    using var doc = JsonDocument.Parse(responseJson);
    var root = doc.RootElement;

    Assert(root.TryGetProperty("id", out _), "Response must have 'id' field");
    Assert(root.TryGetProperty("type", out _), "Response must have 'type' field");
    Assert(root.TryGetProperty("action", out _), "Response must have 'action' field");
    Assert(root.TryGetProperty("payload", out _), "Response must have 'payload' field");
    Assert(root.TryGetProperty("timestamp", out _), "Response must have 'timestamp' field");

    var action = root.GetProperty("action").GetString();
    Assert(action == "get_sessions", $"Action should be 'get_sessions', got '{action}'");

    var payload = root.GetProperty("payload");
    Assert(payload.TryGetProperty("success", out _), "Payload must have 'success' field");
    Assert(payload.TryGetProperty("sessions", out _), "Payload must have 'sessions' field");

    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    server.Stop();
    server.Dispose();
    Pass("Protocol message format compliance");
}
catch (Exception ex) { Fail("Protocol message format compliance", ex.Message); }

// --- Summary ---
Console.WriteLine("\n" + new string('=', 50));
Console.WriteLine($"Results: {passed} passed, {failed} failed, {passed + failed} total");
Console.WriteLine(new string('=', 50));
Environment.ExitCode = failed > 0 ? 1 : 0;

// === Helper Methods ===

void Assert(bool condition, string message)
{
    if (!condition) throw new Exception($"Assertion failed: {message}");
}

void Pass(string testName)
{
    passed++;
    Console.WriteLine($"  PASS: {testName}");
}

void Fail(string testName, string error)
{
    failed++;
    Console.WriteLine($"  FAIL: {testName} - {error}");
}

async Task<T> WaitWithTimeout<T>(Task<T> task, int timeoutMs)
{
    var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
    if (completed != task) throw new TimeoutException($"Operation timed out after {timeoutMs}ms");
    return await task;
}

// === Mock Services ===

class MockClaudeAutomationService : IClaudeAutomationService
{
    public bool IsClaudeRunning => true;
    public string CurrentMode => "chat";
    public string? LastInputText { get; private set; }

    public event EventHandler<string>? OutputChanged;
    public event EventHandler<bool>? ClaudeStatusChanged;
    public event EventHandler<List<ButtonInfo>>? ActionButtonsChanged;

    public Task<bool> FindClaudeWindowAsync() => Task.FromResult(true);

    public Task<string> GetOutputAsync(string scope = "latest")
        => Task.FromResult("Mock Claude output for testing");

    public Task<bool> SendInputAsync(string text)
    {
        LastInputText = text;
        return Task.FromResult(true);
    }

    public Task<bool> SwitchModeAsync(string targetMode) => Task.FromResult(true);

    public Task<List<SessionInfo>> GetSessionsAsync()
        => Task.FromResult(new List<SessionInfo>
        {
            new() { Id = "s1", Name = "Test Session", IsActive = true, LastMessage = "Hello" }
        });

    public Task<bool> SelectSessionAsync(string sessionId) => Task.FromResult(true);
    public Task<bool> AddSessionAsync() => Task.FromResult(true);

    public Task<List<ProjectInfo>> GetProjectsAsync()
        => Task.FromResult(new List<ProjectInfo>
        {
            new() { Id = "p1", Name = "TestProject", Path = "C:\\Test", IsActive = true }
        });

    public Task<bool> SelectProjectAsync(string projectId) => Task.FromResult(true);
    public Task<List<ButtonInfo>> GetActionButtonsAsync() => Task.FromResult(new List<ButtonInfo>());
    public Task<bool> ClickButtonAsync(string buttonId) => Task.FromResult(true);
    public void StartMonitoring() { }
    public void StopMonitoring() { }
    public void StartProcessWatcher() { }
    public void StopProcessWatcher() { }
}
