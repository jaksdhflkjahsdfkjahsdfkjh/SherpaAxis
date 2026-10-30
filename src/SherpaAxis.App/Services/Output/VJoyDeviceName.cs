using Microsoft.Win32;
using SherpaAxis.Bridge;

namespace SherpaAxis.App.Services.Output;

public sealed class VJoyDeviceName(RegistryKey root, string oemPath, string backupPath)
{
    public const string DisplayName = "FEEL-VR Pedals One (SherpaAxis)";
    public static VJoyDeviceName Default(RegistryKey? userRoot = null) => new(userRoot ?? Registry.CurrentUser,
        @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_1234&PID_BEAD",
        @"Software\SherpaAxis\VirtualControllerName");

    public bool Apply(IReadOnlyList<VJoyDeviceInfo> devices, uint selectedId)
    {
        var configured = devices.Where(device => device.State != VJoyDeviceState.Missing).ToArray();
        if (configured.Length != 1 || configured[0].Id != selectedId || !configured[0].IsPedalsOnly)
        { Restore(); return false; }
        using var oem = root.CreateSubKey(oemPath);
        using var backup = root.CreateSubKey(backupPath);
        if (backup.GetValue("Saved") is not 1)
        {
            var previous = oem.GetValue("OEMName") as string;
            backup.SetValue("HadName", previous is null ? 0 : 1);
            backup.SetValue("PreviousName", previous ?? "");
            backup.SetValue("Saved", 1);
            backup.Flush();
        }
        oem.SetValue("OEMName", DisplayName);
        return true;
    }

    public void Restore()
    {
        using (var backup = root.OpenSubKey(backupPath))
        {
            if (backup?.GetValue("Saved") is not 1) { return; }
            using var oem = root.OpenSubKey(oemPath, true);
            if (oem?.GetValue("OEMName") as string == DisplayName)
            {
                if (backup.GetValue("HadName") is 1) { oem.SetValue("OEMName", backup.GetValue("PreviousName") as string ?? ""); }
                else { oem.DeleteValue("OEMName", false); }
            }
        }
        root.DeleteSubKey(backupPath, false);
    }
}
