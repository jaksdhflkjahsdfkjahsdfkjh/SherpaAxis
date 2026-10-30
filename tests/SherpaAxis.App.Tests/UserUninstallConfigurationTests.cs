using Microsoft.Win32;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Output;
using SherpaAxis.App.Services.Setup;
using SherpaAxis.Bridge;

namespace SherpaAxis.App.Tests;

public sealed class UserUninstallConfigurationTests
{
    [Fact]
    public void InputAndStartupCleanupTargetsOnlyTheResolvedUsersRegistry()
    {
        var path = @"Software\SherpaAxis.Tests\UserCleanup-" + Guid.NewGuid().ToString("N");
        using var root = Registry.CurrentUser.CreateSubKey(path);
        try
        {
            using var desktop = root.CreateSubKey("DesktopUser");
            using var administrator = root.CreateSubKey("Administrator");
            const string executable = @"C:\SherpaAxis\SherpaAxis.App.exe";
            VJoyDeviceInfo[] devices = [new(2, VJoyDeviceState.Free, true, true)];
            VJoyDeviceName.Default(desktop).Apply(devices, 2);
            VJoyDeviceName.Default(administrator).Apply(devices, 2);
            new WindowsStartupRegistration(executable, desktop).SetEnabled(true);
            new WindowsStartupRegistration(executable, administrator).SetEnabled(true);

            VJoyDeviceName.Default(desktop).Restore();
            new WindowsStartupRegistration(executable, desktop).RemoveIfOwned();

            Assert.False(new WindowsStartupRegistration(executable, desktop).IsEnabled);
            Assert.True(new WindowsStartupRegistration(executable, administrator).IsEnabled);
            using var desktopBackup = desktop.OpenSubKey(@"Software\SherpaAxis\VirtualControllerName");
            Assert.Null(desktopBackup);
            using var adminBackup = administrator.OpenSubKey(@"Software\SherpaAxis\VirtualControllerName");
            Assert.NotNull(adminBackup);
            Assert.Equal(1, adminBackup.GetValue("Saved"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path); }
    }

    [Fact]
    public void AnotherCopyOfTheAppsStartupEntryIsPreserved()
    {
        var path = @"Software\SherpaAxis.Tests\UserStartup-" + Guid.NewGuid().ToString("N");
        using var user = Registry.CurrentUser.CreateSubKey(path);
        try
        {
            var existing = new WindowsStartupRegistration(@"C:\Another Copy\SherpaAxis.App.exe", user);
            existing.SetEnabled(true);
            new WindowsStartupRegistration(@"C:\SherpaAxis\SherpaAxis.App.exe", user).RemoveIfOwned();
            Assert.True(existing.IsEnabled);
            var account = new SavedDataUser("S-1-5-21-0-0-0-1001", @"C:\Users\DesktopUser\AppData\Local");
            Assert.Equal(@"C:\Users\DesktopUser\AppData\Local\SherpaAxis\physical-pedal-visibility.json", account.VisibilityStatePath);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path); }
    }
}
