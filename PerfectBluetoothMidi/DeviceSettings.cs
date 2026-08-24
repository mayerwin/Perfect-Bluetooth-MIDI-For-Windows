using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfectBluetoothMidi;

/// <summary>
/// Per-device persisted settings, keyed by BLE MAC address. The only thing
/// stored today is the outgoing MIDI channel override — but this is the
/// natural home for future per-device knobs (e.g. custom names, preferred
/// write mode, wake-up-needed hint, etc.).
///
/// Persistence lives in
///   %AppData%\PerfectBluetoothMidi\devices.json
/// as a map of "XX:XX:XX:XX:XX:XX" → <see cref="DeviceSetting"/>. We use
/// System.Text.Json because it has zero external deps in .NET 10 and handles
/// the read/write/deserialise path with enough robustness for a local cache.
/// </summary>
public sealed class DeviceSetting
{
    /// <summary>Human-readable name last seen in advertising (best-effort).</summary>
    public string? Name { get; set; }

    /// <summary>
    /// 1..16: rewrite outgoing channel-scoped status bytes to this channel.
    /// 0 (default) = pass-through: don't touch the channel nibble.
    /// Roland FP-90X was observed to listen on channel 4 even when its
    /// front-panel "Transmit Channel" reads 1 — RX and TX channels are
    /// independent so this setting is genuinely device-specific.
    /// </summary>
    public int TransmitChannel { get; set; }

    /// <summary>
    /// True once we've learned this device rejects the targeted by-UUID GATT
    /// service query (throws ERROR_BAD_COMMAND / HRESULT 0x80070016) and is only
    /// discoverable via a full primary-service enumeration — observed on the
    /// Roland Go:Keys 5. When set, the connect path skips the doomed by-UUID
    /// attempts and goes straight to full enumeration, shaving ~1 s off each
    /// connect. Purely a performance hint: if it's ever wrong, discovery still
    /// resolves correctly (full enumeration is a superset of the by-UUID query).
    /// </summary>
    public bool PreferFullServiceDiscovery { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// Source-generated JSON context. Lets the trimmer see exactly which types
/// participate in serialisation, so the unused parts of <c>System.Text.Json</c>
/// can be dropped and the reflection-based path is avoided entirely
/// (silences IL2026 trim warnings).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, DeviceSetting>))]
internal partial class DeviceSettingsJsonContext : JsonSerializerContext { }

internal static class DeviceSettingsStore
{
    private static readonly string FilePath;
    private static readonly object _lock = new();

    static DeviceSettingsStore()
    {
        // See AppPaths: portable folder next to the exe, AppData only when
        // that is not writable. Directory creation and the migration of an
        // existing devices.json happen there.
        FilePath = AppPaths.DeviceSettingsFile;
    }

    public static string PathForLog => FilePath;

    /// <summary>Canonical colon-uppercase MAC form, used as the JSON key.</summary>
    public static string FormatMac(ulong addr)
    {
        byte[] bytes = BitConverter.GetBytes(addr);
        return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
            bytes[5], bytes[4], bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    /// <summary>
    /// Inverse of <see cref="FormatMac"/>. Accepts colon-, dash-, or no-
    /// separator hex MAC strings (case-insensitive). Returns false on any
    /// parse failure or zero address (treated as "not set"). The
    /// AutoReconnect flow uses this on <see cref="AppSettings.LastConnectedMac"/>.
    /// </summary>
    public static bool TryParseMac(string? s, out ulong addr)
    {
        addr = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        string cleaned = s.Replace(":", "").Replace("-", "").Trim();
        if (cleaned.Length != 12) return false;
        ulong result = 0;
        for (int i = 0; i < 12; i += 2)
        {
            if (!byte.TryParse(cleaned.AsSpan(i, 2),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out byte b)) return false;
            result = (result << 8) | b;
        }
        if (result == 0) return false;
        addr = result;
        return true;
    }

    public static Dictionary<string, DeviceSetting> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath)) return new();
            try
            {
                string json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json)) return new();
                return JsonSerializer.Deserialize(json, DeviceSettingsJsonContext.Default.DictionaryStringDeviceSetting)
                       ?? new Dictionary<string, DeviceSetting>();
            }
            catch
            {
                // Corrupt file? Don't clobber it — just return empty and let
                // the next Save overwrite. If that ever burns us we'll move to
                // versioned filenames.
                return new();
            }
        }
    }

    public static DeviceSetting? Get(ulong address) => Get(FormatMac(address));
    public static DeviceSetting? Get(string mac) => LoadAll().TryGetValue(mac, out var s) ? s : null;

    public static void Save(ulong address, DeviceSetting setting) => Save(FormatMac(address), setting);

    public static void Save(string mac, DeviceSetting setting)
    {
        lock (_lock)
        {
            var all = LoadAll();
            all[mac] = setting;
            try
            {
                string json = JsonSerializer.Serialize(all, DeviceSettingsJsonContext.Default.DictionaryStringDeviceSetting);
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // Non-fatal: settings just won't persist this session. The
                // caller doesn't need to care — the in-memory UI state is the
                // source of truth for the running app.
            }
        }
    }
}
