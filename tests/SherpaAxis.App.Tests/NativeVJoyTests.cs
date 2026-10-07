using System.IO;
using System.Runtime.InteropServices;
using SherpaAxis.Bridge;

namespace SherpaAxis.App.Tests;

[CollectionDefinition("Native vJoy", DisableParallelization = true)]
public sealed class NativeVJoyTestGroup;

public sealed class VJoyFactAttribute : FactAttribute
{
    public VJoyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHERPAAXIS_VJOY_TEST") != "1")
        {
            Skip = "Opt-in hardware test: requires a dedicated vJoy device 1 with X/Y/Z and closed games.";
        }
    }
}

[Collection("Native vJoy")]
public sealed class NativeVJoyTests
{
    [VJoyFact]
    public async Task RepeatedNativeEnablesPublishWindowsAxesAndReleaseOnStaleInput()
    {
        var joystick = FindJoystick();
        for (var cycle = 0; cycle < 10; cycle++)
        {
            await using var bridge = new OutputBridge(() => new VJoyController(VJoyController.DefaultLibraryPath));
            await bridge.Ready.WaitAsync(TimeSpan.FromSeconds(3));
            bridge.Submit(new(.25, .5, .75));
            await WaitForAsync(joystick.Id, position =>
                Near(position.X, .25, joystick.Caps.XMin, joystick.Caps.XMax) &&
                Near(position.Y, .5, joystick.Caps.YMin, joystick.Caps.YMax) &&
                Near(position.Z, .75, joystick.Caps.ZMin, joystick.Caps.ZMax));
            if (cycle == 9)
            {
                await WaitForAsync(joystick.Id, Neutral);
                Assert.Contains("no fresh input", bridge.Status, StringComparison.Ordinal);
                bridge.Submit(new(1, 1, 1));
                await Task.Delay(20);
                Assert.True(Neutral(Read(joystick.Id)));
            }
            else
            {
                bridge.Invalidate("native regression test");
                await WaitForAsync(joystick.Id, Neutral);
            }
        }
        using var reacquired = new VJoyController(VJoyController.DefaultLibraryPath);
        reacquired.Write(default);
    }

    private static bool Neutral(Position position) => position.X == 0 && position.Y == 0 && position.Z == 0;
    private static bool Near(uint value, double fraction, uint min, uint max) => Math.Abs(value - (min + fraction * (max - min))) <= 4;

    private static async Task WaitForAsync(uint id, Func<Position, bool> predicate)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (predicate(Read(id))) { return; }
            await Task.Delay(3);
        }
        throw new TimeoutException("Windows joystick readback did not reach the expected axis state.");
    }

    private static (uint Id, Capabilities Caps) FindJoystick()
    {
        var matches = new List<(uint, Capabilities)>();
        for (uint id = 0; id < JoyGetNumDevs(); id++)
        {
            if (JoyGetDevCaps(id, out var caps, (uint)Marshal.SizeOf<Capabilities>()) == 0 && caps.Vendor == 0x1234 && caps.Product == 0xBEAD)
            {
                matches.Add((id, caps));
            }
        }
        return Assert.Single(matches);
    }

    private static Position Read(uint id)
    {
        var position = new Position { Size = (uint)Marshal.SizeOf<Position>(), Flags = 0xFF };
        var result = JoyGetPosEx(id, ref position);
        if (result != 0) { throw new IOException($"Windows joystick read failed: {result}."); }
        return position;
    }

    [DllImport("winmm.dll", EntryPoint = "joyGetNumDevs")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint JoyGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "joyGetDevCapsW", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint JoyGetDevCaps(nuint id, out Capabilities capabilities, uint size);
    [DllImport("winmm.dll", EntryPoint = "joyGetPosEx")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint JoyGetPosEx(uint id, ref Position position);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Capabilities
    {
        public ushort Vendor, Product;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint XMin, XMax, YMin, YMax, ZMin, ZMax, Buttons, PeriodMin, PeriodMax;
        public uint RMin, RMax, UMin, UMax, VMin, VMax, Flags, MaxAxes, Axes, MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegistryKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Oem;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Position
    {
        public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2;
    }
}
