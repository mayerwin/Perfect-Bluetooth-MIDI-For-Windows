using System;
using System.IO;

namespace PerfectBluetoothMidi;

/// <summary>
/// Startup breadcrumb trail that survives a process death our managed crash
/// net cannot see.
///
/// Why this exists: <see cref="Program"/> installs AppDomain.UnhandledException,
/// TaskScheduler.UnobservedTaskException and a try/catch around all of Main,
/// which between them catch every *managed* failure and write
/// <c>PerfectBluetoothMidi.crash.log</c>. None of them fire for a native fail-fast
/// (STATUS_STACK_BUFFER_OVERRUN / 0xC0000409, what __fastfail raises): the
/// process is torn down on the spot with no unwind, no handlers, no dump from
/// us. That is what issue #5 reported — the user had nothing but an Event
/// Viewer BEX64 entry naming module "unknown", which is unactionable.
///
/// A fail-fast can't be caught, so instead we leave a trail. Each risky
/// startup phase writes its name to a marker file BEFORE entering, and
/// <see cref="Complete"/> deletes the file once startup finishes. A marker
/// still on disk at the next launch therefore means "the previous run died
/// during this phase", which we log loudly and, for phases that have a
/// fallback, route around automatically.
///
/// Deliberately plain text and hand-rolled: no JSON, no serializer, nothing
/// reflection-based, so it stays trim-safe (see the trim notes in CLAUDE.md)
/// and cannot itself throw during the one code path whose whole job is to
/// work when everything else is broken. Every operation is best-effort;
/// failing to write a breadcrumb must never be what stops the app starting.
/// </summary>
internal static class StartupTrace
{
    /// <summary>Phase name recorded for the WMS App SDK runtime probe.</summary>
    public const string PhaseWmsSdkInit = "wms-sdk-init";

    /// <summary>Phase name recorded while opening the virtual UMP endpoint.</summary>
    public const string PhaseVirtualEndpoint = "virtual-endpoint-open";

    private static readonly object _lock = new();
    private static string? _markerPath;
    private static bool _initialised;

    /// <summary>
    /// Phase the PREVIOUS run died in, or null if it shut down cleanly (or
    /// this is a first run). Read after <see cref="Initialize"/>.
    /// </summary>
    public static string? PreviousFailurePhase { get; private set; }

    /// <summary>
    /// True when the WMS SDK probe must be skipped this run: either the user
    /// passed <c>--no-wms</c>, or the previous run died inside the probe.
    /// The caller falls back to the loopback backend, which needs none of the
    /// SDK runtime DLLs.
    /// </summary>
    public static bool SkipWmsProbe { get; private set; }

    /// <summary>Reason <see cref="SkipWmsProbe"/> is set, for the log + UI.</summary>
    public static string? SkipWmsReason { get; private set; }

    /// <summary>
    /// Read any breadcrumb left by a previous run, report it, and decide
    /// whether to enter safe mode. Call once, early in Main, AFTER
    /// <c>CrashLog.Initialize</c> so the findings reach the log file.
    /// </summary>
    public static void Initialize(bool forceSkipWms, bool forceEnableWms)
    {
        lock (_lock)
        {
            if (_initialised) return;
            _initialised = true;

            _markerPath = ResolveMarkerPath();

            string? phase = null;
            try
            {
                if (_markerPath is not null && File.Exists(_markerPath))
                {
                    phase = File.ReadAllText(_markerPath).Trim();
                    // Consume it: if this run dies too, the phase we're in
                    // now is what gets recorded, and a one-off failure never
                    // pins the app into safe mode forever.
                    try { File.Delete(_markerPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                CrashLog.Append($"Startup marker unreadable ({ex.GetType().Name}: {ex.Message}); continuing.");
            }

            if (!string.IsNullOrEmpty(phase))
            {
                PreviousFailurePhase = phase;
                CrashLog.Append(
                    $"NOTE: the previous run did not shut down cleanly. It stopped during startup phase '{phase}'. " +
                    "A native fail-fast (or a kill / power loss) leaves no crash log, so this breadcrumb is all we have.");
            }

            if (forceEnableWms)
            {
                // --wms overrides everything, including a safe mode latched
                // by a previous crash. Lets a user re-test after installing
                // or repairing the SDK runtime.
                CrashLog.Append("--wms passed: forcing the WMS SDK probe on for this run.");
            }
            else if (forceSkipWms)
            {
                SkipWmsProbe = true;
                SkipWmsReason = "--no-wms was passed on the command line";
            }
            else if (phase == PhaseWmsSdkInit || phase == PhaseVirtualEndpoint)
            {
                SkipWmsProbe = true;
                // Kept short: this string gets nested inside other log lines
                // and the backend-selection message. The full explanation goes
                // out once, below.
                SkipWmsReason = $"the previous run died during '{phase}' (Windows MIDI Services App SDK work)";
            }

            if (SkipWmsProbe)
                CrashLog.Append(
                    $"WMS SDK safe mode: {SkipWmsReason}. Using the classic loopback backend for this run, " +
                    "which needs none of the SDK runtime DLLs. Pass --wms to force the SDK probe back on " +
                    "once the underlying problem is fixed.");
        }
    }

    /// <summary>
    /// Record that we are about to enter <paramref name="phase"/>. Written
    /// and flushed to disk before the risky call, so a fail-fast inside that
    /// call leaves the breadcrumb behind.
    /// </summary>
    public static void Begin(string phase)
    {
        CrashLog.Append($"Startup phase: {phase}…");
        lock (_lock)
        {
            if (_markerPath is null) return;
            try { File.WriteAllText(_markerPath, phase); }
            catch { /* best-effort: never block startup on a breadcrumb */ }
        }
    }

    /// <summary>
    /// Leave the current phase, however it ended. The phase didn't take the
    /// process down, which is all the breadcrumb is there to record, so this
    /// is called from a finally and says nothing about success or failure.
    /// </summary>
    public static void ClearPhase() => Clear();

    /// <summary>
    /// Drop safe mode for the rest of this run, so the next EnsureInitialized
    /// probes for real. Called when the user explicitly asks to try the
    /// virtual port again; the equivalent of --wms, but reachable from the UI.
    /// </summary>
    public static void ClearSafeMode()
    {
        lock (_lock)
        {
            SkipWmsProbe  = false;
            SkipWmsReason = null;
        }
    }

    /// <summary>Startup got far enough that a crash is no longer a startup crash.</summary>
    public static void Complete() => Clear();

    private static void Clear()
    {
        lock (_lock)
        {
            if (_markerPath is null) return;
            try { if (File.Exists(_markerPath)) File.Delete(_markerPath); }
            catch { }
        }
    }

    /// <summary>
    /// startup.marker in the app's data folder (see <see cref="AppPaths"/>),
    /// alongside the settings files, so it inherits a location already proven
    /// writable. Returns null if that can't be resolved, in which case tracing
    /// is simply off and the app still starts.
    /// </summary>
    private static string? ResolveMarkerPath()
    {
        try
        {
            return AppPaths.StartupMarkerFile;
        }
        catch
        {
            return null;
        }
    }
}
