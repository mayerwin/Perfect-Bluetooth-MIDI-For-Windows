using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace PerfectBluetoothMidi;

/// <summary>
/// Keeps one GUI instance per install, and lets a second launch bring the
/// running one to the front instead of starting a rival copy.
///
/// Why this is not optional. With "start minimised to the system tray" on,
/// every launch hides its window, so double-clicking the exe to "open the app"
/// silently started ANOTHER hidden process. The user could never get a window
/// back, and the copies fought over the virtual MIDI port name and the BLE
/// device. Both issues #3 and #4 hit this. A tray icon alone does not fix it:
/// people reach for the exe they launched last time.
///
/// So a second launch now signals the first and exits immediately, which makes
/// double-clicking the exe mean "show me the app" whether it is visible,
/// hidden in the tray, or not running at all.
///
/// Mechanism is two named kernel objects and no dependencies: a Mutex that
/// decides who is primary, and an EventWaitHandle the newcomer sets to ask the
/// primary to show itself. Both are <c>Local\</c>-scoped, so they are per-user
/// and per-session — two people on the same machine via Fast User Switching
/// each get their own instance, which is what you want.
///
/// Scoped to the install, not the machine: the name is derived from the app's
/// data folder, so two portable copies in different folders stay independent
/// (they have separate settings, so treating them as one app would be wrong).
///
/// GUI only. CLI invocations (<c>--scan</c>, <c>--connect</c>) never take part,
/// so scripted runs still work alongside a running GUI.
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;
    private static Thread? _listener;
    private static volatile bool _stop;

    /// <summary>
    /// Raised on a background thread when another launch asked us to show the
    /// window. Handlers must marshal to the UI thread themselves.
    /// </summary>
    public static event Action? ActivationRequested;

    /// <summary>
    /// True if we are the only instance and should carry on starting. False if
    /// another instance already owns this install — the caller should exit,
    /// having already asked the owner to surface via <see cref="SignalPrimary"/>.
    /// Never throws: if the kernel objects cannot be created for any reason we
    /// report ourselves primary, because starting a possibly-duplicate app is a
    /// far smaller failure than refusing to start at all.
    /// </summary>
    public static bool TryBecomePrimary()
    {
        try
        {
            string key = KeyForInstall();
            _mutex = new Mutex(initiallyOwned: false, $"Local\\PerfectBluetoothMidi-{key}-mutex");
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
                                                 $"Local\\PerfectBluetoothMidi-{key}-activate");

            // Zero timeout: we only want it if it is free right now.
            bool primary;
            try { primary = _mutex.WaitOne(TimeSpan.Zero, exitContext: false); }
            catch (AbandonedMutexException) { primary = true; } // previous owner crashed; we inherit it

            return primary;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Ask the instance that owns this install to show its window. Called by a
    /// second launch just before it exits. Best-effort.
    /// </summary>
    public static void SignalPrimary()
    {
        try { _activateEvent?.Set(); } catch { }
    }

    /// <summary>
    /// Start watching for later launches. Primary instance only, after the UI
    /// exists. The thread is background so it never holds the process open.
    /// </summary>
    public static void StartListening()
    {
        if (_activateEvent is null || _listener is not null) return;
        _listener = new Thread(() =>
        {
            while (!_stop)
            {
                try
                {
                    // Time-boxed so _stop is honoured promptly on shutdown.
                    if (!_activateEvent.WaitOne(TimeSpan.FromMilliseconds(500))) continue;
                    if (_stop) return;
                    ActivationRequested?.Invoke();
                }
                catch
                {
                    return; // handle disposed during shutdown
                }
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstanceListener",
        };
        _listener.Start();
    }

    /// <summary>Release the mutex and stop listening. Safe to call twice.</summary>
    public static void Release()
    {
        _stop = true;
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
        try { _activateEvent?.Dispose(); } catch { }
        _activateEvent = null;
    }

    /// <summary>
    /// Short, filesystem-safe token identifying this install. Hashes the data
    /// folder rather than using it raw: kernel object names have a length limit
    /// and cannot contain a backslash.
    /// </summary>
    private static string KeyForInstall()
    {
        string root;
        try { root = AppPaths.Root; } catch { root = "default"; }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 8);
    }
}
