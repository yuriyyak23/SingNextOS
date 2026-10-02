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
        if (!ValidateMappingClosureGeneration(parent).IsSuccess)
        {
            childRecord.State = PlatformChildDomainState.Faulted;
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted,
                "Parent mapping provider generation changed before guest admission.");
        }
        lock (_secureDomainLifecycleGate)
        {
            if (parent.LocalAuthorizationRevoked || parent.ClosureState != PlatformExternalClosureState.Active ||
                childRecord.State == PlatformChildDomainState.Faulted || childRecord.TransitionInFlight)
                return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformBindingRevoked,
                    "Parent mapping or child changed before guest admission.");
            parent.PendingGuestMaps++;
            childRecord.PendingChildEffects++;
        }
        try
        {
        var backendEpoch = BackendEpoch;
        PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> result;
        try { result = provider.MapGuestRegion(request); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            childRecord.State = PlatformChildDomainState.Faulted;
            parent.ClosureState = PlatformExternalClosureState.Faulted;
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted,
                $"Guest mapping may have taken effect without a receipt: {exception.Message}");
        }
        if (BackendEpoch != backendEpoch ||
            childRecord.State == PlatformChildDomainState.Faulted ||
            parent.ClosureState != PlatformExternalClosureState.Active ||
            parent.LocalAuthorizationRevoked ||
            !ValidateMappingClosureGeneration(parent).IsSuccess)
        {
            childRecord.State = PlatformChildDomainState.Faulted;
            parent.ClosureState = PlatformExternalClosureState.Faulted;
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted,
                "Backend generation changed during guest mapping; parents remain pinned.");
        }
        if (!result.IsSuccess)
        {
            if (result.Status != PlatformAuthorityStatus.NotAccepted)
            {
                childRecord.State = PlatformChildDomainState.Faulted;
                parent.ClosureState = PlatformExternalClosureState.Faulted;
            }
            return FromProviderFailure<PlatformGuestMapping>(result.Status, result.Message);
        }
        var leaseValidation = PlatformGuestMemoryContract.ValidateLease(request, result.Value!);
        if (!leaseValidation.IsSuccess)
        {
            var cleanupProven = false;
            try
            {
                var cleanup = provider.UnmapGuestRegion(result.Value!);
                cleanupProven = cleanup.IsSuccess &&
                    PlatformGuestMemoryContract.ValidateClosureReceipt(result.Value!, cleanup.Value!).IsSuccess &&
                    BackendEpoch == backendEpoch;
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // Lost cleanup receipt cannot release the parent mapping.
            }
            if (!cleanupProven)
            {
                childRecord.State = PlatformChildDomainState.Faulted;
                parent.ClosureState = PlatformExternalClosureState.Faulted;
            }
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted, leaseValidation.Message!);
        }
        lock (_secureDomainLifecycleGate)
        {
            if (BackendEpoch != backendEpoch || parent.LocalAuthorizationRevoked ||
                parent.ClosureState != PlatformExternalClosureState.Active ||
                childRecord.State == PlatformChildDomainState.Faulted)
            {
                childRecord.State = PlatformChildDomainState.Faulted;
                parent.ClosureState = PlatformExternalClosureState.Faulted;
                return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted,
                    "Guest mapping changed before local publication; parent remains pinned.");
            }
            var mapping = new PlatformGuestMapping(new(_nextGuestBindingId++), new(1), child, parentMapping);
            _guestBindings.Add(mapping.MappingId, new(mapping, result.Value!));
            return KernelResult<PlatformGuestMapping>.Ok(mapping);
        }
        }
        finally
        {
            lock (_secureDomainLifecycleGate)
            {
                parent.PendingGuestMaps--;
                childRecord.PendingChildEffects--;
            }
        }
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
        var backendEpoch = BackendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            if (record.PendingExecutableEffects != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Executable artifact bind or start must settle before guest unmap.");
            if (_executableArtifacts.Values.Any(artifact => artifact.Binding.GuestMapping == mapping))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Bound executable artifact has no exact release evidence for this guest mapping.");
            if (record.Closure != PlatformExternalClosureState.Active)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked,
                    "Guest mapping closure is already in progress.");
            record.Closure = PlatformExternalClosureState.Draining;
        }
        PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> result;
        try { result = provider.UnmapGuestRegion(record.ProviderLease); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            record.Closure = PlatformExternalClosureState.Faulted;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Guest unmap may have taken effect without a closure receipt: {exception.Message}");
        }
        if (BackendEpoch != backendEpoch || record.Closure == PlatformExternalClosureState.Faulted)
        {
            record.Closure = PlatformExternalClosureState.Faulted;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Backend reset during guest unmap leaves mapping closure uncertain.");
        }
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
        lock (_secureDomainLifecycleGate)
            record.Closure = PlatformExternalClosureState.Closed;
        return KernelResult.Ok();
    }
}
