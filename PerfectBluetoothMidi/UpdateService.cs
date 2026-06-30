#if SELF_UPDATE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PerfectBluetoothMidi;

/// <summary>
/// Lightweight, dependency-free self-updater for the portable single-file
/// build. Checks the project's GitHub releases for a newer version, downloads
/// the <c>PerfectBluetoothMidi.exe</c> asset over HTTPS, verifies it against
/// the release asset's SHA-256 digest, then swaps itself in place and relaunches.
///
/// This whole file compiles to nothing unless the <c>SELF_UPDATE</c> constant
/// is defined (driven by the <c>EnableSelfUpdate</c> MSBuild property, default
/// true). The Microsoft Store build is published with
/// <c>-p:EnableSelfUpdate=false</c>, which removes the updater entirely — the
/// Store handles its own updates and forbids self-updating apps.
///
/// Why a custom updater instead of Velopack/Squirrel/NetSparkle: those assume an
/// installer/package distribution model. This app ships as a single portable exe
/// attached to a GitHub release, and the swap below is the idiomatic pattern for
/// that model — no extra process, no package store, no install step.
///
/// The swap relies on a Windows quirk: you cannot delete or overwrite a running
/// executable, but you CAN rename/move it (the open image handle keeps the file
/// data alive; only the directory entry moves). So we rename the running exe to
/// <c>*.old</c>, drop the freshly-downloaded exe at the original path, launch it,
/// and exit. The leftover <c>*.old</c> is deleted on the next launch by
/// <see cref="CleanupLeftovers"/>.
/// </summary>
internal static class UpdateService
{
    // GitHub repo coordinates. Kept here (not in settings) on purpose — the
    // update source is a property of the build, not user-configurable.
    private const string Owner = "mayerwin";
    private const string Repo  = "Perfect-Bluetooth-MIDI-For-Windows";

    private const string AssetName = "PerfectBluetoothMidi.exe";

