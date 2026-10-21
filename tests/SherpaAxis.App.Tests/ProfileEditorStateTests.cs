using SherpaAxis.App.Models;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App.Tests;

public sealed class ProfileEditorStateTests
{
    [Fact]
    public void ProfileAssessmentSeparatesUnsavedSettingsFromInvalidText()
    {
        var model = new DashboardViewModel(new DashboardEngine());
        model.Editor.LowerDeadzone = "12";
        model.RefreshUnsavedState();
        Assert.Contains("Unsaved", model.DraftStatus, StringComparison.Ordinal);
        model.Editor.LowerDeadzone = "0";
        model.RefreshUnsavedState();
        Assert.DoesNotContain("Unapplied", model.DraftStatus, StringComparison.Ordinal);
        model.Editor.LowerDeadzone = "unfinished";
        model.RefreshUnsavedState(); model.RefreshUnsavedState();
        Assert.NotEmpty(model.ValidationMessage);
        var draft = model.SelectedProfile!;
        model.Load(new("Another", DashboardEngine.DefaultProfile().Axes));
        model.SwitchProfile(draft);
        Assert.Equal("unfinished", model.Editor.LowerDeadzone);
        model.Editor.LowerDeadzone = "5";
        model.Apply(); model.RefreshUnsavedState();
        Assert.DoesNotContain("Unapplied", model.DraftStatus, StringComparison.Ordinal);
        Assert.True(model.SelectedProfile!.HasUnsavedChanges);
    }

    [Fact]
    public void UnchangedDraftChecksAvoidRepeatedProfileSerialization()
    {
        var model = new DashboardViewModel(new DashboardEngine());
        model.RefreshUnsavedState();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) { model.RefreshUnsavedState(); }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 200_000, $"Unchanged draft refresh allocated {allocated} bytes.");
    }
}
