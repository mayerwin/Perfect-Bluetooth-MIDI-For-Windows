using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PerfectBluetoothMidi;

/// <summary>
/// Single source of truth for where this app keeps its files.
///
/// The app ships as one self-contained exe with no installer, so it should
/// behave like a portable app: everything it writes lives in a
/// <c>PerfectBluetoothMidi.data</c> folder NEXT TO THE EXE, and moving or
/// deleting that pair takes the whole installation with it. Historically the
/// two settings files went to <c>%AppData%\PerfectBluetoothMidi</c> while the
/// crash log went beside the exe, which was neither one thing nor the other;
/// <see cref="MigrateLegacyFiles"/> cleans that up on first run.
///
/// Roaming AppData stays as a fallback for the one case portable mode can't
/// serve: an exe in a read-only location (Program Files, a network share, a
/// locked-down managed desktop). We decide by actually writing a probe file
/// rather than guessing from the path, because "is this directory writable"
/// depends on ACLs, virtualisation and policy in ways a path can't tell you.
///
/// ORDERING CONSTRAINT: this type must not call into <see cref="CrashLog"/>.
/// CrashLog's own static initialiser asks us for <see cref="CrashLogFile"/>,
/// so logging from here would be a cycle. Instead we buffer what happened in
/// <see cref="StartupMessages"/> and Program.Main drains it into the log once
/// CrashLog is up.
/// </summary>
internal static class AppPaths
{
    /// <summary>Folder name created next to the exe in portable mode.</summary>
    private const string PortableFolderName = "PerfectBluetoothMidi.data";

    private static readonly List<string> _messages = new();
    private static readonly object _lock = new();

    /// <summary>Directory every app-written file lives in. Created if needed.</summary>
    public static string Root { get; }

    /// <summary>
    /// True when <see cref="Root"/> is the folder beside the exe (the normal
    /// case), false when we fell back to roaming AppData because the exe's
    /// directory wasn't writable.
    /// </summary>
    public static bool IsPortable { get; }

    public static string AppSettingsFile   => Path.Combine(Root, "app.json");
    public static string DeviceSettingsFile => Path.Combine(Root, "devices.json");
    public static string StartupMarkerFile => Path.Combine(Root, "startup.marker");
    public static string CrashLogFile      => Path.Combine(Root, "PerfectBluetoothMidi.crash.log");

    /// <summary>
    /// What happened while resolving paths, in order, for the log. Drained by
    /// Program.Main after CrashLog is initialised. See the ordering note above.
    /// </summary>
    public static IReadOnlyList<string> StartupMessages
    {
        get { lock (_lock) return _messages.ToArray(); }
    }

    static AppPaths()
    {
        string? exeDir = ResolveExeDirectory();
        string? portable = null;

        if (exeDir is not null)
        {
            string candidate = Path.Combine(exeDir, PortableFolderName);
            if (TryPrepareWritableDirectory(candidate, out string? why))
            {
                portable = candidate;
            }
            else
            {
                Note($"Portable data folder '{candidate}' is not usable ({why}). " +
                     "Falling back to roaming AppData. This is expected when the exe " +
                     "sits somewhere read-only such as Program Files.");
            }
        }

        if (portable is not null)
        {
            Root = portable;
            IsPortable = true;
        }
        else
        {
            Root = LegacyAppDataFolder();
            IsPortable = false;
            try { Directory.CreateDirectory(Root); } catch { }
        }

        MigrateLegacyFiles(exeDir);
    }

    /// <summary>
    /// Move files written by earlier versions into <see cref="Root"/>: the two
    /// settings files from <c>%AppData%\PerfectBluetoothMidi</c>, and the crash
    /// log from beside the exe. Copy-verify-then-delete rather than File.Move
    /// so a failure midway leaves the original readable instead of nothing at
    /// all. Entirely best-effort: losing a setting is bad, failing to start
    /// because of a migration is worse.
    /// </summary>
    private static void MigrateLegacyFiles(string? exeDir)
    {
        if (!IsPortable) return; // already living in AppData; nothing to move

        string legacy = LegacyAppDataFolder();
        try
        {
            if (Directory.Exists(legacy) &&
                !string.Equals(Path.GetFullPath(legacy).TrimEnd('\\'),
                               Path.GetFullPath(Root).TrimEnd('\\'),
                               StringComparison.OrdinalIgnoreCase))
            {
                foreach (string name in new[] { "app.json", "devices.json" })
                    TryMoveInto(Path.Combine(legacy, name), Path.Combine(Root, name));

                // Leave no empty shell behind, but only if we really emptied it.
                try
                {
                    if (Directory.Exists(legacy) &&
                        Directory.GetFileSystemEntries(legacy).Length == 0)
                    {
                        Directory.Delete(legacy);
                        Note($"Removed the now-empty legacy settings folder '{legacy}'.");
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Note($"Legacy settings migration skipped ({ex.GetType().Name}: {ex.Message}).");
        }

        // Crash log used to be written as <exe>.crash.log beside the exe.
        if (exeDir is not null)
        {
            try
            {
                string oldCrash = Path.Combine(exeDir, "PerfectBluetoothMidi.crash.log");
                TryMoveInto(oldCrash, CrashLogFile);
            }
            catch { }
        }
    }

    /// <summary>
    /// Move one file, but never clobber a destination that already exists
    /// (the new location wins: it is by definition more recent than a file
    /// left over from an older version).
    /// </summary>
    private static void TryMoveInto(string source, string destination)
    {
        try
        {
            if (!File.Exists(source)) return;
            if (File.Exists(destination))
            {
                Note($"Kept the existing '{Path.GetFileName(destination)}'; " +
                     $"the older copy at '{source}' was left alone.");
                return;
            }

            File.Copy(source, destination, overwrite: false);
            if (File.Exists(destination))
            {
                try { File.Delete(source); } catch { /* copy succeeded; that's what matters */ }
                Note($"Migrated '{Path.GetFileName(source)}' into '{Root}'.");
            }
        }
        catch (Exception ex)
        {
            Note($"Could not migrate '{source}' ({ex.GetType().Name}: {ex.Message}); " +
                 "the app will start with defaults for it.");
        }
    }

    /// <summary>
    /// Create <paramref name="dir"/> if needed and prove we can write in it by
    /// round-tripping a probe file. Guessing from the path is not enough:
    /// Program Files, network shares and managed desktops all fail in ways a
    /// path inspection would miss.
    /// </summary>
    private static bool TryPrepareWritableDirectory(string dir, out string? failureReason)
    {
        failureReason = null;
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static string LegacyAppDataFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PerfectBluetoothMidi");

    /// <summary>
    /// Directory holding the running exe. Environment.ProcessPath is the right
    /// answer for a single-file publish (it gives the exe, not the extracted
    /// temp bundle); MainModule is the fallback.
    /// </summary>
    private static string? ResolveExeDirectory()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return null;
            return Path.GetDirectoryName(exe);
        }
        catch
        {
            return null;
        }
    }

    private static void Note(string message)
    {
        lock (_lock) _messages.Add(message);
    }
}
