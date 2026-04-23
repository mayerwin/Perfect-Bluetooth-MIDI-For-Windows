using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfectBluetoothMidi;

/// <summary>
/// App-global settings (currently just the theme preference). Kept separate
/// from <see cref="DeviceSetting"/> because that one is keyed by BLE MAC;
/// these apply to the whole app.
///
/// Persists to %AppData%\PerfectBluetoothMidi\app.json. Corrupt/missing file
/// → defaults returned; callers don't have to worry about exceptions.
/// </summary>
public sealed class AppSettings
{
    /// <summary>"System", "Light", or "Dark". Anything else falls back to "System".</summary>
    public string Theme { get; set; } = "System";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext { }

internal static class AppSettingsStore
{
    private static readonly string FilePath;
    private static readonly object _lock = new();

    static AppSettingsStore()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PerfectBluetoothMidi");
        try { Directory.CreateDirectory(folder); } catch { }
        FilePath = Path.Combine(folder, "app.json");
    }

    public static AppSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppSettings();
                string json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
                return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)
                       ?? new AppSettings();
            }
            catch { return new AppSettings(); }
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (_lock)
        {
            try
            {
                string json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
                File.WriteAllText(FilePath, json);
            }
            catch { /* non-fatal: UI state is the source of truth this session */ }
        }
    }
}
