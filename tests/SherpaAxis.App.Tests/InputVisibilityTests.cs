using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Win32;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services.Output;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Bridge;

namespace SherpaAxis.App.Tests;

public sealed class InputVisibilityTests
{
    private const string Pedal = @"HID\VID_0483&PID_A2EA&Col01\pedals";
    private const string Other = @"HID\VID_346E&PID_0000&MI_02\wheel";

    [Fact]
    public async Task HidingPreservesOtherDevicesAndRestoresOnlyOwnedChanges()
    {
        using var fake = new FakeHide();
        fake.Active = true; fake.Hidden.Add(Other); fake.Applications.Add(@"C:\Other\feeder.exe");
        Assert.True((await fake.Service.ConfigureAsync(true, CancellationToken.None)).Ready);
        Assert.Contains(Pedal, fake.Hidden);
        Assert.Contains(Other, fake.Hidden);
        Assert.Equal(2, fake.Hidden.Count);
        Assert.True(File.Exists(fake.StatePath));
        await fake.Service.ReleaseAsync(CancellationToken.None);
        Assert.Equal(Other, Assert.Single(fake.Hidden));
        Assert.Equal(@"C:\Other\feeder.exe", Assert.Single(fake.Applications));
        Assert.True(fake.Active);
        Assert.False(File.Exists(fake.StatePath));
    }

    [Fact]
    public async Task HidingEnablesAnEmptyConfigurationAndRestoresItAfterExit()
    {
        using var fake = new FakeHide();
        await fake.Service.ConfigureAsync(true, CancellationToken.None);
        Assert.True(fake.Active);
        Assert.Equal(Pedal, Assert.Single(fake.Hidden));
        Assert.Contains(fake.Application, fake.Applications);
        await fake.Service.ReleaseAsync(CancellationToken.None);
        Assert.False(fake.Active);
        Assert.Empty(fake.Hidden);
        Assert.Empty(fake.Applications);
    }

    [Fact]
    public async Task ExistingPedalHideAndWhitelistEntriesAreNotClaimedOrRemoved()
    {
        using var fake = new FakeHide();
        fake.Active = true; fake.Hidden.Add(Pedal); fake.Applications.Add(fake.Application);
        await fake.Service.ConfigureAsync(true, CancellationToken.None);
        await fake.Service.ReleaseAsync(CancellationToken.None);
        Assert.Contains(Pedal, fake.Hidden);
        Assert.Contains(fake.Application, fake.Applications);
        Assert.True(fake.Active);
    }

    [Fact]
    public async Task InvertedAndDisabledSharedConfigurationsArePreserved()
    {
        using var fake = new FakeHide();
        fake.Inverted = true;
        Assert.False((await fake.Service.ConfigureAsync(true, CancellationToken.None)).Ready);
        Assert.Empty(fake.Applications);
        Assert.Empty(fake.Hidden);
        fake.Inverted = false; fake.Hidden.Add(Other);
        Assert.False((await fake.Service.ConfigureAsync(true, CancellationToken.None)).Ready);
        Assert.False(fake.Active);
        Assert.Equal(Other, Assert.Single(fake.Hidden));
        await fake.Service.ReleaseAsync(CancellationToken.None);
        Assert.Empty(fake.Applications);
        Assert.Equal(Other, Assert.Single(fake.Hidden));
    }

    [Fact]
    public async Task PartialWritesCanBeRecoveredByANewProcessFromTheDurableRecord()
    {
        using var fake = new FakeHide();
        fake.FailAfterHide = true;
        await Assert.ThrowsAsync<IOException>(() => fake.Service.ConfigureAsync(true, CancellationToken.None));
        Assert.True(fake.JournalExistedBeforeHide);
        Assert.Contains(Pedal, fake.Hidden);
        using var restarted = fake.CreateService();
        await restarted.ReleaseAsync(CancellationToken.None);
        Assert.Empty(fake.Hidden);
        Assert.Empty(fake.Applications);
        Assert.False(File.Exists(fake.StatePath));
    }

