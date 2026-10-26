using SingPlus.Contracts;
using SingPlus.Sip.Sdk;

namespace SingPlus.Boot.Samples;

[BoundedPayload(32)]
public readonly record struct CounterValue(int Value) : IBoundedPayload
{
    public int PayloadSize => sizeof(int);
    public int MaxPayloadSize => 32;
}

[SipContract, InitialState("Ready")]
public interface ICounterService
{
    [Message(1), Transition("Ready", "Ready")]
    CounterValue Increment(CounterValue request);
}

// A static verifier root, not an unrestricted CLR entry point.
public static class Arithmetic
{
    public static int Increment(int value) => value + 1;
}
