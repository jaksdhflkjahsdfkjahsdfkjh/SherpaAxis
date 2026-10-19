using System.Diagnostics;
using System.Globalization;
using System.IO;
using SherpaAxis.Bridge;

namespace SherpaAxis.App.Services.Output;

public sealed record VJoySetupResult(bool Ready, string Detail, bool RestartRequired = false);

public sealed class VJoySetup(Func<IReadOnlyList<VJoyDeviceInfo>> inspect, Func<uint, CancellationToken, Task> create)
{
    public async Task<VJoySetupResult> ConfigureAsync(uint? rememberedId, CancellationToken token)
    {
        var devices = inspect();
        if (rememberedId.HasValue)
        {
            var previous = devices.Single(device => device.Id == rememberedId);
            if (previous.State is VJoyDeviceState.Busy or VJoyDeviceState.Owned)
            { return new(false, $"vJoy {rememberedId} is in use. Close other feeders and try again."); }
            if (previous.State != VJoyDeviceState.Missing)
            {
                if (previous is { State: VJoyDeviceState.Free, IsPedalsOnly: true }) { return new(true, "Virtual pedal device is ready."); }
                return new(false, "The saved virtual device has been changed. Automatic setup preserves existing configurations. Review it in Configure vJoy.");
            }
        }
        else if (devices.Any(device => device is { State: VJoyDeviceState.Free, IsPedalsOnly: true }))
        { return new(true, "Virtual pedal device is ready."); }

        var id = rememberedId ?? devices.FirstOrDefault(device => device.State == VJoyDeviceState.Missing)?.Id;
        if (id is null) { return new(false, "All 16 vJoy device slots are occupied. Free a slot in Configure vJoy."); }
        token.ThrowIfCancellationRequested();
        await create(id.Value, token).ConfigureAwait(false);
        // The vendor helper can return zero even when registry writes failed. Query the driver again.
        var actual = inspect().Single(device => device.Id == id);
        return actual is { State: VJoyDeviceState.Free, IsPedalsOnly: true }
            ? new(true, "Virtual pedal device is ready.")
            : new(false, "Device setup is not active yet. Restart Windows if requested, then check again. Existing vJoy configurations were preserved.", true);
    }

    public static VJoySetup Default() => new(() => VJoyController.Inspect(VJoyController.DefaultLibraryPath), async (id, token) =>
    {
        var executable = Path.Combine(Path.GetDirectoryName(VJoyController.DefaultLibraryPath)!, "vJoyConfig.exe");
        if (!File.Exists(executable)) { throw new FileNotFoundException("vJoy configuration tool is missing. Repair the vJoy installation.", executable); }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        // No -f: upstream refuses to replace an existing descriptor even if another process creates it after inspection.
        foreach (var arg in new[] { id.ToString(CultureInfo.InvariantCulture), "-a", "x", "y", "z", "-b", "0", "-p", "0", "-s", "0" }) { start.ArgumentList.Add(arg); }
        using var process = Process.Start(start) ?? throw new IOException("Could not start automatic vJoy configuration.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new IOException("vJoy configuration did not finish. Restart Windows, then check again; setup may already have created the device.");
        }
        if (process.ExitCode != 0) { throw new IOException($"vJoy configuration exited with code {process.ExitCode}. Restart Windows if requested, then check again."); }
    });
}
