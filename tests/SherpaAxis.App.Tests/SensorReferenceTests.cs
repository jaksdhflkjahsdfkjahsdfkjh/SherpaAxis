using System.IO;
using SherpaAxis.App.Services.Hardware;
using SherpaAxis.FeelVR;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class SensorReferenceTests
{
    private static readonly PedalCalibration Applied = new(new(1000, 15000), new(3275500, 4625500), new(1000, 15000));
    private static readonly HardwareCalibrationRange Raw = new(3200500, 4700500);

    [Fact]
    public async Task ReferencesMatchThePhysicalControllerPedalAndExactAppliedBounds()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-sensor-reference-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SensorReferenceStore(folder);
            Assert.Empty(await store.ReadAsync("PE001700", Applied));
            await store.WriteAsync(new("PE001700", PedalChannel.Brake, Raw, Applied.Brake));
            var restored = await new SensorReferenceStore(folder).ReadAsync("PE001700", Applied);
            Assert.Equal(Raw, Assert.Single(restored).Value);
            Assert.Equal(Raw, (await store.ReadAsync("PE001800", Applied))[PedalAxis.Brake]);
            Assert.Empty(await store.ReadAsync("PE0017FF", Applied));
            Assert.Empty(await store.ReadAsync("PE001700", Applied.WithRange(PedalChannel.Brake, new(3275501, 4625500))));
            await store.WriteAsync(new("PE001700", PedalChannel.Throttle, new(500, 15500), Applied.Throttle));
            Assert.Equal(2, (await store.ReadAsync("PE001700", Applied)).Count);
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public async Task AReferenceCannotUseTrimmedBoundsAsAReplacementForAWiderAppliedRange()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-sensor-reference-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SensorReferenceStore(folder);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(new("PE001700", PedalChannel.Brake, new(3300000, 4600000), Applied.Brake)));
            Assert.False(Directory.Exists(folder));
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("identity")]
    [InlineData("bounds")]
    public async Task CorruptOrMismatchedMetadataIsRejectedInsteadOfInventingARawReference(string problem)
    {
        var folder = Path.Combine(Path.GetTempPath(), "SherpaAxis-sensor-reference-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SensorReferenceStore(folder);
            await store.WriteAsync(new("PE001700", PedalChannel.Brake, Raw, Applied.Brake));
            var path = Assert.Single(Directory.GetFiles(folder));
            var text = await File.ReadAllTextAsync(path);
            text = problem switch
            {
                "identity" => text.Replace("PE001700", "PE0017FF", StringComparison.Ordinal),
                "bounds" => text.Replace("3275500", "3275501", StringComparison.Ordinal),
                _ => "{broken"
            };
            await File.WriteAllTextAsync(path, text);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync("PE001700", Applied));
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }
}
