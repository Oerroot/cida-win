using System;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cida.Desktop;

public static class ThemeService
{
    private static bool _listening;
    private static bool _dark;
    public static void ApplyWindowFrame(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var enabled = _dark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    public static void Start()
    {
        Apply(); if (_listening) return;
        _listening = true; SystemEvents.UserPreferenceChanged += OnChanged;
    }
    public static void Stop()
    {
        if (_listening) SystemEvents.UserPreferenceChanged -= OnChanged;
        _listening = false;
    }
    private static void OnChanged(object sender, UserPreferenceChangedEventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Apply());
    public static void Apply(bool? dark = null)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        if (!app.Resources.Contains("Paper"))
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Cida;component/Resources/Controls.xaml", UriKind.Relative) });
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark ??= key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { dark ??= false; }
        _dark = dark == true;
        string[] names = ["PanelBackground", "Paper", "PanelBorder", "Ink", "InkSecondary", "Accent", "AccentSoft", "Error", "AccentInk"];
        string[] colors = dark == true
            ? ["#20251F", "#272D24", "#424A3D", "#ECEEE7", "#B2BAAB", "#94C6A2", "#354334", "#F1A79B", "#172019"]
            : ["#FFFFFF", "#F5F3ED", "#DADDD5", "#242922", "#646C60", "#346847", "#E9EFE7", "#A43B32", "#FFFFFF"];
        for (var i = 0; i < names.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            brush.Freeze(); app.Resources[names[i]] = brush;
        }
        if (SystemParameters.HighContrast)
        {
            app.Resources["PanelBackground"] = SystemColors.WindowBrush;
            app.Resources["Paper"] = SystemColors.WindowBrush;
            app.Resources["Ink"] = SystemColors.WindowTextBrush;
            app.Resources["InkSecondary"] = SystemColors.WindowTextBrush;
            app.Resources["Accent"] = SystemColors.HighlightBrush;
            app.Resources["AccentInk"] = SystemColors.HighlightTextBrush;
            app.Resources["AccentSoft"] = SystemColors.ControlBrush;
            app.Resources["PanelBorder"] = SystemColors.WindowTextBrush;
            app.Resources["Error"] = SystemColors.WindowTextBrush;
        }
        foreach (Window window in app.Windows) ApplyWindowFrame(window);
    }
}
