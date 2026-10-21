namespace Cida.Core;

/// <summary>
/// The step an update session is on. Ported from upstream CidaUpdateDriver (#79): the panel and
/// the Settings window both read this instead of owning update state of their own.
/// </summary>
public enum UpdatePhase
{
    /// <summary>Nothing is happening; no update was offered yet.</summary>
    Idle,

    /// <summary>A check is in flight. A new check clears the previous version and notes.</summary>
    Checking,

    /// <summary>The check finished and this build is up to date.</summary>
    Current,

    /// <summary>A newer version is offered, but nothing was downloaded yet.</summary>
    Found,

    /// <summary>The accepted update is downloading; leaving the panel does not stop it.</summary>
    Downloading,

    /// <summary>The update is on disk. Installing needs one confirmation.</summary>
    Ready,

    /// <summary>The updater is applying the downloaded update and relaunching.</summary>
    Installing,

    /// <summary>The last step failed. The reason is in <see cref="UpdateSession.Error"/>.</summary>
    Failed,
}

/// <summary>
/// A pure update session: the one-confirmation install rule from upstream #79 without any
/// updater dependency. The explicit consent to install after the download lives here: accepting
/// the offer (<see cref="ChooseInstall"/>) carries it through the download, and leaving the watch
/// (<see cref="Watch"/>) revokes it even if the panel returns before the download finishes.
/// </summary>
public sealed class UpdateSession
{
    private readonly List<string> _notes = [];
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
    private string? _notice;

    /// <summary>The current step.</summary>
    public UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;

    /// <summary>The offered version, or null once the session ended.</summary>
    public string? Version { get; private set; }

    /// <summary>This build's version, named next to <see cref="Version"/>.</summary>
    public string? CurrentVersion { get; private set; }

    /// <summary>Release notes, one item per line; empty when the feed has none.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>The last reported download progress, 0-100 while downloading.</summary>
    public int? DownloadPercent { get; private set; }

    /// <summary>The failure reason while <see cref="Phase"/> is <see cref="UpdatePhase.Failed"/>.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether the panel or Settings is showing this session right now.</summary>
    public bool IsWatching { get; private set; }

    /// <summary>
    /// Explicit consent to install as soon as the download finishes. It is only ever set by
    /// <see cref="ChooseInstall"/> and never survives leaving the watch.
    /// </summary>
    public bool InstallWhenReady { get; private set; }

    /// <summary>One line for the Settings status row, built from the current step.</summary>
    public string StatusText => Phase switch
    {
        UpdatePhase.Checking => "正在检查更新…",
        UpdatePhase.Current => string.IsNullOrEmpty(CurrentVersion)
            ? "当前已是最新版本"
            : $"辞达 {CurrentVersion} 已是最新版本",
        UpdatePhase.Found => FoundStatus,
        UpdatePhase.Downloading => $"{Label}正在下载 · {DownloadPercent ?? 0}%",
        UpdatePhase.Ready => $"{Label}已下载",
        UpdatePhase.Installing => "正在安装…",
        UpdatePhase.Failed => Error ?? "更新没有完成，请重试。",
        _ => _notice ?? "尚未检查更新",
    };

    private string Label => string.IsNullOrEmpty(Version) ? "辞达" : $"辞达 {Version} ";

    private string FoundStatus
    {
        get
        {
            if (string.IsNullOrEmpty(Version)) return "发现可用更新";
            var statement = $"辞达 {Version} 可以安装";
            return string.IsNullOrEmpty(CurrentVersion) ? statement : statement + $" · 当前 {CurrentVersion}";
        }
    }

    /// <summary>
    /// Starts a user-initiated check. A failed fresh check must not show an earlier version as
    /// available, so the previous version, notes, progress and error are cleared here.
    /// </summary>
    public void BeginCheck()
    {
        Phase = UpdatePhase.Checking;
        Version = null;
        CurrentVersion = null;
        _notes.Clear();
        DownloadPercent = null;
        Error = null;
        _notice = null;
        InstallWhenReady = false;
    }

    /// <summary>The check found a newer version and the session offers it.</summary>
    public void Found(string version, string? currentVersion = null, IEnumerable<string>? notes = null)
    {
        Phase = UpdatePhase.Found;
        Version = version;
        CurrentVersion = string.IsNullOrEmpty(currentVersion) ? null : currentVersion;
        _notes.Clear();
        if (notes != null)
        {
            foreach (var note in notes)
            {
                if (!string.IsNullOrWhiteSpace(note)) _notes.Add(note.Trim());
            }
        }
        DownloadPercent = null;
        Error = null;
        _notice = null;
        InstallWhenReady = false;
    }

    /// <summary>The check finished and this build is up to date.</summary>
    public void MarkCurrent(string? version)
    {
        Phase = UpdatePhase.Current;
        Version = null;
        CurrentVersion = string.IsNullOrEmpty(version) ? null : version;
        _notes.Clear();
        DownloadPercent = null;
        Error = null;
        _notice = null;
        InstallWhenReady = false;
    }

