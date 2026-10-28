using SherpaAxis.App.Services.Input;
using SherpaAxis.Profiles;

namespace SherpaAxis.App.Tests;

public sealed class ButtonActionTests
{
    private static readonly CorrectionButton Key = new("keyboard", 65);
    private static readonly CorrectionButton Wheel = new("controller", 21, "wheel");

    [Fact]
    public void TextEditingSuppressesKeyboardActionsWithoutInterruptingControllerActions()
    {
        using var buttons = new ButtonStateBuffer();
        buttons.Set(Wheel, true);
        var revision = buttons.GetConnectionRevision(Wheel);
        buttons.SetKeyboardEditing(true);
        buttons.Set(Key, true);
        Assert.False(buttons.IsDown(Key));
        Assert.True(buttons.IsDown(Wheel));
        Assert.Equal(revision, buttons.GetConnectionRevision(Wheel));
        buttons.SetKeyboardEditing(false);
        Assert.False(buttons.IsDown(Key));
        buttons.Set(Key, false); buttons.Set(Key, true);
        Assert.True(buttons.IsDown(Key));
    }

    [Theory]
    [InlineData("keyboard", 65, null)]
    [InlineData("controller", 21, "wheel")]
    public void BindingCaptureReceivesPressesButActionsRequireReleaseBeforeRearming(string kind, int code, string? device)
    {
        using var buttons = new ButtonStateBuffer();
        var button = new CorrectionButton(kind, code, device);
        CorrectionButton? captured = null;
        buttons.Pressed += value => captured = value;
        var revision = buttons.GetConnectionRevision(button);
        buttons.SetBindingCapture(true);
        buttons.Set(button, true);
        Assert.Equal(button, captured);
        Assert.False(buttons.IsDown(button));
        Assert.NotEqual(revision, buttons.GetConnectionRevision(button));
        buttons.SetBindingCapture(false);
        Assert.False(buttons.IsDown(button));
        buttons.Set(button, false); buttons.Set(button, true);
        Assert.True(buttons.IsDown(button));
    }

    [Fact]
    public void ClosingBindingCaptureCannotCancelTextEditingSuppression()
    {
        using var buttons = new ButtonStateBuffer();
        buttons.SetKeyboardEditing(true);
        buttons.SetBindingCapture(true);
        buttons.SetBindingCapture(false);
        buttons.Set(Key, true);
        Assert.False(buttons.IsDown(Key));
        buttons.Set(Wheel, true);
        Assert.True(buttons.IsDown(Wheel));
    }
}

[Collection("WPF UI")]
public sealed class ButtonActionUiTests
{
    [Fact]
    public Task TextEntryDialogsKeepKeyboardActionsSuppressedAndRestoreThemAfterFailure() => HardwarePedalUiTests.OnUiAsync(async () =>
    {
        var buttons = new ButtonStateBuffer();
        var desktop = new SettingsAndTrayTests.FakeDesktop();
        await using var main = new MainWindow(new(), desktopServices: desktop.Services with { Buttons = _ => buttons });
        await main.InitializeForLaunchAsync();
        var key = new CorrectionButton("keyboard", 65);
        var wheel = new CorrectionButton("controller", 21, "wheel");
        Assert.Throws<InvalidOperationException>(() => main.ShowTextEntryDialog(() =>
        {
            buttons.Set(key, true); buttons.Set(wheel, true);
            Assert.False(buttons.IsDown(key));
            Assert.True(buttons.IsDown(wheel));
            main.ShowTextEntryDialog(() => false);
            Assert.False(buttons.IsDown(key));
            throw new InvalidOperationException("Dialog failed");
        }));
        Assert.False(buttons.IsDown(key));
        buttons.Set(key, false); buttons.Set(key, true);
        Assert.True(buttons.IsDown(key));
    });
}
