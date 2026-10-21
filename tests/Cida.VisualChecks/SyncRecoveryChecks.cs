using Cida.Core;
using Cida.Desktop;
using Cida.Platform;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using TabControl = System.Windows.Controls.TabControl;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace Cida.VisualChecks;

// Real WPF entry points with memory-only settings and loopback HTTP. No user hotkeys,
// clipboard, UIA selection, model account, update download or installer is used.
internal static class SyncRecoveryChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task RunAsync(string directory)
    {
        var passed = new List<string>();
        await UpdateWatchAsync(); passed.Add("update visibility revokes consent without restoring it");
        await CurrentTrialConfigurationAsync(); passed.Add("trial reloads saved configuration and preserves draft guards");
        PromptRecovery(); passed.Add("prompt edits release the panel recovery gate");
        await ParagraphRecoveryAsync(); passed.Add("paragraph entry recovers typed length rejection");
        await CheckHandoffAsync(); passed.Add("handoff uses matching checks and rejects drafts and cancellation");
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { passed }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task UpdateWatchAsync()
    {
        using var fixture = new Fixture("http://127.0.0.1:9/chat/completions");
        var window = fixture.Window;
        var pages = Find<TabControl>(window, "Pages");
        var session = Field<UpdateSession>(fixture.Model.Updates, "_session");
        Require(!session.IsWatching, "Constructing a hidden Settings window must not watch updates.");
        window.Show();
        Require(!session.IsWatching, "The model tab must not watch updates.");
        pages.SelectedIndex = 3;
        Require(session.IsWatching, "The visible update tab must watch updates.");
        Accept(); pages.SelectedIndex = 1;
        Require(!session.IsWatching && !session.InstallWhenReady, "Leaving the update tab must revoke consent.");
        pages.SelectedIndex = 3;
        Require(!session.DownloadCompleted(), "Returning to the tab must not restore consent.");
        Accept(); window.Hide(); window.Show();
        Require(!session.DownloadCompleted(), "Hide/show must not restore consent.");
        Accept(); window.WindowState = WindowState.Minimized;
        Require(!session.IsWatching && !session.InstallWhenReady, "Minimizing must revoke consent.");
        window.WindowState = WindowState.Normal;
        Require(!session.DownloadCompleted(), "Restoring the window must not restore consent.");
        Accept();
        Require(session.DownloadCompleted(), "Staying on the update tab should still install after one confirmation.");
        Accept(); window.Close();
        Require(!session.IsWatching && !session.DownloadCompleted(), "Closing must revoke consent.");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        void Accept() { session.Found("0.2.0-rc.8"); session.ChooseInstall(); session.BeginDownload(); }
    }

    private static async Task CurrentTrialConfigurationAsync()
    {
        using var server = new ModelServer();
        using var fixture = new Fixture(server.Endpoint);
        var window = fixture.Window;
        Invoke(window, "OnPreview", window, new RoutedEventArgs());
        await UntilAsync(() => !fixture.Model.ActionEditor.IsRunning);
        Require(Find<Button>(window, "ModelSettingsButton").Visibility == Visibility.Visible, "A configuration rejection should gate retries.");
        fixture.Settings = fixture.Settings with { ModelService = fixture.Settings.ModelService with { Model = "fixed" }, ApiKey = "synthetic-new-key" };
        fixture.Model.ReloadSettings();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var preview = Find<Button>(window, "PreviewButton");
        Require(preview.Visibility == Visibility.Visible && preview.IsEnabled, "Reload must refresh the trial gate without reopening Settings.");
        Invoke(window, "OnPreview", window, new RoutedEventArgs());
        await UntilAsync(() => fixture.Model.ActionEditor.Previews.Count == 1);
        Require(server.Models.SequenceEqual(new[] { "rejected", "fixed" }), "The retry must use the current saved model.");
        Require(server.LastAuthorization == "Bearer synthetic-new-key", "The retry must use the current saved key.");
        Find<TextBox>(window, "ActionPrompt").Text += " Unsaved draft.";
        Invoke(window, "OnPreview", window, new RoutedEventArgs());
        Require(server.Models.Count == 2 && !preview.IsEnabled, "An unsaved prompt must not run.");
        Invoke(window, "OnSave", window, new RoutedEventArgs());
        Require(fixture.Saves == 0 && Find<TextBlock>(window, "SaveStatus").Text.Contains("其他入口"), "The external-change save guard must remain effective.");
    }

    private static void PromptRecovery()
    {
        using var fixture = new Fixture("http://127.0.0.1:9/chat/completions");
        var panel = new PanelWindow(fixture.Model);
        try
        {
            panel.SetSourceText("Synthetic source");
            panel.BeginResult(fixture.Settings.EffectiveActions.First(), panel.State.Source);
            panel.FailResult(new ProcessingFailure(ProcessingFailureCategory.Configuration, "rejected", fixture.Settings.ModelServiceFingerprint));
            Require(!CanSubmit(), "An unchanged configuration failure must remain gated.");
            var original = fixture.Settings;
            fixture.Settings = original with { TranslationPrompt = original.TranslationPrompt + " Shorter policy." };
            fixture.Model.ReloadSettings(); panel.RefreshConfiguration();
            Require(CanSubmit() && Find<Button>(panel, "ExecuteButton").Content.ToString() == "执行 ↵", "A changed prompt must allow a new request.");
            fixture.Settings = original; fixture.Model.ReloadSettings(); panel.RefreshConfiguration();
            Require(!CanSubmit(), "Restoring the failing prompt must restore the gate.");
            bool CanSubmit() => PanelPresentation.CanSubmit((PanelPresentationInput)Invoke(panel, "Presentation")!);
        }
        finally { panel.Close(); }
    }

    private static async Task ParagraphRecoveryAsync()
    {
        using var server = new ModelServer { MaximumInput = 60 };
        using var fixture = new Fixture(server.Endpoint);
        fixture.Settings = fixture.Settings with { ModelService = fixture.Settings.ModelService with { Model = "fixed" } };
        fixture.Model.ReloadSettings();
        var source = string.Join("\n\n", Enumerable.Repeat("A synthetic paragraph with a lossless boundary.", 4));
        var target = new TranslationLayer.LayerParagraph(source, new System.Drawing.RectangleF(0, 0, 300, 50));
        using var session = new LayerSession(fixture.Model, new WindowInteropHelper(fixture.Window).EnsureHandle(), target);
        // Exercise the real _target branch, without starting UIA polling or placing overlays.
        await (Task)Invoke(session, "TranslateAsync", new[] { source }, -1L)!;
        var key = (string)Invoke(session, "CacheKey", source)!;
        Require(Field<LayerTranslationCache>(session, "_cache").TryGet(key, out var result) && result == source,
            "The paragraph branch must cache the complete ordered result after length recovery.");
        Require(server.Models.Count > 2 && server.Inputs.Count(text => text == source) == 1,
            "The rejected full paragraph must be split rather than resent.");
    }

    private static async Task CheckHandoffAsync()
    {
        using var server = new ModelServer();
        using var fixture = new Fixture(server.Endpoint);
        var window = fixture.Window;
        fixture.LastCheck = new(false, 401, "synthetic failure", DateTime.UtcNow, fixture.Settings.ModelServiceFingerprint);
        Require(Prompt().Contains("最近一次检查未通过"), "A matching stored failure must reach the handoff.");
        fixture.LastCheck = fixture.LastCheck with { Fingerprint = "obsolete" };
        Require(!Prompt().Contains("最近一次检查未通过"), "An obsolete failure must not diagnose current settings.");
        await (Task)Invoke(window, "RunCheckAsync")!;
        Require(fixture.LastCheck.Fingerprint == fixture.Settings.ModelServiceFingerprint && Prompt().Contains("最近一次检查未通过"),
            "The visible connection check must persist and reach the handoff.");
        fixture.LastCheck = fixture.LastCheck with { Passed = true, CheckedAt = DateTime.UtcNow.AddMinutes(1) };
        Require(!Prompt().Contains("最近一次检查未通过"), "An older local-time failure must not override a newer UTC check.");
        await (Task)Invoke(window, "RunCheckAsync")!;
        Require(fixture.LastCheck.Passed && !Prompt().Contains("最近一次检查未通过"), "A UI check must not overwrite newer matching evidence.");
        Find<TextBox>(window, "ModelName").Text = "another-rejected-model";
        await (Task)Invoke(window, "RunCheckAsync")!;
        Require(fixture.LastCheck.Passed && !Prompt().Contains("最近一次检查未通过"), "An unsaved draft check must not replace or diagnose the saved configuration.");
        // Once that exact draft is saved externally, its local failure becomes relevant.
        fixture.Settings = fixture.Settings with { ModelService = fixture.Settings.ModelService with { Model = "another-rejected-model" } };
        fixture.Model.ReloadSettings();
        Require(Prompt().Contains("最近一次检查未通过"), "A local check should become relevant when its fingerprint is current.");
        var previousLocal = Field<ModelServiceCheckRecord>(window, "_lastCheck");
        var previousStored = fixture.LastCheck;
        server.HoldResponses = true;
        var requests = server.Models.Count;
        var check = (Task)Invoke(window, "RunCheckAsync")!;
        await UntilAsync(() => server.Models.Count > requests);
        Invoke(window, "OnCancelCheck", window, new RoutedEventArgs());
        await check;
        Require(Find<TextBlock>(window, "CheckStatus").Text == "检查已取消", "A cancelled check must leave an explicit cancelled status.");
        Require(fixture.LastCheck == previousStored && Field<ModelServiceCheckRecord>(window, "_lastCheck") == previousLocal,
            "Cancelling a check must not replace diagnostic evidence.");
        string Prompt() => (string)Invoke(window, "ConfigurationPrompt")!;
    }

    private static T Find<T>(Window window, string name) => (T)window.FindName(name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do { await Task.Delay(20); if (condition()) return; } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("Expected recovery state did not arrive.");
    }

    private sealed class Fixture : IDisposable
    {
        public CidaSettings Settings;
        public ModelServiceCheckRecord? LastCheck;
        public int Saves;
        public AppModel Model { get; }
        public SettingsWindow Window { get; }
        public Fixture(string endpoint)
        {
            Settings = new CidaSettings
            {
                ModelService = new ModelConfiguration { Endpoint = endpoint, Model = "rejected" },
                Shortcut = null, CaptureShortcut = null, LayerShortcut = null, ImproveShortcut = null,
            };
            Model = new AppModel(new ConfigurationStore(() => Settings, value => { Settings = value; Saves++; }, () => Settings.ApiKey,
                _ => { }, () => { }, () => false, () => LastCheck, value => LastCheck = value, () => false, _ => { }, () => { }));
            Window = new SettingsWindow(Model) { ShowActivated = false };
            Window.SourceInitialized += (_, _) => Window.Left = -30000;
        }
        public void Dispose() { Window.Close(); Model.ActionEditor.StopPreview(); Model.Stop(); }
    }

    private sealed class ModelServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serve;
        private readonly List<string> _models = [], _inputs = [];
        public List<string> Models { get { lock (_models) return _models.ToList(); } }
        public List<string> Inputs { get { lock (_models) return _inputs.ToList(); } }
        public string? LastAuthorization { get; private set; }
        public int MaximumInput { get; init; } = int.MaxValue;
        public volatile bool HoldResponses;
        public string Endpoint { get; }
        public ModelServer()
        {
            _listener.Start(); Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/chat/completions";
            _serve = Task.Run(ServeAsync);
        }
        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = client.GetStream();
                    var header = new List<byte>(); var one = new byte[1];
                    while (header.Count < 16384 && await stream.ReadAsync(one, _stop.Token) > 0)
                    {
                        header.Add(one[0]); if (header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                    }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                    var size = int.Parse(lines.First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                    var body = new byte[size]; await stream.ReadExactlyAsync(body, _stop.Token);
                    using var json = JsonDocument.Parse(body);
                    var model = json.RootElement.GetProperty("model").GetString()!;
                    var text = json.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
                    LastAuthorization = lines.FirstOrDefault(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
                    lock (_models) { _models.Add(model); _inputs.Add(text); }
                    if (HoldResponses) await Task.Delay(Timeout.Infinite, _stop.Token);
                    var status = model != "fixed" ? 401 : text.Length > MaximumInput ? 413 : 200;
                    var reply = status == 200
                        ? JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } })
                        : "{\"error\":{\"code\":\"synthetic_rejection\"}}";
                    var bytes = Encoding.UTF8.GetBytes(reply);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _serve.GetAwaiter().GetResult(); _stop.Dispose(); }
    }
}
