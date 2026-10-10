using System.Reflection;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Cida.Core;
using Cida.Desktop;
using Cida.Platform;
using Xunit;

namespace Cida.Windows.Tests;

public sealed class WindowsRegressionTests
{
    [Fact]
    public void CopyInputMatchesTheNativeAbi()
    {
        var input = typeof(SelectionReader).GetNestedType("INPUT", BindingFlags.NonPublic)!;
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf(input));
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf(input, "U").ToInt32());
    }

    [DesktopFact]
    public Task FocusedSelectionAndPanelReopeningUseTheSourceWindow() => OnStaAsync(async () =>
    {
        var inspectionBefore = Environment.GetEnvironmentVariable("CIDA_UI_INSPECTION");
        Environment.SetEnvironmentVariable("CIDA_UI_INSPECTION", "1"); // A shared desktop must not auto-hide the fixture during observation.
        // This secondary STA fixture tests selection/paste/UIA, not the user's
        // live IME composition. Keep shared TSF transitory input out of it.
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources["PanelBackground"] = System.Windows.Media.Brushes.WhiteSmoke;
        application.Resources["PanelBorder"] = System.Windows.Media.Brushes.Gray;
        application.Resources["Ink"] = System.Windows.Media.Brushes.Black;
        application.Resources["InkSecondary"] = System.Windows.Media.Brushes.Gray;
        application.Resources["Accent"] = System.Windows.Media.Brushes.Brown;
        var settings = new CidaSettings();
        var store = new ConfigurationStore(() => settings, value => settings = value, () => null,
            _ => { }, () => { }, () => false, () => null, _ => { }, () => false, _ => { }, () => { });
        var model = new AppModel(store);
        var first = new System.Windows.Controls.TextBox { Text = "unselected first control" };
        var second = new System.Windows.Controls.TextBox { Text = "selected second control" };
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(first, false);
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(second, false);
        var content = new StackPanel();
        content.Children.Add(first);
        content.Children.Add(second);
        var source = new Window { Title = "Cida isolated regression test", Content = content,
            Width = 360, Height = 140, ShowInTaskbar = false };
        try
        {
            source.Show();
            var hwnd = new WindowInteropHelper(source).EnsureHandle();
            FocusSource(source);
            second.Focus();
            second.SelectAll();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(source.IsVisible, $"Test source closed before selection; HWND={hwnd}, current={new WindowInteropHelper(source).Handle}");
            var selection = await Task.Run(() => new SelectionReader().ReadFromAutomation(hwnd));
            Assert.Equal(second.Text, selection.Text);
            Invoke(model, "TogglePanel");
            System.Windows.Input.InputMethod.SetIsInputMethodEnabled(Composer(model)!, false);
            await UntilAsync(() => Composer(model)?.Text == second.Text);
            Assert.Equal(hwnd, model.SourceWindow);
            Invoke(model, "TogglePanel"); // Hide.
            second.Text = "new selection after reopening";
            FocusSource(source);
            second.Focus();
            second.SelectAll();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Invoke(model, "TogglePanel");
            System.Windows.Input.InputMethod.SetIsInputMethodEnabled(Composer(model)!, false);
            await UntilAsync(() => Composer(model)?.Text == second.Text);

            // Register both layer combinations against a real message window without touching default hotkeys.
            settings = settings with
            {
                Shortcut = new(0x85, ShortcutModifiers.Control | ShortcutModifiers.Alt),
                CaptureShortcut = new(0x86, ShortcutModifiers.Control | ShortcutModifiers.Alt),
                LayerShortcut = new(0x87, ShortcutModifiers.Control | ShortcutModifiers.Alt),
                ImproveShortcut = new(0x84, ShortcutModifiers.Control | ShortcutModifiers.Alt),
            };
            model.ReloadSettings();
            var hotkeys = Field<GlobalHotkeySource>(model, "_hotkeys");
            var actions = Field<Dictionary<int, GlobalHotkeySource.GlobalShortcutActionMirror>>(hotkeys, "_actions");
            Assert.Equal(5, actions.Count);
            Assert.Contains(GlobalHotkeySource.GlobalShortcutActionMirror.WholeWindowTranslationLayer, actions.Values);

            // Settings check reports failure in the still-visible window with the isolated incomplete config.
            var settingsWindow = new SettingsWindow(model);
            try
            {
                settingsWindow.Show();
                await (Task)typeof(SettingsWindow).GetMethod("RunCheckAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(settingsWindow, null)!;
                Assert.True(settingsWindow.IsVisible);
                Assert.StartsWith("检查失败", Field<System.Windows.Controls.TextBlock>(settingsWindow, "_status").Text);
            }
            finally { settingsWindow.Close(); }

            var bounds = new System.Drawing.RectangleF(120, 150, 300, 40);
            var overlay = new LayerOverlayWindow("isolated translation", bounds);
            overlay.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            GetWindowRect(new WindowInteropHelper(overlay).Handle, out var overlayBounds);
            Assert.Equal(120, overlayBounds.Left);
            Assert.Equal(150, overlayBounds.Top);
            Assert.InRange(overlayBounds.Right - overlayBounds.Left, 299, 301);
            typeof(AppModel).GetField("_layerOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(model, overlay);
            Invoke(model, "ToggleLayer", false);
            Assert.False(overlay.IsVisible);
            Assert.Null(typeof(AppModel).GetField("_layerOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model));

            await CopyPreservesFormatsAsync(source, second, hwnd);
            ((PanelWindow)Field<PanelWindow>(model, "_panel")).Hide();
            await ReplacementRequiresUnchangedSelectionAsync(source, second, hwnd);
            var image = (System.Windows.Media.Imaging.BitmapSource)typeof(PanelWindow).Assembly.GetType("Cida.Desktop.ResultDocument")!
                .GetMethod("Image", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, new object[] { string.Join("\n\n", Enumerable.Repeat("Long result with a preserved final line.", 25)), "测试" })!;
            Assert.True(image.PixelHeight > 1000, "Copy-image must include the entire result, beyond the panel viewport.");
            await ReadOnlyParagraphsUseActualRangesAsync();
        }
        finally { model.Stop(); source.Close(); Environment.SetEnvironmentVariable("CIDA_UI_INSPECTION", inspectionBefore); }
    });

    private static System.Windows.Controls.TextBox? Composer(AppModel model)
    {
        var panel = typeof(AppModel).GetField("_panel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model);
        return panel == null ? null : (System.Windows.Controls.TextBox)typeof(PanelWindow)
            .GetField("_composer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel)!;
    }
    private static async Task ReadOnlyParagraphsUseActualRangesAsync()
    {
        var document = new System.Windows.Documents.FlowDocument { PagePadding = new Thickness(0), FontSize = 16 };
        for (var index = 0; index < 12; index++) document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run($"Visible paragraph {index}: careful reading keeps the meaning clear.")) { Margin = new Thickness(0, 0, 0, 12) });
        var reader = new System.Windows.Controls.RichTextBox { IsReadOnly = true, Document = document, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var window = new Window { Title = "Cida read-only paragraph fixture", Content = reader, Width = 480, Height = 330, ShowInTaskbar = false };
        using var worker = new AccessibilityWorker();
        try
        {
            window.Show(); FocusSource(window); var hwnd = new WindowInteropHelper(window).Handle;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var paragraphs = (await worker.QueryAsync(new("window", (long)hwnd)))?.Paragraphs;
            Assert.NotNull(paragraphs); Assert.NotEmpty(paragraphs); Assert.True(paragraphs.Count < 12);
            var first = paragraphs.First();
            var pointer = (await worker.QueryAsync(new("paragraph", (long)hwnd, (int)first.Bounds.Left + 10, (int)first.Bounds.Top + 5)))?.Paragraph;
            Assert.NotNull(pointer); Assert.Equal(first.FullText, pointer.Text); Assert.Equal(first.StartOffset, pointer.StartOffset);
            Assert.InRange(pointer.Bounds.Height, 1, 150);
            using var server = new ControlledModelServer();
            var settings = new CidaSettings { ModelService = new ModelConfiguration { Endpoint = server.Endpoint, Model = "fixture" }, ApiKey = "synthetic-fixture-key" };
            var store = new ConfigurationStore(() => settings, _ => { }, () => settings.ApiKey, _ => { }, () => { }, () => true, () => null, _ => { }, () => false, _ => { }, () => { });
            var model = new AppModel(store);
            using var session = new LayerSession(model, hwnd, pointer);
            session.Start();
            await UntilAsync(() => Field<Dictionary<string, LayerOverlayWindow>>(session, "_overlays").Values.Any(overlay => overlay.IsVisible));
            Assert.Equal(1, server.Requests);
            reader.ScrollToEnd(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var after = (await worker.QueryAsync(new("window", (long)hwnd)))?.Paragraphs;
            Assert.NotNull(after); Assert.NotEmpty(after); Assert.DoesNotContain(after, p => p.Text == first.Text);
            await UntilAsync(() => Field<Dictionary<string, LayerOverlayWindow>>(session, "_overlays").Count == 0);
            reader.ScrollToHome(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await UntilAsync(() => Field<Dictionary<string, LayerOverlayWindow>>(session, "_overlays").Values.Any(overlay => overlay.IsVisible));
            Assert.Equal(1, server.Requests); // Restoring the visible paragraph reuses its translation.
            ((System.Windows.Documents.Run)((System.Windows.Documents.Paragraph)document.Blocks.FirstBlock).Inlines.FirstInline).Text = "Changed source paragraph invalidates its old translation.";
            await UntilAsync(() => Field<bool>(session, "_disposed"));
            Assert.Empty(Field<Dictionary<string, LayerOverlayWindow>>(session, "_overlays"));
            session.Dispose();
            using var whole = new LayerSession(model, hwnd); whole.Start();
            await UntilAsync(() => Field<Dictionary<string, LayerOverlayWindow>>(whole, "_overlays").Count > 1);
            whole.ToggleOriginal(); Assert.All(Field<Dictionary<string, LayerOverlayWindow>>(whole, "_overlays").Values, overlay => Assert.False(overlay.IsVisible));
            whole.Dispose(); model.Stop();
            reader.IsReadOnly = false;
            var editable = (await worker.QueryAsync(new("window", (long)hwnd)))?.Paragraphs;
            Assert.NotNull(editable); Assert.Empty(editable);
        }
        finally { window.Close(); }
    }
    private static async Task ReplacementRequiresUnchangedSelectionAsync(Window source, System.Windows.Controls.TextBox editor, nint hwnd)
    {
        using var clipboard = ClipboardSnapshot.Capture();
        Assert.NotNull(clipboard);
        using var worker = new AccessibilityWorker();
        editor.Text = "prefix target suffix"; editor.IsUndoEnabled = false; editor.IsUndoEnabled = true;
        FocusSource(source); editor.Focus(); editor.Select(7, 6);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var original = (await worker.QueryAsync(new("replacement", (long)hwnd)))?.Replacement;
        Assert.NotNull(original); Assert.True(original.Editable);
        editor.Select(0, 6);
        Assert.False(await SelectionReplacer.ReplaceAsync(original, "better", worker, CancellationToken.None));
        Assert.Equal("prefix target suffix", editor.Text);
        editor.Text = "prefix target changed"; editor.Select(7, 6);
        Assert.False(await SelectionReplacer.ReplaceAsync(original, "better", worker, CancellationToken.None));
        editor.Text = "prefix target suffix"; editor.IsUndoEnabled = false; editor.IsUndoEnabled = true; editor.Select(7, 6);
        original = (await worker.QueryAsync(new("replacement", (long)hwnd)))?.Replacement;
        Assert.NotNull(original);
        Assert.True(await SelectionReplacer.ReplaceAsync(original, "better", worker, CancellationToken.None));
        Assert.Equal("prefix better suffix", editor.Text);
        Assert.True(editor.CanUndo); editor.Undo(); Assert.Equal("prefix target suffix", editor.Text);
        editor.IsReadOnly = true; editor.Select(7, 6);
        var readOnly = (await worker.QueryAsync(new("replacement", (long)hwnd)))?.Replacement;
        Assert.True(readOnly == null || !readOnly.Editable);
        editor.IsReadOnly = false;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static void Invoke(object target, string method, params object[] arguments) => target.GetType()
        .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, arguments);

    private static async Task CopyPreservesFormatsAsync(Window source, System.Windows.Controls.TextBox editor, nint hwnd)
    {
        using var original = ClipboardSnapshot.Capture();
        Assert.NotNull(original); // Abort before writing if the user's clipboard cannot be preserved.
        try
        {
            var data = new System.Windows.Forms.DataObject();
            data.SetData(System.Windows.Forms.DataFormats.UnicodeText, "clipboard before copy");
            data.SetData(System.Windows.Forms.DataFormats.Rtf, "{\\rtf1 original rich text}");
            data.SetData(System.Windows.Forms.DataFormats.FileDrop, new[] { @"C:\cida-test-fixture.txt" });
            data.SetData("Cida.Regression.Custom", new MemoryStream(new byte[] { 1, 2, 3, 4 }));
            using var image = new System.Drawing.Bitmap(3, 2);
            image.SetPixel(1, 1, System.Drawing.Color.Red);
            data.SetData(System.Windows.Forms.DataFormats.Bitmap, image);
            System.Windows.Forms.Clipboard.SetDataObject(data, true);
            FocusSource(source);
            editor.Focus();
            editor.SelectAll();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var result = await Task.Run(() => new SelectionReader().ReadByCopying(hwnd));
            Assert.Equal(editor.Text, result.Text);
            Assert.Equal("clipboard before copy", System.Windows.Forms.Clipboard.GetText());
            Assert.Equal("{\\rtf1 original rich text}", System.Windows.Forms.Clipboard.GetText(System.Windows.Forms.TextDataFormat.Rtf));
            Assert.Equal(@"C:\cida-test-fixture.txt", System.Windows.Forms.Clipboard.GetFileDropList()[0]);
            using var restoredImage = System.Windows.Forms.Clipboard.GetImage();
            Assert.NotNull(restoredImage);
            Assert.Equal(3, restoredImage!.Width);
            Assert.True(System.Windows.Forms.Clipboard.TryGetData<MemoryStream>("Cida.Regression.Custom", out var custom));
            using (custom) Assert.Equal(new byte[] { 1, 2, 3, 4 }, custom!.ToArray());

            using var saved = ClipboardSnapshot.Capture();
            Assert.True(saved != null, ClipboardSnapshot.LastCaptureFailure);
            var expectedSequence = ClipboardSnapshot.SequenceNumber();
            // Publish the simulated other application's copy eagerly: SetText
            // leaves a lazy OLE data object owned by this secondary STA fixture.
            System.Windows.Forms.Clipboard.SetDataObject("newer clipboard must survive", true);
            Assert.Equal("newer clipboard must survive", System.Windows.Forms.Clipboard.GetText());
            var newSequence = ClipboardSnapshot.SequenceNumber();
            Assert.NotEqual(expectedSequence, newSequence);
            Assert.False(ClipboardSnapshot.Restore(saved!, hwnd, expectedSequence));
            var nativeText = ClipboardSnapshot.ReadText(hwnd, newSequence);
            Assert.Equal("newer clipboard must survive", nativeText);
            Assert.Equal("newer clipboard must survive", System.Windows.Forms.Clipboard.GetText());
        }
        finally
        {
            // Cleanup is an owned write, not a source application's Ctrl+C. OLE's
            // temporary owner HWND and foreground changes must not decide cleanup.
            // Preserve any unrelated copy made while the interactive test runs.
            var sequence = ClipboardSnapshot.SequenceNumber();
            var current = System.Windows.Forms.Clipboard.GetText();
            if (current is "newer clipboard must survive" or "clipboard before copy" || current == editor.Text)
            {
                Assert.True(ClipboardSnapshot.WriteOwnedText("Cida regression cleanup", sequence, out var ownedSequence));
                Assert.True(ClipboardSnapshot.RestoreOwned(original!, ownedSequence), "Original test clipboard could not be restored; owned=" + ownedSequence + ", current=" + ClipboardSnapshot.SequenceNumber() + ", remaining=" + string.Join(",", original!.Formats.Where(entry => entry.Handle != 0).Select(entry => entry.Format)));
            }
            ClipboardSnapshot.ReleaseWindow();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out RECT bounds);
    private static void FocusSource(Window source)
    {
        var thread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(SelectionReader.ForegroundWindow(), out _);
        var attached = foregroundThread != 0 && foregroundThread != thread && AttachThreadInput(thread, foregroundThread, true);
        try
        {
            source.Activate();
            SetForegroundWindow(new WindowInteropHelper(source).Handle);
        }
        finally { if (attached) AttachThreadInput(thread, foregroundThread, false); }
        Assert.Equal(new WindowInteropHelper(source).Handle, SelectionReader.ForegroundWindow());
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    private static async Task UntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("UI state did not arrive");
            await Task.Delay(20);
        }
    }
    private static Task OnStaAsync(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completed.SetResult(); }
                catch (Exception error) { completed.SetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task;
    }
}

public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CIDA_DESKTOP_TESTS") != "1")
            Skip = "Run on an interactive Windows desktop with CIDA_DESKTOP_TESTS=1.";
    }
}
