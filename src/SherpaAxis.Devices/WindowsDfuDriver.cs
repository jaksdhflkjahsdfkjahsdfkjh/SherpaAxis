using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SherpaAxis.Devices;

[SupportedOSPlatform("windows")]
public static class WindowsDfuDriver
{
    public static bool Bind(UsbDeviceLocation expected, string infPath)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!expected.InstanceId.StartsWith(@"USB\VID_0483&PID_DF11\", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(infPath) || !File.Exists(infPath))
        { throw new IOException("A supported pedal bootloader and bundled DfuSe driver are required."); }
        var present = WindowsUsbDevices.Find(0x0483, 0xDF11);
        if (present.Count != 1 || !string.Equals(present[0].InstanceId, expected.InstanceId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(present[0].LocationPath, expected.LocationPath, StringComparison.OrdinalIgnoreCase))
        { throw new IOException("The bootloader no longer uniquely matches the original pedal USB port. No driver was changed."); }
        var set = SetupDiCreateDeviceInfoList(0, 0);
        if (set == -1) { throw Error(); }
        var device = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
        try
        {
            if (!SetupDiOpenDeviceInfoW(set, expected.InstanceId, 0, 0, ref device)) { throw Error(); }
            var parameters = new InstallParameters { Size = Marshal.SizeOf<InstallParameters>(), DriverPath = "" };
            if (!SetupDiGetDeviceInstallParamsW(set, ref device, ref parameters)) { throw Error(); }
            parameters.Flags |= 0x10000; // DI_ENUMSINGLEINF
            parameters.FlagsEx |= 0x800; // DI_FLAGSEX_ALLOWEXCLUDEDDRVS
            parameters.DriverPath = infPath;
            if (!SetupDiSetDeviceInstallParamsW(set, ref device, ref parameters)
                || !SetupDiBuildDriverInfoList(set, ref device, 2)) { throw Error(); }
            try
            {
                var driver = new DriverInfo { Size = Marshal.SizeOf<DriverInfo>(), Description = "", Manufacturer = "", Provider = "" };
                if (!SetupDiEnumDriverInfoW(set, ref device, 2, 0, ref driver)) { throw Error(); }
                if (!string.Equals(driver.Provider, "STMicroelectronics", StringComparison.OrdinalIgnoreCase))
                { throw new IOException("The bundled DFU driver provider does not match STMicroelectronics."); }
                if (!DiInstallDevice(0, set, ref device, ref driver, 0, out var restart)) { throw Error(); }
                return restart;
            }
            finally { _ = SetupDiDestroyDriverInfoList(set, ref device, 2); }
        }
        finally { _ = SetupDiDestroyDeviceInfoList(set); }
    }

    private static IOException Error() => new("Cannot prepare the pedal DfuSe driver: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public int Size; public Guid Class; public uint DevInst; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InstallParameters
    {
        public int Size; public uint Flags; public uint FlagsEx; public nint Parent; public nint MessageHandler;
        public nint MessageContext; public nint FileQueue; public nuint ClassReserved; public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DriverInfo
    {
        public int Size; public uint Type; public nuint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Provider;
        public System.Runtime.InteropServices.ComTypes.FILETIME Date; public ulong Version;
    }
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetupDiCreateDeviceInfoList(nint classGuid, nint parent);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiOpenDeviceInfoW(nint set, string instanceId, nint parent, uint flags, ref DeviceInfo device);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstallParamsW(nint set, ref DeviceInfo device, ref InstallParameters parameters);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiSetDeviceInstallParamsW(nint set, ref DeviceInfo device, ref InstallParameters parameters);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiBuildDriverInfoList(nint set, ref DeviceInfo device, uint type);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDriverInfoW(nint set, ref DeviceInfo device, uint type, uint index, ref DriverInfo driver);
    [DllImport("newdev.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DiInstallDevice(nint parent, nint set, ref DeviceInfo device, ref DriverInfo driver, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool restart);
    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDriverInfoList(nint set, ref DeviceInfo device, uint type);
    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
