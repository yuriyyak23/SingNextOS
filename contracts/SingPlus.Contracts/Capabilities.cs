namespace SingPlus.Contracts;

public sealed record CapabilityDescriptorV1(
    CapabilityId CapabilityId,
    DomainId IssuerDomainId,
    DomainId SubjectDomainId,
    ResourceKind ResourceKind,
    string ResourceId,
    CapabilityRights Rights,
    ulong Generation,
    ulong RevocationEpoch);

public readonly record struct MmioRegionCapability(CapabilityId CapabilityId);
public readonly record struct IrqCapability(CapabilityId CapabilityId);
public readonly record struct DmaCapability(CapabilityId CapabilityId);
