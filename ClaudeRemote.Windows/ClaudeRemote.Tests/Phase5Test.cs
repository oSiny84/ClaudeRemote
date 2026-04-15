using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ClaudeRemote.Windows.Models;
using ClaudeRemote.Windows.Services;

namespace ClaudeRemote.Tests;

public static class Phase5Test
{
    private static int _passed;
    private static int _failed;

    public static async Task RunAsync()
    {
        Console.WriteLine("=== ClaudeRemote Phase 5 Integration Test ===\n");
        _passed = 0;
        _failed = 0;

        await TestHeartbeat();
        await TestMessageQueuing();
        await TestChunkSplitting();
        await TestChunkReassembly();
        TestProcessWatcherExists();

        Console.WriteLine("\n" + new string('=', 50));
        Console.WriteLine($"Phase 5 Results: {_passed} passed, {_failed} failed, {_passed + _failed} total");
        Console.WriteLine(new string('=', 50));
    }

    private static async Task TestHeartbeat()
    {
        Console.WriteLine("[Test 1] Heartbeat (30s interval → receive within 35s)");
        try
        {
            var server = new WebSocketServerService();
            server.Start(19001);

            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri("ws://localhost:19001"), CancellationToken.None);
            await Task.Delay(300);

            // Wait for heartbeat (default 30s, we wait up to 35s)
            var buffer = new byte[4096];
            using var cts = new CancellationTokenSource(35_000);
            ClaudeMessage? heartbeat = null;

            while (!cts.Token.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var msg = JsonSerializer.Deserialize<ClaudeMessage>(json);
                if (msg?.Type == MessageType.Status && msg.Action == MessageAction.Heartbeat)
                {
                    heartbeat = msg;
                    break;
                }
            }

            Assert(heartbeat != null, "Should receive heartbeat message");
            Assert(heartbeat!.Payload != null, "Heartbeat should have payload");

            if (heartbeat.Payload!.TryGetValue("uptime", out var uptimeVal))
            {
                var uptime = uptimeVal is JsonElement je ? je.GetInt64() : 0;
                Console.WriteLine($"  Heartbeat received: uptime={uptime}s");
                Assert(uptime >= 0, "Uptime should be >= 0");
            }

            if (heartbeat.Payload.TryGetValue("clientCount", out var ccVal))
            {
                var cc = ccVal is JsonElement je2 ? je2.GetInt32() : 0;
                Assert(cc == 1, $"ClientCount should be 1, got {cc}");
            }

            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            server.Stop();
            server.Dispose();
            Pass("Heartbeat");
        }
        catch (OperationCanceledException) { Fail("Heartbeat", "Timed out waiting for heartbeat (35s)"); }
        catch (Exception ex) { Fail("Heartbeat", ex.Message); }
    }

    private static async Task TestMessageQueuing()
    {
        Console.WriteLine("\n[Test 2] Message queuing on disconnect + flush on reconnect");
        try
        {
            var server = new WebSocketServerService();
            server.Start(19002);

            // Send messages while no client is connected → should be queued
            var msg1 = "{\"type\":\"content\",\"action\":\"output_update\",\"payload\":{\"content\":\"queued1\"}}";
            var msg2 = "{\"type\":\"content\",\"action\":\"output_update\",\"payload\":{\"content\":\"queued2\"}}";
            await server.SendToAllAsync(msg1);
            await server.SendToAllAsync(msg2);

            // Now connect a client → should receive flushed messages
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri("ws://localhost:19002"), CancellationToken.None);

            var received = new List<string>();
            var buffer = new byte[4096];
            using var cts = new CancellationTokenSource(3000);

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var parsed = JsonSerializer.Deserialize<ClaudeMessage>(text);
                    // Only count content messages (not heartbeat)
                    if (parsed?.Type == MessageType.Content)
                        received.Add(text);
                    if (received.Count >= 2) break;
                }
            }
            catch (OperationCanceledException) { }

            Console.WriteLine($"  Received {received.Count} queued messages after connect");
            Assert(received.Count == 2, $"Should receive 2 queued messages, got {received.Count}");

            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            server.Stop();
            server.Dispose();
            Pass("Message queuing");
        }
        catch (Exception ex) { Fail("Message queuing", ex.Message); }
    }

    private static async Task TestChunkSplitting()
    {
        Console.WriteLine("\n[Test 3] Chunk splitting for large output (>10KB)");
        try
        {
            var server = new WebSocketServerService();
            server.Start(19003);

            var mockClaude = new MockClaudeService();
            var sessionMgr = new SessionManager(mockClaude);
            var processor = new MessageProcessor(mockClaude, sessionMgr, server);

            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri("ws://localhost:19003"), CancellationToken.None);
            await Task.Delay(300);

            // Generate 25KB content
            var largeContent = new string('A', 25 * 1024);
            await processor.SendChunkedAsync(largeContent);

            var chunks = new List<ClaudeMessage>();
            var buffer = new byte[16384];
            using var cts = new CancellationTokenSource(5000);

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var msg = JsonSerializer.Deserialize<ClaudeMessage>(json);
                    if (msg?.Action == MessageAction.OutputChunk)
                    {
                        chunks.Add(msg);
                        if (msg.Payload != null && msg.Payload.TryGetValue("totalChunks", out var tc))
                        {
                            var total = tc is JsonElement je ? je.GetInt32() : 0;
                            if (chunks.Count >= total) break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }

            Console.WriteLine($"  Received {chunks.Count} chunks for 25KB content");
            Assert(chunks.Count >= 3, $"25KB should produce at least 3 chunks (8KB each), got {chunks.Count}");

            // Verify chunk metadata
            if (chunks.Count > 0 && chunks[0].Payload != null)
            {
                var firstPayload = chunks[0].Payload;
                Assert(firstPayload.ContainsKey("chunkId"), "Chunk should have chunkId");
                Assert(firstPayload.ContainsKey("chunkIndex"), "Chunk should have chunkIndex");
                Assert(firstPayload.ContainsKey("totalChunks"), "Chunk should have totalChunks");
                Assert(firstPayload.ContainsKey("data"), "Chunk should have data");

                var idx = firstPayload["chunkIndex"] is JsonElement je2 ? je2.GetInt32() : -1;
                Assert(idx == 0, $"First chunk index should be 0, got {idx}");
            }

            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            server.Stop();
            server.Dispose();
            Pass("Chunk splitting");
        }
        catch (Exception ex) { Fail("Chunk splitting", ex.Message); }
    }

    private static async Task TestChunkReassembly()
    {
        Console.WriteLine("\n[Test 4] Chunk data integrity (reassembly)");
        try
        {
            var server = new WebSocketServerService();
            server.Start(19004);

            var mockClaude = new MockClaudeService();
            var sessionMgr = new SessionManager(mockClaude);
            var processor = new MessageProcessor(mockClaude, sessionMgr, server);

            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri("ws://localhost:19004"), CancellationToken.None);
            await Task.Delay(300);

            // 12KB content with known pattern
            var original = string.Concat(Enumerable.Range(0, 1200).Select(i => $"LINE{i:D4}\n"));
            await processor.SendChunkedAsync(original);

            var chunkData = new SortedDictionary<int, string>();
            var buffer = new byte[16384];
            using var cts = new CancellationTokenSource(5000);
            int expectedTotal = 0;

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var msg = JsonSerializer.Deserialize<ClaudeMessage>(json);
                    if (msg?.Action == MessageAction.OutputChunk && msg.Payload != null)
                    {
                        var idx = msg.Payload["chunkIndex"] is JsonElement ie ? ie.GetInt32() : 0;
                        var total = msg.Payload["totalChunks"] is JsonElement te ? te.GetInt32() : 0;
                        var data = msg.Payload["data"]?.ToString() ?? "";
                        chunkData[idx] = data;
                        expectedTotal = total;
                        if (chunkData.Count >= total) break;
                    }
                }
            }
            catch (OperationCanceledException) { }

            var reassembled = string.Concat(chunkData.Values);
            Console.WriteLine($"  Original: {original.Length} chars, Reassembled: {reassembled.Length} chars");
            Assert(reassembled == original, "Reassembled content should match original");

            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            server.Stop();
            server.Dispose();
            Pass("Chunk data integrity");
        }
        catch (Exception ex) { Fail("Chunk data integrity", ex.Message); }
    }

    private static void TestProcessWatcherExists()
    {
        Console.WriteLine("\n[Test 5] Process watcher start/stop");
        try
        {
            var service = new ClaudeAutomationService();
            service.StartProcessWatcher();
            // No exception = success
            Thread.Sleep(500);
            service.StopProcessWatcher();
            service.Dispose();
            Pass("Process watcher start/stop");
        }
        catch (Exception ex) { Fail("Process watcher start/stop", ex.Message); }
    }

    // --- Helpers ---

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception($"Assertion failed: {message}");
    }

    private static void Pass(string name) { _passed++; Console.WriteLine($"  PASS: {name}"); }
    private static void Fail(string name, string error) { _failed++; Console.WriteLine($"  FAIL: {name} - {error}"); }

    // Simple mock (no WPF dependencies)
    private class MockClaudeService : IClaudeAutomationService
    {
        public bool IsClaudeRunning => true;
        public string CurrentMode => "chat";
        public event EventHandler<string>? OutputChanged;
        public event EventHandler<bool>? ClaudeStatusChanged;
        public event EventHandler<List<ButtonInfo>>? ActionButtonsChanged;
        public Task<bool> FindClaudeWindowAsync() => Task.FromResult(true);
        public Task<string> GetOutputAsync(string scope = "latest") => Task.FromResult("mock");
        public Task<bool> SendInputAsync(string text) => Task.FromResult(true);
        public Task<bool> SwitchModeAsync(string targetMode) => Task.FromResult(true);
        public Task<List<SessionInfo>> GetSessionsAsync() => Task.FromResult(new List<SessionInfo>());
        public Task<bool> SelectSessionAsync(string sessionId) => Task.FromResult(true);
        public Task<bool> AddSessionAsync() => Task.FromResult(true);
        public Task<List<ProjectInfo>> GetProjectsAsync() => Task.FromResult(new List<ProjectInfo>());
        public Task<bool> SelectProjectAsync(string projectId) => Task.FromResult(true);
        public Task<List<ButtonInfo>> GetActionButtonsAsync() => Task.FromResult(new List<ButtonInfo>());
        public Task<bool> ClickButtonAsync(string buttonId) => Task.FromResult(true);
        public void StartMonitoring() { }
        public void StopMonitoring() { }
        public void StartProcessWatcher() { }
        public void StopProcessWatcher() { }
    }
}
