using System.IO;
using SherpaAxis.App.Models;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services.Hardware;

public sealed class CorrectedCalibrationWriter(Func<CancellationToken, Task<PedalControllerConnection>> connect, string backupDirectory,
    SensorReferenceStore? references = null)
{
    public async Task SaveAsync(CorrectedCalibration correction, CancellationToken token)
    {
        PedalHardwareSettings.Validate(correction.Corrected);
        var channel = (PedalChannel)correction.Axis;
        var originalRange = correction.Original.Range(channel);
        var correctedRange = correction.Corrected.Range(channel);
        var shift = correctedRange.Minimum - originalRange.Minimum;
        if (correctedRange.Maximum - originalRange.Maximum != shift || correction.Original.WithRange(channel, correctedRange) != correction.Corrected)
        { throw new InvalidOperationException("Drift correction must translate only the selected pedal's bounds without changing its span."); }
        SensorReferenceStore.Validate(new(correction.SerialNumber, channel, correction.RawOriginal, originalRange));
        var correctedReference = new SensorReference(correction.SerialNumber, channel,
            new(checked(correction.RawOriginal.Minimum + shift), checked(correction.RawOriginal.Maximum + shift)), correctedRange);
        SensorReferenceStore.Validate(correctedReference);
        await using var connection = await connect(token).ConfigureAwait(false);
        if (!PedalDeviceIdentity.SameController(correction.SerialNumber, connection.Identity.SerialNumber)
            || connection.Snapshot.Calibration != correction.Original)
        { throw new IOException("Controller identity or calibration changed. Fix drift again before saving."); }
        var operation = Guid.NewGuid().ToString("N");
        var recovery = new HardwareRecoveryStore(Path.Combine(backupDirectory, $"drift-recovery-{operation}.json"));
        using var transaction = new PedalConfigurationTransaction(connection.Snapshot, connection.Reader, connection.Writer,
            recovery, TimeSpan.FromSeconds(2), connection.Identity.SerialNumber);
        var backup = await transaction.CreateHardwareBackupAsync(token).ConfigureAwait(false);
        await new HardwareRecoveryStore(Path.Combine(backupDirectory, $"drift-backup-{operation}.json")).WriteAsync(backup, token).ConfigureAwait(false);
        await transaction.ChangeAsync(correction.Corrected, token).ConfigureAwait(false);
        await transaction.SaveAsync(token).ConfigureAwait(false);
        try
        {
            await (references ?? new SensorReferenceStore(Path.Combine(backupDirectory, "SensorReferences")))
                .WriteAsync(correctedReference, token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or OperationCanceledException)
        { throw new IOException("Calibration was saved, but its raw reference could not be retained. Drift correction is blocked until this pedal is calibrated again.", failure); }
    }
}
