using System.Runtime.InteropServices;

namespace Cida.Desktop;
/// <summary>Modifier state at the moment a global hotkey fires.</summary>
internal static class KeyboardState
{
    private const short HighBit = unchecked((short)0x8000);
    private const int VK_SHIFT = 0x10;

    public static bool ShiftIsDown() => (GetAsyncKeyState(VK_SHIFT) & HighBit) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
