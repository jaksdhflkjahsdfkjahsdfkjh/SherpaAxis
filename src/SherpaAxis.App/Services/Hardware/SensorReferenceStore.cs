using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Services.Hardware;

public sealed record SensorReference(string SerialNumber, PedalChannel Channel, HardwareCalibrationRange Raw, HardwareCalibrationRange Applied);

public sealed class SensorReferenceStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string DefaultDirectory => Path.Combine(HardwareRecoveryStore.DefaultDirectory, "SensorReferences");

    public async Task WriteAsync(SensorReference reference, CancellationToken token = default)
    {
        Validate(reference);
        await AtomicFile.WriteAsync(FilePath(reference.SerialNumber, reference.Channel, reference.Applied),
            JsonSerializer.SerializeToUtf8Bytes(reference, Options), token: token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<PedalAxis, HardwareCalibrationRange>> ReadAsync(string serialNumber, PedalCalibration applied,
        CancellationToken token = default)
    {
        var result = new Dictionary<PedalAxis, HardwareCalibrationRange>();
        foreach (var channel in Enum.GetValues<PedalChannel>())
        {
            var range = applied.Range(channel);
            var path = FilePath(serialNumber, channel, range);
            if (!File.Exists(path)) { continue; }
            await using var stream = File.OpenRead(path);
            if (stream.Length > 4096) { throw new InvalidDataException("Sensor reference is too large."); }
            SensorReference reference;
            try
            {
                reference = await JsonSerializer.DeserializeAsync<SensorReference>(stream, Options, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Sensor reference is empty.");
            }
            catch (JsonException failure) { throw new InvalidDataException("Sensor reference is malformed.", failure); }
            Validate(reference);
            if (!PedalDeviceIdentity.SameController(serialNumber, reference.SerialNumber) || reference.Channel != channel || reference.Applied != range)
            { throw new InvalidDataException("Sensor reference does not match the controller calibration."); }
            result.Add((PedalAxis)channel, reference.Raw);
        }
        return result;
    }

    public static void Validate(SensorReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (string.IsNullOrWhiteSpace(reference.SerialNumber) || !Enum.IsDefined(reference.Channel)
            || reference.Raw is null || reference.Applied is null || !reference.Raw.HasUsableRange || !reference.Applied.HasUsableRange
            || reference.Raw.Minimum < 0 || reference.Raw.Maximum > PedalHardwareSettings.Defaults.Range(reference.Channel).Maximum
            || reference.Applied.Minimum < reference.Raw.Minimum || reference.Applied.Maximum > reference.Raw.Maximum)
        { throw new InvalidDataException("Raw calibration must enclose the hardware bounds for the same controller and pedal."); }
    }

    private string FilePath(string serialNumber, PedalChannel channel, HardwareCalibrationRange range)
    {
        var identity = PedalDeviceIdentity.Parse(serialNumber);
        var key = FormattableString.Invariant($"{identity.Hardware}/{identity.Serial.ToUpperInvariant()}/{channel}/{range.Minimum}/{range.Maximum}");
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
    }
}
