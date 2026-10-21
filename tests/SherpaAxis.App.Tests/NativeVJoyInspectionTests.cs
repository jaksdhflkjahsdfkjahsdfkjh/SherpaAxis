using System.Runtime.InteropServices;
using SherpaAxis.Bridge;

namespace SherpaAxis.App.Tests;

public sealed class VJoyInspectionFactAttribute : FactAttribute
{
    public VJoyInspectionFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHERPAAXIS_VJOY_INSPECTION_TEST") != "1")
        { Skip = "Opt-in read-only check: requires the installed vJoy 2.2.2 driver and SDK."; }
    }
}

[Collection("Native vJoy")]
public sealed class NativeVJoyInspectionTests
{
    [VJoyInspectionFact]
    public async Task ParallelStartupInspectionCreatesOneSdkWindow()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspections = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return VJoyController.Inspect(VJoyController.DefaultLibraryPath);
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(inspections).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, devices => Assert.Equal(Enumerable.Range(1, 16).Select(id => (uint)id), devices.Select(device => device.Id)));

        var windows = 0;
        Assert.True(EnumWindows((window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var process);
            if (process == (uint)Environment.ProcessId)
            {
                var name = new char[256];
                var length = GetClassName(window, name, name.Length);
                if (new string(name, 0, length) == "win32app_vJoyInterface_DLL") { windows++; }
            }
            return true;
        }, 0));
        Assert.Equal(1, windows);
    }

    private delegate bool EnumWindow(nint window, nint parameter);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindow callback, nint parameter);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetClassName(nint window, [Out] char[] name, int maximum);
}
