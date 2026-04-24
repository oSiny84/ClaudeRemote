using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ClaudeRemote.Windows.Services;
using ClaudeRemote.Windows.ViewModels;
using ClaudeRemote.Windows.Services;
using Serilog;

namespace ClaudeRemote.Windows;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    // Runtime toggle for the file sink. The File sink is wired unconditionally
    // via WriteTo.Conditional; flipping this bool takes effect immediately
    // without re-initializing the logger.
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = AppSettings.Load();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Conditional(
                _ => Settings.LogToFile,
                wt => wt.File("logs/clauderemote-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    fileSizeLimitBytes: 10 * 1024 * 1024, // 10MB per file
                    rollOnFileSizeLimit: true))
            .WriteTo.Console()
            .CreateLogger();

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        Log.Information("ClaudeRemote Windows started");
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Services
        services.AddSingleton<IClaudeAutomationService, ClaudeAutomationService>();
        services.AddSingleton<IWebSocketServerService, WebSocketServerService>();
        services.AddSingleton<IFileServerService, FileServerService>();   // Phase 12
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<IMessageProcessor, MessageProcessor>();

        // ViewModels
        services.AddTransient<MainViewModel>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
