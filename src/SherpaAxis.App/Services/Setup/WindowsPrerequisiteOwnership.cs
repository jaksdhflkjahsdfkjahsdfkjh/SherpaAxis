using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SherpaAxis.App.Services.Diagnostics;

namespace SherpaAxis.App.Services.Setup;

internal static class WindowsPrerequisiteOwnership
{
    private const string KeyPath = @"SOFTWARE\SherpaAxis\Prerequisites";
    private const string HidHideProduct = "{01E0AB21-D1CC-42B4-9DFF-84FFE4F26DAF}";
    private static string VJoyUninstaller => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "unins000.exe");
    private static string InfName(PrerequisiteStep step) => step switch
    { PrerequisiteStep.VJoy => "vjoy.inf", PrerequisiteStep.HidHide => "hidhide.inf", PrerequisiteStep.DfuSe => "sttube.inf", _ => throw new ArgumentOutOfRangeException(nameof(step)) };
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }

    internal static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        { throw new UnauthorizedAccessException("Administrator approval is required to remove drivers."); }
    }

    internal static bool IsPresent(PrerequisiteStep step)
    {
        if (DriverFiles(step).Any()) { return true; }
        if (step == PrerequisiteStep.VJoy)
        { using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\vjoy"); return File.Exists(VJoyUninstaller) || service is not null; }
        if (step == PrerequisiteStep.HidHide)
        { using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + HidHideProduct); return key is not null; }
        return false;
    }

    private static IEnumerable<string> DriverFiles(PrerequisiteStep step)
    {
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository");
        if (!Directory.Exists(store)) { return []; }
        var name = InfName(step);
        return Directory.EnumerateDirectories(store, name + "_*").Select(directory => Path.Combine(directory, name)).Where(File.Exists);
    }

    private static OwnedDriver[] Drivers(PrerequisiteStep step)
    {
        var hashes = DriverFiles(step).Select(Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        return Directory.EnumerateFiles(inf, "oem*.inf")
            .Select(path => new OwnedDriver(Path.GetFileName(path), Hash(path)))
            .Where(driver => hashes.Contains(driver.Hash)).OrderBy(driver => driver.PublishedName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static void RecordInstalled(PrerequisiteStep step)
    {
        RequireAdministrator();
        var drivers = Drivers(step);
        if (drivers.Length == 0) { throw new IOException($"Could not record ownership of the installed {step} driver package."); }
        using var product = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + HidHideProduct);
        var item = new OwnedPrerequisite(step, drivers, step == PrerequisiteStep.VJoy ? Hash(VJoyUninstaller) : null,
            step == PrerequisiteStep.HidHide ? product?.GetValue("DisplayVersion") as string : null);
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
        key.SetValue(step.ToString(), JsonSerializer.Serialize(item));
        key.DeleteValue("Pending" + step, false);
        key.DeleteValue("Pending" + step + "Hash", false);
        if (step == PrerequisiteStep.VJoy) { key.DeleteValue("PendingVJoyUninstallerHash", false); }
    }

    internal static void FinalizePending()
    {
        RequireAdministrator();
        foreach (var step in new[] { PrerequisiteStep.VJoy, PrerequisiteStep.HidHide, PrerequisiteStep.DfuSe })
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, true);
            if (key?.GetValue("Pending" + step) is not 1) { continue; }
            var expected = key.GetValue("Pending" + step + "Hash") as string;
            var drivers = Drivers(step);
            if (expected is null || drivers.Length == 0 || drivers.Any(driver => !driver.Hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
                || step == PrerequisiteStep.VJoy && (!File.Exists(VJoyUninstaller)
                    || !Hash(VJoyUninstaller).Equals(key.GetValue("PendingVJoyUninstallerHash") as string, StringComparison.OrdinalIgnoreCase)))
            {
                key.DeleteValue("Pending" + step, false);
                key.DeleteValue("Pending" + step + "Hash", false);
                if (step == PrerequisiteStep.VJoy) { key.DeleteValue("PendingVJoyUninstallerHash", false); }
                DiagnosticLog.Default.Write("driver-ownership", $"{step} changed before ownership was finalized; it remains unowned.");
                continue;
            }
            RecordInstalled(step);
        }
    }

    internal static IReadOnlyList<OwnedPrerequisite> Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        var items = new List<OwnedPrerequisite>();
        foreach (var step in new[] { PrerequisiteStep.HidHide, PrerequisiteStep.VJoy, PrerequisiteStep.DfuSe })
        {
            if (key?.GetValue(step.ToString()) is not string json) { continue; }
            if (json.Length > 65_536) { throw new InvalidDataException("Driver ownership record is too large."); }
            OwnedPrerequisite item;
            try { item = JsonSerializer.Deserialize<OwnedPrerequisite>(json) ?? throw new InvalidDataException("Driver ownership record is empty."); }
            catch (JsonException failure) { throw new InvalidDataException("Driver ownership record is invalid.", failure); }
            Validate(item, step);
            items.Add(item);
        }
        return items;
    }

    internal static void Validate(OwnedPrerequisite item, PrerequisiteStep step)
    {
        if (step is not (PrerequisiteStep.VJoy or PrerequisiteStep.HidHide or PrerequisiteStep.DfuSe) || item.Component != step || item.Drivers is not { Length: > 0 and <= 16 }
            || item.Drivers.Any(driver => driver is null || driver.PublishedName is null || driver.Hash is null
                || !Regex.IsMatch(driver.PublishedName, @"\Aoem\d+\.inf\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                || !Regex.IsMatch(driver.Hash, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant))
            || step == PrerequisiteStep.VJoy && (item.UninstallerHash is null || !Regex.IsMatch(item.UninstallerHash, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)))
        { throw new InvalidDataException("Driver ownership record is invalid. Existing drivers were preserved."); }
    }

    internal static bool Unchanged(OwnedPrerequisite item)
    {
        if (!IsPresent(item.Component)) { return true; }
        if (Drivers(item.Component).Except(item.Drivers).Any()) { return false; }
        if (item.Component == PrerequisiteStep.VJoy && File.Exists(VJoyUninstaller) && Hash(VJoyUninstaller) != item.UninstallerHash) { return false; }
        if (item.Component == PrerequisiteStep.HidHide)
        {
            using var products = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (products is not null)
            {
                foreach (var name in products.GetSubKeyNames())
                {
                    using var product = products.OpenSubKey(name);
                    if (product?.GetValue("DisplayName") as string != "HidHide") { continue; }
                    if (!name.Equals(HidHideProduct, StringComparison.OrdinalIgnoreCase)
                        || product.GetValue("DisplayVersion") as string != item.ProductVersion) { return false; }
                }
            }
        }
        return true;
    }

    internal static async Task<bool> RemoveAsync(IProgress<string> progress, CancellationToken token)
    {
        RequireAdministrator();
        using var gate = new Semaphore(1, 1, @"Global\SherpaAxis.PrerequisiteSetup");
        if (!gate.WaitOne(0))
        { throw new IOException("Driver setup is still running. Wait for it to finish before uninstalling."); }
        try
        {
            if (WindowsPrerequisiteSetup.HasActiveInstaller(WindowsPrerequisiteSetup.ReadProgress()))
            { throw new IOException("Driver setup is still running. Wait for it to finish before uninstalling."); }
            var removal = new PrerequisiteRemoval(Read, Unchanged, RemoveComponent, step =>
            { using var key = Registry.LocalMachine.OpenSubKey(KeyPath, true); key?.DeleteValue(step.ToString(), false); });
            var restart = await removal.RemoveAsync(progress, token).ConfigureAwait(false);
            await WindowsPrerequisiteSetup.StoreAsync(new("uninstall", Guid.NewGuid(), restart
                ? "Restart Windows, then open SherpaAxis again to finish device setup." : "Driver removal completed.",
                RestartRequired: restart, Boot: WindowsPrerequisiteSetup.Boot)).ConfigureAwait(false);
            return restart;
        }
        finally { gate.Release(); }
    }

    private static async Task<int> RemoveComponent(OwnedPrerequisite item, CancellationToken token)
    {
        if (!IsPresent(item.Component)) { return 0; }
        var restart = false;
        int code;
        if (item.Component == PrerequisiteStep.VJoy && File.Exists(VJoyUninstaller))
        {
            code = await RunAsync(VJoyUninstaller, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], token).ConfigureAwait(false);
            if (code is not (0 or 3010)) { return code; }
            restart |= code == 3010;
        }
        if (item.Component == PrerequisiteStep.HidHide)
        {
            code = await RunAsync(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), ["/x", HidHideProduct, "/qn", "/norestart"], token).ConfigureAwait(false);
            if (code is not (0 or 3010 or 1605)) { return code; }
            restart |= code == 3010;
        }
        foreach (var driver in item.Drivers)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", driver.PublishedName);
            if (!File.Exists(path)) { continue; }
            if (Hash(path) != driver.Hash) { throw new IOException("An installed driver package changed during removal. The remaining packages were preserved."); }
            code = await RunAsync(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), ["/delete-driver", driver.PublishedName, "/uninstall"], token).ConfigureAwait(false);
            if (code is not (0 or 3010)) { return code; }
            restart |= code == 3010;
        }
        return restart || item.Component is PrerequisiteStep.VJoy or PrerequisiteStep.HidHide ? 3010 : 0;
    }

    private static async Task<int> RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new IOException("Could not start driver removal.");
        var status = new PrerequisiteProgress("uninstall", Guid.NewGuid(), "Removing drivers installed by SherpaAxis…",
            ActiveProcess: process.Id, ProcessStarted: process.StartTime.ToUniversalTime());
        await WindowsPrerequisiteSetup.StoreAsync(status).ConfigureAwait(false);
        var output = await WindowsPrerequisiteSetup.WaitForProcessAsync(process, TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
        await WindowsPrerequisiteSetup.StoreAsync(status with { ActiveProcess = null, ProcessStarted = null }).ConfigureAwait(false);
        Directory.CreateDirectory(WindowsPrerequisiteSetup.LogDirectory);
        await File.AppendAllTextAsync(Path.Combine(WindowsPrerequisiteSetup.LogDirectory, "uninstall.log"), executable + Environment.NewLine + output + Environment.NewLine, token).ConfigureAwait(false);
        return process.ExitCode;
    }
}
