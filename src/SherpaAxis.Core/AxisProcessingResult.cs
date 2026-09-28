namespace SherpaAxis.Core;

public readonly record struct AxisProcessingResult(
    double Input,
    double CorrectedInput,
    double Normalized,
    double CorrectedNormalized,
    double AfterDeadzone,
    double Output,
    double BaselineCorrection);
