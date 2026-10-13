using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;

namespace SherpaAxis.App.Localization;

public sealed class Localizer : INotifyPropertyChanged
{
    private readonly Dictionary<string, string> ukrainian;
    private readonly Dictionary<string, string> keys;
    private readonly (Regex Pattern, string Translation)[] templates;
    private string language = "en";
    public static Localizer Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    private Localizer()
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream("SherpaAxis.App.Localization.uk.json")!;
        ukrainian = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        keys = ukrainian.Keys.ToDictionary(KeyFor, StringComparer.Ordinal);
        templates = ukrainian.Where(pair => Regex.IsMatch(pair.Key, @"\{\d+\}"))
            .OrderByDescending(pair => pair.Key.Length)
            .Select(pair => (new Regex("\\A" + Regex.Replace(Regex.Escape(pair.Key), @"\\\{(\d+)\}", match => $"(?<v{match.Groups[1].Value}>.*?)") + "\\z",
                RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)), pair.Value)).ToArray();
    }

    public string Language
    {
        get => language;
        set
        {
            var next = NormalizeLanguage(value);
            if (language == next) { return; }
            language = next;
            PropertyChanged?.Invoke(this, new(nameof(Language)));
            PropertyChanged?.Invoke(this, new("Item[]"));
        }
    }
    public string this[string key] => Translate(keys.TryGetValue(key, out var source) ? source : key);
    public static string NormalizeLanguage(string? value) => value is "uk" or "uk-UA" ? "uk" : "en";
    public static string KeyFor(string source) => "T" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
    public static string Translate(string source) => Instance.TranslateText(source, Instance.Language);
    public static void Bind(DependencyObject target, DependencyProperty property, string source) => BindingOperations.SetBinding(target, property,
        new Binding($"[{KeyFor(source)}]") { Source = Instance, Mode = BindingMode.OneWay });
    public IReadOnlyDictionary<string, string> Translations => ukrainian;

    public string TranslateText(string source, string code) => TranslateText(source, code, 0);
    private string TranslateText(string source, string code, int depth)
    {
        if (NormalizeLanguage(code) != "uk" || source.Length == 0 || depth > 4 || !source.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) { return source; }
        if (ukrainian.TryGetValue(source, out var translated)) { return translated; }
        if (source.Contains('\n')) { return string.Join("\n", source.Split('\n').Select(line => TranslateText(line, code, depth + 1))); }
        if (source.Length <= 4096)
        {
            foreach (var (pattern, translation) in templates)
            {
                var match = pattern.Match(source);
                if (!match.Success) { continue; }
                return Regex.Replace(translation, @"\{(\d+)(:t)?\}", token =>
                {
                    var text = match.Groups["v" + token.Groups[1].Value].Value;
                    return token.Groups[2].Success ? TranslateText(text, code, depth + 1) : text;
                });
            }
        }
        foreach (var separator in new[] { "   ·   ", " · " })
        {
            if (source.Contains(separator, StringComparison.Ordinal))
            { return string.Join(separator, source.Split(separator, StringSplitOptions.None).Select(part => TranslateText(part, code, depth + 1))); }
        }
        return source;
    }
}
