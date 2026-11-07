using System.ComponentModel;
using System.Diagnostics;
using SherpaAxis.App.Services.Persistence;

namespace SherpaAxis.App.Services.Desktop;

internal sealed record GamePreset(string Name, string[] Processes, string IconFile);

internal static class GameProfileDetection
{
    internal static IReadOnlyList<GamePreset> Presets { get; } =
    [
        new("Assetto Corsa", ["acs", "acs_x86"], "assetto-corsa.jpg"),
        new("Assetto Corsa Competizione", ["AC2-Win64-Shipping"], "assetto-corsa-competizione.jpg"),
        new("iRacing", ["iRacingSim64DX11"], "iracing.jpg"),
        new("Le Mans Ultimate", ["Le Mans Ultimate"], "le-mans-ultimate.jpg"),
    ];

    internal static IReadOnlySet<string> RunningProcesses()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { names.Add(process.ProcessName); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
        }
        return names;
    }
}

// Consume a launch once, so a manual profile choice is respected until another bound game starts.
internal sealed class GameProfileSelection
{
    private readonly List<string> running = [];
    private string? handled;
    internal string? Pending { get; private set; }

    internal void Observe(IEnumerable<GameProfileBinding> bindings, IReadOnlySet<string> processes)
    {
        var names = processes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = bindings.Where(binding => binding.Processes.Any(process => names.Contains(process)))
            .Select(binding => binding.ProfileFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        running.RemoveAll(name => !matches.Contains(name));
        foreach (var name in matches.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!running.Contains(name, StringComparer.OrdinalIgnoreCase)) { running.Add(name); }
        }
        var current = running.LastOrDefault();
        if (!string.Equals(current, handled, StringComparison.OrdinalIgnoreCase)) { Pending = current; }
        else { Pending = null; }
        if (current is null) { handled = null; }
    }

    internal void Acknowledge() { handled = Pending; Pending = null; }
    internal void Reset() { running.Clear(); handled = Pending = null; }
}
