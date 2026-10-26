using SingPlus.Contracts;
using SingPlus.Sip.Sdk;

namespace SingPlus.Sip.Compute;

/// <summary>
/// Typed Sing+ ingress for the bounded DSC1 Copy v1 contour.
/// The source authority is read-only and temporary; destination ownership moves
/// exclusively to the service and is returned only through the correlated response.
/// This contract does not expose platform mappings, provider identities, lanes,
/// opcodes, descriptors, or executable HybridCPU claims.
/// </summary>
[SipContract, InitialState("Ready")]
public interface IComputeService
{
    [Message(1)]
    [Transition("Ready", "Ready")]
    [RequiresCapability(
        ResourceKind.Compute,
        CapabilityResourceIds.Dsc1Copy,
        CapabilityRights.Execute)]
    [ReturnsOwnership]
    ValueTask<OwnedBuffer<byte>> CopyAsync(
        [Borrows] OwnedBuffer<byte> source,
        [Consumes] OwnedBuffer<byte> destination);
}
