using Cida.Core;
using Cida.Platform;
using System.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cida.Desktop;

public partial class App : System.Windows.Application
{
    private AppModel _model = null!;
    private SingleInstance? _instance;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        DevelopmentTrace.Stage("startup arguments=" + e.Args.Length);
        if (e.Args.Length > 0)
        {
            ConsoleBridge.AttachParent();
            var code = await CommandLineHost.RunAsync(e.Args);
            Shutdown(code);
            return;
        }
        _instance = new SingleInstance();
        DevelopmentTrace.Stage("instance owner=" + _instance.IsOwner);
        if (!_instance.IsOwner) { await _instance.NotifyAsync(); Shutdown(); return; }
        _model = new AppModel();
        DevelopmentTrace.Stage("model created");
        _model.Start();
        DevelopmentTrace.Stage("model started");
        _instance.Listen(_model.OpenPanel);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _model?.Stop();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
