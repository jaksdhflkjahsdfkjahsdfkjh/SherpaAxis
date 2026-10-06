using System.Runtime.InteropServices;

namespace SherpaAxis.Bridge;

public sealed class VJoyController : IVirtualController
{
    private readonly nint library;
    private readonly uint device;
    private readonly Update update;
    private readonly Release release;
    private readonly (int Minimum, int Maximum)[] ranges;
    private bool acquired;
    private bool disposed;

    public static string DefaultLibraryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x64", "vJoyInterface.dll");

    public VJoyController(string libraryPath, uint deviceId = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        if (deviceId is < 1 or > 16) { throw new ArgumentOutOfRangeException(nameof(deviceId)); }
        if (!Path.IsPathFullyQualified(libraryPath)) { throw new ArgumentException("Choose the installed x64 vJoyInterface.dll using an absolute path.", nameof(libraryPath)); }
        if (!File.Exists(libraryPath)) { throw new FileNotFoundException("vJoy is not installed at the selected path. Install the signed Brunner vJoy 2.2.2.0 package and configure X, Y, Z axes first.", libraryPath); }
        device = deviceId;
        library = NativeLibrary.Load(libraryPath, typeof(VJoyController).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        try
        {
            if (Export<Enabled>("vJoyEnabled")() == 0) { throw new IOException("vJoy driver is unavailable or disabled."); }
            var match = Export<Match>("DriverMatch");
            if (match(out var dllVersion, out var driverVersion) == 0 || dllVersion != 0x222 || driverVersion != 0x222)
            {
                throw new IOException($"Expected matching vJoy 2.2.2 driver and SDK (DLL {dllVersion:X3}, driver {driverVersion:X3}).");
            }
            if (Export<DeviceStatus>("GetVJDStatus")(device) != 1) { throw new IOException($"vJoy device {device} is missing, busy, or already owned. Configure a dedicated device with X, Y, Z axes."); }
            var exists = Export<AxisExists>("GetVJDAxisExist");
            var minimum = Export<AxisRange>("GetVJDAxisMin");
            var maximum = Export<AxisRange>("GetVJDAxisMax");
            ranges = new (int, int)[3];
            for (uint i = 0; i < 3; i++)
            {
                var usage = 0x30 + i;
                if (exists(device, usage) == 0 || minimum(device, usage, out var min) == 0 || maximum(device, usage, out var max) == 0 || max <= min)
                {
                    throw new IOException("vJoy device requires enabled X, Y, and Z axes with valid logical ranges.");
                }
                ranges[i] = (min, max);
            }
            update = Export<Update>("UpdateVJD");
            release = Export<Release>("RelinquishVJD");
            if (Export<Acquire>("AcquireVJD")(device) == 0) { throw new IOException("Could not acquire the selected vJoy device."); }
            acquired = true;
            Write(default);
        }
        catch
        {
            if (acquired) { Export<Release>("RelinquishVJD")(device); }
            NativeLibrary.Free(library);
            throw;
        }
    }

    public void Write(PedalOutput output)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var report = new Position
        {
            Device = (byte)device,
            X = Map(output.Throttle, ranges[0].Minimum, ranges[0].Maximum),
            Y = Map(output.Brake, ranges[1].Minimum, ranges[1].Maximum),
            Z = Map(output.Clutch, ranges[2].Minimum, ranges[2].Maximum),
            Hat = uint.MaxValue,
            Hat1 = uint.MaxValue,
            Hat2 = uint.MaxValue,
            Hat3 = uint.MaxValue
        };
        if (update(device, ref report) == 0) { throw new IOException("vJoy rejected the axis report; output has stopped."); }
    }

    public static int Map(double value, int minimum, int maximum)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1) { throw new ArgumentOutOfRangeException(nameof(value)); }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximum, minimum);
        return (int)Math.Round(minimum + value * ((double)maximum - minimum), MidpointRounding.AwayFromZero);
    }

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    public void Dispose()
    {
        if (disposed) { return; }
        try { if (acquired) { try { Write(default); } finally { release(device); acquired = false; } } }
        finally { disposed = true; NativeLibrary.Free(library); }
        GC.SuppressFinalize(this);
    }

    // vJoy 2.2.2 public.h API v3, pack 4. Submit all pedal axes in one report.
    [StructLayout(LayoutKind.Explicit, Size = 124, Pack = 4)]
    private struct Position
    {
        [FieldOffset(0)] public byte Device;
        [FieldOffset(16)] public int X;
        [FieldOffset(20)] public int Y;
        [FieldOffset(24)] public int Z;
        [FieldOffset(80)] public uint Hat;
        [FieldOffset(84)] public uint Hat1;
        [FieldOffset(88)] public uint Hat2;
        [FieldOffset(92)] public uint Hat3;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Enabled();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Match(out ushort dllVersion, out ushort driverVersion);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceStatus(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AxisExists(uint id, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AxisRange(uint id, uint usage, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Acquire(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Release(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Update(uint id, ref Position position);
}
