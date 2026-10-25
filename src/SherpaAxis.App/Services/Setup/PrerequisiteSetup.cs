using System.IO;

namespace SherpaAxis.App.Services.Setup;

internal enum PrerequisiteStep { VJoy, HidHide, DfuSe, VirtualDevice }
internal sealed record Prerequisites(bool VJoy, bool HidHide, bool DfuSe);

internal sealed class PrerequisiteSetup(Func<Prerequisites> inspect, Action verify,
    Func<PrerequisiteStep, CancellationToken, Task<int>> run)
{
    internal async Task<bool> InstallAsync(IProgress<string> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        verify();
        var installed = inspect();
        var restart = false;
        foreach (var (step, needed, message) in new[]
        {
            (PrerequisiteStep.VJoy, !installed.VJoy, "Installing the vJoy driver and runtime…"),
            (PrerequisiteStep.HidHide, !installed.HidHide, "Installing HidHide duplicate-input protection…"),
            (PrerequisiteStep.DfuSe, !installed.DfuSe, "Preparing the pedal firmware USB driver…")
        })
        {
            if (!needed) { continue; }
            token.ThrowIfCancellationRequested();
            progress.Report(message);
            var result = await run(step, token).ConfigureAwait(false);
            if (result is not (0 or 3010)) { throw new IOException($"{step} setup failed (code {result}). Review the setup logs before retrying."); }
            restart |= result == 3010;
        }
        if (restart) { return true; }
        var actual = inspect();
        if (!actual.VJoy || !actual.HidHide || !actual.DfuSe)
        { throw new IOException("Driver installation did not provide all required components. Restart Windows, then try again."); }
        progress.Report("Configuring the virtual pedal device…");
        var configured = await run(PrerequisiteStep.VirtualDevice, token).ConfigureAwait(false);
        if (configured == 3010) { return true; }
        if (configured != 0)
        { throw new IOException("Virtual pedal setup needs attention. Close other vJoy feeders and restart Windows. Existing devices were preserved."); }
        return false;
    }
}
