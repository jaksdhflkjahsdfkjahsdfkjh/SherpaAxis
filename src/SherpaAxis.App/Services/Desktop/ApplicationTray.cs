using System.ComponentModel;
using System.Drawing;
using SherpaAxis.App.Models;
using SherpaAxis.App.Services;
using Forms = System.Windows.Forms;

namespace SherpaAxis.App.Services.Desktop;

public sealed class ApplicationTray : IApplicationTray
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Icon image;
    private readonly Func<TrayMenuState> state;
    private bool disposed;
    public event Action<TrayCommand>? CommandRequested;
    public event Action<ProfileTab>? ProfileRequested;

    public ApplicationTray(Func<TrayMenuState> state)
    {
        this.state = state;
        using var stream = System.Windows.Application.GetResourceStream(new Uri("/SherpaAxis.App;component/Assets/SherpaAxis.ico", UriKind.Relative))!.Stream;
        image = new Icon(stream);
        menu.Renderer = new Forms.ToolStripProfessionalRenderer(new TrayColors());
        menu.BackColor = Color.FromArgb(23, 39, 59);
        menu.ForeColor = Color.FromArgb(232, 239, 248);
        menu.ShowImageMargin = false;
        menu.Opening += (_, _) => RebuildMenu();
        icon = new Forms.NotifyIcon { Icon = image, Text = Localization.Localizer.Translate("SherpaAxis · Pedals Utility"), ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => CommandRequested?.Invoke(TrayCommand.Show);
        PropertyChangedEventManager.AddHandler(Localization.Localizer.Instance, LanguageChanged, nameof(Localization.Localizer.Language));
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) => icon.Text = Localization.Localizer.Translate("SherpaAxis · Pedals Utility");

    internal void RebuildMenu()
    {
        foreach (Forms.ToolStripItem item in menu.Items.Cast<Forms.ToolStripItem>().ToArray()) { item.Dispose(); }
        menu.Items.Clear();
        var current = state();
        icon.Text = Localization.Localizer.Translate("SherpaAxis · Pedals Utility");
        Add("Open SherpaAxis", TrayCommand.Show);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("Tune", TrayCommand.Tune, current.CanSelectMode, current.Mode == SidePanelMode.Tuning);
        Add("Calibrate", TrayCommand.Calibrate, current.CanSelectMode, current.Mode == SidePanelMode.Calibration);
        var profiles = new Forms.ToolStripMenuItem(Localization.Localizer.Translate("Switch profile")) { Enabled = current.CanSwitchProfile, ForeColor = menu.ForeColor };
        profiles.DropDown.Renderer = menu.Renderer;
        profiles.DropDown.BackColor = menu.BackColor;
        foreach (var profile in current.Profiles)
        {
            var item = new Forms.ToolStripMenuItem(profile.Name.Replace("&", "&&", StringComparison.Ordinal)) { Checked = ReferenceEquals(profile, current.SelectedProfile), ForeColor = menu.ForeColor };
            item.Click += (_, _) => ProfileRequested?.Invoke(profile);
            profiles.DropDownItems.Add(item);
        }
        menu.Items.Add(profiles);
        Add("Settings", TrayCommand.Settings, current.CanSelectMode, current.Mode == SidePanelMode.Settings);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("Exit", TrayCommand.Exit);
    }

    private void Add(string text, TrayCommand command, bool enabled = true, bool selected = false)
    {
        var item = new Forms.ToolStripMenuItem(Localization.Localizer.Translate(text)) { Enabled = enabled, Checked = selected, ForeColor = menu.ForeColor };
        item.Click += (_, _) => CommandRequested?.Invoke(command);
        menu.Items.Add(item);
    }
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        PropertyChangedEventManager.RemoveHandler(Localization.Localizer.Instance, LanguageChanged, nameof(Localization.Localizer.Language));
        icon.Visible = false; icon.Dispose(); menu.Dispose(); image.Dispose();
        GC.SuppressFinalize(this);
    }
    private sealed class TrayColors : Forms.ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(23, 39, 59);
        public override Color MenuItemSelected => Color.FromArgb(41, 66, 85);
        public override Color MenuItemSelectedGradientBegin => MenuItemSelected;
        public override Color MenuItemSelectedGradientEnd => MenuItemSelected;
        public override Color MenuBorder => Color.FromArgb(52, 73, 96);
        public override Color MenuItemBorder => Color.FromArgb(69, 214, 187);
        public override Color CheckBackground => MenuItemSelected;
        public override Color CheckSelectedBackground => MenuItemSelected;
    }
}
