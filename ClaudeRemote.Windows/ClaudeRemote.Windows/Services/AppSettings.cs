using System.IO;
using System.Text.Json;

namespace ClaudeRemote.Windows.Services;

/// <summary>
/// Tiny JSON-backed settings store for user-toggleable flags.
/// File lives next to the exe as <c>appsettings.json</c>.
/// </summary>
public sealed class AppSettings
{
    public bool LogToFile { get; set; } = false;

    private static readonly string SettingsPath = Path.Combine(
        AppContext.BaseDirectory, "appsettings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* best-effort persistence */ }
    }
}
