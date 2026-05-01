using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfectBluetoothMidi;

/// <summary>
/// App-global settings — theme, host-endpoint backend choice, virtual port
/// name. Kept separate from <see cref="DeviceSetting"/> because that one is
/// keyed by BLE MAC; these apply to the whole app.
///
/// Persists to %AppData%\PerfectBluetoothMidi\app.json. Corrupt/missing file
/// → defaults returned; callers don't have to worry about exceptions.
/// </summary>
public sealed class AppSettings
{
    /// <summary>"System", "Light", or "Dark". Anything else falls back to "System".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// Which host-side MIDI surface to use for the bridge:
    ///   "Auto"     — prefer the WMS App SDK virtual-device path; fall back to
    ///                WinMM loopback if the SDK runtime isn't installed.
    ///   "Virtual"  — force the virtual-device path. Bridge fails to start if
    ///                the SDK runtime is missing.
    ///   "Loopback" — force the legacy WMS-loopback / WinMM path even when
    ///                the SDK runtime is available (escape hatch for users
    ///                who hit a virtual-device bug).
    /// Anything else is treated as "Auto".
    /// </summary>
    public string HostBackend { get; set; } = "Auto";

    /// <summary>
    /// Endpoint name shown to other apps when running the virtual-device
    /// backend. Pre-populated to a sensible default; the user can edit it.
    /// Ignored entirely in the loopback path (the user picks an existing
    /// loopback there).
    /// </summary>
    public string VirtualPortName { get; set; } = "BT-MIDI Bridge";

    /// <summary>
    /// When true (default), the app starts a BLE scan on launch and, if it
    /// sees the device whose MAC is in <see cref="LastConnectedMac"/>,
    /// connects to it automatically. If that device isn't advertising
    /// (powered off, paired elsewhere, out of range), the scan times out
    /// silently and the user falls back to the manual flow.
    /// </summary>
    public bool AutoReconnectOnLaunch { get; set; } = true;

    /// <summary>
    /// MAC of the last BLE device the app successfully connected to,
    /// formatted as <c>XX:XX:XX:XX:XX:XX</c> (matches
    /// <see cref="DeviceSettingsStore.FormatMac"/>). Empty if the user
    /// hasn't connected to anything yet. Used by the auto-reconnect flow
    /// to know which advertisement to act on.
    /// </summary>
    public string LastConnectedMac { get; set; } = "";
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
