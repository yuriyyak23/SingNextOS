using SingPlus.Contracts;
using SingPlus.Sip.Sdk;

namespace SingPlus.Sip.Drivers;

public enum DriverInterruptPollStatus : byte
{
    NoDelivery = 0,
    Delivered = 1,
    ResourceUnavailable = 2,
}

[BoundedPayload(192)]
public readonly record struct DriverInterruptPollResponse(
    DriverInterruptPollStatus Status,
    KernelEvent Event) : IBoundedPayload
{
    public int PayloadSize => Status == DriverInterruptPollStatus.Delivered ? 176 : 16;
    public int MaxPayloadSize => 192;
}

/// <summary>
/// Typed driver-facing event service. The client receives only committed Sing kernel
/// event evidence. The endpoint session does not expose device, MMIO, IRQ-binding,
/// DMA, platform-provider, or hardware authority.
/// </summary>
[SipContract, InitialState("Ready")]
public interface IDriverInterruptService
{
    [Message(1), Transition("Ready", "Ready")]
    ValueTask<DriverInterruptPollResponse> PollAsync();
}
