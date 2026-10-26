using SingPlus.Contracts;
using SingPlus.Sip.Sdk;

namespace SingPlus.Apps.Snake;

[BoundedPayload(8)]
public readonly record struct SnakeMove(int Direction) : IBoundedPayload
{
    public int PayloadSize => sizeof(int);
    public int MaxPayloadSize => 8;
}

// Fixed primitive values only: no mutable board or host object crosses the ABI.
[BoundedPayload(64)]
public readonly record struct SnakeFrame(
    ulong Cells0, ulong Cells1, ulong Cells2, int Head, int Food, int Score, int Status) : IBoundedPayload
{
    public int PayloadSize => 40;
    public int MaxPayloadSize => 64;
}

[SipContract, InitialState("Ready")]
public interface ISnakeService
{
    [Message(1), Transition("Ready", "Ready")]
    SnakeFrame Step(SnakeMove request);
}