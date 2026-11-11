namespace SherpaAxis.App.Models;

public enum SidePanelMode { Tuning, Calibration, Settings, PedalOverlay }

public sealed record ApplicationPreferences(int SchemaVersion = 1, bool MinimizeOnClose = false,
    bool StartWithWindows = false, bool StartInTray = false, bool MinimizeToTray = false,
    bool PauseGraphWhenHidden = true, string Language = "en", bool HidePhysicalPedals = true,
    bool TourDismissed = false, bool CheckForUpdates = true, PedalOverlayPreferences? Overlay = null);

public sealed record PedalOverlayPreferences(double WindowSeconds = 10, bool ShowRaw = false, bool ShowFiltered = false,
    bool ShowOutput = true, double Width = 640, double Height = 140, double? Left = null, double? Top = null, bool Locked = false);
