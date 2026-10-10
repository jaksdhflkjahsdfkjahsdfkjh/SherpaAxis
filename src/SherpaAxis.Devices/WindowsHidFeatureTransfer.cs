using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SherpaAxis.Devices;

[SupportedOSPlatform("windows")]
internal static class WindowsHidFeatureTransfer
{
    private const uint SharedReadWrite = 3;
    private const uint OpenExisting = 3;
    private const uint Overlapped = 0x40000000;

    internal static async Task<byte[]> RunAsync(HidDeviceInfo collection, byte[] buffer, bool write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        cancellationToken.ThrowIfCancellationRequested();
        if (collection.FeatureReportLength == 0 || buffer.Length != collection.FeatureReportLength)
        {
            throw new ArgumentException("Feature buffer must match the collection's feature report length.", nameof(buffer));
        }

        using var handle = HidNative.CreateFileW(collection.Path, 0, SharedReadWrite, 0, OpenExisting, Overlapped, 0);
        if (handle.IsInvalid)
        {
            throw NativeError("Cannot open HID feature collection", Marshal.GetLastPInvokeError());
        }

        using var boundHandle = ThreadPoolBoundHandle.BindHandle(handle);
        return await RunOnHandleAsync(boundHandle, handle, buffer, write, cancellationToken).ConfigureAwait(false);
    }

    internal static Task<byte[]> RunOnHandleAsync(ThreadPoolBoundHandle boundHandle, SafeFileHandle handle,
        byte[] buffer, bool write, CancellationToken cancellationToken, uint? readControlCode = null) =>
        new FeatureOperation(boundHandle, handle, buffer, write, readControlCode, cancellationToken).Start();

    private static IOException NativeError(string message, int code)
    {
        var error = new Win32Exception(code);
        return new IOException($"{message} (Win32 {code}): {error.Message}", error);
    }

    private sealed unsafe class FeatureOperation
    {
        private const uint GetFeatureControlCode = 0x000B0192;
        // HID_IN_CTL_CODE(100), input buffer only; successful SET_FEATURE returns no output bytes.
        private const uint SetFeatureControlCode = 0x000B0191;
        private const int IoPending = 997;
        private const int OperationAborted = 995;
        private readonly object gate = new();
        private readonly ThreadPoolBoundHandle boundHandle;
        private readonly SafeFileHandle handle;
        private readonly byte[] buffer;
        private readonly bool write;
        private readonly uint? readControlCode;
        private readonly CancellationToken cancellationToken;
        private readonly TaskCompletionSource<byte[]> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly NativeOverlapped* overlapped;
        private CancellationTokenRegistration cancellation;
        private bool submitted;
        private bool completed;

        internal FeatureOperation(ThreadPoolBoundHandle boundHandle, SafeFileHandle handle, byte[] buffer, bool write,
            uint? readControlCode, CancellationToken cancellationToken)
        {
            this.boundHandle = boundHandle;
            this.handle = handle;
            this.cancellationToken = cancellationToken;
            this.buffer = buffer;
            this.write = write;
            this.readControlCode = readControlCode;
            overlapped = boundHandle.AllocateNativeOverlapped(OnCompleted, this, buffer);
        }

        internal Task<byte[]> Start()
        {
            lock (gate)
            {
                try
                {
                    cancellation = cancellationToken.UnsafeRegister(static state => ((FeatureOperation)state!).Cancel(), this);
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
                var success = write
                    ? HidNative.DeviceIoControl(handle, SetFeatureControlCode, pointer, (uint)buffer.Length, 0, 0, 0, (nint)overlapped)
                    : HidNative.DeviceIoControl(handle, readControlCode ?? GetFeatureControlCode, 0, 0, pointer, (uint)buffer.Length, 0, (nint)overlapped);
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
            ((FeatureOperation)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!).Complete(error, transferred);

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
                completion.TrySetException(NativeError(write ? "Cannot write HID feature report" : "Cannot read HID feature report", (int)error));
            }
            else if (!write && (readControlCode.HasValue ? transferred > buffer.Length : transferred != buffer.Length))
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
