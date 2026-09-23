using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SherpaAxis.Devices;

[SupportedOSPlatform("windows")]
internal static class HidNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Attributes
    {
        internal int Size;
        internal ushort VendorId;
        internal ushort ProductId;
        internal ushort Version;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct Capabilities
    {
        [FieldOffset(0)] internal ushort Usage;
        [FieldOffset(2)] internal ushort UsagePage;
        [FieldOffset(4)] internal ushort InputReportLength;
        [FieldOffset(6)] internal ushort OutputReportLength;
        [FieldOffset(8)] internal ushort FeatureReportLength;
    }

    [DllImport("hid.dll", ExactSpelling = true)]
    internal static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint CM_Get_Device_Interface_List_SizeW(
        out uint length, in Guid guid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint CM_Get_Device_Interface_ListW(
        in Guid guid, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern SafeFileHandle CreateFileW(
        string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);

    [DllImport("hid.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint data);

    [DllImport("hid.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_FreePreparsedData(nint data);

    [DllImport("hid.dll", ExactSpelling = true)]
    internal static extern int HidP_GetCaps(nint data, out Capabilities capabilities);
}