    /// <summary>
    /// This build cannot update itself (e.g. it runs from a development checkout). The reason is
    /// shown as the status; no update controls apply.
    /// </summary>
    public void MarkUnavailable(string reason)
    {
        Phase = UpdatePhase.Idle;
        Version = null;
        CurrentVersion = null;
        _notes.Clear();
        DownloadPercent = null;
        Error = null;
        _notice = string.IsNullOrWhiteSpace(reason) ? null : reason;
        InstallWhenReady = false;
    }

    /// <summary>The accepted update starts downloading.</summary>
    public void BeginDownload()
    {
        Phase = UpdatePhase.Downloading;
        DownloadPercent = 0;
        Error = null;
    }

    /// <summary>A progress callback from the downloader. Percent is clamped to 0-100.</summary>
    public void DownloadProgress(int percent)
    {
        DownloadPercent = Math.Clamp(percent, 0, 100);
    }

    /// <summary>
    /// The download finished. The one-confirmation rule: install right away only when the user
    /// accepted the update and is still watching. Otherwise the session stays <see cref="UpdatePhase.Ready"/>
    /// and the deferred install remains reachable later.
    /// </summary>
    /// <returns>True when the caller should install and relaunch now.</returns>
    public bool DownloadCompleted()
    {
        DownloadPercent = 100;
        Error = null;
        if (InstallWhenReady && IsWatching)
        {
            Phase = UpdatePhase.Installing;
            InstallWhenReady = false;
            return true;
        }
        Phase = UpdatePhase.Ready;
        InstallWhenReady = false;
        return false;
    }

    /// <summary>The updater is applying the update; there is no second question to answer.</summary>
    public void MarkInstalling()
    {
        Phase = UpdatePhase.Installing;
        InstallWhenReady = false;
        Error = null;
    }

    /// <summary>The current step failed. The install intent is cleared; the version stays named
    /// so a retry can say what failed.</summary>
    public void Fail(string message)
    {
        Phase = UpdatePhase.Failed;
        Error = string.IsNullOrWhiteSpace(message) ? "更新没有完成，请重试。" : message;
        InstallWhenReady = false;
        DownloadPercent = null;
        _notice = null;
    }

    /// <summary>The user cancelled a download; the offered version is still downloadable.</summary>
    public void CancelDownload()
    {
        if (Phase != UpdatePhase.Downloading) return;
        Phase = Version == null ? UpdatePhase.Idle : UpdatePhase.Found;
        DownloadPercent = null;
        Error = null;
        InstallWhenReady = false;
    }

    /// <summary>Ends the session and forgets everything but the skipped versions.</summary>
    public void Reset()
    {
        Phase = UpdatePhase.Idle;
        Version = null;
        CurrentVersion = null;
        _notes.Clear();
        DownloadPercent = null;
        Error = null;
        _notice = null;
        InstallWhenReady = false;
        IsWatching = false;
    }

    /// <summary>
    /// The panel or Settings starts or stops showing this session. Leaving revokes the install
    /// intent permanently: returning before the download finishes does not restore it.
    /// </summary>
    public void Watch(bool watching)
    {
        IsWatching = watching;
        if (!watching) InstallWhenReady = false;
    }

    /// <summary>
    /// The user answered the offer. On <see cref="UpdatePhase.Found"/> this carries the explicit
    /// install intent through the download; on <see cref="UpdatePhase.Ready"/> the deferred
    /// download is installed now.
    /// </summary>
    /// <returns>True when the caller should install and relaunch immediately.</returns>
    public bool ChooseInstall()
    {
        switch (Phase)
        {
            case UpdatePhase.Found when Version != null:
                InstallWhenReady = true;
                return false;
            case UpdatePhase.Ready when Version != null:
                Phase = UpdatePhase.Installing;
                InstallWhenReady = false;
                Error = null;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Remembers this version as skipped and ends the offered session.</summary>
    /// <returns>The skipped version, or null when there was nothing to skip.</returns>
    public string? SkipVersion()
    {
        var skipped = Version;
        if (skipped != null) _skipped.Add(skipped);
        Phase = UpdatePhase.Idle;
        Version = null;
        CurrentVersion = null;
        _notes.Clear();
        DownloadPercent = null;
        Error = null;
        InstallWhenReady = false;
        _notice = skipped == null ? null : $"已跳过版本 {skipped}，不再提示。";
        return skipped;
    }

    /// <summary>Restores a persisted skip, e.g. the one read from the profile directory.</summary>
    public void RememberSkipped(string version)
    {
        if (!string.IsNullOrWhiteSpace(version)) _skipped.Add(version.Trim());
    }

    /// <summary>Whether this version was skipped and must not be offered again.</summary>
    public bool IsSkipped(string? version) =>
        !string.IsNullOrWhiteSpace(version) && _skipped.Contains(version.Trim());
}
