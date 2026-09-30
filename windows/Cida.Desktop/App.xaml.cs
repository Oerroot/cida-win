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

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // Velopack's hook: handles install/update/uninstall events; no-op unpackaged.
        Velopack.VelopackApp.Build().Run();
        base.OnStartup(e);
        _model = new AppModel();
        _model.Start();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _model?.Stop();
        base.OnExit(e);
    }
}