    /// <summary>Public releases page, used as the manual-download fallback.</summary>
    public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repo}/releases/latest";

    /// <summary>Full path to the running executable, or null if unknowable.</summary>
    public static string? ExePath => Environment.ProcessPath;

    /// <summary>
    /// The running app's version, normalised to Major.Minor.Build (the form
    /// release tags use — e.g. "v1.4.0"). Revision is dropped so a 1.4.0.0
    /// assembly version compares equal to a "v1.4.0" tag.
    /// </summary>
    public static Version CurrentVersion { get; } =
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    // One static HttpClient for the process — avoids socket exhaustion. GitHub
    // requires a User-Agent header or it returns 403.
    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        c.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("PerfectBluetoothMidi", CurrentVersion.ToString()));
        c.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Details of a release newer than the running build.</summary>
    public sealed record UpdateInfo(
        Version Version,
        string Tag,
        string AssetUrl,
        string? Sha256Hex,
        long AssetSize,
        string HtmlUrl,
        string? Notes);

    /// <summary>
    /// Whether this build is in a position to replace itself: we know our exe
    /// path, it's a real .exe on disk, and its folder is writable. Returns the
    /// reason it can't in <paramref name="reason"/> for logging. When false the
    /// caller should fall back to opening <see cref="ReleasesPageUrl"/>.
    /// </summary>
    public static bool CanSelfUpdate(out string reason)
    {
        string? exe = ExePath;
        if (string.IsNullOrEmpty(exe))
        {
            reason = "the running executable path is unknown";
            return false;
        }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe))
        {
            reason = "this doesn't look like the packaged single-file exe";
            return false;
        }
        string? dir = Path.GetDirectoryName(exe);
        if (dir is null || !IsDirectoryWritable(dir))
        {
            reason = "the app's folder isn't writable (try a non-protected location, or update manually)";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// Query GitHub for the latest release and return its details if it's newer
    /// than <see cref="CurrentVersion"/>; otherwise null. Throws on network /
    /// API errors so the caller can surface them (for manual checks) or swallow
    /// them (for background checks).
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        // JsonDocument is reflection-free, so it stays safe under PublishTrimmed
        // without needing a source-generated context for GitHub's schema.
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        // The /releases/latest endpoint already excludes drafts and prereleases,
        // but guard defensively in case the endpoint behaviour ever changes.
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;

        string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!TryParseTag(tag, out Version version)) return null;
        if (version <= CurrentVersion) return null; // already current or newer

        // Find our exe asset and (when available) its SHA-256 digest.
        string? assetUrl = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (!a.TryGetProperty("name", out var n) ||
                    !string.Equals(n.GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                size     = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long s) ? s : 0;
                // GitHub exposes "digest": "sha256:<hex>" for assets uploaded
                // since June 2025; older assets have null. Verify when present.
                if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    string raw = d.GetString() ?? "";
                    if (raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = raw["sha256:".Length..].Trim();
                }
                break;
            }
        }

        if (string.IsNullOrEmpty(assetUrl)) return null; // release without our asset — nothing to install

        string html  = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? ReleasesPageUrl) : ReleasesPageUrl;
        string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

        return new UpdateInfo(version, tag!, assetUrl!, sha, size, html, notes);
    }

    /// <summary>
    /// Download the new exe, verify its SHA-256 against the release digest, and
    /// swap it in place of the running exe. On success the new exe sits at
    /// <see cref="ExePath"/> and the old one is renamed to <c>*.old</c> (cleaned
    /// up next launch). Throws on any failure, having rolled back the swap so the
    /// current exe is left intact. Call <see cref="Relaunch"/> + quit afterwards.
    /// </summary>
    public static async Task StageAsync(
        UpdateInfo info, IProgress<double>? progress, Action<string>? log, CancellationToken ct = default)
    {
        string exe = ExePath ?? throw new InvalidOperationException("Unknown executable path.");
        string updatePath = exe + ".update";

        log?.Invoke($"Downloading {AssetName} for v{info.Version}…");
        await DownloadToFileAsync(info.AssetUrl, updatePath, info.AssetSize, progress, ct).ConfigureAwait(false);

        // Integrity check. With a digest we verify the SHA-256; without one
        // (pre-June-2025 assets) we proceed on HTTPS alone but say so.
        if (!string.IsNullOrEmpty(info.Sha256Hex))
        {
            string actual = await ComputeSha256Async(updatePath, ct).ConfigureAwait(false);
            if (!string.Equals(actual, info.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(updatePath);
                throw new InvalidOperationException(
                    $"Downloaded file failed SHA-256 verification (expected {info.Sha256Hex}, got {actual}).");
            }
            log?.Invoke("SHA-256 verified.");
        }
        else
        {
            log?.Invoke("No SHA-256 digest published for this asset; relying on HTTPS transport integrity.");
        }

        // Atomic-ish swap on the same volume. Rename the running exe out of the
        // way (allowed for a running image), then move the new exe into place.
        string backup = PickBackupPath(exe);
        log?.Invoke("Installing update…");
        File.Move(exe, backup);
        try
        {
            File.Move(updatePath, exe);
        }
        catch
        {
            // Roll back so the user is never left without a working exe.
            try { File.Move(backup, exe); } catch { /* best-effort restore */ }
            TryDelete(updatePath);
            throw;
        }
    }

    /// <summary>Start the (now-updated) exe fresh. Caller quits afterwards.</summary>
    public static void Relaunch()
    {
        string? exe = ExePath;
        if (string.IsNullOrEmpty(exe)) return;
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        });
    }

    /// <summary>
    /// Delete any leftover <c>*.old</c> backups (from a prior in-place update)
    /// and any stale <c>*.update</c> download. Best-effort and silent — called
    /// once at startup. The <c>*.old</c> can't be removed until the old process
    /// has exited, which is exactly why this runs on the next launch.
    /// </summary>
    public static void CleanupLeftovers()
    {
        try
        {
            string? exe = ExePath;
            if (string.IsNullOrEmpty(exe)) return;
            string? dir = Path.GetDirectoryName(exe);
            string name = Path.GetFileName(exe);
            if (dir is null) return;

            foreach (var f in Directory.EnumerateFiles(dir, name + ".old*"))
                TryDelete(f);
            TryDelete(exe + ".update");
        }
        catch { /* never let cleanup affect startup */ }
    }

    // ------------------------------------------------------------- helpers

    private static async Task DownloadToFileAsync(
        string url, string destPath, long expectedSize, IProgress<double>? progress, CancellationToken ct)
    {
        TryDelete(destPath); // clear any partial leftover
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? expectedSize;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0 && progress is not null)
                    progress.Report(Math.Clamp((double)read / total, 0, 1));
            }
        }

        if (expectedSize > 0)
        {
            long got = new FileInfo(destPath).Length;
            if (got != expectedSize)
            {
                TryDelete(destPath);
                throw new InvalidOperationException(
                    $"Downloaded size {got} bytes != expected {expectedSize} bytes (truncated download).");
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash); // upper-case hex; compared case-insensitively
    }

    /// <summary>First free <c>*.old</c> name (a prior one may still be locked).</summary>
    private static string PickBackupPath(string exe)
    {
        string candidate = exe + ".old";
        for (int i = 1; File.Exists(candidate) && i < 1000; i++)
        {
            TryDelete(candidate);
            if (!File.Exists(candidate)) break;
            candidate = exe + ".old" + i;
        }
        return candidate;
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".pbm_update_probe_" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* locked / gone — ignore */ }
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor < 0 ? 0 : v.Minor, v.Build < 0 ? 0 : v.Build);

    /// <summary>Parse a release tag like "v1.4.0" / "1.4" into a normalised Version.</summary>
    private static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        string s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        // Stop at the first non-version character (e.g. "1.4.0-rc1" -> "1.4.0").
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var parsed)) return false;
        version = Normalize(parsed);
        return true;
    }
}
#endif
