using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SherpaAxis.App.Services.Setup;

internal sealed record SavedDataUser(string Sid, string LocalApplicationData)
{
    internal string VisibilityStatePath => Path.Combine(LocalApplicationData, "SherpaAxis", "physical-pedal-visibility.json");
    internal RegistryKey OpenRegistry() => Registry.Users.OpenSubKey(Sid, writable: true)
        ?? throw new IOException("The desktop account's registry is unavailable. Saved data was kept.");
    internal void RequireExpectedUser(string? expectedSid)
    {
        if (expectedSid is not null && !string.Equals(expectedSid, Sid, StringComparison.Ordinal))
        { throw new UnauthorizedAccessException("Saved data removal requires the Windows account that started uninstalling. Another account's data was not removed."); }
    }

    internal static SavedDataUser Interactive()
    {
        var shell = GetShellWindow();
        if (shell == 0 || GetWindowThreadProcessId(shell, out var processId) == 0)
        { throw new IOException("Cannot identify the desktop account. Sign in normally before deleting saved SherpaAxis data."); }
        using var process = Process.GetProcessById(checked((int)processId));
        using var current = Process.GetCurrentProcess();
        if (process.SessionId != current.SessionId) { throw new IOException("The desktop account belongs to another Windows session."); }
        using var handle = OpenProcess(0x1000, false, process.Id);
        if (handle.IsInvalid || !OpenProcessToken(handle, 0x000E, out var token))
        { throw new IOException("Cannot identify the desktop account for saved data removal.", new Win32Exception(Marshal.GetLastPInvokeError())); }
        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            var sid = identity.User?.Value ?? throw new IOException("The desktop account has no Windows identity.");
            var folder = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
            nint path = 0;
            try
            {
                var result = SHGetKnownFolderPath(in folder, 0, token, out path);
                if (result < 0) { throw new IOException("Cannot locate the desktop account's saved data.", Marshal.GetExceptionForHR(result)); }
                var local = Marshal.PtrToStringUni(path);
                if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local))
                { throw new IOException("The desktop account's saved data folder is unavailable."); }
                return new(sid, local);
            }
            finally { Marshal.FreeCoTaskMem(path); }
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetShellWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, SafeAccessTokenHandle token, out nint path);
}
