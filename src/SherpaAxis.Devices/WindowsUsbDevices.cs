using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SherpaAxis.Devices;

public sealed record UsbDeviceLocation(string InstanceId, string LocationPath, bool DriverReady = true);

[SupportedOSPlatform("windows")]
public static class WindowsUsbDevices
{
    public static IReadOnlyList<string> FindHidInstances()
    {
        var set = SetupDiGetClassDevsW(0, "HID", 0, 6);
        if (set == -1) { throw Error(); }
        try
        {
            var instances = new List<string>();
            for (uint index = 0; ; index++)
            {
                var data = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref data))
                { if (Marshal.GetLastPInvokeError() == 259) { break; } throw Error(); }
                var buffer = new char[1024];
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, buffer, buffer.Length, out var length)) { throw Error(); }
                instances.Add(new string(buffer, 0, length - 1));
            }
            return instances;
        }
        finally { _ = SetupDiDestroyDeviceInfoList(set); }
    }

    public static IReadOnlyList<UsbDeviceLocation> Find(ushort vendor, ushort product)
    {
        var prefix = string.Create(CultureInfo.InvariantCulture, $"USB\\VID_{vendor:X4}&PID_{product:X4}\\");
        var set = SetupDiGetClassDevsW(0, "USB", 0, 6); // PRESENT | ALLCLASSES
        return Enumerate(set, prefix);
    }

    public static IReadOnlyList<UsbDeviceLocation> FindDfuInterfaces()
    {
        var guid = new Guid("3FE809AB-FB91-4CB5-A643-69670D52366E"); // ST DfuSe driver interface
        return Enumerate(GetDfuDevs(in guid, null, 0, 0x12), null);
    }

    private static List<UsbDeviceLocation> Enumerate(nint set, string? prefix)
    {
        if (set == -1) { throw Error(); }
        try
        {
            var result = new List<UsbDeviceLocation>();
            for (uint index = 0; ; index++)
            {
                var data = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref data))
                { if (Marshal.GetLastPInvokeError() == 259) { break; } throw Error(); }
                var idBuffer = new char[1024];
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, idBuffer, idBuffer.Length, out var idLength)) { throw Error(); }
                var id = new string(idBuffer, 0, idLength - 1);
                if (prefix is not null && !id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { continue; }
                var bytes = new byte[8192];
                if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, 0x23, out _, bytes, bytes.Length, out var length)) { throw Error(); }
                var location = Encoding.Unicode.GetString(bytes, 0, checked((int)length)).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(location)) { throw new IOException("The USB port identity is unavailable; firmware updating cannot identify the bootloader."); }
                result.Add(new(id, location));
            }
            return result;
        }
        finally { _ = SetupDiDestroyDeviceInfoList(set); }
    }

    private static IOException Error() => new("Cannot inspect USB device locations: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public int Size; public Guid Class; public uint DevInst; public nint Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(nint classGuid, string enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint GetDfuDevs(in Guid classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref DeviceInfo data, [Out] char[] id, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref DeviceInfo data, uint property, out uint type, [Out] byte[] buffer, int size, out uint required);
    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
