using System.Diagnostics;

namespace SingPlus.Drivers;

public interface ITimerDriver
{
    long GetTimestamp();
    long Frequency { get; }
}

public sealed class HostTimerDriver : ITimerDriver
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
}
