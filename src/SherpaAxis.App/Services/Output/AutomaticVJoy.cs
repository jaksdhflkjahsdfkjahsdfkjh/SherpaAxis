using System.Globalization;
using System.IO;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.Bridge;

namespace SherpaAxis.App.Services.Output;

public sealed class AutomaticVJoy(string path, Func<IReadOnlyList<VJoyDeviceInfo>> inspect, Func<uint, IVirtualController> connect,
    Action<IReadOnlyList<VJoyDeviceInfo>, uint>? nameDevice = null)
{
    private readonly object sync = new();
    private uint? selected;
    private bool loaded;
    public uint? SelectedId { get { lock (sync) { Load(); return selected; } } }
    public static string DefaultPath => AppDataPaths.VirtualDevice;
    public static AutomaticVJoy Default() => new(DefaultPath, () => VJoyController.Inspect(VJoyController.DefaultLibraryPath),
        id => new VJoyController(VJoyController.DefaultLibraryPath, id), (devices, id) =>
        {
            try { VJoyDeviceName.Default().Apply(devices, id); }
            catch (Exception failure) when (Diagnostics.OperationErrors.IsExpected(failure))
            { Diagnostics.DiagnosticLog.Default.Write("vjoy-name", "Could not update the Windows controller display name.", failure); }
        });

    public IVirtualController Connect()
    {
        lock (sync)
        {
            Load();
            var devices = inspect();
            var id = VJoyDeviceSelection.Select(devices, selected);
            var controller = connect(id);
            try
            {
                // Persist only after acquisition succeeds, before publishing any pedal values.
                if (selected != id)
                {
                    var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
                    Directory.CreateDirectory(directory);
                    var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                    try { File.WriteAllText(temporary, id.ToString(CultureInfo.InvariantCulture)); File.Move(temporary, path, true); }
                    finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
                    selected = id;
                }
                nameDevice?.Invoke(devices, id);
                return controller;
            }
            catch { controller.Dispose(); throw; }
        }
    }

    private void Load()
    {
        if (loaded) { return; }
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 32 || !uint.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture, out var id) || id is < 1 or > 16)
            { throw new InvalidDataException("The saved vJoy device ID is invalid. Review virtual-device.txt in the SherpaAxis settings folder."); }
            selected = id;
        }
        loaded = true;
    }
}
