using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Cida.Desktop;

public sealed class UpdateService
{
    private UpdateManager? _manager;
    private UpdateInfo? _available;
    private bool _busy;
    private Task<UpdateInfo?>? _check;
    private CancellationTokenSource? _downloadCancellation;
    public string Status { get; private set; } = "尚未检查更新";
    public bool CanDownload => _available != null && !_busy && _manager?.UpdatePendingRestart == null;
    public bool CanRestart => _manager?.UpdatePendingRestart != null && !_busy;
    public bool IsDownloading => _downloadCancellation != null;
    public event Action? Changed;
    public static string Version => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(App).Assembly)?.InformationalVersion.Split('+')[0] ?? "0.2.0-rc.4";
    private void Notify() => Changed?.Invoke();
    public async Task CheckDailyAsync()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("CIDA_PROFILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cida"), "update-check.txt");
        try
        {
            if (File.Exists(path) && DateTime.TryParse(File.ReadAllText(path), out var last) && DateTime.UtcNow - last.ToUniversalTime() < TimeSpan.FromDays(1)) return;
            await CheckAsync();
            // Failed checks also back off; no repeated requests on every launch.
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
        }
        catch { /* Manual checks remain available; timestamps contain no user content. */ }
    }
    public async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true; Status = "正在检查更新…"; Notify();
        try
        {
            _manager ??= new UpdateManager(new GithubSource("https://github.com/Oerroot/cida-win", null, IsPreview()),
                new UpdateOptions { ExplicitChannel = IsPreview() ? "win-x64-preview" : "win-x64" });
            if (!_manager.IsInstalled) { Status = "开发版本：请从发布页下载安装包。"; return; }
            if (_manager.UpdatePendingRestart != null) { Status = "更新已下载，重启后安装。"; return; }
            // Reuse an unfinished check after a UI timeout; Velopack's API has no cancellation parameter.
            if (_check == null || _check.IsCompleted) _check = _manager.CheckForUpdatesAsync();
            _available = await _check.WaitAsync(TimeSpan.FromSeconds(25));
            Status = _available == null ? "当前已是最新版本" : $"发现 {_available.TargetFullRelease.Version}，可下载更新。";
        }
        catch { Status = "检查失败，请稍后重试或打开发布页下载。"; }
        finally { _busy = false; Notify(); }
    }
    private static bool IsPreview() =>
        typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute info] && info.InformationalVersion.Contains('-');
    public async Task DownloadAsync()
    {
        if (!CanDownload || _manager == null || _available == null) return;
        _busy = true; _downloadCancellation = new(); Status = "正在下载更新…"; Notify();
        try
        {
            await _manager.DownloadUpdatesAsync(_available, percent =>
            { Status = $"正在下载更新 · {percent}%"; Notify(); }, _downloadCancellation.Token);
            Status = "更新已下载，可现在重启安装，也可以稍后安装。";
        }
        catch (OperationCanceledException) { Status = "下载已取消，可以重新下载。"; }
        catch { Status = "下载失败，请重试或从发布页下载完整安装包。"; }
        finally { _downloadCancellation.Dispose(); _downloadCancellation = null; _busy = false; Notify(); }
    }
    public void CancelDownload() => _downloadCancellation?.Cancel();
    public void Restart()
    {
        if (CanRestart && _manager?.UpdatePendingRestart is { } asset) _manager.ApplyUpdatesAndRestart(asset);
    }
}
