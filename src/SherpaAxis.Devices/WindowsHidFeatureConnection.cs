using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SherpaAxis.Devices;

[SupportedOSPlatform("windows")]
public sealed class WindowsHidFeatureConnection : IFeatureReportReader, IFeatureReportWriter, IAsyncDisposable
{
    private readonly SafeFileHandle handle;
    private readonly ThreadPoolBoundHandle boundHandle;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    public HidDeviceInfo Device { get; }

    public WindowsHidFeatureConnection(HidDeviceInfo collection, bool exclusive = false)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.FeatureReportLength == 0) { throw new ArgumentException("Collection has no feature reports.", nameof(collection)); }
        Device = collection;
        handle = HidNative.CreateFileW(collection.Path, 0, exclusive ? 0u : 3u, 0, 3, 0x40000000, 0);
        if (handle.IsInvalid)
        {
            var failure = new Win32Exception(Marshal.GetLastPInvokeError());
            handle.Dispose();
            throw new IOException($"Cannot open HID feature session: {failure.Message}", failure);
        }
        try { boundHandle = ThreadPoolBoundHandle.BindHandle(handle); }
        catch { handle.Dispose(); throw; }
    }

    public Task<byte[]> ReadAsync(HidDeviceInfo collection, byte reportId, CancellationToken cancellationToken)
    {
        VerifyCollection(collection);
        var bytes = new byte[collection.FeatureReportLength];
        bytes[0] = reportId;
        return RunAsync(bytes, false, cancellationToken);
    }

    public async Task WriteAsync(HidDeviceInfo collection, ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        VerifyCollection(collection);
        if (report.Length != collection.FeatureReportLength) { throw new ArgumentException("Supply the complete padded feature report.", nameof(report)); }
        _ = await RunAsync(report.ToArray(), true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HidDeviceIdentity> ReadIdentityAsync(CancellationToken cancellationToken = default)
    {
        var manufacturer = await ReadStringAsync(0x000B01BA, cancellationToken).ConfigureAwait(false);
        var product = await ReadStringAsync(0x000B01BE, cancellationToken).ConfigureAwait(false);
        var serial = await ReadStringAsync(0x000B01C2, cancellationToken).ConfigureAwait(false);
        return new(manufacturer, product, serial);
    }

    private async Task<string> ReadStringAsync(uint code, CancellationToken cancellationToken)
    {
        var bytes = await RunAsync(new byte[512], false, cancellationToken, code).ConfigureAwait(false);
        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end >= 0 ? text[..end] : throw new InvalidDataException("HID identity string is not terminated.");
    }

    private async Task<byte[]> RunAsync(byte[] bytes, bool write, CancellationToken cancellationToken, uint? readCode = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Retain one file handle for the session; unplugging cannot redirect later writes to a replacement at the same path.
            return await WindowsHidFeatureTransfer.RunOnHandleAsync(boundHandle, handle, bytes, write, cancellationToken, readCode).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private void VerifyCollection(HidDeviceInfo collection)
    {
        if (collection != Device) { throw new ArgumentException("Feature session belongs to a different HID collection.", nameof(collection)); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; boundHandle.Dispose(); handle.Dispose(); }
        finally { gate.Release(); gate.Dispose(); }
        GC.SuppressFinalize(this);
    }
}
