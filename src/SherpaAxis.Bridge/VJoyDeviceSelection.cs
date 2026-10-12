namespace SherpaAxis.Bridge;

public enum VJoyDeviceState { Owned, Free, Busy, Missing, Unknown }

public sealed record VJoyDeviceInfo(uint Id, VJoyDeviceState State, bool HasPedalAxes, bool IsPedalsOnly);

public static class VJoyDeviceSelection
{
    // Once selected, keep the ID stable: silently changing IDs would invalidate game bindings.
    public static uint Select(IReadOnlyList<VJoyDeviceInfo> devices, uint? rememberedId)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (rememberedId.HasValue)
        {
            var remembered = devices.SingleOrDefault(device => device.Id == rememberedId);
            if (remembered is { State: VJoyDeviceState.Free, HasPedalAxes: true, IsPedalsOnly: true }) { return remembered.Id; }
            throw new IOException($"vJoy device {rememberedId} is unavailable or its configuration changed. Keeping this ID to preserve game bindings. Close its other feeder or run automatic setup.");
        }
        return devices.OrderBy(device => device.Id).FirstOrDefault(device => device is
        { State: VJoyDeviceState.Free, HasPedalAxes: true, IsPedalsOnly: true })?.Id
            ?? throw new IOException("No free pedal-only vJoy device is configured. Run automatic setup in Settings.");
    }
}
