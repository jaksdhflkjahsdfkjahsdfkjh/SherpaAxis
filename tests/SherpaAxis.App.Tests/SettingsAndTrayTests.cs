using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using SherpaAxis.App.ViewModels;
using Forms = System.Windows.Forms;

namespace SherpaAxis.App.Tests;

[Collection("WPF UI")]
public sealed class SettingsAndTrayTests
{
    private static readonly TrayCommand[] ExpectedTrayCommands = [TrayCommand.Show, TrayCommand.Tune, TrayCommand.Calibrate, TrayCommand.Settings, TrayCommand.Exit];
    private static readonly string[] TrayLabels = ["Open SherpaAxis", "Tune", "Calibrate", "Settings", "Exit"];
    [Fact]
    public async Task PreferencesRoundTripAndFailedOrCancelledSavesPreserveTheLastFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SherpaAxis-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        var store = new ApplicationPreferencesStore(path);
        try
        {
            Assert.Equal(new(), await store.LoadAsync(CancellationToken.None));
            var preferences = new ApplicationPreferences(MinimizeOnClose: true, StartWithWindows: true, StartInTray: true, MinimizeToTray: true);
            await store.SaveAsync(preferences, CancellationToken.None);
            Assert.Equal(preferences, await store.LoadAsync(CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new(), cancelled.Token));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(SchemaVersion: 5), CancellationToken.None));
            Assert.Equal(preferences, await store.LoadAsync(CancellationToken.None));
            Assert.Single(Directory.EnumerateFiles(directory));
            await File.WriteAllTextAsync(path, "{ invalid");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
            await File.WriteAllTextAsync(path, "{\"SchemaVersion\":2}");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
    }

    [Fact]
    public void StartupCommandQuotesTheExecutableAndRejectsInvalidPaths()
    {
        Assert.Equal("\"C:\\A folder\\SherpaAxis.App.exe\" --startup", WindowsStartupRegistration.BuildCommand(@"C:\A folder\SherpaAxis.App.exe"));
        Assert.Throws<ArgumentException>(() => WindowsStartupRegistration.BuildCommand("SherpaAxis.App.exe"));
        Assert.Throws<ArgumentException>(() => WindowsStartupRegistration.BuildCommand("C:\\bad\"path\\app.exe"));
        Assert.Throws<ArgumentException>(() => WindowsStartupRegistration.BuildCommand("C:\\" + new string('a', 260) + "\\app.exe"));
    }

    internal sealed class FakeDesktop
    {
        internal FakeStore Store { get; } = new();
        internal FakeStartup Startup { get; } = new();
        internal FakeTray Tray { get; } = new();
        internal DesktopServices Services => new(Store, Startup, _ => Tray);
    }
    internal sealed class FakeStore : IApplicationPreferencesStore
    {
        internal ApplicationPreferences Value { get; set; } = new();
        internal bool Fail { get; set; }
        internal Task? WriteBarrier { get; set; }
        public Task<ApplicationPreferences> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
        public async Task SaveAsync(ApplicationPreferences preferences, CancellationToken cancellationToken)
        {
            if (Fail) { throw new IOException("Test settings disk failure"); }
            if (WriteBarrier is { } pending) { await pending.WaitAsync(cancellationToken); }
            Value = preferences;
        }
    }
    internal sealed class FakeStartup : IStartupRegistration
    {
        internal bool Enabled { get; set; }
        internal bool Fail { get; set; }
        internal List<bool> Changes { get; } = [];
        public bool IsEnabled => Enabled;
        public void SetEnabled(bool enabled)
        { if (Fail) { throw new UnauthorizedAccessException("Test startup permission failure"); } Enabled = enabled; Changes.Add(enabled); }
    }
    internal sealed class FakeTray : IApplicationTray
    {
        internal bool Disposed { get; private set; }
        public event Action<TrayCommand>? CommandRequested;
        public event Action<ProfileTab>? ProfileRequested;
        internal void Command(TrayCommand command) => CommandRequested?.Invoke(command);
        internal void Profile(ProfileTab profile) => ProfileRequested?.Invoke(profile);
        public void Dispose() { Disposed = true; }
    }
}
