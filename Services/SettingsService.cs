using System.IO;
using System.Text.Json;

namespace ClaudetRelay.Services;

public class AppSettings
{
    // Legacy — kept only for one-time migration to Windows Credential Manager
    public string ClaudeApiKey        { get; set; } = "";

    public string OllamaBaseUrl       { get; set; } = "http://localhost:11434";
    public string OllamaModel         { get; set; } = "llama3.2";
    public string LastTheme           { get; set; } = "";
    public string SelectedProvider    { get; set; } = "Anthropic";
    public string SelectedCloudModel  { get; set; } = "";
    public bool   CloudAIEnabled      { get; set; } = true;
    public int    OllamaInstanceCount { get; set; } = 1;
}

public static class SettingsService
{
    private static readonly string FilePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Settings", "settings.json");

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, ReadOpts)
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, WriteOpts);
            File.WriteAllText(FilePath, json);
        }
        catch { /* silent – missing save should not crash the app */ }
    }
}
