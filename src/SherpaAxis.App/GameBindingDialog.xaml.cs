using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SherpaAxis.App.Localization;
using SherpaAxis.App.Services.Desktop;
using SherpaAxis.App.Services.Persistence;
using SherpaAxis.App.ViewModels;

namespace SherpaAxis.App;

internal sealed partial class GameBindingDialog : Window
{
    private readonly List<(CheckBox Check, GamePreset Game)> presets = [];
    private bool ready;
    private readonly List<string> customExecutables = [];
    private readonly string profileName;
    private readonly HashSet<string> initialProcesses;
    private readonly IReadOnlyList<GameBindingOwner> owners;
    internal string[] Processes { get; private set; } = [];
    internal GameBindingTransfer[] Transfers { get; private set; } = [];

    internal GameBindingDialog(Window owner, string profileName, IReadOnlyList<string> processes, IReadOnlyList<GameBindingOwner>? otherOwners = null,
        IReadOnlySet<string>? runningProcesses = null)
    {
        owners = otherOwners ?? [];
        this.profileName = profileName;
        InitializeComponent();
        processes = processes.Select(GameBindingStore.NormalizeProcess).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        initialProcesses = processes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Owner = owner;
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        MaxHeight = SystemParameters.WorkArea.Height;
        MinHeight = Math.Min(MinHeight, Math.Max(280, MaxHeight - 40));
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, Math.Max(MinHeight, MaxHeight - 40));
        ProfileLabel.Text = profileName;
        ProfileLabel.ToolTip = profileName;
        foreach (var game in GameProfileDetection.Presets)
        {
            var check = new CheckBox
            {
                Content = game.Name,
                DataContext = new GameTileState(game.Name, LoadIcon(game.IconFile), profileName, game.Processes.Any(initialProcesses.Contains), initialProcesses.Count > 0,
                    owners.Where(candidate => candidate.Processes.Intersect(game.Processes, StringComparer.OrdinalIgnoreCase).Any()).Select(candidate => candidate.Name)
                        .Concat(game.Processes.Any(name => processes.Contains(name, StringComparer.OrdinalIgnoreCase)) ? [profileName] : []).Distinct().ToArray()),
                Style = (Style)FindResource("GameTile"),
                IsChecked = game.Processes.All(name => processes.Contains(name, StringComparer.OrdinalIgnoreCase)),
                ToolTip = string.Join(", ", game.Processes.Select(name => name + ".exe"))
            };
            AutomationProperties.SetName(check, game.Name);
            check.Checked += SelectionChanged; check.Unchecked += SelectionChanged;
            PresetGames.Children.Add(check); presets.Add((check, game));
        }
        // Keep partial preset bindings as custom entries rather than silently adding executable names.
        var covered = presets.Where(item => item.Check.IsChecked == true).SelectMany(item => item.Game.Processes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        customExecutables.AddRange(processes.Where(name => !covered.Contains(name)).Select(name => name + ".exe"));
        foreach (var (check, _) in presets) { ((GameTileState)check.DataContext).Selected = check.IsChecked == true; }
        UpdateRunningProcesses(runningProcesses ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        ready = true;
        UpdateCustomGames();
        Localizer.Instance.PropertyChanged += LanguageChanged;
        Closed += (_, _) => Localizer.Instance.PropertyChanged -= LanguageChanged;
    }

    private static BitmapImage LoadIcon(string file)
    {
        var image = new BitmapImage(new Uri("pack://application:,,,/SherpaAxis.App;component/Assets/Games/" + file, UriKind.Absolute));
        image.Freeze();
        return image;
    }

    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Localizer.Language)) { UpdateCustomGames(); }
    }

    private void SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) { return; }
        var preset = presets.First(item => item.Check == sender).Game;
        var names = CustomNames();
        var remaining = names.Where(name => !preset.Processes.Any(process => string.Equals(name, process, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, process + ".exe", StringComparison.OrdinalIgnoreCase))).ToArray();
        // An explicit tile choice replaces legacy custom aliases, so deselecting it really removes that game.
        customExecutables.Clear(); customExecutables.AddRange(remaining);
        ((GameTileState)((CheckBox)sender).DataContext).Selected = ((CheckBox)sender).IsChecked == true;
        UpdateCustomGames();
    }

    private string[] CustomNames() => customExecutables.ToArray();

    internal void UpdateRunningProcesses(IReadOnlySet<string> processes)
    {
        var running = processes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (check, game) in presets) { ((GameTileState)check.DataContext).Running = game.Processes.Any(running.Contains); }
    }

    private void ReassignGameClick(object sender, RoutedEventArgs e)
    {
        var state = (GameTileState)((FrameworkElement)sender).DataContext;
        if (state.ShowReassign) { presets.First(item => item.Game.Name == state.Name).Check.IsChecked = true; }
        e.Handled = true;
    }

    private void UpdateCustomGames()
    {
        AdditionalGames.Children.Clear();
        var names = CustomNames();
        EmptyGamesHint.Visibility = names.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var name in names)
        {
            var row = new DockPanel { Tag = name, Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
            var remove = new Button
            {
                Content = "\uE711",
                FontFamily = new("Segoe MDL2 Assets"),
                FontSize = 11,
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                Margin = new Thickness(12, 0, 0, 0),
                ToolTip = Localizer.Translate("Remove game")
            };
            AutomationProperties.SetName(remove, Localizer.Translate("Remove game") + ": " + name);
            remove.Click += (_, _) => { customExecutables.Remove(name); UpdateCustomGames(); };
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var known = GameProfileDetection.Presets.FirstOrDefault(game => game.Processes.Any(process => string.Equals(process + ".exe", name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(process, name, StringComparison.OrdinalIgnoreCase)));
            if (known is not null)
            {
                row.Children.Add(new Image { Source = LoadIcon(known.IconFile), Width = 24, Height = 24, Margin = new Thickness(0, 0, 12, 0) });
            }
            else
            {
                row.Children.Add(new TextBlock
                {
                    Text = "\uE7FC",
                    FontFamily = new("Segoe MDL2 Assets"),
                    Width = 24,
                    FontSize = 20,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                });
            }
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name });
            string? normalized = null;
            try { normalized = GameBindingStore.NormalizeProcess(name); }
            catch (InvalidDataException) { }
            var transfers = normalized is null ? [] : GameBindingAssignments.Conflicts([normalized], owners);
            var conflicts = owners.Where(candidate => transfers.Any(transfer => string.Equals(transfer.FromProfileFileName, candidate.FileName, StringComparison.OrdinalIgnoreCase)))
                .Select(candidate => candidate.Name).Distinct().ToArray();
            label.Children.Add(new TextBlock
            {
                Text = Localizer.Translate((normalized is not null && initialProcesses.Contains(normalized) ? "Bound to: " : "Bind to: ") + profileName),
                FontSize = 11,
                Foreground = SherpaAxis.App.Themes.AppColors.MutedBrush,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                ToolTip = conflicts.Length > 0 ? Localizer.Translate("Bound to: " + string.Join(", ", conflicts)) : null
            });
            row.Children.Add(label);
            AdditionalGames.Children.Add(row);
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var count = presets.Count(item => item.Check.IsChecked == true) + CustomNames().Length;
        SelectionSummary.Text = Localizer.Translate(count == 0 ? "No games selected" : $"Selected: {count}");
    }

    private void BrowseGameClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = Localizer.Translate("Game executable (*.exe)|*.exe"), Title = Localizer.Translate("Choose game executable") };
        if (picker.ShowDialog(this) != true) { return; }
        try { AddExecutable(picker.FileName); }
        catch (InvalidDataException failure) { ErrorLabel.Text = Localizer.Translate(failure.Message); ErrorLabel.Visibility = Visibility.Visible; }
    }

    internal void AddExecutable(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("Choose a game executable (.exe)."); }
        var name = GameBindingStore.NormalizeProcess(Path.GetFileName(path));
        var preset = presets.FirstOrDefault(item => item.Game.Processes.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (preset.Check is not null) { preset.Check.IsChecked = true; return; }
        if (CustomNames().Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item, name + ".exe", StringComparison.OrdinalIgnoreCase))) { return; }
        customExecutables.Add(name + ".exe");
        UpdateCustomGames();
    }

    internal string[] SelectedProcesses() => presets.Where(item => item.Check.IsChecked == true).SelectMany(item => item.Game.Processes)
        .Concat(CustomNames().Select(GameBindingStore.NormalizeProcess)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal GameBindingTransfer[] SelectedTransfers() => GameBindingAssignments.Conflicts(SelectedProcesses(), owners);

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        try { Processes = SelectedProcesses(); Transfers = SelectedTransfers(); DialogResult = true; }
        catch (InvalidDataException failure)
        {
            ErrorLabel.Text = Localizer.Translate(failure.Message); ErrorLabel.Visibility = Visibility.Visible;
            SaveButton.Focus();
        }
    }

    private sealed class GameTileState(string name, BitmapImage icon, string targetProfile, bool alreadyBoundHere, bool profileHasBindings, string[] boundProfiles) : ObservableObject
    {
        private bool selected, running;
        public string Name { get; } = name;
        public BitmapImage Icon { get; } = icon;
        public bool Selected { get => selected; set { if (Set(ref selected, value)) { Changed(nameof(OwnerStatus)); Changed(nameof(ShowReassign)); } } }
        public bool Running { get => running; set { if (Set(ref running, value)) { Changed(nameof(ShowReassign)); } } }
        public bool ShowReassign => Running && profileHasBindings && !alreadyBoundHere && !Selected;
        public string OwnerStatus => Selected && !alreadyBoundHere
            ? "Bind to: " + targetProfile
            : boundProfiles.Length > 0 ? "Bound to: " + string.Join(", ", boundProfiles) : "Not bound";
    }
}
