namespace SingPlus.Platform;

public enum PlatformDmaDirection
{
    DeviceReadsMemory = 0,
    DeviceWritesMemory,
    Bidirectional,
}

public readonly record struct PlatformDmaRange(long Offset, long Length)
{
    public bool Fits(long mappingLength) =>
        mappingLength > 0 &&
        Offset >= 0 &&
        Length > 0 &&
        Length <= mappingLength &&
        Offset <= mappingLength - Length;
}

public readonly record struct PlatformDmaGrantRequest(
    PlatformProviderDeviceLease DeviceLease,
    PlatformProviderRegionMappingLease MappingLease,
    PlatformRegionSlice MappingSlice,
    PlatformDmaRange Range,
    PlatformDmaDirection Direction);

public readonly record struct PlatformProviderDmaGrantId(ulong Value);

public readonly record struct PlatformProviderDmaGrant(
    PlatformProviderDmaGrantId GrantId,
    PlatformProviderLeaseGeneration Generation,
    PlatformProviderDeviceLease DeviceLease,
    PlatformProviderRegionMappingLease MappingLease,
    PlatformDmaRange Range,
    PlatformDmaDirection Direction);

/// <summary>
/// DMA authority family contract. Version 1 established exact admission-only grants;
/// version 2 adds grant-scoped non-coherent prepare/acquire visibility cycles. A grant
/// still does not submit a transfer and carries no bus address, IOMMU identity,
/// descriptor/queue identity, or completion authority.
/// </summary>
public static class PlatformDmaGrantContract
{
    public const uint ContractVersion = 2;

    public static PlatformAuthorityResult ValidateRequest(PlatformDmaGrantRequest request)
    {
        if (request.DeviceLease.LeaseId.Value == 0 ||
            request.DeviceLease.Generation.Value == 0 ||
            request.MappingLease.MappingId.Value == 0 ||
            request.MappingLease.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA grant admission requires materialized provider device and mapping identities.");
        }

        var slice = PlatformOwnedRegionMappingContract.ValidateSlice(request.MappingSlice);
        if (!slice.IsSuccess) return slice;

        if (request.DeviceLease.DomainLease != request.MappingLease.DomainLease)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "DMA device and region mapping must belong to the exact same provider domain lifetime.");
        }

        if (request.MappingLease.Region != request.MappingSlice.Region ||
            request.MappingLease.Access != request.MappingSlice.Access)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "DMA grant admission must bind the exact provider mapping to its exact region slice.");
        }

        if (!request.Range.Fits(request.MappingSlice.Length))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "DMA grant range must be positive, non-overflowing, and contained in the exact mapped slice.");
        }

        if (!Enum.IsDefined(request.Direction))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "DMA direction is undefined.");
        }

        var requiredDeviceRights = PlatformDeviceRights.Configure;
        var requiredMappingAccess = PlatformMemoryAccess.None;
        switch (request.Direction)
        {
            case PlatformDmaDirection.DeviceReadsMemory:
                requiredDeviceRights |= PlatformDeviceRights.Read;
                requiredMappingAccess |= PlatformMemoryAccess.Read;
                break;
            case PlatformDmaDirection.DeviceWritesMemory:
                requiredDeviceRights |= PlatformDeviceRights.Write;
                requiredMappingAccess |= PlatformMemoryAccess.Write;
                break;
            case PlatformDmaDirection.Bidirectional:
                requiredDeviceRights |= PlatformDeviceRights.Read | PlatformDeviceRights.Write;
                requiredMappingAccess |= PlatformMemoryAccess.Read | PlatformMemoryAccess.Write;
                break;
        }

        if ((request.DeviceLease.Rights & requiredDeviceRights) != requiredDeviceRights)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider device lease lacks Configure plus the rights required by the DMA direction.");
        }

        if ((request.MappingSlice.Access & requiredMappingAccess) != requiredMappingAccess)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The exact mapped-region access does not cover the requested DMA direction.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateGrant(
        PlatformDmaGrantRequest request,
        PlatformProviderDmaGrant grant)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess) return requestValidation;

        if (grant.GrantId.Value == 0 || grant.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider DMA grant identity must be materialized.");
        }

        if (grant.DeviceLease != request.DeviceLease ||
            grant.MappingLease != request.MappingLease ||
            grant.Range != request.Range ||
            grant.Direction != request.Direction)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider DMA grant does not match the exact admission request.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformDmaGrantProvider
{
    PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(
        PlatformDmaGrantRequest request);

    PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant);
}

/// <summary>Optional exact guarantee: NotAccepted on BindDmaGrant proves this invocation
/// performed no effect. It proves neither closure of a different invocation nor release.</summary>
public interface IPlatformDmaGrantAdmissionNoEffectProvider : IPlatformDmaGrantProvider { }
/// <summary>Optional exact guarantee: NotAccepted on RevokeDmaGrant proves this invocation
/// performed no effect. The existing admitted grant remains open.</summary>
public interface IPlatformDmaGrantRevocationNoEffectProvider : IPlatformDmaGrantProvider { }

/// <summary>Pure surviving original lifetime lookup. Success proves the exact admitted
/// grant closed at its producing owner. Missing/failure/NotAccepted never proves closure
/// or no-effect. Lookup cannot initiate or repeat revocation.</summary>
public interface IPlatformDmaGrantRevocationResolutionProvider : IPlatformDmaGrantProvider
{
    PlatformAuthorityResult ResolveDmaGrantRevocation(PlatformProviderDmaGrant grant);
}

/// <summary>Optional surviving-original guarantee: NotAccepted from the pure resolution
/// proves only that the retained revocation invocation performed no effect. It is not
/// grant closure and cannot discharge independent users. The producer retains original
/// request/raw/source continuity and consumes no-effect only after exact source checks;
/// lookup never starts another revocation. Without this opt-in, NotAccepted is unknown.</summary>
public interface IPlatformDmaGrantRevocationResolutionNoEffectProvider : IPlatformDmaGrantRevocationResolutionProvider { }

/// <summary>Opaque original request identity, retained by existing admission owners;
/// neither an authority object nor a durable token.</summary>
public sealed class PlatformOriginalDmaGrantBindRequest(PlatformDmaGrantRequest request)
{
    public Guid InvocationId { get; } = Guid.NewGuid();
    public PlatformDmaGrantRequest Request { get; } = request;
}
/// <summary>Exactly one of no-effect or cleanup-only grant is returned. Neither grants
/// DMA permission. The original request reference must match; absence is unknown.</summary>
public readonly record struct PlatformOriginalDmaGrantBindResolution(PlatformOriginalDmaGrantBindRequest Request,
    bool NoEffectGuaranteed, PlatformProviderDmaGrant? CleanupGrant);
public interface IPlatformOriginalDmaGrantProvider : IPlatformDmaGrantProvider
{
    PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(PlatformOriginalDmaGrantBindRequest request);
    PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution> ResolveDmaGrantBind(PlatformOriginalDmaGrantBindRequest request);
}
