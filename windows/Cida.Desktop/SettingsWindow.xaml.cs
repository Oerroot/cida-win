using Cida.Core;
using Cida.Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;



namespace Cida.Desktop;

/// <summary>
/// Settings: the model service summary, the language pair, the prompts, the shortcuts and
/// the launch-at-login switch. Writes go through the same ConfigurationStore the CLI uses.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppModel _model;
    private readonly TextBox _endpoint = new();
    private readonly TextBox _model2 = new();
    private readonly ComboBox _format = new();
    private readonly TextBox _apiKey = new();
    private readonly TextBox _myLanguage = new();
    private readonly TextBox _foreignLanguage = new();
    private readonly CheckBox _launchAtLogin = new() { Content = "开机启动" };
    private readonly TextBlock _status = new();
    private readonly Button _check = new() { Content = "检查" };

    public SettingsWindow(AppModel model)
    {
        _model = model;
        Title = "辞达 · 设置";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = FindResource("PanelBackground") as Brush;

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(Label("模型服务"));
        root.Children.Add(Field("端点", _endpoint));
        _format.ItemsSource = ModelRequestFormatExtensions.AllCases.Select(f => f.RawValue()).ToList();
        root.Children.Add(Field("格式", _format));
        root.Children.Add(Field("模型", _model2));
        _apiKey.Text = "（不改就不动已存的 Key）";
        root.Children.Add(Field("API Key", _apiKey));

        var checkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _check.Click += async (_, _) => await RunCheckAsync();
        checkRow.Children.Add(_check);
        _status.Margin = new Thickness(12, 0, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        checkRow.Children.Add(_status);
        root.Children.Add(checkRow);

        root.Children.Add(Label("语言"));
        root.Children.Add(Field("我的语言", _myLanguage));
        root.Children.Add(Field("常用外语", _foreignLanguage));

        root.Children.Add(Label("启动"));
        _launchAtLogin.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(_launchAtLogin);

        var save = new Button { Content = "保存", Width = 88, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        save.Click += (_, _) => Save();
        root.Children.Add(save);

        Content = root;
        Load();
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = FindResourceStatic("InkSecondary"),
        Margin = new Thickness(0, 14, 0, 2),
    };

    private static System.Windows.Media.Brush FindResourceStatic(string key) =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(key);

    private static StackPanel Field(string header, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        panel.Children.Add(new TextBlock { Text = header, FontSize = 11 });
        control.Margin = new Thickness(0, 2, 0, 0);
        panel.Children.Add(control);
        return panel;
    }

    private void Load()
    {
        var settings = _model.Settings;
        _endpoint.Text = settings.ModelService.Endpoint;
        _model2.Text = settings.ModelService.Model;
        _format.SelectedItem = settings.ModelService.Format.RawValue();
        var (my, foreign) = settings.RequestLanguages();
        _myLanguage.Text = my;
        _foreignLanguage.Text = foreign;
        _launchAtLogin.IsChecked = _model.Store.LaunchAtLogin();
    }

    private void Save()
    {
        var settings = _model.Settings with
        {
            ModelService = _model.Settings.ModelService with
            {
                Endpoint = _endpoint.Text.Trim(),
                Model = _model2.Text.Trim(),
                Format = ModelRequestFormatExtensions.FromRawValue((string?)_format.SelectedItem)
                    ?? ModelRequestFormat.ChatCompletions,
            },
            MyLanguage = _myLanguage.Text.Trim(),
            ForeignLanguage = _foreignLanguage.Text.Trim(),
            LaunchAtLogin = _launchAtLogin.IsChecked == true,
        };
        _model.Store.SaveSettings(settings);
        var key = _apiKey.Text.Trim();
        var touched = key.Length > 0 && key != "（不改就不动已存的 Key）";
        if (touched)
        {
            _model.Store.SaveApiKey(key);
        }
        _model.Store.SetLaunchAtLogin(_launchAtLogin.IsChecked == true);
        _model.ReloadSettings();
        Close();
    }

    private async Task RunCheckAsync()
    {
        Save();
        _status.Text = "正在检查…";
        // Re-read through the store so the key travels with the settings.
        var settings = _model.Store.LoadSettings() with { ApiKey = _model.Store.ReadApiKey() ?? "" };
        var result = await ModelServiceCheck.RunAsync(settings);
        _model.Store.SaveLastCheck(result.Record);
        _status.Text = result.Passed
            ? $"已就绪 · {result.Model}"
            : $"检查失败 · {result.Failure?.Reason() ?? "未知错误"}";
    }
}
