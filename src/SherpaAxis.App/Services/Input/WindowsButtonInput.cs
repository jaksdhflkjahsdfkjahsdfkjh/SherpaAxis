using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Services.Input;

public sealed class WindowsButtonInput : IButtonStateSource
{
    private static readonly ushort[] Usages = [4, 5, 6, 8];
    private readonly HwndSource source;
    private readonly ButtonStateBuffer state = new();
    private readonly Dictionary<nint, HidButtons> devices = [];
    private readonly Dictionary<nint, HashSet<int>> keyboards = [];
    private bool disposed;
    public event Action<CorrectionButton>? Pressed { add => state.Pressed += value; remove => state.Pressed -= value; }
    public bool IsDown(CorrectionButton button) => state.IsDown(button);
    public WindowsButtonInput(HwndSource source)
    {
        this.source = source;
        var registrations = Usages.Select(usage => new RawDevice(1, usage, 0x2100, source.Handle)).ToArray();
        if (!RegisterRawInputDevices(registrations, (uint)registrations.Length, (uint)Marshal.SizeOf<RawDevice>()))
        { throw new Win32Exception(Marshal.GetLastPInvokeError(), "Keyboard/controller button input could not be registered."); }
        source.AddHook(InputMessage);
    }
    private nint InputMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0xFE && wParam == 2) { RemoveDevice(lParam); }
        if (message != 0xFF || disposed) { return 0; }
        uint size = 0;
        var headerSize = (uint)(8 + 2 * nint.Size);
        if (GetRawInputData(lParam, 0x10000003, 0, ref size, headerSize) == uint.MaxValue || size < headerSize || size > 1_048_576) { return 0; }
        var memory = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, 0x10000003, memory, ref size, headerSize) != size) { return 0; }
            var bytes = new byte[size]; Marshal.Copy(memory, bytes, 0, bytes.Length);
            var device = Marshal.ReadIntPtr(memory, 8);
            var payload = bytes.AsSpan((int)headerSize);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (type == 1 && payload.Length >= 16)
            {
                var key = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]);
                if (key is 0 or > 254) { return 0; }
                if (!keyboards.TryGetValue(device, out var keys)) { keyboards[device] = keys = []; }
                if ((BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]) & 1) == 0) { keys.Add(key); } else { keys.Remove(key); }
                state.Set(new("keyboard", key), keyboards.Values.Any(set => set.Contains(key)));
            }
            else if (type == 2 && payload.Length >= 8)
            {
                var reportSize = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                var count = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
                if (reportSize == 0 || (ulong)reportSize * count > (ulong)(payload.Length - 8)) { return 0; }
                if (!devices.TryGetValue(device, out var hid))
                {
                    if (devices.Count >= 128) { return 0; }
                    hid = ReadDevice(device); if (hid is null) { return 0; }
                    devices[device] = hid;
                }
                if (hid.MaximumButtons == 0) { return 0; }
                for (var index = 0; index < count; index++)
                {
                    var report = payload.Slice(8 + (int)(index * reportSize), (int)reportSize).ToArray();
                    var usages = new ushort[hid.MaximumButtons]; uint length = (uint)usages.Length;
                    if (HidP_GetUsages(0, 9, 0, usages, ref length, hid.Data, report, reportSize) != 0x00110000) { continue; }
                    var previous = hid.Reports.Values.SelectMany(set => set).ToHashSet();
                    hid.Reports[report[0]] = usages.Take((int)length).Where(value => value != 0).Select(value => (int)value).ToHashSet();
                    var current = hid.Reports.Values.SelectMany(set => set).ToHashSet();
                    foreach (var button in previous.Except(current)) { state.Set(new("controller", button, hid.Path), false); }
                    foreach (var button in current.Except(previous)) { state.Set(new("controller", button, hid.Path), true); }
                }
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
        return 0;
    }
    private static HidButtons? ReadDevice(nint device)
    {
        uint chars = 0;
        if (GetRawInputDeviceInfoW(device, 0x20000007, 0, ref chars) == uint.MaxValue || chars is 0 or > 4096) { return null; }
        var name = Marshal.AllocHGlobal((int)(chars + 1) * 2);
        string path;
        try
        {
            if (GetRawInputDeviceInfoW(device, 0x20000007, name, ref chars) == uint.MaxValue) { return null; }
            path = (Marshal.PtrToStringUni(name, (int)chars) ?? "").TrimEnd('\0').ToUpperInvariant();
        }
        finally { Marshal.FreeHGlobal(name); }
        uint size = 0;
        if (GetRawInputDeviceInfoW(device, 0x20000005, 0, ref size) == uint.MaxValue || size is 0 or > 1_048_576 || path.Length is 0 or > 1024) { return null; }
        var data = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfoW(device, 0x20000005, data, ref size) == uint.MaxValue) { Marshal.FreeHGlobal(data); return null; }
        return new(path, data, (int)Math.Min(4096, HidP_MaxUsageListLength(0, 9, data)));
    }
    private void RemoveDevice(nint device)
    {
        if (devices.Remove(device, out var hid)) { state.RemoveDevice(hid.Path); Marshal.FreeHGlobal(hid.Data); }
        if (keyboards.Remove(device, out var keys))
        { foreach (var key in keys) { state.Set(new("keyboard", key), keyboards.Values.Any(set => set.Contains(key))); } }
    }
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        source.RemoveHook(InputMessage);
        var registrations = Usages.Select(usage => new RawDevice(1, usage, 1, 0)).ToArray();
        _ = RegisterRawInputDevices(registrations, (uint)registrations.Length, (uint)Marshal.SizeOf<RawDevice>());
        foreach (var device in devices.Values) { Marshal.FreeHGlobal(device.Data); }
        devices.Clear(); keyboards.Clear(); state.Dispose(); GC.SuppressFinalize(this);
    }
    private sealed record HidButtons(string Path, nint Data, int MaximumButtons)
    { internal Dictionary<byte, HashSet<int>> Reports { get; } = []; }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct RawDevice(ushort UsagePage, ushort Usage, uint Flags, nint Target);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetRawInputDeviceInfoW(nint device, uint command, nint data, ref uint size);
    [DllImport("hid.dll")]
    private static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, nint data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort page, ushort collection, [Out] ushort[] usages,
        ref uint length, nint data, [In] byte[] report, uint reportLength);
}
