namespace SherpaAxis.App.Models;

public sealed record FilterExplanation(string Name, string WhatItDoes, string DrivingFeel);

public static class FilteringGuide
{
    public static IReadOnlyList<FilterExplanation> Algorithms { get; } =
    [
        new("Off / bypass", "Passes the input through without smoothing.",
            "The most direct response, including any sensor jitter. Curves and deadzones still apply."),
        new("Exponential", "Moves the filtered signal toward each new input. A larger time constant makes it move more slowly.",
            "Calms small fluctuations but also softens quick pedal changes. Sharp uses 3 ms; Balanced uses 10 ms."),
        new("Two-stage exponential", "Runs the signal through two smoothing stages.",
            "Smoother transitions with more delay. A quick brake press or release may feel softer. Smooth uses 20 ms for each stage."),
        new("Moving average", "Averages the most recent reports. A larger window includes more history.",
            "Reduces chatter, but quick changes are spread over the window. Its delay depends on report rate, not the time-constant field."),
        new("Median", "Sorts the recent readings and chooses the middle value. Isolated high or low spikes are often removed.",
            "Useful for occasional spikes. Genuine fast changes can also be delayed until enough new readings enter the window."),
        new("Weighted moving average", "Averages recent reports while giving more weight to the newest ones.",
            "Balances smoothing with a stronger response to recent movement than an equal-weight average of the same window."),
        new("Median + exponential", "Removes spikes with a median window, then smooths that result. Both window and time constant matter.",
            "Combines spike rejection and smoothing, with added delay. Spike guard uses 5 reports followed by an 8 ms exponential stage.")
    ];
    public const string Summary = "Filtering affects deliberate pedal movements as well as noise. Larger settings usually add delay. At 500 reports/s, five reports span about 8 ms. None of these algorithms detects release, repairs baseline drift, or changes hardware calibration. Apply settings, then compare Input vs filtered or Filtered vs curved. Use Filter difference to magnify small changes.";
}
