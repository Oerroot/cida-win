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

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0)
        {
            ConsoleBridge.AttachParent();
            var code = await CommandLineHost.RunAsync(e.Args);
            Shutdown(code);
            return;
        }
        _model = new AppModel();
        _model.Start();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _model?.Stop();
        base.OnExit(e);
    }
}
