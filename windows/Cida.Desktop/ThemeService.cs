using System;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cida.Desktop;

public static class ThemeService
{
    public static event Action? Changed;
    private static bool _listening;
    private static bool _dark;

    // The palette is the single source of truth for both the brushes Apply installs and the
    // contrast test in tests/Cida.Windows.Tests. #80 keeps three text levels: primary (Ink),
    // secondary for essential instructions, feedback and quiet commands, tertiary for still
    // readable but less important labels, and InkHint only for decorative key hints (never for
    // text the user must read to act).
    private static readonly string[] PaletteNames =
    [
        "PanelBackground", "Paper", "PanelBorder", "Ink", "InkSecondary", "InkTertiary", "InkHint",
        "Accent", "AccentSoft", "Error", "AccentInk", "ControlBackground", "ControlBorder",
        "ControlHover", "ControlPressed", "AccentPressed", "DisabledInk",
    ];

    // Light secondary is the pre-#80 #646C60: #80 folds the old lighter hint into InkHint instead
    // of lightening essential text. Tertiary stays above the 3:1 large/secondary-text floor.
    private static readonly string[] LightPalette =
    [
        "#FFFFFF", "#F5F3ED", "#DADDD5", "#242922", "#646C60", "#7A8375", "#A6ADA0",
        "#346847", "#E9EFE7", "#A43B32", "#FFFFFF", "#F8F9F6", "#B8C1B3",
        "#E9EFE7", "#DDE6D9", "#285337", "#8B9287",
    ];

    // Dark secondary is unchanged and comfortably readable; dark tertiary is a quieter step that
    // still clears 3:1 on both dark surfaces.
    private static readonly string[] DarkPalette =
    [
        "#20251F", "#272D24", "#424A3D", "#ECEEE7", "#B2BAAB", "#8A947F", "#6B7463",
        "#94C6A2", "#354334", "#F1A79B", "#172019", "#2D342A", "#66715F",
        "#3B4937", "#465940", "#7AAC88", "#818A7A",
    ];

    /// <summary>
    /// The named colors this theme installs, in the appearance the caller asks for. Pure and
    /// headless: the design-token test checks WCAG contrast from here without a WPF application.
    /// </summary>
    public static IReadOnlyList<(string Name, string Hex)> Palette(bool dark)
    {
        var colors = dark ? DarkPalette : LightPalette;
        var entries = new (string Name, string Hex)[PaletteNames.Length];
        for (var index = 0; index < PaletteNames.Length; index++)
        {
            entries[index] = (PaletteNames[index], colors[index]);
        }
        return entries;
    }

    public static void ApplyWindowFrame(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var enabled = _dark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        if (window is PanelWindow)
        {
            var rounded = 2; // DWMWCP_ROUND: native corners, without a layered HWND.
            DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    public static void Start()
    {
        Apply(); if (_listening) return;
        _listening = true; SystemEvents.UserPreferenceChanged += OnChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }
    public static void Stop()
    {
        if (_listening) SystemEvents.UserPreferenceChanged -= OnChanged;
        if (_listening) SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _listening = false;
    }
    private static void OnChanged(object sender, UserPreferenceChangedEventArgs e) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Apply());
    private static void OnDisplayChanged(object? sender, EventArgs e) =>
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
        foreach (var (name, hex) in Palette(_dark))
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze(); app.Resources[name] = brush;
        }
        if (SystemParameters.HighContrast)
        {
            app.Resources["PanelBackground"] = SystemColors.WindowBrush;
            app.Resources["Paper"] = SystemColors.WindowBrush;
            app.Resources["Ink"] = SystemColors.WindowTextBrush;
            app.Resources["InkSecondary"] = SystemColors.WindowTextBrush;
            app.Resources["InkTertiary"] = SystemColors.WindowTextBrush;
            app.Resources["InkHint"] = SystemColors.WindowTextBrush;
            app.Resources["Accent"] = SystemColors.HighlightBrush;
            app.Resources["AccentInk"] = SystemColors.HighlightTextBrush;
            app.Resources["AccentSoft"] = SystemColors.ControlBrush;
            app.Resources["PanelBorder"] = SystemColors.WindowTextBrush;
            app.Resources["Error"] = SystemColors.WindowTextBrush;
            app.Resources["ControlBackground"] = SystemColors.ControlBrush;
            app.Resources["ControlBorder"] = SystemColors.WindowTextBrush;
            app.Resources["ControlHover"] = SystemColors.ControlBrush;
            app.Resources["ControlPressed"] = SystemColors.ControlBrush;
            app.Resources["AccentPressed"] = SystemColors.HighlightBrush;
            app.Resources["DisabledInk"] = SystemColors.GrayTextBrush;
        }
        foreach (Window window in app.Windows) ApplyWindowFrame(window);
        Changed?.Invoke();
    }
}
