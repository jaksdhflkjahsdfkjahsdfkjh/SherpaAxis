using System.Runtime.InteropServices;

namespace SherpaAxis.Bridge;

public sealed class VJoyController : IVirtualController
{
    // The SDK initializes process-global device/window state without synchronization.
    private static readonly object SdkSync = new();
    // Keep each SDK module loaded for the process; unloading/reloading stalled repeated enables.
    private static readonly Dictionary<string, nint> Libraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly nint library;
    private readonly uint device;
    private readonly Update update;
    private readonly Release release;
    private readonly (int Minimum, int Maximum)[] ranges;
    private bool acquired;
    private bool disposed;
    public uint DeviceId => device;

    public static string DefaultLibraryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x64", "vJoyInterface.dll");

    public static IReadOnlyList<VJoyDeviceInfo> Inspect(string libraryPath)
    {
        lock (SdkSync)
        {
            var handle = OpenValidatedLibrary(libraryPath);
            T Api<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));
            var status = Api<DeviceStatus>("GetVJDStatus");
            var exists = Api<AxisExists>("GetVJDAxisExist");
            var minimum = Api<AxisRange>("GetVJDAxisMin");
            var maximum = Api<AxisRange>("GetVJDAxisMax");
            var buttons = Api<DeviceStatus>("GetVJDButtonNumber");
            var continuous = Api<DeviceStatus>("GetVJDContPovNumber");
            var discrete = Api<DeviceStatus>("GetVJDDiscPovNumber");
            var ffb = Api<DeviceStatus>("IsDeviceFfb");
            return Enumerable.Range(1, 16).Select(value =>
            {
                var id = (uint)value;
                var state = (VJoyDeviceState)status(id);
                // Do not query another feeder's busy device; it cannot be a candidate for acquisition.
                if (state is VJoyDeviceState.Busy or VJoyDeviceState.Missing or VJoyDeviceState.Unknown) { return new VJoyDeviceInfo(id, state, false, false); }
                var xyz = Enumerable.Range(0x30, 3).All(usage => DecodeAxisPresence(exists(id, (uint)usage))
                    && minimum(id, (uint)usage, out var min) != 0 && maximum(id, (uint)usage, out var max) != 0 && max > min);
                // Match the dedicated pedal descriptor, not an arbitrary free wheel/gamepad.
                var extraAxes = Enumerable.Range(0x33, 0x48 - 0x33 + 1).Any(usage => DecodeAxisPresence(exists(id, (uint)usage)));
                var pedalsOnly = xyz && !extraAxes && buttons(id) == 0 && continuous(id) == 0 && discrete(id) == 0 && ffb(id) == 0;
                return new VJoyDeviceInfo(id, state, xyz, pedalsOnly);
            }).ToArray();
        }
    }

    public static bool DecodeAxisPresence(int result) => result switch
    {
        1 => true,
        // Despite the BOOL API declaration, the 2.2.2 SDK returns NO_SUCH_AXIS (-10).
        0 or -10 => false,
        _ => throw new IOException($"vJoy axis inspection failed (SDK code {result}).")
    };

    private static nint OpenValidatedLibrary(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        if (!Path.IsPathFullyQualified(libraryPath)) { throw new ArgumentException("Choose the installed x64 vJoyInterface.dll using an absolute path.", nameof(libraryPath)); }
        if (!File.Exists(libraryPath)) { throw new FileNotFoundException("vJoy is not installed. Run the SherpaAxis installer to install the driver and SDK together.", libraryPath); }
        var handle = LoadLibrary(Path.GetFullPath(libraryPath));
        if (Marshal.GetDelegateForFunctionPointer<Enabled>(NativeLibrary.GetExport(handle, "vJoyEnabled"))() == 0)
        { throw new IOException("vJoy driver is unavailable or disabled. Finish installation and restart Windows if requested."); }
        var match = Marshal.GetDelegateForFunctionPointer<Match>(NativeLibrary.GetExport(handle, "DriverMatch"));
        if (match(out var dllVersion, out var driverVersion) == 0 || dllVersion != 0x222 || driverVersion != 0x222)
        { throw new IOException($"Expected matching vJoy 2.2.2 driver and SDK (DLL {dllVersion:X3}, driver {driverVersion:X3})."); }
        return handle;
    }

    public VJoyController(string libraryPath, uint deviceId = 1)
    {
        lock (SdkSync)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
            if (deviceId is < 1 or > 16) { throw new ArgumentOutOfRangeException(nameof(deviceId)); }
            device = deviceId;
            library = OpenValidatedLibrary(libraryPath);
            try
            {
                if (Export<DeviceStatus>("GetVJDStatus")(device) != 1) { throw new IOException($"vJoy device {device} is missing, busy, or already owned. Configure a dedicated device with X, Y, Z axes."); }
                var exists = Export<AxisExists>("GetVJDAxisExist");
                var minimum = Export<AxisRange>("GetVJDAxisMin");
                var maximum = Export<AxisRange>("GetVJDAxisMax");
                ranges = new (int, int)[3];
                for (uint i = 0; i < 3; i++)
                {
                    var usage = 0x30 + i;
                    if (!DecodeAxisPresence(exists(device, usage)) || minimum(device, usage, out var min) == 0 || maximum(device, usage, out var max) == 0 || max <= min)
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
                throw;
            }
        }
    }

    public void Write(PedalOutput output)
    {
        lock (SdkSync)
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
    }

    public static int Map(double value, int minimum, int maximum)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1) { throw new ArgumentOutOfRangeException(nameof(value)); }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximum, minimum);
        return (int)Math.Round(minimum + value * ((double)maximum - minimum), MidpointRounding.AwayFromZero);
    }

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static nint LoadLibrary(string path)
    {
        lock (SdkSync)
        {
            if (!Libraries.TryGetValue(path, out var handle))
            {
                handle = NativeLibrary.Load(path, typeof(VJoyController).Assembly,
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
                Libraries.Add(path, handle);
            }
            return handle;
        }
    }

    public void Dispose()
    {
        lock (SdkSync)
        {
            if (disposed) { return; }
            try { if (acquired) { try { Write(default); } finally { release(device); acquired = false; } } }
            finally { disposed = true; }
            GC.SuppressFinalize(this);
        }
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
