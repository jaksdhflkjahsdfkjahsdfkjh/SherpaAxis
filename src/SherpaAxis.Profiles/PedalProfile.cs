using System.Collections.ObjectModel;
using System.Text;

namespace SherpaAxis.Profiles;

public sealed class PedalProfile
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public PedalProfile(string name, IReadOnlyDictionary<PedalAxis, AxisProfile> axes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(axes);
        name = name.Trim();
        _ = StrictUtf8.GetByteCount(name);
        if (name.Length > 80 || name.Any(char.IsControl))
        {
            throw new ArgumentException("Profile names must have at most 80 characters and no control characters.", nameof(name));
        }

        var copy = new Dictionary<PedalAxis, AxisProfile>(axes);
        if (copy.Count is < 1 or > 3 || copy.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value is null))
        {
            throw new ArgumentException("Profiles require settings for one or more valid pedals.", nameof(axes));
        }

        Name = name;
        Axes = new ReadOnlyDictionary<PedalAxis, AxisProfile>(copy);
    }

    public string Name { get; }
    public IReadOnlyDictionary<PedalAxis, AxisProfile> Axes { get; }
}
