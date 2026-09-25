using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SherpaAxis.Devices;

[SupportedOSPlatform("windows")]
public sealed class WindowsHidFeatureReader : IFeatureReportReader
{
    private const uint SharedReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint Overlapped = 0x40000000;

    public async Task<byte[]> ReadAsync(HidDeviceInfo collection, byte reportId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        cancellationToken.ThrowIfCancellationRequested();
        if (collection.FeatureReportLength == 0)
        {
            throw new ArgumentException("Collection has no feature reports.", nameof(collection));
        }

        using var handle = HidNative.CreateFileW(collection.Path, 0, SharedReadWrite, 0, OpenExisting, Overlapped, 0);
        if (handle.IsInvalid)
        {
            throw NativeError("Cannot open HID feature collection", Marshal.GetLastPInvokeError());
        }

        using var boundHandle = ThreadPoolBoundHandle.BindHandle(handle);
        var operation = new FeatureRead(boundHandle, handle, collection.FeatureReportLength, reportId, cancellationToken);
        return await operation.Start().ConfigureAwait(false);
    }

    private static IOException NativeError(string message, int code)
    {
        var error = new Win32Exception(code);
        return new IOException($"{message} (Win32 {code}): {error.Message}", error);
    }

    private sealed unsafe class FeatureRead
    {
        // HID_OUT_CTL_CODE(100): GET_FEATURE only, not SET_FEATURE.
        private const uint GetFeatureControlCode = 0x000B0192;
        private const int IoPending = 997;
        private const int OperationAborted = 995;
        private readonly object gate = new();
        private readonly ThreadPoolBoundHandle boundHandle;
        private readonly SafeFileHandle handle;
        private readonly byte[] buffer;
        private readonly CancellationToken cancellationToken;
        private readonly TaskCompletionSource<byte[]> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly NativeOverlapped* overlapped;
        private CancellationTokenRegistration cancellation;
        private bool submitted;
        private bool completed;

        internal FeatureRead(ThreadPoolBoundHandle boundHandle, SafeFileHandle handle, int length, byte reportId,
            CancellationToken cancellationToken)
        {
            this.boundHandle = boundHandle;
            this.handle = handle;
            this.cancellationToken = cancellationToken;
            buffer = new byte[length];
            buffer[0] = reportId;
            overlapped = boundHandle.AllocateNativeOverlapped(OnCompleted, this, buffer);
        }

        internal Task<byte[]> Start()
        {
            lock (gate)
            {
                try
                {
                    cancellation = cancellationToken.UnsafeRegister(static state => ((FeatureRead)state!).Cancel(), this);
                }
                catch
                {
                    boundHandle.FreeNativeOverlapped(overlapped);
                    throw;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    Complete(OperationAborted, 0);
                    return completion.Task;
                }

                var pointer = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);
                var success = HidNative.DeviceIoControl(handle, GetFeatureControlCode, 0, 0,
                    pointer, (uint)buffer.Length, 0, (nint)overlapped);
                var error = success ? 0 : Marshal.GetLastPInvokeError();
                if (!success && error != IoPending)
                {
                    Complete((uint)error, 0);
                }
                else
                {
                    submitted = true;
                }
            }

            return completion.Task;
        }

        private void Cancel()
        {
            lock (gate)
            {
                if (submitted && !completed)
                {
                    HidNative.CancelIoEx(handle, (nint)overlapped);
                }
            }
        }

        private static void OnCompleted(uint error, uint transferred, NativeOverlapped* overlapped) =>
            ((FeatureRead)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!).Complete(error, transferred);

        private void Complete(uint error, uint transferred)
        {
            lock (gate)
            {
                completed = true;
                cancellation.Unregister();
                // Cancellation only requests completion; keep pinned memory until Windows completes the I/O.
                boundHandle.FreeNativeOverlapped(overlapped);
            }

            if (error == OperationAborted && cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            else if (error != 0)
            {
                completion.TrySetException(NativeError("Cannot read HID feature report", (int)error));
            }
            else if (transferred != buffer.Length)
            {
                completion.TrySetException(new InvalidDataException($"Expected {buffer.Length} feature bytes; received {transferred}."));
            }
            else
            {
                completion.TrySetResult(buffer);
            }
        }
    }
}
