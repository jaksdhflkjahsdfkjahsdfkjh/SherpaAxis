using System.ComponentModel;
using System.IO;
using System.Security;

namespace SherpaAxis.App.Services.Diagnostics;

internal static class OperationErrors
{
    public static bool IsExpected(Exception failure) => failure is IOException or InvalidDataException
        or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException
        or OperationCanceledException or DllNotFoundException or BadImageFormatException
        or EntryPointNotFoundException or SecurityException or Win32Exception;
}
