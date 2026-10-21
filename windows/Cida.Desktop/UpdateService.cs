using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cida.Core;
using Velopack;
using Velopack.Sources;

namespace Cida.Desktop;

/// <summary>
/// Drives the Velopack updater through a pure <see cref="UpdateSession"/>. Ported from upstream
/// CidaUpdater/CidaUpdateDriver (#79): accepting the offer is the only confirmation, the install
/// intent travels through the download, and leaving the watch revokes it while the download keeps
/// going quietly. The existing members stay as compatibility wrappers for the wave-1 Settings
/// window; the new ones are what the #79 UI uses.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateSession _session = new();
    private UpdateManager? _manager;
    private UpdateInfo? _available;
    private VelopackAsset? _prepared;
    private bool _busy;
    private Task<UpdateInfo?>? _check;
    private CancellationTokenSource? _downloadCancellation;

    public UpdateService()
    {
        // A version skipped in an earlier run is not offered again.
        if (ReadSkippedVersion() is { } skipped) _session.RememberSkipped(skipped);
    }

    /// <summary>Cleanup hook run right before the updater replaces this build and relaunches.</summary>
    public Action? PrepareForRestart { get; set; }

    /// <summary>The update session step, for the #79 panel and Settings surfaces.</summary>
    public UpdatePhase Phase => _session.Phase;

    /// <summary>The offered version, when one is known.</summary>
    public string? AvailableVersion => _session.Version;

    /// <summary>Release notes for the offered update, one item per line.</summary>
    public IReadOnlyList<string> Notes => _session.Notes;

    /// <summary>One confirmation is enough to install the offered update.</summary>
    public bool CanInstallNow
    {
        get
        {
            if (_busy) return false;
            return _session.Phase switch
            {
                UpdatePhase.Found => _available != null,
                UpdatePhase.Ready => _prepared != null || PendingRestart != null,
                _ => false,
            };
        }
    }

    /// <summary>The offered version can be skipped and never offered again.</summary>
    public bool CanSkipVersion => !_busy && _session.Phase == UpdatePhase.Found && _session.Version != null;

    /// <summary>One line describing the current step (compatibility: the Settings status row).</summary>
    public string Status => _session.StatusText;

    /// <summary>Compatibility: an update is offered and can be downloaded now.</summary>
    public bool CanDownload => !_busy && _available != null && _session.Phase == UpdatePhase.Found;

    /// <summary>Compatibility: a downloaded update can be applied and relaunch the app.</summary>
    public bool CanRestart => !_busy && (_prepared != null || PendingRestart != null);

    /// <summary>Compatibility: a download is running.</summary>
    public bool IsDownloading => _downloadCancellation != null;

    /// <summary>The reported download progress (0-100) while downloading, for a determinate bar.</summary>
    public int? DownloadPercent =>
        _session.Phase == UpdatePhase.Downloading ? _session.DownloadPercent : null;

    /// <summary>Notified after every state change.</summary>
    public event Action? Changed;

    public static string Version => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion.Split('+')[0] ?? "0.2.0-rc.7";

    private void Notify() => Changed?.Invoke();

    // MARK: Checks

    /// <summary>
    /// Checks at most once a day, silently. The timestamp file lives in the profile directory so
    /// a failed check also backs off instead of requesting on every launch.
    /// </summary>
    public async Task CheckDailyAsync()
    {
        try
        {
            if (File.Exists(CheckStampPath) && DateTime.TryParse(File.ReadAllText(CheckStampPath), out var last) && DateTime.UtcNow - last.ToUniversalTime() < TimeSpan.FromDays(1)) return;
            await CheckAsync();
            // Failed checks also back off; no repeated requests on every launch.
            Directory.CreateDirectory(ProfileDirectory);
            File.WriteAllText(CheckStampPath, DateTime.UtcNow.ToString("O"));
        }
        catch { /* Manual checks remain available; timestamps contain no user content. */ }
    }

    /// <summary>
    /// Starts a user-initiated check. A fresh check clears the earlier version, notes and error
    /// first, so a failed check never shows a stale update as available.
    /// </summary>
    public async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        _session.BeginCheck();
        Notify();
        try
        {
            var manager = Manager();
            if (!manager.IsInstalled)
            {
                _session.MarkUnavailable("开发版本：请从发布页下载安装包。");
                return;
            }
            if (PendingRestart is { } prepared)
            {
                // A download finished in an earlier session (or launch): it is installable now.
                _prepared = prepared;
                _available = null;
                _session.Found(prepared.Version.ToString(), CurrentVersionLabel(), ReleaseNotes(prepared));
                _session.BeginDownload();
                _session.DownloadCompleted();
                return;
            }
            // Reuse an unfinished check after a UI timeout; Velopack's API has no cancellation parameter.
            if (_check == null || _check.IsCompleted) _check = manager.CheckForUpdatesAsync();
            var update = await _check.WaitAsync(TimeSpan.FromSeconds(25));
            if (update == null)
            {
                _session.MarkCurrent(CurrentVersionLabel());
                return;
            }
            var release = update.TargetFullRelease;
            var version = release.Version.ToString();
            if (_session.IsSkipped(version))
            {
                // The skipped version is still the latest release: stay quiet instead of offering it again.
                _available = null;
                _session.Found(version, CurrentVersionLabel());
                _session.SkipVersion();
                return;
            }
            _available = update;
            _prepared = null;
            _session.Found(version, CurrentVersionLabel(), ReleaseNotes(release));
        }
        catch
        {
            _session.Fail("检查失败，请稍后重试或打开发布页下载。");
        }
        finally
        {
            _busy = false;
            Notify();
        }
    }

    // MARK: Download and install

    /// <summary>
    /// Compatibility wrapper: downloads the offered update without installing it. The downloaded
    /// update stays reachable through <see cref="Restart"/> / <see cref="InstallAndRestartAsync"/>.
    /// </summary>
    public async Task DownloadAsync()
    {
        if (_busy) return;
        if (_session.Phase != UpdatePhase.Found || _available == null) return;
        await DownloadCoreAsync();
    }

    /// <summary>
    /// The one-confirmation install: accepting the offer carries the install intent through the
    /// download, and the app relaunches as soon as it finishes while still being watched. When the
    /// user left meanwhile, the session settles on Ready and the deferred install stays reachable.
    /// </summary>
    public async Task InstallAndRestartAsync()
    {
        if (_busy) return;
        if (_session.ChooseInstall())
        {
            // The update was already downloaded: install it now.
            ApplyPreparedUpdate();
            return;
        }
        if (_session.Phase != UpdatePhase.Found || _available == null) return;
        Notify();
        await DownloadCoreAsync();
    }

    private async Task DownloadCoreAsync()
    {
        if (_manager is not { } manager || _available is not { } update) return;
        if (_busy || _downloadCancellation != null) return;
        _busy = true;
        _session.BeginDownload();
        _downloadCancellation = new CancellationTokenSource();
        Notify();
        try
        {
            await manager.DownloadUpdatesAsync(update, percent =>
            {
                _session.DownloadProgress(percent);
                Notify();
            }, _downloadCancellation.Token);
            _prepared = update;
            var installNow = _session.DownloadCompleted();
            Notify();
            if (installNow) ApplyPreparedUpdate();
        }
        catch (OperationCanceledException)
        {
            _session.CancelDownload();
            Notify();
        }
        catch
        {
            _session.Fail("下载失败，请重试或从发布页下载完整安装包。");
            Notify();
        }
        finally
        {
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
            _busy = false;
            Notify();
        }
    }

    /// <summary>Stops a running download; the offered version stays downloadable.</summary>
    public void CancelDownload() => _downloadCancellation?.Cancel();

    /// <summary>
    /// Compatibility wrapper: applies the downloaded update and relaunches. The new flow auto
    /// installs after <see cref="InstallAndRestartAsync"/>; this stays for the wave-1 Settings
    /// restart button and for a Ready session after the user left the panel.
    /// </summary>
    public void Restart()
    {
        if (_busy) return;
        if (_prepared == null && PendingRestart == null) return;
        ApplyPreparedUpdate();
    }

    /// <summary>
    /// The Settings page became visible or hidden. Leaving during a download or a Ready session
    /// revokes the automatic restart intent; the download itself keeps going quietly.
    /// </summary>
    public void SetWatched(bool watching)
    {
        if (_session.IsWatching == watching) return;
        _session.Watch(watching);
        Notify();
    }

    /// <summary>Remembers the offered version as skipped and clears the offer; it is persisted in
    /// the profile directory so later checks stay quiet about it.</summary>
    public void SkipVersion()
    {
        if (_session.Phase != UpdatePhase.Found) return;
        var skipped = _session.SkipVersion();
        _available = null;
        if (skipped != null) WriteSkippedVersion(skipped);
        Notify();
    }

    private void ApplyPreparedUpdate()
    {
        if (_manager is not { } manager) return;
        var prepared = _prepared ?? PendingRestart;
        if (prepared == null)
        {
            _session.Fail("没有可安装的更新，请重新检查。");
            Notify();
            return;
        }
        _session.MarkInstalling();
        Notify();
        try { PrepareForRestart?.Invoke(); } catch { /* Window cleanup is best effort; installing still matters. */ }
        try
        {
            manager.ApplyUpdatesAndRestart(prepared);
        }
        catch
        {
            _session.Fail("无法安装更新，请从下载页重新安装。");
            Notify();
        }
    }

    // MARK: Velopack helpers

    private UpdateManager Manager()
    {
        _manager ??= new UpdateManager(new GithubSource("https://github.com/Oerroot/cida-win", null, IsPreview()),
            new UpdateOptions { ExplicitChannel = IsPreview() ? "win-x64-preview" : "win-x64" });
        return _manager;
    }

    private VelopackAsset? PendingRestart
    {
        get
        {
            try { return _manager?.UpdatePendingRestart; }
            catch { return null; }
        }
    }

    private string CurrentVersionLabel()
    {
        try { return _manager?.CurrentVersion?.ToString() ?? ""; }
        catch { return ""; }
    }

    private static bool IsPreview() =>
        typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute info] && info.InformationalVersion.Contains('-');

    /// <summary>Release notes from the feed as display lines, plain text or the older HTML list.</summary>
    private static IReadOnlyList<string> ReleaseNotes(VelopackAsset release)
    {
        var lines = NoteLines(release.NotesMarkdown);
        if (lines.Count == 0) lines = NoteLines(release.NotesHTML);
        return lines;
    }

    private static List<string> NoteLines(string? text)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return lines;
        if (text.Contains("<li", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = text;
            while (true)
            {
                var open = remainder.IndexOf("<li", StringComparison.OrdinalIgnoreCase);
                if (open < 0) break;
                var tagEnd = remainder.IndexOf('>', open);
                if (tagEnd < 0) break;
                var start = tagEnd + 1;
                var close = remainder.IndexOf("</li>", start, StringComparison.OrdinalIgnoreCase);
                var item = close < 0 ? remainder[start..] : remainder[start..close];
                var line = StripTags(item).Trim();
                if (line.Length > 0) lines.Add(line);
                if (close < 0) break;
                remainder = remainder[(close + 5)..];
            }
            return lines;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = line[2..].Trim();
            else if (line.StartsWith('#')) line = line.TrimStart('#').Trim();
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    private static string StripTags(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var inside = false;
        foreach (var character in text)
        {
            if (character == '<') inside = true;
            else if (character == '>') inside = false;
            else if (!inside) builder.Append(character);
        }
        return builder.ToString()
            .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
            .Replace("&#39;", "'").Replace("&amp;", "&");
    }

    // MARK: Profile files

    private static string ProfileDirectory =>
        Environment.GetEnvironmentVariable("CIDA_PROFILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cida");

    private static string CheckStampPath => Path.Combine(ProfileDirectory, "update-check.txt");

    private static string SkippedVersionPath => Path.Combine(ProfileDirectory, "update-skip.txt");

    private static string? ReadSkippedVersion()
    {
        try
        {
            if (!File.Exists(SkippedVersionPath)) return null;
            foreach (var line in File.ReadLines(SkippedVersionPath))
            {
                if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            }
        }
        catch { /* A broken skip file only means the update may be offered again. */ }
        return null;
    }

    private static void WriteSkippedVersion(string version)
    {
        try
        {
            Directory.CreateDirectory(ProfileDirectory);
            File.WriteAllText(SkippedVersionPath, version + Environment.NewLine);
        }
        catch { /* Skipping still applies for this run; the next launch may offer the version again. */ }
    }
}
