using System.Globalization;
using SherpaAxis.Core;

namespace SherpaAxis.App.Models;

public static class FilterPresets
{
    public static IReadOnlyList<string> Names { get; } = ["Off", "Sharp", "Balanced", "Smooth", "Spike guard", "Custom"];

    public static LowPassFilterSettings? Create(string name) => name switch
    {
        "Off" => null,
        "Sharp" => new(.003),
        "Balanced" => new(.01),
        "Smooth" => new(.02, kind: SignalFilterKind.TwoStageExponential),
        "Spike guard" => new(.008, kind: SignalFilterKind.MedianExponential, windowSamples: 5),
        _ => throw new ArgumentException("Choose a filter preset.", nameof(name))
    };

    public static string Identify(AxisEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!editor.FilterEnabled) { return "Off"; }
        foreach (var name in Names.Where(n => n is not "Off" and not "Custom"))
        {
            var preset = Create(name)!;
            if (editor.FilterKind == preset.Kind.ToString() && Number(editor.FilterMilliseconds) == preset.TimeConstantSeconds * 1000
                && Number(editor.FilterGapMilliseconds) == preset.ResetAfterSeconds * 1000 && Number(editor.FilterWindowSamples) == preset.WindowSamples) { return name; }
        }
        return "Custom";
    }

    private static double Number(string text) => double.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
}
