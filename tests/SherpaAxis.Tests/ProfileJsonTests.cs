using System.Text;
using System.Text.Json.Nodes;
using SherpaAxis.Core;
using SherpaAxis.Profiles;

namespace SherpaAxis.Tests;

public sealed class ProfileJsonTests
{
    [Theory]
    [InlineData(BaselineCorrectionMode.Off)]
    [InlineData(BaselineCorrectionMode.Auto)]
    [InlineData(BaselineCorrectionMode.ByButton)]
    public void CorrectionModesAndKeyboardOrControllerBindingsRoundTrip(BaselineCorrectionMode mode)
    {
        var profile = new PedalProfile("Bindings", new Dictionary<PedalAxis, AxisProfile>
        {
            [PedalAxis.Brake] = new(new(0, 1000), maximumBaselineCorrection: 5, correctionMode: mode,
                correctionButton: new("keyboard", 65)),
            [PedalAxis.Throttle] = new(new(0, 1000), maximumBaselineCorrection: 5, correctionMode: mode,
                correctionButton: new("controller", 64, "wheel-path"))
        });
        var loaded = ProfileJson.Deserialize(ProfileJson.Serialize(profile));
        Assert.Equal(mode, loaded.Axes[PedalAxis.Brake].CorrectionMode);
        Assert.Equal(new("keyboard", 65), loaded.Axes[PedalAxis.Brake].CorrectionButton);
        Assert.Equal(new("controller", 64, "WHEEL-PATH"), loaded.Axes[PedalAxis.Throttle].CorrectionButton);
        Assert.Equal(0, loaded.Axes[PedalAxis.Brake].CreateProcessor().BaselineCorrection);
    }

    [Theory]
    [InlineData("keyboard", 0, null)]
    [InlineData("keyboard", 255, null)]
    [InlineData("keyboard", 65, "device")]
    [InlineData("controller", 1, null)]
    [InlineData("controller", 65536, "device")]
    [InlineData("mouse", 1, null)]
    public void UnsupportedBindingsAreRejected(string kind, int code, string? device) =>
        Assert.Throws<ArgumentException>(() => new CorrectionButton(kind, code, device));

    [Fact]
    public void RoundTripsIndependentAxesAndEveryCurveKindWithoutSessionState()
    {
        var profile = new PedalProfile("Three pedals", new Dictionary<PedalAxis, AxisProfile>
        {
            [PedalAxis.Clutch] = new(new(100, 200, true), curve: ProfileCurve.PiecewiseLinear([new(0, 0), new(.5, .2), new(1, 1)])),
            [PedalAxis.Brake] = new(new(100, 300), new(.1, .2), ProfileCurve.Power(2), 10, new(.01, .2)),
            [PedalAxis.Throttle] = new(new(0, 100))
        });
        var active = profile.Axes[PedalAxis.Brake].CreateProcessor();
        active.ConfirmReleasedBaseline(105);
        active.Process(105, 0);
        active.Process(200, .002);
        var bytes = ProfileJson.Serialize(profile);
        var loaded = ProfileJson.Deserialize(bytes);
        Assert.Equal(bytes, ProfileJson.Serialize(loaded));
        Assert.Equal("Three pedals", loaded.Name);
        Assert.Equal(3, loaded.Axes.Count);
        var brake = loaded.Axes[PedalAxis.Brake];
        Assert.Equal(100, brake.Calibration.Minimum);
        Assert.Equal(300, brake.Calibration.Maximum);
        Assert.Equal(.1, brake.Deadzone.Lower);
        Assert.Equal(.2, brake.Deadzone.Upper);
        Assert.Equal("power", brake.Curve.Kind);
        Assert.Equal(2, brake.Curve.Exponent);
        Assert.Equal(10, brake.MaximumBaselineCorrection);
        Assert.Equal(.01, brake.Filter!.TimeConstantSeconds);
        Assert.Equal(.2, brake.Filter.ResetAfterSeconds);
        var fresh = brake.CreateProcessor();
        Assert.Equal(0, fresh.BaselineCorrection);
        var first = fresh.Process(200, 0);
        Assert.True(first.FilterReset);
        Assert.Equal(200, first.FilteredInput);
        Assert.Equal("linear", loaded.Axes[PedalAxis.Throttle].Curve.Kind);
        Assert.Null(loaded.Axes[PedalAxis.Throttle].Filter);
        var clutch = loaded.Axes[PedalAxis.Clutch];
        Assert.True(clutch.Calibration.Inverted);
        Assert.Equal(.2, clutch.CreateProcessor().Process(150).Output, 12);
        Assert.DoesNotContain("baselineCorrection", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("history", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfileCollectionsAreDefensiveAndCurvePointsCannotBeMutated()
    {
        CurvePoint[] points = [new(0, 0), new(.5, .2), new(1, 1)];
        var curve = ProfileCurve.PiecewiseLinear(points);
        var axes = new Dictionary<PedalAxis, AxisProfile> { [PedalAxis.Brake] = new(new(0, 100), curve: curve) };
        var profile = new PedalProfile("Original", axes);
        axes.Clear();
        points[1] = new(.5, .9);
        Assert.Single(profile.Axes);
        Assert.Equal(.2, profile.Axes[PedalAxis.Brake].CreateProcessor().Process(50).Output, 12);
        Assert.Throws<NotSupportedException>(() => ((IList<CurvePoint>)curve.Points)[1] = new(.5, .9));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<PedalAxis, AxisProfile>)profile.Axes).Clear());
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("name")]
    [InlineData("axes")]
    [InlineData("axes.brake.calibration")]
    [InlineData("axes.brake.calibration.minimum")]
    [InlineData("axes.brake.calibration.maximum")]
    [InlineData("axes.brake.calibration.inverted")]
    [InlineData("axes.brake.deadzone.lower")]
    [InlineData("axes.brake.deadzone.upper")]
    [InlineData("axes.brake.curve.kind")]
    [InlineData("axes.brake.filter")]
    [InlineData("axes.brake.filter.timeConstantSeconds")]
    [InlineData("axes.brake.filter.resetAfterSeconds")]
    [InlineData("axes.brake.maximumBaselineCorrection")]
    public void MissingRequiredSettingsAreRejectedInsteadOfDefaulted(string path)
    {
        var json = Document();
        var (parent, key) = Parent(json, path);
        parent.Remove(key);
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("axes")]
    [InlineData("axes.brake")]
    [InlineData("axes.brake.calibration")]
    [InlineData("axes.brake.deadzone")]
    [InlineData("axes.brake.curve")]
    [InlineData("axes.brake.curve.kind")]
    public void RequiredObjectsCannotBeNull(string path)
    {
        var json = Document();
        var (parent, key) = Parent(json, path);
        parent[key] = null;
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void UnsupportedSchemaVersionsAreRejected(int version)
    {
        var json = Document();
        json["schemaVersion"] = version;
        var error = Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Contains("Unsupported profile schema", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatePropertiesAndDuplicateAxesAreRejected()
    {
        var bytes = ProfileJson.Serialize(Profile());
        var original = Encoding.UTF8.GetString(bytes);
        var duplicateName = original.Replace("\"name\": \"Test\"", "\"name\": \"Test\", \"name\": \"Other\"", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(duplicateName)));
        var axis = Document()["axes"]!["brake"]!.ToJsonString();
        var duplicateAxes = "{\"schemaVersion\":1,\"name\":\"Test\",\"axes\":{\"brake\":" + axis + ",\"brake\":" + axis + "}}";
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(duplicateAxes)));
    }

