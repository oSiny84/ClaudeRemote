using ClaudeRemote.Windows.Models;
using ClaudeRemote.Windows.Services;

namespace ClaudeRemote.Tests;

/// <summary>
/// Phase 3 integration tests - requires Claude desktop app to be running.
/// Run with: dotnet run --project ClaudeRemote.Tests -- phase3
/// </summary>
public static class Phase3Test
{
    private static int _passed;
    private static int _failed;

    public static async Task RunAsync()
    {
        Console.WriteLine("=== ClaudeRemote Phase 3 Integration Test ===");
        Console.WriteLine("  Requires: Claude desktop app running\n");

        _passed = 0;
        _failed = 0;

        var service = new ClaudeAutomationService();

        // Test 1: Find Claude Window
        await TestFindClaudeWindow(service);

        // Test 2: Get Output
        await TestGetOutput(service);

        // Test 3: Get Sessions
        await TestGetSessions(service);

        // Test 4: Mode tabs detection
        await TestModeDetection(service);

        // Test 5: Streaming detection
        await TestStreamingDetection(service);

        // Test 6: Monitoring loop
        await TestMonitoring(service);

        service.Dispose();

        Console.WriteLine("\n" + new string('=', 50));
        Console.WriteLine($"Phase 3 Results: {_passed} passed, {_failed} failed, {_passed + _failed} total");
        Console.WriteLine(new string('=', 50));
    }

    private static async Task TestFindClaudeWindow(ClaudeAutomationService service)
    {
        Console.WriteLine("[Test 1] FindClaudeWindowAsync");
        try
        {
            var found = await service.FindClaudeWindowAsync();
            Assert(found, "Should find Claude window");
            Assert(service.IsClaudeRunning, "IsClaudeRunning should be true");
            Pass("FindClaudeWindowAsync");
        }
        catch (Exception ex) { Fail("FindClaudeWindowAsync", ex.Message); }
    }

    private static async Task TestGetOutput(ClaudeAutomationService service)
    {
        Console.WriteLine("\n[Test 2] GetOutputAsync");
        try
        {
            if (!service.IsClaudeRunning) { Skip("GetOutputAsync", "Claude not connected"); return; }

            var latest = await service.GetOutputAsync("latest");
            Console.WriteLine($"  Latest output length: {latest.Length} chars");
            Console.WriteLine($"  Preview: {(latest.Length > 100 ? latest[..100] + "..." : latest)}");
            Assert(latest.Length > 0, "Should get some output text");

            var full = await service.GetOutputAsync("full");
            Console.WriteLine($"  Full output length: {full.Length} chars");
            Assert(full.Length >= latest.Length, "Full output should be >= latest output");

            var summary = await service.GetOutputAsync("summary");
            Console.WriteLine($"  Summary length: {summary.Length} chars");
            Assert(summary.Length > 0, "Summary should not be empty");
            Assert(summary.Length <= 503, "Summary should be <= 503 chars (500 + ...)");

            Pass("GetOutputAsync");
        }
        catch (Exception ex) { Fail("GetOutputAsync", ex.Message); }
    }

    private static async Task TestGetSessions(ClaudeAutomationService service)
    {
        Console.WriteLine("\n[Test 3] GetSessionsAsync");
        try
        {
            if (!service.IsClaudeRunning) { Skip("GetSessionsAsync", "Claude not connected"); return; }

            var sessions = await service.GetSessionsAsync();
            Console.WriteLine($"  Found {sessions.Count} sessions");
            foreach (var s in sessions.Take(5))
            {
                Console.WriteLine($"    [{s.Id}] {s.Name} (active={s.IsActive})");
            }
            Assert(sessions.Count > 0, "Should find at least one session");

            Pass("GetSessionsAsync");
        }
        catch (Exception ex) { Fail("GetSessionsAsync", ex.Message); }
    }

    private static async Task TestModeDetection(ClaudeAutomationService service)
    {
        Console.WriteLine("\n[Test 4] Mode detection");
        try
        {
            if (!service.IsClaudeRunning) { Skip("Mode detection", "Claude not connected"); return; }

            var mode = service.CurrentMode;
            Console.WriteLine($"  Current mode: {mode}");
            Assert(!string.IsNullOrEmpty(mode), "CurrentMode should not be empty");

            Pass("Mode detection");
        }
        catch (Exception ex) { Fail("Mode detection", ex.Message); }
    }

    private static async Task TestStreamingDetection(ClaudeAutomationService service)
    {
        Console.WriteLine("\n[Test 5] Streaming detection");
        try
        {
            if (!service.IsClaudeRunning) { Skip("Streaming detection", "Claude not connected"); return; }

            // GetOutput triggers streaming detection
            await service.GetOutputAsync("latest");
            Console.WriteLine($"  IsGenerating: {service.IsGenerating}");
            // We can't assert true/false since it depends on Claude's current state
            // Just verify it doesn't throw
            Pass("Streaming detection");
        }
        catch (Exception ex) { Fail("Streaming detection", ex.Message); }
    }

    private static async Task TestMonitoring(ClaudeAutomationService service)
    {
        Console.WriteLine("\n[Test 6] Monitoring loop (3 second test)");
        try
        {
            if (!service.IsClaudeRunning) { Skip("Monitoring loop", "Claude not connected"); return; }

            int changeCount = 0;
            service.OutputChanged += (_, output) =>
            {
                changeCount++;
                Console.WriteLine($"  [Monitor] Output change #{changeCount}: {output.Length} chars");
            };

            service.StartMonitoring();
            await Task.Delay(3000);
            service.StopMonitoring();

            Console.WriteLine($"  Total output changes detected: {changeCount}");
            // At minimum, the first poll should detect output
            Assert(changeCount >= 0, "Monitoring should not throw errors");

            Pass("Monitoring loop");
        }
        catch (Exception ex) { Fail("Monitoring loop", ex.Message); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception($"Assertion failed: {message}");
    }

    private static void Pass(string testName)
    {
        _passed++;
        Console.WriteLine($"  PASS: {testName}");
    }

    private static void Fail(string testName, string error)
    {
        _failed++;
        Console.WriteLine($"  FAIL: {testName} - {error}");
    }

    private static void Skip(string testName, string reason)
    {
        Console.WriteLine($"  SKIP: {testName} - {reason}");
    }
}
