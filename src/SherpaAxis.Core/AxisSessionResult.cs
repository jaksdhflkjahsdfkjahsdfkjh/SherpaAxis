namespace SherpaAxis.Core;

public readonly record struct AxisSessionResult(
    AxisProcessingResult Processing,
    double TargetCorrection,
    bool ReleaseConfirmed,
    bool CorrectionSuspended,
    double CorrectionOutputDelta,
    AxisSessionResetReason ResetReason);
