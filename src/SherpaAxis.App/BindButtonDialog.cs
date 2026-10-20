using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Input;
using SherpaAxis.Profiles;

namespace SherpaAxis.App;

internal sealed class BindButtonDialog : Window
{
    internal CorrectionButton? Binding { get; private set; }
    internal BindButtonDialog(Window owner, IButtonStateSource source)
    {
        Owner = owner; Title = Localization.Localizer.Translate("Bind baseline correction"); Width = 440; Height = 220;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        Background = new SolidColorBrush(Color.FromRgb(17, 30, 48)); Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(owner.Resources);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = Localization.Localizer.Translate("Press a keyboard key or wheel/gamepad button"), FontSize = 16, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = Localization.Localizer.Translate("Escape cancels. After binding, hold that control only while the pedal is fully released."),
            FontSize = 12,
            Foreground = Brushes.LightSlateGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 18)
        });
        var cancel = new Button { Content = Localization.Localizer.Translate("Cancel"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(cancel); Content = panel;
        void Pressed(CorrectionButton button)
        {
            if (button.Kind == "keyboard" && button.Code == KeyInterop.VirtualKeyFromKey(Key.Escape)) { DialogResult = false; return; }
            Binding = button; DialogResult = true;
        }
        source.Pressed += Pressed;
        Closed += (_, _) => source.Pressed -= Pressed;
    }
}
