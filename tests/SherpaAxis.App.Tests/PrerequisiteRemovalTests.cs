using System.IO;
using SherpaAxis.App.Services.Setup;

namespace SherpaAxis.App.Tests;

public sealed class PrerequisiteRemovalTests
{
    private static OwnedPrerequisite Item(PrerequisiteStep step) => new(step, [new("oem42.inf", new('A', 64))], new('B', 64));

    [Fact]
    public async Task OnlyRecordedComponentsAreRemovedAndRestartRequestsAreRetained()
    {
        var items = new[] { Item(PrerequisiteStep.HidHide), Item(PrerequisiteStep.DfuSe) };
        var removed = new List<PrerequisiteStep>();
        var forgotten = new List<PrerequisiteStep>();
        var service = new PrerequisiteRemoval(() => items, _ => true, (item, _) =>
        { removed.Add(item.Component); return Task.FromResult(item.Component == PrerequisiteStep.HidHide ? 3010 : 0); }, forgotten.Add);
        Assert.True(await service.RemoveAsync(new Progress<string>(), CancellationToken.None));
        Assert.Equal([PrerequisiteStep.HidHide, PrerequisiteStep.DfuSe], removed);
        Assert.Equal(removed, forgotten);
    }

    [Fact]
    public async Task MissingOwnershipRecordsNeverRemovePreExistingDrivers()
    {
        var service = new PrerequisiteRemoval(() => [], _ => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        Assert.False(await service.RemoveAsync(new Progress<string>(), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailuresAndTimeoutsKeepTheRecordAndStopFurtherRemoval(bool timeout)
    {
        var items = new[] { Item(PrerequisiteStep.VJoy), Item(PrerequisiteStep.DfuSe) };
        var calls = 0;
        var service = new PrerequisiteRemoval(() => items, _ => true, (_, _) =>
        { calls++; return timeout ? Task.FromException<int>(new TimeoutException()) : Task.FromResult(1603); }, _ => Assert.Fail("Failed removal must retain ownership."));
        if (timeout) { await Assert.ThrowsAsync<TimeoutException>(() => service.RemoveAsync(new Progress<string>(), CancellationToken.None)); }
        else { await Assert.ThrowsAsync<IOException>(() => service.RemoveAsync(new Progress<string>(), CancellationToken.None)); }
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ChangedDriverPackagesArePreservedBeforeAnyUninstallerStarts()
    {
        var service = new PrerequisiteRemoval(() => [Item(PrerequisiteStep.VJoy)], _ => false,
            (_, _) => throw new InvalidOperationException(), _ => Assert.Fail("Changed driver must remain recorded."));
        var error = await Assert.ThrowsAsync<IOException>(() => service.RemoveAsync(new Progress<string>(), CancellationToken.None));
        Assert.Contains("has changed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDoesNotForgetOrRemoveDrivers()
    {
        var service = new PrerequisiteRemoval(() => [Item(PrerequisiteStep.DfuSe)], _ => true,
            (_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException());
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RemoveAsync(new Progress<string>(), stop.Token));
    }

    [Theory]
    [InlineData("../other.inf")]
    [InlineData("C:\\Windows\\other.inf")]
    [InlineData("oem42.inf /force")]
    [InlineData("vjoy.inf")]
    public void OwnershipCannotSupplyArbitraryDriverPathsOrCommandArguments(string name)
    {
        var item = Item(PrerequisiteStep.VJoy) with { Drivers = [new(name, new('A', 64))] };
        Assert.Throws<InvalidDataException>(() => WindowsPrerequisiteOwnership.Validate(item, PrerequisiteStep.VJoy));
    }

    [Fact]
    public void OwnershipRejectsMissingHashesAndUnknownComponents()
    {
        var item = Item(PrerequisiteStep.VJoy);
        WindowsPrerequisiteOwnership.Validate(item, PrerequisiteStep.VJoy);
        Assert.Throws<InvalidDataException>(() => WindowsPrerequisiteOwnership.Validate(item with { UninstallerHash = null }, PrerequisiteStep.VJoy));
        Assert.Throws<InvalidDataException>(() => WindowsPrerequisiteOwnership.Validate(item with { Drivers = [new("oem42.inf", "bad")] }, PrerequisiteStep.VJoy));
        Assert.Throws<InvalidDataException>(() => WindowsPrerequisiteOwnership.Validate(item, PrerequisiteStep.VirtualDevice));
    }
}
