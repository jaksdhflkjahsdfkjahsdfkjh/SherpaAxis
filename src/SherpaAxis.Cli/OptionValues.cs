using System.Globalization;

namespace SherpaAxis.Cli;

internal sealed class OptionValues
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public OptionValues(string[] arguments, IEnumerable<string> allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var option = arguments[index];
            if (!names.Contains(option) || index + 1 >= arguments.Length ||
                string.IsNullOrWhiteSpace(arguments[index + 1]) || !values.TryAdd(option, arguments[index + 1]))
            {
                throw new ArgumentException($"Unknown, missing, or repeated option: {option}");
            }
        }
    }

    public bool Contains(string key) => values.ContainsKey(key);
    public string? Get(string key) => values.GetValueOrDefault(key);
    public string Required(string key) => Get(key) ?? throw new ArgumentException($"Required option: {key}.");

    public double Number(string key, double? fallback = null)
    {
        if (!values.TryGetValue(key, out var value) && fallback.HasValue)
        {
            return fallback.Value;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            throw new ArgumentException($"{key} requires a finite number using a decimal point.");
        }

        return number;
    }

    public bool Boolean(string key, bool fallback = false)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return bool.TryParse(value, out var result) ? result : throw new ArgumentException($"{key} requires true or false.");
    }
}
