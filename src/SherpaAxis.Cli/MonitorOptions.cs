using System.Globalization;

namespace SherpaAxis.Cli;

internal sealed record MonitorOptions(TimeSpan Duration, string? CsvPath, string? DevicePath)
{
    public static MonitorOptions Parse(string[] arguments)
    {
        var seconds = 10;
        string? csv = null;
        string? device = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var option = arguments[index];
            if (index + 1 >= arguments.Length || !seen.Add(option))
            {
                throw new ArgumentException($"Missing or repeated option: {option}");
            }

            var value = arguments[index + 1];
            switch (option)
            {
                case "--seconds":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds is < 1 or > 3600)
                    {
                        throw new ArgumentException("--seconds must be an integer between 1 and 3600.");
                    }

                    break;
                case "--csv" when !string.IsNullOrWhiteSpace(value):
                    csv = value;
                    break;
                case "--device" when !string.IsNullOrWhiteSpace(value):
                    device = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown or invalid option: {option}");
            }
        }

        return new MonitorOptions(TimeSpan.FromSeconds(seconds), csv, device);
    }
}
