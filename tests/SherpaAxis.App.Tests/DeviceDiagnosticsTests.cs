using System.IO;
using SherpaAxis.App.Services;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Bridge;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Tests;

public sealed class DeviceDiagnosticsTests
{
    [Fact]
    public void NativeAxisCodesDoNotMistakeMissingAxesOrSdkFailuresForEnabledAxes()
    {
        Assert.True(VJoyController.DecodeAxisPresence(1));
        Assert.False(VJoyController.DecodeAxisPresence(0));
        Assert.False(VJoyController.DecodeAxisPresence(-10));
        Assert.Throws<IOException>(() => VJoyController.DecodeAxisPresence(-2));
        Assert.Throws<IOException>(() => VJoyController.DecodeAxisPresence(-9));
    }

    private static VJoyDeviceInfo[] Devices(params VJoyDeviceInfo[] values) => Enumerable.Range(1, 16)
        .Select(id => values.SingleOrDefault(value => value.Id == id) ?? new((uint)id, VJoyDeviceState.Missing, false, false)).ToArray();

    [Fact]
    public void SelectionSkipsBusyDevicesAndOtherControllerConfigurations()
    {
        var devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Busy, true, true), new(2, VJoyDeviceState.Free, true, false), new(3, VJoyDeviceState.Free, true, true));
        Assert.Equal(3u, VJoyDeviceSelection.Select(devices, null));
        Assert.Throws<IOException>(() => VJoyDeviceSelection.Select(devices, 1));
        Assert.Throws<IOException>(() => VJoyDeviceSelection.Select(devices, 2));
        Assert.Throws<IOException>(() => VJoyDeviceSelection.Select(devices, 4));
    }

    [Fact]
    public void AutomaticConnectionPersistsTheAcquiredIdAndDoesNotSwitchBindingsOnRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-vjoy-selection-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "device.txt");
        var devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Busy, true, true), new(2, VJoyDeviceState.Free, true, true));
        var connected = new List<uint>();
        var controller = new FakeController();
        try
        {
            var first = new AutomaticVJoy(path, () => devices, id => { connected.Add(id); return controller; });
            using (first.Connect()) { Assert.Equal(2u, first.SelectedId); }
            devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Free, true, true), new(2, VJoyDeviceState.Free, true, true));
            var restart = new AutomaticVJoy(path, () => devices, id => { connected.Add(id); return new FakeController(); });
            using (restart.Connect()) { Assert.Equal(2u, restart.SelectedId); }
            devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Free, true, true), new(2, VJoyDeviceState.Busy, true, true));
            Assert.Throws<IOException>(() => restart.Connect());
            Assert.Equal(new uint[] { 2, 2 }, connected);
            Assert.Equal("2", File.ReadAllText(path));
            Assert.Equal(0, controller.Writes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FailedAcquisitionDoesNotRememberAnIdAndInvalidSavedIdsAreNotSilentlyReplaced()
    {
        var path = Path.Combine(Path.GetTempPath(), "SherpaAxis-vjoy-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            var output = new AutomaticVJoy(path, () => Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Free, true, true)), _ => throw new IOException("Acquisition raced"));
            Assert.Throws<IOException>(() => output.Connect());
            Assert.False(File.Exists(path));
            File.WriteAllText(path, "17");
            Assert.Throws<InvalidDataException>(() => new AutomaticVJoy(path, () => [], _ => new FakeController()).Connect());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SetupCreatesOnlyAnUnusedSlotAndVerifiesTheDriverAfterCreation()
    {
        var devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Busy, true, true));
        var created = new List<uint>();
        var setup = new VJoySetup(() => devices, (id, _) =>
        { created.Add(id); devices = Devices(devices[0], new(id, VJoyDeviceState.Free, true, true)); return Task.CompletedTask; });
        var result = await setup.ConfigureAsync(null, CancellationToken.None);
        Assert.True(result.Ready);
        Assert.Equal(new uint[] { 2 }, created);
        Assert.True((await setup.ConfigureAsync(null, CancellationToken.None)).Ready);
        Assert.Single(created);
    }

    [Fact]
    public async Task SetupPreservesBusyOrChangedRememberedDevicesAndHandlesFullSlots()
    {
        var created = 0;
        var devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Busy, true, true), new(2, VJoyDeviceState.Free, true, true));
        var setup = new VJoySetup(() => devices, (_, _) => { created++; return Task.CompletedTask; });
        Assert.False((await setup.ConfigureAsync(1, CancellationToken.None)).Ready);
        devices = Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Free, false, false));
        Assert.False((await setup.ConfigureAsync(1, CancellationToken.None)).Ready);
        devices = Enumerable.Range(1, 16).Select(id => new VJoyDeviceInfo((uint)id, VJoyDeviceState.Busy, true, true)).ToArray();
        var full = await setup.ConfigureAsync(null, CancellationToken.None);
        Assert.False(full.Ready);
        Assert.False(full.RestartRequired);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task SetupDoesNotTreatHelperSuccessAsSuccessfulConfiguration()
    {
        var setup = new VJoySetup(() => Devices(new VJoyDeviceInfo(1, VJoyDeviceState.Free, true, false)), (_, _) => Task.CompletedTask);
        var result = await setup.ConfigureAsync(null, CancellationToken.None);
        Assert.False(result.Ready);
        Assert.True(result.RestartRequired);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.ConfigureAsync(null, new CancellationToken(true)));
    }

    [Fact]
    public async Task SetupRecreatesTheRememberedSlotRatherThanChangingTheDeviceId()
    {
        var created = new List<uint>();
        var devices = Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Free, true, true));
        var setup = new VJoySetup(() => devices, (id, _) =>
        { created.Add(id); devices = Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Free, true, true), new(id, VJoyDeviceState.Free, true, true)); return Task.CompletedTask; });
        Assert.True((await setup.ConfigureAsync(3, CancellationToken.None)).Ready);
        Assert.Equal(new uint[] { 3 }, created);
    }

    [Fact]
    public void FailureToPersistTheDeviceSelectionReleasesTheAcquiredController()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-vjoy-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var controller = new FakeController();
        try
        {
            var output = new AutomaticVJoy(directory, () => Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Free, true, true)), _ => controller);
            Assert.Throws<UnauthorizedAccessException>(() => output.Connect());
            Assert.True(controller.Disposed);
            Assert.Equal(0, controller.Writes);
        }
        finally { Directory.Delete(directory); }
    }

    [Theory]
    [InlineData("PE001700", false, "supported")]
    [InlineData("PE001600", true, "update to 017")]
    [InlineData("PE001800", true, "before changing firmware")]
    [InlineData("WE001700", true, "Hardware WE0")]
    public async Task FirmwareChecksDistinguishOlderAndNewerVersions(string serial, bool warning, string detail)
    {
        var reads = 0;
        var diagnostics = new DeviceDiagnostics(_ => { reads++; return Task.FromResult<HidDeviceIdentity?>(new("FEEL-VR", "Pedals", serial)); },
            () => Devices(new VJoyDeviceInfo(2, VJoyDeviceState.Owned, true, true)), () => 2);
        var result = await diagnostics.CheckAsync(CancellationToken.None);
        Assert.Equal(warning, result.FirmwareWarning);
        Assert.Contains(detail, result.Checks[0].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Checks.Skip(1), check => check.NeedsAttention);
        Assert.Contains(result.Checks, check => check.Detail.Contains("vJoy 2", StringComparison.Ordinal));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task MissingDriverAndUnreadableIdentityRemainActionableWithoutAnyAcquisition()
    {
        var diagnostics = new DeviceDiagnostics(_ => Task.FromException<HidDeviceIdentity?>(new IOException("Identity missing")),
            () => throw new DllNotFoundException("Install vJoy"), () => null);
        var result = await diagnostics.CheckAsync(CancellationToken.None);
        Assert.Equal(2, result.Checks.Count);
        Assert.All(result.Checks, check => Assert.True(check.NeedsAttention));
        var missing = new DeviceDiagnostics(_ => Task.FromResult<HidDeviceIdentity?>(null), () => Devices(), () => null);
        var disconnected = await missing.CheckAsync(CancellationToken.None);
        Assert.Contains("Connect the FEEL-VR", disconnected.Checks[0].Detail, StringComparison.Ordinal);
        Assert.Contains(disconnected.Checks, check => check.Title == "Virtual output setup" && check.NeedsAttention);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => missing.CheckAsync(new CancellationToken(true)));
    }

    private sealed class FakeController : IVirtualController
    {
        public int Writes { get; private set; }
        public bool Disposed { get; private set; }
        public void Write(PedalOutput output) => Writes++;
        public void Dispose() => Disposed = true;
    }
}

[Collection("WPF UI")]
public sealed class DeviceDiagnosticsUiTests
{

}
