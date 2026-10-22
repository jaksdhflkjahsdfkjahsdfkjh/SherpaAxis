using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using SherpaAxis.App.Models;
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
        menu.Renderer = new TrayRenderer();
        menu.BackColor = Color.FromArgb(23, 39, 59);
        menu.ForeColor = Color.FromArgb(232, 239, 248);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Opening += (_, _) => RebuildMenu();
        icon = new Forms.NotifyIcon { Icon = image, Text = Localization.Localizer.Translate("SherpaAxis · Pedals Utility"), ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => CommandRequested?.Invoke(TrayCommand.Show);
        icon.BalloonTipClicked += (_, _) => CommandRequested?.Invoke(TrayCommand.ViewUpdate);
        PropertyChangedEventManager.AddHandler(Localization.Localizer.Instance, LanguageChanged, nameof(Localization.Localizer.Language));
    }

    private void LanguageChanged(object? sender, PropertyChangedEventArgs e) => icon.Text = Localization.Localizer.Translate("SherpaAxis · Pedals Utility");
    public void NotifyUpdate(string version)
    {
        if (disposed) { return; }
        icon.ShowBalloonTip(5000, Localization.Localizer.Translate("SherpaAxis update available"),
            Localization.Localizer.Translate($"{version} is available. Click to view the release."), Forms.ToolTipIcon.Info);
    }

    internal void RebuildMenu()
    {
        foreach (Forms.ToolStripItem item in menu.Items.Cast<Forms.ToolStripItem>().ToArray()) { item.Dispose(); }
        menu.Items.Clear();
        var current = state();
        icon.Text = Localization.Localizer.Translate("SherpaAxis · Pedals Utility");
        Add("Open SherpaAxis", TrayCommand.Show);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("Tune", TrayCommand.Tune, current.CanSelectMode);
        Add("Calibrate", TrayCommand.Calibrate, current.CanSelectMode);
        var profiles = new Forms.ToolStripMenuItem(Localization.Localizer.Translate("Switch profile")) { Enabled = current.CanSwitchProfile, ForeColor = menu.ForeColor };
        profiles.DropDown.Renderer = menu.Renderer;
        profiles.DropDown.BackColor = menu.BackColor;
        profiles.DropDown.ForeColor = menu.ForeColor;
        if (profiles.DropDown is Forms.ToolStripDropDownMenu profileMenu)
        { profileMenu.ShowImageMargin = false; profileMenu.ShowCheckMargin = true; }
        foreach (var profile in current.Profiles)
        {
            var item = new Forms.ToolStripMenuItem(profile.Name.Replace("&", "&&", StringComparison.Ordinal)) { Checked = ReferenceEquals(profile, current.SelectedProfile), ForeColor = menu.ForeColor };
            item.Click += (_, _) => ProfileRequested?.Invoke(profile);
            profiles.DropDownItems.Add(item);
        }
        menu.Items.Add(profiles);
        Add("Settings", TrayCommand.Settings, current.CanSelectMode);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("Exit", TrayCommand.Exit);
    }

    private void Add(string text, TrayCommand command, bool enabled = true)
    {
        var item = new Forms.ToolStripMenuItem(Localization.Localizer.Translate(text)) { Enabled = enabled, ForeColor = menu.ForeColor };
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
    private sealed class TrayRenderer() : Forms.ToolStripProfessionalRenderer(new TrayColors())
    {
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            var highlighted = e.Item.Selected || e.Item.Pressed;
            using var fill = new SolidBrush(highlighted ? ColorTable.MenuItemSelected : ColorTable.ToolStripDropDownBackground);
            e.Graphics.FillRectangle(fill, new Rectangle(Point.Empty, e.Item.Size));
        }
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(232, 239, 248) : Color.FromArgb(149, 169, 193);
            base.OnRenderItemText(e);
        }
        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            var state = e.Graphics.Save();
            try
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var box = new Rectangle(e.ImageRectangle.X + 1, e.ImageRectangle.Y + 1, 14, 14);
                using var fill = new SolidBrush(Color.FromArgb(13, 23, 40));
                using var outline = new Pen(Color.FromArgb(69, 214, 187));
                using var mark = new Pen(Color.FromArgb(120, 227, 198), 2) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                e.Graphics.FillRectangle(fill, box);
                e.Graphics.DrawRectangle(outline, box);
                e.Graphics.DrawLines(mark, [new Point(box.Left + 3, box.Top + 7), new Point(box.Left + 6, box.Top + 10), new Point(box.Left + 11, box.Top + 4)]);
            }
            finally { e.Graphics.Restore(state); }
        }
        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        { e.ArrowColor = Color.FromArgb(180, 196, 216); base.OnRenderArrow(e); }
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
        public override Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd => ToolStripDropDownBackground;
        public override Color MenuItemPressedGradientBegin => MenuItemSelected;
        public override Color MenuItemPressedGradientMiddle => MenuItemSelected;
        public override Color MenuItemPressedGradientEnd => MenuItemSelected;
    }
}
