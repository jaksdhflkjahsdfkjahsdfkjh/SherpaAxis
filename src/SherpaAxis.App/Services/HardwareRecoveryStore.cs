using System.IO;
using System.Text.Json;
using SherpaAxis.FeelVR;

namespace SherpaAxis.App.Services;

public sealed class HardwareRecoveryStore(string filePath) : IHardwareRecoveryStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SherpaAxis", "HardwareBackups");
    public string FilePath { get; } = Path.GetFullPath(filePath);

    public async Task WriteAsync(HardwareRecoverySnapshot snapshot, CancellationToken cancellationToken)
    {
        Validate(snapshot);
        await AtomicFile.WriteAsync(FilePath, JsonSerializer.SerializeToUtf8Bytes(snapshot, Options), cancellationToken).ConfigureAwait(false);
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(FilePath);
        return Task.CompletedTask;
    }

    public static async Task<HardwareRecoverySnapshot> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > 65536) { throw new InvalidDataException("Hardware recovery file is too large."); }
        HardwareRecoverySnapshot snapshot;
        try
        {
            snapshot = await JsonSerializer.DeserializeAsync<HardwareRecoverySnapshot>(stream, Options, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Hardware recovery file is empty.");
        }
        catch (JsonException failure) { throw new InvalidDataException("Hardware recovery file is malformed.", failure); }
        Validate(snapshot);
        return snapshot;
    }

    private static void Validate(HardwareRecoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if ((snapshot.IsManualBackup ? snapshot.SchemaVersion != 2 : snapshot.SchemaVersion != 1) || snapshot.Device is null || !PedalCalibrationReport.Supports(snapshot.Device)
            || string.IsNullOrWhiteSpace(snapshot.Device.Path)) { throw new InvalidDataException("Invalid hardware recovery identity or version."); }
        PedalHardwareSettings.Validate(snapshot.Saved);
        PedalHardwareSettings.Validate(snapshot.LastConfirmed);
        if (snapshot.Attempted is not null) { PedalHardwareSettings.Validate(snapshot.Attempted); }
        if (snapshot.IsManualBackup && (snapshot.SaveUncertain || snapshot.Attempted is not null || snapshot.Saved != snapshot.LastConfirmed || string.IsNullOrWhiteSpace(snapshot.SerialNumber)))
        { throw new InvalidDataException("Invalid manual hardware backup."); }
    }
}
