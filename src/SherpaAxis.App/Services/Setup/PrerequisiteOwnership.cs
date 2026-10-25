using System.IO;

namespace SherpaAxis.App.Services.Setup;

internal sealed record OwnedDriver(string PublishedName, string Hash);
internal sealed record OwnedPrerequisite(PrerequisiteStep Component, OwnedDriver[] Drivers, string? UninstallerHash = null, string? ProductVersion = null);

internal sealed class PrerequisiteRemoval(Func<IReadOnlyList<OwnedPrerequisite>> owned,
    Func<OwnedPrerequisite, bool> unchanged, Func<OwnedPrerequisite, CancellationToken, Task<int>> remove,
    Action<PrerequisiteStep> forget)
{
    internal async Task<bool> RemoveAsync(IProgress<string> progress, CancellationToken token)
    {
        var restart = false;
        foreach (var item in owned())
        {
            token.ThrowIfCancellationRequested();
            if (!unchanged(item))
            { throw new IOException($"{item.Component} has changed since SherpaAxis installed it. It was preserved; use Windows Apps to review it."); }
            progress.Report($"Removing {item.Component} installed by SherpaAxis…");
            var code = await remove(item, token).ConfigureAwait(false);
            if (code is not (0 or 3010))
            { throw new IOException($"{item.Component} removal failed (code {code}). The ownership record was kept so you can retry."); }
            forget(item.Component);
            restart |= code == 3010;
        }
        return restart;
    }
}
