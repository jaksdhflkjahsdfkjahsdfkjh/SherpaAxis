namespace SherpaAxis.Bridge;

public readonly record struct PedalOutput(double Throttle, double Brake, double Clutch);

public interface IVirtualController : IDisposable
{
    void Write(PedalOutput output);
}

public interface IProcessedOutputSink
{
    void Submit(PedalOutput output);
    void Invalidate(string reason);
}
