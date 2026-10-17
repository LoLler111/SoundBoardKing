using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SoundBoardKing.App.Services;

internal static class DarkTitleBar
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    public static void Apply(Window window) => window.SourceInitialized += (_, _) =>
    {
        int enabled = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
