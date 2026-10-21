using Cida.Core;
using Xunit;

namespace Cida.Core.Tests;

/// <summary>
/// Ported from upstream UpdatesTests.swift (#79) as pure state-machine cases: one confirmation
/// installs, leaving the watch revokes the restart, background downloads stay quiet, and a fresh
/// check clears the earlier version details.
/// </summary>
public sealed class UpdateSessionTests
{
    private static UpdateSession Found(
        string version = "2.0", string current = "1.0", string[]? notes = null, bool watching = true)
    {
        var session = new UpdateSession();
        session.Watch(watching);
        session.BeginCheck();
        session.Found(version, current, notes ?? new[] { "Change" });
        return session;
    }

    [Fact]
    public void AcceptedUpdateInstallsWithoutASecondQuestion()
    {
        var session = Found();
        Assert.Equal(UpdatePhase.Found, session.Phase);

        // The first answer is the only confirmation: it carries the intent through the download.
        Assert.False(session.ChooseInstall());
        Assert.True(session.InstallWhenReady);

        session.BeginDownload();
        session.DownloadProgress(38);
        Assert.Equal(UpdatePhase.Downloading, session.Phase);

        Assert.True(session.DownloadCompleted());
        Assert.Equal(UpdatePhase.Installing, session.Phase);
        Assert.False(session.InstallWhenReady, "The one confirmation is spent by installing");
        Assert.Equal("正在安装…", session.StatusText);
        Assert.Equal(100, session.DownloadPercent);
    }

    [Fact]
    public void LeavingDownloadRevokesRestartEvenAfterReturning()
    {
        var session = Found();
        session.ChooseInstall();
        session.BeginDownload();
        Assert.True(session.InstallWhenReady);

        // Hiding the panel revokes the explicit install consent; the download keeps going.
        session.Watch(false);
        Assert.False(session.InstallWhenReady);
        Assert.Equal(UpdatePhase.Downloading, session.Phase);
        session.Watch(true);

        Assert.False(session.DownloadCompleted(), "Returning to progress is not permission to restart");
        Assert.Equal(UpdatePhase.Ready, session.Phase);
        Assert.False(session.InstallWhenReady);
        Assert.Contains("已下载", session.StatusText);

        // The deferred download is still installable from Settings with one confirmation.
        Assert.True(session.ChooseInstall());
        Assert.Equal(UpdatePhase.Installing, session.Phase);
        Assert.Contains("正在安装", session.StatusText);
    }

    [Fact]
    public void HiddenDownloadStaysQuietAndDeferredInstallRemainsReachable()
    {
        var session = new UpdateSession();
        session.BeginCheck();
        session.Found("2.0", "1.0", []);
        Assert.False(session.IsWatching);

        // A scheduled/background download completes while nobody watches: no automatic install.
        session.BeginDownload();
        Assert.False(session.DownloadCompleted());
        Assert.Equal(UpdatePhase.Ready, session.Phase);

        // Opening Settings later shows the downloaded update; it still needs a confirmation.
        session.Watch(true);
        Assert.Equal(UpdatePhase.Ready, session.Phase);
        Assert.Equal("辞达 2.0 已下载", session.StatusText);
        Assert.True(session.ChooseInstall());
        Assert.Equal(UpdatePhase.Installing, session.Phase);
    }

    [Fact]
    public void FailureClearsInstallIntentAndRecheckClearsPreviousDetails()
    {
        var session = Found(notes: ["第一条", "第二条"]);
        session.ChooseInstall();
        session.BeginDownload();
        session.DownloadProgress(40);

        session.Fail("下载失败，请重试。");
        Assert.Equal(UpdatePhase.Failed, session.Phase);
        Assert.False(session.InstallWhenReady, "A failure clears the install intent");
        Assert.Equal("下载失败，请重试。", session.Error);
        Assert.Equal("下载失败，请重试。", session.StatusText);
        Assert.Equal("2.0", session.Version);
        Assert.Equal(new[] { "第一条", "第二条" }, session.Notes);

        // A user-initiated check must not show an earlier version as available.
        session.BeginCheck();
        Assert.Equal(UpdatePhase.Checking, session.Phase);
        Assert.Null(session.Version);
        Assert.Null(session.CurrentVersion);
        Assert.Empty(session.Notes);
        Assert.Null(session.DownloadPercent);
        Assert.Null(session.Error);
        Assert.False(session.InstallWhenReady);
        Assert.Equal("正在检查更新…", session.StatusText);
    }

    [Fact]
    public void FailedFreshCheckShowsNoPreviousVersion()
    {
        var session = Found();
        session.Fail("网络中断");
        session.BeginCheck();
        session.Fail("检查失败，请稍后重试或打开发布页下载。");

        Assert.Equal(UpdatePhase.Failed, session.Phase);
        Assert.Null(session.Version);
        Assert.Empty(session.Notes);
        Assert.DoesNotContain("2.0", session.StatusText);
        Assert.Equal("检查失败，请稍后重试或打开发布页下载。", session.StatusText);
    }

