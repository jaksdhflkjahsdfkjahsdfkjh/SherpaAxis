using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SherpaAxis.App.Services;

namespace SherpaAxis.App;

internal sealed class ProfileNameDialog : Window
{
    private readonly TextBox name;
    internal string ProfileName => name.Text.Trim();
    internal ProfileNameDialog(Window owner, string title, string initial)
    {
        Owner = owner; Title = Localization.Localizer.Translate(title); Width = 420; Height = 190; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        Background = new SolidColorBrush(Color.FromRgb(17, 30, 48)); Foreground = Brushes.White;
        Resources.MergedDictionaries.Add(owner.Resources);
        var panel = new StackPanel { Margin = new Thickness(20) };
        name = new TextBox { Text = initial, MaxLength = 80, Margin = new Thickness(0, 0, 0, 18) };
        panel.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = Localization.Localizer.Translate("Save"), IsDefault = true };
        save.Click += (_, _) => { if (ProfileName.Length > 0 && !ProfileName.Any(char.IsControl)) { DialogResult = true; } };
        actions.Children.Add(save); actions.Children.Add(new Button { Content = Localization.Localizer.Translate("Cancel"), IsCancel = true });
        panel.Children.Add(actions); Content = panel;
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
