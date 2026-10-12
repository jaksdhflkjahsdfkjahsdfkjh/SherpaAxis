using SherpaAxis.Core;

namespace SherpaAxis.App.Models;

public sealed record FilterExplanation(string Name, string WhatItDoes, string DrivingFeel, string ExampleKey)
{
    public LowPassFilterSettings? ExampleFilter => ExampleKey switch
    {
        "off" => null,
        "exponential" => new(.01),
        "two-stage" => new(.02, kind: SignalFilterKind.TwoStageExponential),
        "moving-average" => new(.01, kind: SignalFilterKind.MovingAverage, windowSamples: 11),
        "median" => new(.01, kind: SignalFilterKind.Median, windowSamples: 11),
        "weighted-average" => new(.01, kind: SignalFilterKind.WeightedMovingAverage, windowSamples: 11),
        "median-exponential" => new(.008, kind: SignalFilterKind.MedianExponential, windowSamples: 5),
        _ => throw new InvalidOperationException("Unknown filter example.")
    };
    public string ExampleSettings => ExampleKey switch
    {
        "off" => "Bypass · input and filtered lines overlap exactly",
        "exponential" => "Time constant: 10 ms (Balanced)",
        "two-stage" => "Time constant: 20 ms per stage (Smooth)",
        "median-exponential" => "5 reports + 8 ms exponential (Spike guard)",
        _ => "Window: 11 reports · about 20 ms of input at 500 Hz"
    };
    public IReadOnlyList<FilterScreenshot> Examples =>
    [
        new(Name, "Press and release", "Compare how the filtered signal follows a full brake movement.", ImagePath("movement")),
        new(Name, "Small variations · zoomed", "The vertical range is magnified. These are recorded changes, not added noise.", ImagePath("detail"))
    ];
    private string ImagePath(string scenario) => $"/SherpaAxis.App;component/Assets/FilterExamples/{ExampleKey}-{scenario}.png";
}

public sealed record FilterScreenshot(string FilterName, string Title, string Caption, string ImagePath)
{
    public string FullTitle => $"{FilterName} · {Title}";
}

public static class FilteringGuide
{
    public static IReadOnlyList<FilterExplanation> Algorithms { get; } =
    [
        new("Off / bypass", "Passes the input through without smoothing.",
            "The most direct response, including any sensor jitter. Curves and deadzones still apply.", "off"),
        new("Exponential", "Moves the filtered signal toward each new input. A larger time constant makes it move more slowly.",
            "Calms small fluctuations but also softens quick pedal changes. Sharp uses 3 ms; Balanced uses 10 ms.", "exponential"),
        new("Two-stage exponential", "Runs the signal through two smoothing stages.",
            "Smoother transitions with more delay. A quick brake press or release may feel softer. Smooth uses 20 ms for each stage.", "two-stage"),
        new("Moving average", "Averages the most recent reports. A larger window includes more history.",
            "Reduces chatter, but quick changes are spread over the window. Its delay depends on report rate, not the time-constant field.", "moving-average"),
        new("Median", "Sorts the recent readings and chooses the middle value. Isolated high or low spikes are often removed.",
            "Useful for occasional spikes. Genuine fast changes can also be delayed until enough new readings enter the window.", "median"),
        new("Weighted moving average", "Averages recent reports while giving more weight to the newest ones.",
            "Balances smoothing with a stronger response to recent movement than an equal-weight average of the same window.", "weighted-average"),
        new("Median + exponential", "Removes spikes with a median window, then smooths that result. Both window and time constant matter.",
            "Combines spike rejection and smoothing, with added delay. Spike guard uses 5 reports followed by an 8 ms exponential stage.", "median-exponential")
    ];
    public const string ExampleSource = "Recorded examples: your October 18 brake capture, 37,684 reports at about 500 Hz. Each filter uses the same two segments. Dashed grey = original input; orange = filtered input. No synthetic noise, curves, deadzones or baseline correction. Example settings may differ from your current settings.";
    public const string Summary = "Filtering affects deliberate pedal movements as well as noise. Larger settings usually add delay. At 500 reports/s, five reports span about 8 ms. None of these algorithms detects release, repairs baseline drift, or changes hardware calibration. Apply settings, then compare Input vs filtered or Filtered vs curved. Use Filter difference to magnify small changes.";
}