    [Theory]
    [InlineData("baselineCorrection")]
    [InlineData("releasedBaseline")]
    [InlineData("filterHistory")]
    [InlineData("unknownSetting")]
    public void RuntimeStateAndUnknownSettingsAreRejected(string field)
    {
        var json = Document();
        json["axes"]!["brake"]![field] = 1;
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void InvalidNumbersAndConflictingCurveFieldsAreRejected()
    {
        var mutations = new Action<JsonNode>[]
        {
            json => json["axes"]!["brake"]!["calibration"]!["maximum"] = 100,
            json => json["axes"]!["brake"]!["calibration"]!["minimum"] = "100",
            json => json["axes"]!["brake"]!["deadzone"]!["lower"] = .9,
            json => json["axes"]!["brake"]!["maximumBaselineCorrection"] = 101,
            json => json["axes"]!["brake"]!["filter"]!["timeConstantSeconds"] = 0,
            json => json["axes"]!["brake"]!["curve"]!["kind"] = "unknown",
            json => json["axes"]!["brake"]!["curve"]!["points"] = new JsonArray(),
            json => json["axes"]!["brake"]!["curve"]!["exponent"] = 0,
            json => json["axes"]!["brake"]!["curve"]!["kind"] = "linear"
        };
        foreach (var mutate in mutations)
        {
            var json = Document();
            mutate(json);
            Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
        }

        var text = Encoding.UTF8.GetString(ProfileJson.Serialize(Profile()));
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(
            text.Replace("\"minimum\": 100", "\"minimum\": 1e9999", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":1")]
    public void MalformedOrNonObjectProfilesAreRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void UnknownAxesEmptyProfilesAndOversizedDocumentsAreRejected()
    {
        var json = Document();
        var axes = json["axes"]!.AsObject();
        axes["wheel"] = axes["brake"]!.DeepClone();
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
        axes.Clear();
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Throws<InvalidDataException>(() => ProfileJson.Deserialize(new byte[ProfileJson.MaximumFileBytes + 1]));
        Assert.Throws<ArgumentException>(() => ProfileCurve.PiecewiseLinear(Enumerable.Range(0, 1025).Select(i => new CurvePoint(i / 1024d, i / 1024d))));
    }

    [Fact]
    public void NamesRoundTripUnicodeAndRejectControlCharactersOrInvalidEncoding()
    {
        var profile = Profile("  Гальмо 🏁  ");
        Assert.Equal("Гальмо 🏁", ProfileJson.Deserialize(ProfileJson.Serialize(profile)).Name);
        Assert.Throws<ArgumentException>(() => Profile("Test\u0000Name"));
        Assert.Throws<EncoderFallbackException>(() => Profile("Broken\ud800"));
        Assert.Throws<ArgumentException>(() => Profile(new string('x', 81)));
    }

    internal static PedalProfile Profile(string name = "Test") => new(name, new Dictionary<PedalAxis, AxisProfile>
    {
        [PedalAxis.Brake] = new(new(100, 200), new(.1, .2), ProfileCurve.Power(2), 10, new(.01))
    });

    private static JsonNode Document() => JsonNode.Parse(ProfileJson.Serialize(Profile()))!;

    private static (JsonObject Parent, string Key) Parent(JsonNode json, string path)
    {
        var keys = path.Split('.');
        foreach (var key in keys[..^1])
        {
            json = json[key]!;
        }

        return (json.AsObject(), keys[^1]);
    }
}