    [Fact]
    public async Task ReconnectsTrackNewInstancesAndOutputFailureKeepsExistingProtection()
    {
        using var fake = new FakeHide();
        await fake.Service.ConfigureAsync(true, CancellationToken.None);
        fake.PedalInstance = @"HID\VID_0483&PID_A2EA&Col01\new-port";
        await fake.Service.ConfigureAsync(true, CancellationToken.None);
        Assert.Equal(2, fake.Hidden.Count);
        fake.OutputReady = false;
        Assert.False((await fake.Service.ConfigureAsync(true, CancellationToken.None)).Ready);
        Assert.Equal(2, fake.Hidden.Count);
        await fake.Service.ConfigureAsync(false, CancellationToken.None);
        Assert.Empty(fake.Hidden);
        Assert.False(fake.Active);
    }

    [Fact]
    public async Task InvalidOwnershipAndInventoryCannotHideUnrelatedCollections()
    {
        using var fake = new FakeHide();
        await File.WriteAllTextAsync(fake.StatePath, JsonSerializer.Serialize(new HidHideOwnership(fake.Application, true, true, [Other]), JsonSerializerOptions.Web));
        await Assert.ThrowsAsync<InvalidDataException>(() => fake.Service.ReleaseAsync(CancellationToken.None));
        Assert.Equal(0, fake.Calls);
        Assert.Throws<InvalidDataException>(() => HidHideVisibility.PedalInstances("not JSON"));
        Assert.False(HidHideVisibility.IsPedalInstance(@"USB\VID_0483&PID_A2EA\controller"));
        Assert.False(HidHideVisibility.IsPedalInstance(@"HID\VID_0483&PID_A2EA&Col02\configuration"));
        Assert.False(HidHideVisibility.IsPedalInstance(@"HID\VID_0483&PID_A2EA0&Col01\other"));
    }

