using System.Runtime.InteropServices;

namespace SherpaAxis.App.Services.Desktop;

internal static class InstanceActivation
{
    internal static readonly int Message = unchecked((int)RegisterWindowMessageW("SherpaAxis.PedalsUtility.Show"));
    internal static void ShowRunningInstance() { if (Message != 0) { _ = PostMessageW((nint)0xFFFF, (uint)Message, 0, 0); } }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string message);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
