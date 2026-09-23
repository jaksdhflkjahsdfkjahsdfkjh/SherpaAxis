using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SherpaAxis.Devices;

[SupportedOSPlatform("windows")]
public sealed class WindowsHidInputSource : IInputReportSource
{
    private const uint SharedReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint GenericRead = 0x80000000;
    private const uint Overlapped = 0x40000000;
    private const int HidParserSuccess = 0x00110000;
    private const uint ConfigurationBufferTooSmall = 0x1A;

    public IReadOnlyList<HidDeviceInfo> Find(ushort vendorId, ushort productId)
    {
        var match = string.Create(CultureInfo.InvariantCulture, $"vid_{vendorId:x4}&pid_{productId:x4}");
        var collections = new List<HidDeviceInfo>();
        foreach (var path in GetPaths().Where(path => path.Contains(match, StringComparison.OrdinalIgnoreCase)))
        {
            using var handle = HidNative.CreateFileW(path, 0, SharedReadWrite, 0, OpenExisting, 0, 0);
            if (handle.IsInvalid)
            {
                throw NativeError("Cannot inspect HID collection");
            }

            var attributes = new HidNative.Attributes { Size = Marshal.SizeOf<HidNative.Attributes>() };
            if (!HidNative.HidD_GetAttributes(handle, ref attributes))
            {
                throw NativeError("Cannot read HID attributes");
            }

            if (attributes.VendorId != vendorId || attributes.ProductId != productId)
            {
                continue;
            }

            if (!HidNative.HidD_GetPreparsedData(handle, out var data))
            {
                throw NativeError("Cannot read HID collection capabilities");
            }

            try
            {
                if (HidNative.HidP_GetCaps(data, out var caps) != HidParserSuccess)
                {
                    throw new IOException("Invalid HID preparsed data.");
                }

                collections.Add(new HidDeviceInfo(path, vendorId, productId, attributes.Version,
                    caps.UsagePage, caps.Usage, caps.InputReportLength,
                    caps.OutputReportLength, caps.FeatureReportLength));
            }
            finally
            {
                HidNative.HidD_FreePreparsedData(data);
            }
        }

        return collections;
    }

    public IInputReportConnection Open(HidDeviceInfo collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (collection.InputReportLength == 0)
        {
            throw new ArgumentException("Collection has no input reports.", nameof(collection));
        }

        var handle = HidNative.CreateFileW(collection.Path, GenericRead, SharedReadWrite, 0, OpenExisting, Overlapped, 0);
        if (handle.IsInvalid)
        {
            var error = NativeError("Cannot open HID input collection");
            handle.Dispose();
            throw error;
        }

        try
        {
            return new InputConnection(new FileStream(handle, FileAccess.Read, collection.InputReportLength, isAsync: true));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string[] GetPaths()
    {
        HidNative.HidD_GetHidGuid(out var guid);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var status = HidNative.CM_Get_Device_Interface_List_SizeW(out var length, in guid, null, 0);
            if (status != 0)
            {
                throw new IOException($"HID interface sizing failed (CONFIGRET 0x{status:X}).");
            }

            if (length is 0 or > 1_048_576)
            {
                throw new IOException("Invalid HID interface list size.");
            }

            var buffer = new char[length];
            status = HidNative.CM_Get_Device_Interface_ListW(in guid, null, buffer, length, 0);
            if (status == ConfigurationBufferTooSmall)
            {
                continue;
            }

            if (status != 0)
            {
                throw new IOException($"HID enumeration failed (CONFIGRET 0x{status:X}).");
            }

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        throw new IOException("HID interface list changed repeatedly during discovery; retry.");
    }

    private static IOException NativeError(string message)
    {
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        return new IOException($"{message} (Win32 {error.NativeErrorCode}): {error.Message}", error);
    }

    private sealed class InputConnection(FileStream stream) : IInputReportConnection
    {
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            stream.ReadAsync(buffer, cancellationToken);

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
