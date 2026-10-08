using Cida.Core;
using Cida.Desktop;
using Cida.Platform;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cida.VisualChecks;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var profile = Path.Combine(AppContext.BaseDirectory, "inspection-profile");
        Environment.SetEnvironmentVariable("CIDA_PROFILE", profile);
        Environment.SetEnvironmentVariable("CIDA_UI_INSPECTION", "1");
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ThemeService.Start();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; _ = ServeAsync(listener);
        var fileStore = new SettingsFileStore(profile); var secrets = new SecretStore(profile);
        var settings = fileStore.LoadSettings() with { ModelService = new ModelConfiguration { Endpoint = $"http://127.0.0.1:{port}/chat/completions", Model = "local-visual-fixture" } };
        var store = new ConfigurationStore(() => settings, value => { settings = value; fileStore.SaveSettings(value); }, secrets.Read, secrets.Save, secrets.Clear, secrets.Exists,
            () => null, _ => { }, () => false, _ => { }, () => { });
        var model = new AppModel(store);
        model.OpenPanel();
        var panel = application.Windows.OfType<PanelWindow>().Single(); panel.Title = "辞达 · 受控界面验证";
        if (args.Contains("--render-suite"))
        {
            application.Dispatcher.BeginInvoke(async () =>
            {
                var exit = 0;
                try { await RenderSuiteAsync(application, model, panel, profile); }
                catch (Exception error) { Directory.CreateDirectory(profile); File.WriteAllText(Path.Combine(profile, "render-error.txt"), error.ToString()); exit = 1; }
                finally { listener.Stop(); model.Stop(); ThemeService.Stop(); application.Shutdown(exit); }
            });
            return application.Run();
        }
        var controls = new StackPanel { Margin = new Thickness(18) };
        controls.Children.Add(new TextBlock { Text = "本地模拟响应 · 使用真实产品视图 · 不调用外部模型", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        void Button(string title, Action action)
        { var button = new System.Windows.Controls.Button { Content = title, Margin = new Thickness(0, 0, 0, 6) }; button.Click += (_, _) => action(); controls.Children.Add(button); }
        Button("主面板", () => { panel.Show(); panel.FocusInput(); });
        Button("设置", () => { panel.Hide(); model.ShowSettings(); });
        Button("浅色", () => ThemeService.Apply(false)); Button("深色", () => ThemeService.Apply(true));
        Button("完成示例", () =>
        {
            panel.SetSourceText("Good software should make the next step feel obvious.\n\nKeep the meaning. Remove the noise.");
            panel.BeginResult(settings.EnabledActions.First(), panel.State.Source);
            panel.AppendResult("好的软件，应当让下一步自然明了。\n\n保留原意，去除冗余。\n\n```csharp\nvar message = \"辞达而已矣\";\n```\n\n这是用于界面验收的本地模拟结果。"); panel.CompleteResult(); panel.Show();
        });
        Button("旧结果", () => panel.SetSourceText(panel.State.Source + "\nEdited source."));
        Button("失败示例", () => { panel.BeginResult(settings.EnabledActions.First(), panel.State.Source); panel.AppendResult("已收到的部分结果。\n"); panel.FailResult("本地模拟：连接中断"); });
        Button("停止示例", () => { panel.BeginResult(settings.EnabledActions.First(), panel.State.Source); panel.AppendResult("已有的部分结果会保留。"); panel.StopResult(); });
        Button("长文示例", () => { panel.SetSourceText(string.Join("\n\n", Enumerable.Repeat("Long text should remain readable without moving the controls out of the window.", 100))); panel.SubmitCurrent(); });
        Button("保存界面截图", () =>
        {
            Directory.CreateDirectory(Path.Combine(profile, "screenshots"));
            foreach (Window window in application.Windows)
            {
                if (!window.IsVisible) continue;
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(profile, "screenshots", (window is PanelWindow ? "panel" : window is SettingsWindow ? "settings" : "controls") + "-" + DateTime.UtcNow.ToString("HHmmss") + ".png")); encoder.Save(stream);
            }
        });
        Button("退出验证", () => application.Shutdown());
        var controller = new Window { Title = "Cida · 界面验收控制器", Width = 340, Height = 720, Content = new ScrollViewer { Content = controls }, Left = 30, Top = 30 };
        controller.Show(); panel.Show(); panel.FocusInput();
        application.Exit += (_, _) => { listener.Stop(); model.Stop(); ThemeService.Stop(); };
        return application.Run();
    }
    private static async Task RenderSuiteAsync(System.Windows.Application application, AppModel model, PanelWindow panel, string profile)
    {
        var directory = Path.Combine(profile, "render-suite"); Directory.CreateDirectory(directory);
        void Save(Window window, string name, double scale = 1)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
        }
        foreach (var dark in new[] { false, true })
        {
            var theme = dark ? "dark" : "light"; ThemeService.Apply(dark); panel.Show(); panel.Width = 800; panel.Height = 620;
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            panel.SetSourceText(""); panel.BeginResult(model.Settings.EnabledActions.First(), ""); panel.CompleteResult(); Save(panel, theme + "-empty");
            panel.SetSourceText("Good software should make the next step feel obvious.\n\nKeep the meaning. Remove the noise.");
            panel.BeginResult(model.Settings.EnabledActions.First(), panel.State.Source);
            panel.AppendResult("好的软件，应当让下一步自然明了。\n\n保留原意，去除冗余。\n\n```csharp\nvar message = \"辞达而已矣\";\n```\n\n这是用于界面验收的本地模拟结果。"); panel.CompleteResult();
            foreach (var scale in new[] {1d, 1.25, 1.5, 2}) Save(panel, theme + "-completed-" + scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), scale);
            panel.SetSourceText(panel.State.Source + "\nEdited source."); Save(panel, theme + "-stale");
            panel.BeginResult(model.Settings.EnabledActions.First(), panel.State.Source); panel.AppendResult("已收到的部分结果。\n"); panel.FailResult("本地模拟：连接中断"); Save(panel, theme + "-failed");
            panel.BeginResult(model.Settings.EnabledActions.First(), panel.State.Source); panel.AppendResult("已有的部分结果会保留。"); panel.StopResult(); Save(panel, theme + "-stopped");
            panel.SetSourceText(string.Join("\n\n", Enumerable.Repeat("Long text should remain readable without moving the controls out of the window.", 100)));
            panel.BeginResult(model.Settings.EnabledActions.First(), panel.State.Source); panel.AppendResult(string.Join("\n\n", Enumerable.Repeat("长文仍然清楚易读，操作按钮保持在固定位置。（本地模拟）", 100))); panel.CompleteResult(); Save(panel, theme + "-long");
            panel.Hide();
            var settings = new SettingsWindow(model); settings.Show(); ThemeService.Apply(dark);
            var pages = (System.Windows.Controls.TabControl)settings.FindName("Pages");
            for (var index = 0; index < 4; index++)
            {
                pages.SelectedIndex = index; await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Save(settings, theme + "-settings-" + index);
            }
            settings.Close();
        }
        panel.Close();
        File.WriteAllText(Path.Combine(directory, "README.txt"), "Controlled renders of real product views with local synthetic data. Raster scales 1/1.25/1.5/2 are not OS DPI or multi-monitor acceptance. No external model request was sent.");
    }
    private static async Task ServeAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); } catch { return; }
            _ = RespondAsync(client);
        }
    }
    private static async Task RespondAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream(); var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 16384 && await stream.ReadAsync(one) > 0)
                {
                    header.Add(one[0]); if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] {13,10,13,10})) break;
                }
                var size = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n").FirstOrDefault(s => s.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))?.Split(':')[1].Trim();
                var body = new byte[int.TryParse(size, out var count) && count <= 1000000 ? count : 0]; await stream.ReadExactlyAsync(body);
                using var json = JsonDocument.Parse(body);
                var source = json.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() ?? "";
                if (source.Contains("[fail]")) { await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); return; }
                var reply = source.Length > 2000 ? string.Join("\n\n", Enumerable.Repeat("长文仍然应当清楚易读，操作按钮保持在固定位置。（本地模拟）", 100)) : "让文字，清楚地抵达。\n\n这是本地界面验收响应，用来验证流式生成、停止和复制；没有调用外部模型。";
                await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n"));
                foreach (var piece in reply.Chunk(8))
                {
                    var data = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = new string(piece) } } } });
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("data: " + data + "\n\n")); await Task.Delay(100);
                }
                await stream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
            }
            catch (Exception e) when (e is IOException or SocketException or JsonException) { }
        }
    }
}
