using System;

namespace Cida.Desktop;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        if (Environment.GetCommandLineArgs() is [_, "--accessibility-worker"])
            return Cida.Platform.AccessibilityWorker.Run();
        Velopack.VelopackApp.Build().Run();
        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }
}
