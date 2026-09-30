using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Cida.Desktop;

internal static class ConsoleBridge
{
    public static void AttachParent()
    {
        var stdout = GetStdHandle(-11);
        // Keep inherited file/pipe handles when the GUI entry point is invoked with redirection.
        if (stdout == 0 || stdout == new nint(-1)) AttachConsole(uint.MaxValue);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);
}
