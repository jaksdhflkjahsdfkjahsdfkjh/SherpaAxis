using System.Text;
using System.Text.Json.Nodes;
using SherpaAxis.Profiles;

namespace SherpaAxis.Tests;

public sealed class ClutchBitePointProfileTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(.43)]
    [InlineData(1)]
    public void OutputAndBindingsRoundTripWithoutChangingProcessing(double output)
    {
        foreach (var button in new CorrectionButton[] { new("keyboard", 65), new("controller", 21, "wheel") })
            foreach (var mode in Enum.GetValues<ClutchBitePointMode>())
            {
                var axis = new AxisProfile(new(100, 1000), bitePoint: new(output, button, mode));
                var profile = new PedalProfile("Launch", new Dictionary<PedalAxis, AxisProfile> { [PedalAxis.Clutch] = axis });
                var restored = ProfileJson.Deserialize(ProfileJson.Serialize(profile)).Axes[PedalAxis.Clutch];
                Assert.Equal(axis.BitePoint, restored.BitePoint);
                Assert.Equal(axis.CreateProcessor().Process(400).Output, restored.CreateProcessor().Process(400).Output);
            }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-.01)]
    [InlineData(1.01)]
    public void InvalidOutputIsRejected(double output) => Assert.Throws<ArgumentOutOfRangeException>(() => new ClutchBitePointSettings(output));

    [Fact]
    public void InvalidModeIsRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => new ClutchBitePointSettings(mode: (ClutchBitePointMode)99));

    [Fact]
    public void SchemaFourKeepsTheOriginalModeAndRejectsTheNewModeField()
    {
        var json = JsonNode.Parse(ProfileJson.Serialize(new PedalProfile("Legacy", new Dictionary<PedalAxis, AxisProfile>
        { [PedalAxis.Clutch] = new(new(0, 1), bitePoint: new(.4, new("keyboard", 65))) })))!;
        json["schemaVersion"] = 4;
        var bite = json["axes"]!["clutch"]!["bitePoint"]!.AsObject();
        bite.Remove("mode");
        Assert.Equal(ClutchBitePointMode.HoldMinimum, ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())).Axes[PedalAxis.Clutch].BitePoint!.Mode);
        bite["mode"] = "HoldFullRange";
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
        json["schemaVersion"] = 5;
        Assert.Equal(ClutchBitePointMode.HoldFullRange, ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())).Axes[PedalAxis.Clutch].BitePoint!.Mode);
        bite["mode"] = "Unsupported";
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData(PedalAxis.Brake)]
    [InlineData(PedalAxis.Throttle)]
    public void NonClutchBindingsAreRejected(PedalAxis axis) => Assert.Throws<ArgumentException>(() =>
        new PedalProfile("Invalid", new Dictionary<PedalAxis, AxisProfile> { [axis] = new(new(0, 1), bitePoint: new()) }));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EarlierSchemasRemainUsableButCannotClaimToSupportBitePoint(int version)
    {
        var profile = new PedalProfile("Legacy", new Dictionary<PedalAxis, AxisProfile> { [PedalAxis.Clutch] = new(new(0, 1)) });
        var json = JsonNode.Parse(ProfileJson.Serialize(profile))!;
        json["schemaVersion"] = version;
        Assert.Null(ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())).Axes[PedalAxis.Clutch].BitePoint);
        json["axes"]!["clutch"]!["bitePoint"] = new JsonObject { ["output"] = .5 };
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }
}
