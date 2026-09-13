using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<PlatformGuestMapping> MapChildGuestRegion(
        PlatformChildBinding child, PlatformRegionMapping parentMapping,
        PlatformGuestAddressRange guestRange, PlatformGuestMemoryAccess access)
    {
        var childResult = ResolveChild(child);
        if (!childResult.IsSuccess) return KernelResult<PlatformGuestMapping>.Fail(childResult.Error, childResult.Message!);
        if (!SupportsChildFeature(PlatformFeatureFamily.ChildGuestMemory, PlatformGuestMemoryContract.ContractVersion) ||
            _provider is not IPlatformGuestMemoryProvider provider)
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformUnsupported, "Guest-memory provider is unavailable.");
        if (!_mappings.TryGetValue(parentMapping.MappingId, out MappingRecord? parent) || parent.Mapping != parentMapping)
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformBindingNotFound, "Exact parent mapping was not found.");
        if (parent.LocalAuthorizationRevoked || parent.ClosureState != PlatformExternalClosureState.Active)
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformBindingRevoked, "Parent mapping is not active.");
        ChildBindingRecord childRecord = childResult.Value!;
        if (parentMapping.DomainBinding != child.ParentBinding)
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.WrongPlatformDomain, "Parent mapping belongs to another domain.");
        var request = new PlatformGuestRegionMappingRequest(childRecord.ProviderLease,
            new PlatformProviderOwnedRegionMapping(parent.ProviderLease, new(parent.ProviderLease.Region, 0,
                parent.ProviderLease.Region.ByteLength, parent.ProviderLease.Access)), guestRange, access);
        var validation = PlatformGuestMemoryContract.ValidateRequest(request, childRecord.State);
        if (!validation.IsSuccess) return FromProviderFailure<PlatformGuestMapping>(validation.Status, validation.Message);
        var result = provider.MapGuestRegion(request);
        if (!result.IsSuccess)
        {
            QuarantineChild(childRecord, result.Status);
            return FromProviderFailure<PlatformGuestMapping>(result.Status, result.Message);
        }
        var leaseValidation = PlatformGuestMemoryContract.ValidateLease(request, result.Value!);
        if (!leaseValidation.IsSuccess)
        {
            _ = provider.UnmapGuestRegion(result.Value!);
            childRecord.State = PlatformChildDomainState.Faulted;
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted, leaseValidation.Message!);
        }
        var mapping = new PlatformGuestMapping(new(_nextGuestBindingId++), new(1), child, parentMapping);
        _guestBindings.Add(mapping.MappingId, new(mapping, result.Value!));
        return KernelResult<PlatformGuestMapping>.Ok(mapping);
    }

    internal KernelResult UnmapChildGuestRegion(PlatformGuestMapping mapping)
    {
        if (!_guestBindings.TryGetValue(mapping.MappingId, out GuestBindingRecord? record))
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Guest mapping was not found.");
        if (record.Mapping.Generation != mapping.Generation)
            return KernelResult.Fail(KernelError.StaleGeneration, "Guest mapping generation is stale.");
        if (record.Mapping != mapping)
            return KernelResult.Fail(KernelError.WrongPlatformDomain, "Guest mapping belongs to another child or parent mapping.");
        if (record.Closure == PlatformExternalClosureState.Closed)
            return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Guest mapping is already closed.");
        if (record.Closure == PlatformExternalClosureState.Faulted)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Guest mapping is quarantined.");
        if (_provider is not IPlatformGuestMemoryProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Guest-memory provider is unavailable.");
        record.Closure = PlatformExternalClosureState.Draining;
        var result = provider.UnmapGuestRegion(record.ProviderLease);
        if (!result.IsSuccess)
        {
            record.Closure = PlatformExternalClosureState.Faulted;
            return FromProviderFailure(result.Status, result.Message);
        }
        var validation = PlatformGuestMemoryContract.ValidateClosureReceipt(record.ProviderLease, result.Value!);
        if (!validation.IsSuccess)
        {
            record.Closure = PlatformExternalClosureState.Faulted;
            return KernelResult.Fail(KernelError.PlatformFaulted, validation.Message!);
        }
        record.Closure = PlatformExternalClosureState.Closed;
        return KernelResult.Ok();
    }
}
