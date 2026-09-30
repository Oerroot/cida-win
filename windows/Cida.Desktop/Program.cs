using System;

namespace Cida.Desktop;

public static class Program
{
    [STAThread]
    public static int Main()
    {
        Velopack.VelopackApp.Build().Run();
        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }
}
