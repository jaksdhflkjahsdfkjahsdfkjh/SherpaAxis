using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using SherpaAxis.Bridge;

namespace SherpaAxis.App.Services.Output;

public static class WindowsHidHide
{
    public static HidHideVisibility Default(AutomaticVJoy? vjoy = null, string? statePath = null) => new(statePath ?? HidHideVisibility.DefaultStatePath,
        Path.Combine(AppContext.BaseDirectory, "SherpaAxis.App.exe"), RunAsync, () =>
        {
            try
            {
                var id = vjoy?.SelectedId;
                var devices = VJoyController.Inspect(VJoyController.DefaultLibraryPath)
                    .Select(device => device.Id == id && device.State == VJoyDeviceState.Owned ? device with { State = VJoyDeviceState.Free } : device).ToArray();
                _ = VJoyDeviceSelection.Select(devices, id);
                return true;
            }
            catch (Exception failure) when (Diagnostics.OperationErrors.IsExpected(failure)) { return false; }
        });

    public static string FindCli()
    {
        foreach (var company in new[] { "Nefarius Software Solutions", "Nefarius Software Solutions e.U." })
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), company, "HidHide");
            foreach (var suffix in new[] { "HidHideCLI.exe", @"x64\HidHideCLI.exe" })
            { var path = Path.Combine(directory, suffix); if (File.Exists(path)) { return path; } }
        }
        throw new FileNotFoundException("HidHide is not installed. Run SherpaAxis Setup and restart Windows if requested.");
    }

    internal static async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(FindCli())
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception failure) { throw new IOException("Could not start HidHide configuration.", failure); }
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("HidHide configuration timed out. Close its configuration client and retry.");
        }
        var result = await output.ConfigureAwait(false); var error = await errors.ConfigureAwait(false);
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error)) { throw new IOException("HidHide configuration failed: " + error.Trim()); }
        if (result.Length > 1_048_576) { throw new InvalidDataException("HidHide returned too much device information."); }
        return result;
    }
}
