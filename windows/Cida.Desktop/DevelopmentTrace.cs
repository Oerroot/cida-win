using System;
using System.Diagnostics;
using System.IO;

namespace Cida.Desktop;

internal static class DevelopmentTrace
{
    [Conditional("DEBUG")]
    public static void Stage(string stage)
    {
        var profile = Environment.GetEnvironmentVariable("CIDA_PROFILE");
        if (string.IsNullOrWhiteSpace(profile)) return;
        try { Directory.CreateDirectory(profile); File.AppendAllText(Path.Combine(profile, "development-stages.log"), DateTime.UtcNow.ToString("O") + " " + stage + "\n"); }
        catch (IOException) { }
    }
}
