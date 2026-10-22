namespace SherpaAxis.App.Models;

public enum SidePanelMode { Tuning, Calibration, Settings }

public sealed record ApplicationPreferences(int SchemaVersion = 1, bool MinimizeOnClose = false,
    bool StartWithWindows = false, bool StartInTray = false, bool MinimizeToTray = false,
    bool PauseGraphWhenHidden = true, string Language = "en", bool HidePhysicalPedals = true,
    bool TourDismissed = false);
