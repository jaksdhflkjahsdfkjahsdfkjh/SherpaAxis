using System.IO;
using SherpaAxis.App.Services.Output;
using SherpaAxis.Bridge;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services.Diagnostics;

public sealed record DeviceCheck(string Title, string Detail, bool NeedsAttention = false);
public sealed record DeviceDiagnosticsResult(IReadOnlyList<DeviceCheck> Checks, bool FirmwareWarning);

public sealed class DeviceDiagnostics(
    Func<CancellationToken, Task<HidDeviceIdentity?>> identity,
    Func<IReadOnlyList<VJoyDeviceInfo>> inspectVJoy,
    Func<uint?> selectedId)
{
    public static DeviceDiagnostics Default(AutomaticVJoy vjoy) => new(ReadIdentityAsync,
        () => VJoyController.Inspect(VJoyController.DefaultLibraryPath), () => vjoy.SelectedId);

    public async Task<DeviceDiagnosticsResult> CheckAsync(CancellationToken token, Func<HidDeviceIdentity?>? ownedIdentity = null)
    {
        token.ThrowIfCancellationRequested();
        var checks = new List<DeviceCheck>();
        var firmwareWarning = false;
        try
        {
            var device = ownedIdentity?.Invoke();
            if (device is null)
            {
                try { device = await identity(token).ConfigureAwait(false); }
                catch (IOException)
                {
                    // Raw monitoring may acquire the exclusive handle while discovery is in flight.
                    device = ownedIdentity?.Invoke();
                    if (device is null) { throw; }
                }
            }
            if (device is null) { checks.Add(new("Firmware", "Connect the FEEL-VR pedals to check firmware.")); }
            else
            {
                var parts = PedalDeviceIdentity.Parse(device.SerialNumber);
                firmwareWarning = parts.Hardware != "PE0" || parts.Firmware != "017";
                checks.Add(new("Firmware", FirmwareDetail(parts), firmwareWarning));
            }
        }
        catch (Exception failure) when (Expected(failure, token))
        { checks.Add(new("Firmware", $"Could not check controller firmware: {failure.Message}", true)); }
        token.ThrowIfCancellationRequested();
        try
        {
            var devices = inspectVJoy();
            checks.Add(new("vJoy driver and SDK", "Matching vJoy 2.2.2 driver and SDK are available."));
            var remembered = selectedId();
            // Diagnostics also accepts our already-acquired device; it never acquires or sends reports.
            var available = devices.Select(device => device.Id == remembered && device.State == VJoyDeviceState.Owned ? device with { State = VJoyDeviceState.Free } : device).ToArray();
            var id = VJoyDeviceSelection.Select(available, remembered);
            checks.Add(new("Virtual pedal device", remembered.HasValue ? $"vJoy {id} · remembered device for game bindings" : $"vJoy {id} · selected automatically when live input starts"));
            checks.Add(new("Pedal axes", "X = throttle · Y = brake · Z = clutch. Configuration verified automatically."));
        }
        catch (Exception failure) when (Expected(failure, token))
        { checks.Add(new("Virtual output setup", failure.Message, true)); }
        return new(checks, firmwareWarning);
    }

    public static string FirmwareDetail(PedalDeviceIdentity identity) => identity switch
    {
        { Hardware: "PE0", Firmware: "017" } => "PE0 · firmware 017 · native calibration supported",
        { Hardware: "PE0" } when int.TryParse(identity.Firmware, out var version) && version < 17 =>
            $"Firmware {identity.Firmware} · update to 017 in Settings. Native calibration requires 017.",
        { Hardware: "PE0" } => $"Firmware {identity.Firmware} is not validated. Native calibration requires 017. Check with FEEL-VR before changing firmware.",
        _ => $"Hardware {identity.Hardware} is not validated for native calibration. Contact FEEL-VR for compatible firmware."
    };

    private static async Task<HidDeviceIdentity?> ReadIdentityAsync(CancellationToken token)
    {
        var devices = new WindowsHidInputSource().Find(PedalInputReport.VendorId, PedalInputReport.ProductId);
        if (devices.Count == 0) { return null; }
        var collections = devices.Where(device => device.FeatureReportLength > 0).ToArray();
        if (collections.Length != 1) { throw new IOException("Connect one FEEL-VR controller to check its firmware."); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await using var connection = new WindowsHidFeatureConnection(collections[0]);
        try { return await connection.ReadIdentityAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException failure) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Controller identity read timed out.", failure); }
    }

    private static bool Expected(Exception failure, CancellationToken token) => failure is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TimeoutException
        || failure is OperationCanceledException && !token.IsCancellationRequested;
}
