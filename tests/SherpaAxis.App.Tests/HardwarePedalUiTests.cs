using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SherpaAxis.App.Services;
using SherpaAxis.App.ViewModels;
using SherpaAxis.Bridge;
using SherpaAxis.Devices;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Tests;

[CollectionDefinition("WPF UI", DisableParallelization = true)]
public sealed class WpfUiTestGroup;

[Collection("WPF UI")]
public sealed class HardwarePedalUiTests
{
    [Fact]
    public async Task HardwareRecoveryFileRoundTripsAndMalformedDataCannotBeUsed()
    {
        var parent = Path.Combine(Path.GetTempPath(), "SherpaAxis-hardware-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(parent, "backup.json");
        var store = new HardwareRecoveryStore(path);
        var snapshot = new HardwareRecoverySnapshot(1, DateTimeOffset.UtcNow, Device.Collection,
            Device.Original, PedalHardwareSettings.Defaults, Device.Original, SerialNumber: "PE001700");
        try
        {
            await store.WriteAsync(snapshot, CancellationToken.None);
            Assert.Equal(snapshot, await HardwareRecoveryStore.ReadAsync(path));
            await File.WriteAllTextAsync(path, "{bad json");
            await Assert.ThrowsAsync<InvalidDataException>(() => HardwareRecoveryStore.ReadAsync(path));
            var manual = snapshot with { SchemaVersion = 2, LastConfirmed = snapshot.Saved, Attempted = null, IsManualBackup = true };
            await store.WriteAsync(manual, CancellationToken.None);
            Assert.Equal(manual, await HardwareRecoveryStore.ReadAsync(path));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(manual with { SchemaVersion = 1 }, CancellationToken.None));
            Assert.Equal(manual, await HardwareRecoveryStore.ReadAsync(path));
            await store.WriteAsync(snapshot, CancellationToken.None);
            await store.ClearAsync(CancellationToken.None);
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.EnumerateFiles(parent));
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
            if (Directory.Exists(parent)) { Directory.Delete(parent); }
        }
    }

    internal static async Task OnUiAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception failure) { completion.SetException(failure); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class TransportLifetime : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ClosingController : IVirtualController
    {
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CloseCount { get; private set; }
        public PedalOutput Last { get; private set; }
        public void Write(PedalOutput output) => Last = output;
        public void Dispose() { CloseCount++; Closing.TrySetResult(); Release.Task.GetAwaiter().GetResult(); }
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        for (var i = 0; i < 100 && !ready(); i++) { await Task.Delay(5); }
        Assert.True(ready());
    }

    private static async IAsyncEnumerable<PedalMonitorEvent> EmptyInput([EnumeratorCancellation] CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        yield break;
    }

    private static PedalMonitorEvent Sample(int brake) => new(PedalConnectionState.Connected, TimeSpan.Zero, 1, new(0, brake, 0));
    private static Button FindButton(DependencyObject parent, string text) => Descendants(parent).OfType<Button>().Single(b => Equals(b.Content, text));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) { yield return descendant; }
        }
    }

    private sealed class Device : IFeatureReportReader, IFeatureReportWriter
    {
        public static HidDeviceInfo Collection { get; } = new("synthetic-pedal", 0x0483, 0xA2EA, 0x200, 0xFF00, 1, 0, 0, 17);
        public static PedalCalibration Original { get; } = new(new(100, 1000), new(200, 2000), new(300, 3000));
        public PedalCalibration State { get; private set; } = Original;
        public List<byte[]> Writes { get; } = [];
        public bool FailWrites { get; set; }
        public Task? WriteBarrier { get; set; }
        public Task<byte[]> ReadAsync(HidDeviceInfo collection, byte reportId, CancellationToken cancellationToken) => Task.FromResult(PedalCalibrationReport.Encode(State));
        public async Task WriteAsync(HidDeviceInfo collection, ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
        {
            var bytes = report.ToArray();
            Writes.Add(bytes);
            if (WriteBarrier is { } pending) { await pending.WaitAsync(cancellationToken); }
            if (FailWrites) { throw new IOException("Simulated controller restoration failure."); }
            if (bytes[0] == 3) { State = PedalCalibrationReport.Decode(bytes); }
        }
    }

    private sealed class Backup : IHardwareRecoveryStore
    {
        public Task WriteAsync(HardwareRecoverySnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
