using SherpaAxis.App.Services.Persistence;

namespace SherpaAxis.App.Services.Desktop;

internal sealed record GameBindingOwner(string FileName, string Name, string[] Processes);
internal sealed record GameBindingTransfer(string Process, string FromProfileFileName);

internal static class GameBindingAssignments
{
    internal static string[] RequestedProcesses(IEnumerable<string> requested, IEnumerable<GameProfileBinding> otherBindings)
    {
        var names = requested.Select(GameBindingStore.NormalizeProcess).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = otherBindings.SelectMany(binding => binding.Processes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Known games move as a unit, including aliases such as the 32- and 64-bit AC processes.
        foreach (var game in GameProfileDetection.Presets)
        {
            if (game.Processes.Any(names.Contains) && game.Processes.Any(others.Contains)) { names.UnionWith(game.Processes); }
        }
        return names.ToArray();
    }

    internal static GameBindingTransfer[] Conflicts(IEnumerable<string> requested, IReadOnlyList<GameBindingOwner> owners)
    {
        var names = RequestedProcesses(requested, owners.Select(owner => new GameProfileBinding(owner.FileName, owner.Processes))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return owners.SelectMany(owner => owner.Processes.Where(names.Contains).Select(process => new GameBindingTransfer(process, owner.FileName))).ToArray();
    }

    internal static GameProfileBinding[] Reassign(IReadOnlyList<GameProfileBinding> current, string target,
        IEnumerable<string> requested, IReadOnlyList<GameBindingTransfer> approved)
    {
        var otherBindings = current.Where(binding => !string.Equals(binding.ProfileFileName, target, StringComparison.OrdinalIgnoreCase)).ToArray();
        var names = RequestedProcesses(requested, otherBindings).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in otherBindings)
        {
            foreach (var process in binding.Processes.Where(names.Contains))
            {
                if (!approved.Any(transfer => string.Equals(transfer.Process, process, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(transfer.FromProfileFileName, binding.ProfileFileName, StringComparison.OrdinalIgnoreCase)))
                { throw new InvalidOperationException("Game bindings changed. Reopen the dialog and review the assignment before saving."); }
            }
        }
        return otherBindings.Select(binding => new GameProfileBinding(binding.ProfileFileName, binding.Processes.Where(process => !names.Contains(process)).ToArray()))
            .Where(binding => binding.Processes.Length > 0)
            .Concat(names.Count > 0 ? [new GameProfileBinding(target, names.ToArray())] : []).ToArray();
    }
}