    [Fact]
    public void ControllerNamingRestoresPreviousNamesAndNeverRenamesMultipleVJoySlots()
    {
        var path = @"Software\SherpaAxis.Tests\Names-" + Guid.NewGuid().ToString("N");
        using var root = Registry.CurrentUser.CreateSubKey(path);
        try
        {
            var name = new VJoyDeviceName(root, "OEM", "Backup");
            using var oem = root.CreateSubKey("OEM");
            oem.SetValue("OEMName", "My previous controller name");
            VJoyDeviceInfo[] single = [new(2, VJoyDeviceState.Free, true, true)];
            Assert.True(name.Apply(single, 2));
            Assert.Equal(VJoyDeviceName.DisplayName, oem.GetValue("OEMName"));
            Assert.True(name.Apply(single, 2));
            VJoyDeviceInfo[] multiple = [single[0], new(1, VJoyDeviceState.Busy, true, false)];
            Assert.False(name.Apply(multiple, 2));
            Assert.Equal("My previous controller name", oem.GetValue("OEMName"));
            name.Apply(single, 2);
            oem.SetValue("OEMName", "Changed by user");
            name.Restore();
            Assert.Equal("Changed by user", oem.GetValue("OEMName"));
            oem.DeleteValue("OEMName");
            name.Apply(single, 2); name.Restore();
            Assert.Null(oem.GetValue("OEMName"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path); }
    }

    internal sealed class FakeHide : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-visibility-" + Guid.NewGuid().ToString("N"));
        internal string StatePath => Path.Combine(directory, "visibility.json");
        internal string Application => Path.Combine(directory, "SherpaAxis.App.exe");
        internal HashSet<string> Hidden { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Applications { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal string PedalInstance { get; set; } = Pedal;
        internal bool Active { get; set; }
        internal bool Inverted { get; set; }
        internal bool OutputReady { get; set; } = true;
        internal bool FailAfterHide { get; set; }
        internal bool JournalExistedBeforeHide { get; private set; }
        internal int Calls { get; private set; }
        internal HidHideVisibility Service { get; }
        internal FakeHide() { Directory.CreateDirectory(directory); Service = CreateService(); }
        internal HidHideVisibility CreateService() => new(StatePath, Application, Run, () => OutputReady);
        private Task<string> Run(IReadOnlyList<string> args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            var output = new List<string>();
            for (var index = 0; index < args.Count; index++)
            {
                switch (args[index])
                {
                    case "--inv-state": output.Add(Inverted ? "--inv-on" : "--inv-off"); break;
                    case "--cloak-state": output.Add(Active ? "--cloak-on" : "--cloak-off"); break;
                    case "--cloak-on": Active = true; break;
                    case "--cloak-off": Active = false; break;
                    case "--app-list": output.AddRange(Applications.Select(path => $"--app-reg \"{path}\"")); break;
                    case "--dev-list": output.AddRange(Hidden.Select(path => $"--dev-hide \"{path}\"")); break;
                    case "--app-reg": Applications.Add(args[++index]); break;
                    case "--app-unreg": Applications.Remove(args[++index]); break;
                    case "--dev-unhide": Hidden.Remove(args[++index]); break;
                    case "--dev-hide":
                        JournalExistedBeforeHide = File.Exists(StatePath);
                        Hidden.Add(args[++index]);
                        if (FailAfterHide) { FailAfterHide = false; throw new IOException("Partial configuration failure"); }
                        break;
                    case "--dev-gaming":
                        output.Add(JsonSerializer.Serialize(new[] { new { devices = new[]
                        {
                            new { present = true, gamingDevice = true, deviceInstancePath = PedalInstance },
                            new { present = true, gamingDevice = false, deviceInstancePath = @"HID\VID_0483&PID_A2EA&Col02\config" },
                            new { present = true, gamingDevice = true, deviceInstancePath = Other },
                            new { present = true, gamingDevice = true, deviceInstancePath = @"HID\HIDCLASS\vjoy" }
                        } } })); break;
                    default: throw new InvalidOperationException("Unexpected HidHide command: " + args[index]);
                }
            }
            return Task.FromResult(string.Join('\n', output));
        }
        public void Dispose() { Service.Dispose(); Directory.Delete(directory, true); }
    }
}

[Collection("WPF UI")]
public sealed class InputVisibilityUiTests
{
    [Fact]
    public Task VisibilityPreferenceAppliesImmediatelyAndExitReleasesOwnedEntries() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        using var fake = new InputVisibilityTests.FakeHide();
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using (var main = new MainWindow(new(), desktopServices: desktop.Services with { InputVisibility = fake.Service },
            profileLibrary: new(Path.Combine(Path.GetDirectoryName(fake.StatePath)!, "profiles"))))
        {
            await main.InitializeForLaunchAsync();
            var model = Assert.IsType<DashboardViewModel>(main.DataContext);
            Assert.True(Assert.IsType<CheckBox>(main.FindName("HidePhysicalPedalsSetting")).IsChecked);
            Assert.NotEmpty(fake.Hidden);
            model.Preferences.HidePhysicalPedals = false;
            await main.SavePreferencesAsync();
            Assert.False(desktop.Store.Value.HidePhysicalPedals);
            Assert.Empty(fake.Hidden);
            model.Preferences.HidePhysicalPedals = true;
            await main.SavePreferencesAsync();
            Assert.NotEmpty(fake.Hidden);
        }
        Assert.Empty(fake.Hidden);
        Assert.Empty(fake.Applications);
    });
}

public sealed class HidHideConfigurationFactAttribute : FactAttribute
{
    public HidHideConfigurationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHERPAAXIS_HIDHIDE_TEST") != "1")
        { Skip = "Opt-in installed HidHide check: registers and removes a temporary application without hiding physical devices."; }
    }
}

public sealed class NativeHidHideTests
{
    [HidHideConfigurationFact]
    public async Task ReleasedCliSupportsQuotedPathsAndPreservesDriverConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis native visibility " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string[] inspect = ["--inv-state", "--cloak-state", "--dev-list", "--app-list"];
        var before = await WindowsHidHide.RunAsync(inspect, CancellationToken.None);
        using var visibility = new HidHideVisibility(Path.Combine(directory, "state.json"), Path.Combine(directory, "SherpaAxis.App.exe"),
            (args, token) => args.Count == 1 && args[0] == "--dev-gaming" ? Task.FromResult("[]") : WindowsHidHide.RunAsync(args, token), () => true);
        try
        {
            Assert.True((await visibility.ConfigureAsync(true, CancellationToken.None)).Ready);
            Assert.True(File.Exists(Path.Combine(directory, "state.json")));
        }
        finally
        {
            await visibility.ReleaseAsync(CancellationToken.None);
            Assert.Equal(before, await WindowsHidHide.RunAsync(inspect, CancellationToken.None));
            Directory.Delete(directory, true);
        }
    }
}