    [Fact]
    public void SkipVersionIsRememberedAndClearsTheOffer()
    {
        var session = Found();
        Assert.False(session.IsSkipped("2.0"));

        Assert.Equal("2.0", session.SkipVersion());
        Assert.True(session.IsSkipped("2.0"));
        Assert.False(session.IsSkipped("2.1"));
        Assert.False(session.IsSkipped(null));
        Assert.Null(session.Version);
        Assert.Equal(UpdatePhase.Idle, session.Phase);
        Assert.Contains("已跳过版本 2.0", session.StatusText);

        // The skip survives a reset and a restored persisted skip is honoured too.
        session.Reset();
        Assert.True(session.IsSkipped("2.0"));
        session.RememberSkipped(" 3.0 ");
        Assert.True(session.IsSkipped("3.0"));
    }

    [Fact]
    public void CurrentAndUnavailableReportTheirOwnStatus()
    {
        var session = new UpdateSession();
        session.BeginCheck();
        session.MarkCurrent("1.0");
        Assert.Equal(UpdatePhase.Current, session.Phase);
        Assert.Equal("辞达 1.0 已是最新版本", session.StatusText);
        Assert.Null(session.Version);

        session.BeginCheck();
        session.MarkUnavailable("开发版本：请从发布页下载安装包。");
        Assert.Equal(UpdatePhase.Idle, session.Phase);
        Assert.Equal("开发版本：请从发布页下载安装包。", session.StatusText);
        Assert.Null(session.Error);

        // A new check clears the unavailable notice.
        session.BeginCheck();
        Assert.Equal("正在检查更新…", session.StatusText);
    }

    [Fact]
    public void StatusTextNamesTheOfferedVersionAndProgress()
    {
        var session = new UpdateSession();
        Assert.Equal("尚未检查更新", session.StatusText);

        session.BeginCheck();
        Assert.Equal("正在检查更新…", session.StatusText);

        session.Found("2.0", "1.0", ["Change"]);
        Assert.Equal("辞达 2.0 可以安装 · 当前 1.0", session.StatusText);

        session.BeginDownload();
        Assert.Equal("辞达 2.0 正在下载 · 0%", session.StatusText);
        session.DownloadProgress(38);
        Assert.Equal("辞达 2.0 正在下载 · 38%", session.StatusText);
        session.DownloadProgress(150);
        Assert.Equal(100, session.DownloadPercent);
        session.DownloadProgress(-5);
        Assert.Equal(0, session.DownloadPercent);

        session.DownloadCompleted();
        Assert.Equal("辞达 2.0 已下载", session.StatusText);
    }

    [Fact]
    public void CancelDownloadKeepsTheOfferDownloadable()
    {
        var session = Found();
        session.BeginDownload();
        session.DownloadProgress(60);
        session.CancelDownload();

        Assert.Equal(UpdatePhase.Found, session.Phase);
        Assert.Equal("2.0", session.Version);
        Assert.Null(session.DownloadPercent);
        Assert.False(session.InstallWhenReady);
        Assert.Equal("辞达 2.0 可以安装 · 当前 1.0", session.StatusText);

        // Retrying the download still works, and one confirmation installs it while watched.
        session.ChooseInstall();
        session.BeginDownload();
        Assert.True(session.DownloadCompleted());
        Assert.Equal(UpdatePhase.Installing, session.Phase);
    }

    [Fact]
    public void ChooseInstallOnlyAppliesToTheOfferAndTheDownloadedUpdate()
    {
        var session = new UpdateSession();
        Assert.False(session.ChooseInstall());
        Assert.False(session.InstallWhenReady);

        session.BeginCheck();
        Assert.False(session.ChooseInstall(), "Nothing is offered while checking");
        Assert.False(session.InstallWhenReady);

        session.Found("2.0", "1.0", []);
        session.ChooseInstall();
        session.BeginDownload();
        Assert.False(session.ChooseInstall(), "A running download has no second question to answer");
        Assert.Equal(UpdatePhase.Downloading, session.Phase);

        session.Fail("下载中断");
        Assert.False(session.ChooseInstall(), "A failed step must be retried through a new check");
        Assert.False(session.InstallWhenReady);
    }

    [Fact]
    public void ResetEndsTheSessionAndForgetsEverythingButSkips()
    {
        var session = Found(notes: ["Change"]);
        session.ChooseInstall();
        session.BeginDownload();
        session.DownloadCompleted();

        session.Reset();
        Assert.Equal(UpdatePhase.Idle, session.Phase);
        Assert.Null(session.Version);
        Assert.Null(session.CurrentVersion);
        Assert.Empty(session.Notes);
        Assert.Null(session.DownloadPercent);
        Assert.Null(session.Error);
        Assert.False(session.IsWatching);
        Assert.False(session.InstallWhenReady);
        Assert.Equal("尚未检查更新", session.StatusText);
    }

    [Fact]
    public void NotesAreTrimmedAndEmptyLinesDropped()
    {
        var session = new UpdateSession();
        session.Found("2.0", "1.0", ["  第一条  ", "", "   ", "第二条"]);
        Assert.Equal(new[] { "第一条", "第二条" }, session.Notes);
    }

    [Fact]
    public void WatchedDownloadWithoutConsentStillAsksBeforeRestarting()
    {
        var session = Found();
        Assert.False(session.ChooseInstall());
        session.Watch(false);
        session.Watch(true);

        session.BeginDownload();
        Assert.False(session.DownloadCompleted());
        Assert.Equal(UpdatePhase.Ready, session.Phase);
        Assert.False(session.InstallWhenReady);
        Assert.True(session.ChooseInstall());
        Assert.Equal(UpdatePhase.Installing, session.Phase);
    }
}
