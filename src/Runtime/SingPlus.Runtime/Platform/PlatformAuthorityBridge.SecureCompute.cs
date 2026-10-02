using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal readonly record struct SecureDomainBindingId(ulong Value);
    internal readonly record struct SecureDomainBindingGeneration(ulong Value);
    internal readonly record struct SecureDomainBinding(SecureDomainBindingId BindingId, SecureDomainBindingGeneration Generation, PlatformDomainBinding Parent);
    private sealed record SecureDomainRecord(SecureDomainBinding Binding, PlatformProviderSecureDomainLease ProviderLease)
    {
        public Dictionary<PlatformRegionMappingId, PlatformProviderSecureRegionBinding> Regions { get; } = [];
        public HashSet<PlatformRegionMappingId> QuarantinedRegions { get; } = [];
        public bool TerminalQuarantined { get; set; }
        public bool TransitionInFlight { get; set; }
        public bool RevokeInFlight { get; set; }
        public HashSet<PlatformRegionMappingId> RegionMutationsInFlight { get; } = [];
        public bool Quarantined => TerminalQuarantined || QuarantinedRegions.Count != 0;
    }
    private readonly Dictionary<SecureDomainBindingId, SecureDomainRecord> _secureDomains = [];
    private ulong _nextSecureDomainBindingId = 1;

    internal KernelResult<SecureDomainBinding> CreateSecureDomain(PlatformDomainBinding parent, PlatformDomainIdentity subject, SecureDomainProfile profile)
    {
        if (_provider is not IPlatformSecureComputeProvider provider ||
            !_featureManifest.Supports(PlatformFeatureFamily.SecureDomains, PlatformSecureComputeContract.ContractVersion, PlatformFeatureAvailability.ProductionSecure))
            return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformUnsupported, "Every requested secure property requires a ProductionSecure provider claim.");
        if (profile.MaximumMemoryBytes <= 0 || profile.RequiredProperties is null || profile.RequiredProperties.Count == 0 ||
            profile.RequiredProperties.Any(property => !Enum.IsDefined(property)) || profile.RequiredProperties.Distinct().Count() != profile.RequiredProperties.Count)
            return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformDenied, "Secure-domain profile is malformed.");
        PlatformProviderDomainLease parentLease;
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            var validParent = ValidateDomain(parent, subject);
            if (!validParent.IsSuccess) return KernelResult<SecureDomainBinding>.Fail(validParent.Error, validParent.Message!);
            var record = _domains[parent.BindingId];
            if (record.ParentRevokeMayHaveEffect)
                return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                    "Parent-domain revoke is in flight or has ambiguous effect.");
            parentLease = record.ProviderLease;
            backendEpoch = BackendEpoch;
            record.PendingSecureCreates = checked(record.PendingSecureCreates + 1);
        }
        try
        {
            PlatformAuthorityResult<PlatformProviderSecureDomainLease> result;
            try
            {
                result = provider.CreateSecureDomain(new(parentLease, profile));
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                PinUnresolvedSecureCreate(parent);
                return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                    $"Secure-domain creation may have taken effect without a lease: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate)
            {
                if (BackendEpoch != backendEpoch || _domains[parent.BindingId].AuthorityState != DomainAuthorityState.Active)
                {
                    PinUnresolvedSecureCreate(parent);
                    return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                        "Backend reset during secure-domain creation leaves the parent pinned.");
                }
                if (!result.IsSuccess)
                {
                    PinUnresolvedSecureCreate(parent);
                    return FromProviderFailure<SecureDomainBinding>(result.Status, result.Message);
                }
            }
            var lease = result.Value;
            if (lease.DomainId.Value == 0 || lease.Generation.Value == 0 || lease.Parent != parentLease ||
                lease.ProvenProperties is null || lease.ProvenProperties.Any(property => !Enum.IsDefined(property)) ||
                profile.RequiredProperties.Any(property => !lease.ProvenProperties.Contains(property)))
            {
                var cleanupClosed = false;
                try
                {
                    var cleanup = provider.RevokeSecureDomain(lease);
                    cleanupClosed = IsExactClosure(cleanup, lease);
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    // A failed cleanup cannot prove that the malformed child was closed.
                }
                lock (_secureDomainLifecycleGate)
                {
                    var exactlyClosed = cleanupClosed && BackendEpoch == backendEpoch &&
                        _domains[parent.BindingId].AuthorityState == DomainAuthorityState.Active;
                    if (!exactlyClosed)
                    {
                        var quarantined = new SecureDomainBinding(new(_nextSecureDomainBindingId++), new(1), parent);
                        _secureDomains.Add(quarantined.BindingId, new(quarantined, lease) { TerminalQuarantined = true });
                    }
                    return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                        exactlyClosed ? "Provider did not prove every requested secure property." : "Malformed secure-domain admission could not be closed and remains quarantined.");
                }
            }
            lock (_secureDomainLifecycleGate)
            {
                if (BackendEpoch != backendEpoch || _domains[parent.BindingId].AuthorityState != DomainAuthorityState.Active)
                {
                    PinUnresolvedSecureCreate(parent);
                    return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                        "Parent or backend changed before secure-domain publication; possible effect remains pinned.");
                }
                var binding = new SecureDomainBinding(new(_nextSecureDomainBindingId++), new(1), parent);
                _secureDomains.Add(binding.BindingId, new(binding, lease));
                return KernelResult<SecureDomainBinding>.Ok(binding);
            }
        }
        finally
        {
            lock (_secureDomainLifecycleGate)
                _domains[parent.BindingId].PendingSecureCreates--;
        }
    }

    private void PinUnresolvedSecureCreate(PlatformDomainBinding parent)
    {
        lock (_secureDomainLifecycleGate)
        {
            var record = _domains[parent.BindingId];
            record.SecureCreateMayHaveEffect = true;
            QuarantineDomain(record);
        }
    }

    internal KernelResult ValidateSecureDomainLocalCommit(SecureDomainBinding binding, PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
        {
            var secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            var parent = ValidateDomain(binding.Parent, subject);
            if (!parent.IsSuccess || secure.Value!.Quarantined)
            {
                secure.Value!.TerminalQuarantined = true;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Secure-domain authority changed before local commit and remains pinned.");
            }
            return KernelResult.Ok();
        }
    }

    internal void QuarantineSecureDomainLocalTransition(SecureDomainBinding binding)
    {
        lock (_secureDomainLifecycleGate)
        {
            if (_secureDomains.TryGetValue(binding.BindingId, out var record) && record.Binding == binding)
                record.TerminalQuarantined = true;
        }
    }

    internal KernelResult BindSecureRegion(SecureDomainBinding binding, PlatformRegionMapping mapping, PlatformSecureRegionClass regionClass)
    {
        if (!Enum.IsDefined(regionClass)) return KernelResult.Fail(KernelError.PlatformDenied, "Secure region class is invalid.");
        SecureDomainRecord record;
        PlatformProviderRegionMappingLease mappingLease;
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            var secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            record = secure.Value!;
            if (record.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
            if (record.TransitionInFlight || record.RevokeInFlight || record.RegionMutationsInFlight.Contains(mapping.MappingId))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain transition, revoke, or exact mapping mutation is in flight.");
            if (record.Regions.ContainsKey(mapping.MappingId)) return KernelResult.Fail(KernelError.PlatformDenied, "Mapping is already bound to this secure domain.");
            if (!_mappings.TryGetValue(mapping.MappingId, out var mapped) || mapped.Mapping != mapping ||
                mapped.LocalAuthorizationRevoked || mapped.ClosureState != PlatformExternalClosureState.Active)
                return KernelResult.Fail(KernelError.StaleGeneration, "Secure region mapping is absent, stale, or closing.");
            if (mapping.DomainBinding != binding.Parent)
                return KernelResult.Fail(KernelError.WrongPlatformDomain, "Secure region belongs to another parent domain.");
            mappingLease = mapped.ProviderLease;
            backendEpoch = BackendEpoch;
            record.RegionMutationsInFlight.Add(mapping.MappingId);
        }
        try
        {
            PlatformAuthorityResult<PlatformProviderSecureRegionBinding> result;
            try { result = ((IPlatformSecureComputeProvider)_provider!).BindSecureRegion(record.ProviderLease, mappingLease, regionClass); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                }
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    $"Secure-region bind may have taken effect without a lease: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate)
            {
                if (BackendEpoch != backendEpoch || record.TerminalQuarantined)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Backend reset during secure-region bind leaves the mapping pinned.");
                }
                if (!result.IsSuccess)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return FromProviderFailure(result.Status, result.Message);
                }
            }
            var providerBinding = result.Value;
            if (providerBinding.BindingId.Value == 0 || providerBinding.Generation.Value == 0 || providerBinding.Domain != record.ProviderLease ||
                providerBinding.Mapping != mappingLease || providerBinding.RegionClass != regionClass)
            {
                var cleanupClosed = false;
                try
                {
                    var cleanup = ((IPlatformSecureComputeProvider)_provider!).UnbindSecureRegion(providerBinding);
                    cleanupClosed = cleanup.IsSuccess && cleanup.Value.Binding == providerBinding && cleanup.Value.Closed;
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    // The malformed provider binding may still exist after cleanup fault.
                }
                lock (_secureDomainLifecycleGate)
                {
                    var exactlyClosed = cleanupClosed && BackendEpoch == backendEpoch && !record.TerminalQuarantined;
                    if (!exactlyClosed)
                    {
                        record.TerminalQuarantined = true;
                        record.QuarantinedRegions.Add(mapping.MappingId);
                    }
                    return KernelResult.Fail(exactlyClosed ? KernelError.PlatformBindingRevoked : KernelError.PlatformFaulted, exactlyClosed
                        ? "Provider secure-region binding was malformed and exactly compensated."
                        : "Provider secure-region binding was malformed; exact compensation failed and the secure domain is quarantined.");
                }
            }
            lock (_secureDomainLifecycleGate)
            {
                if (BackendEpoch != backendEpoch || record.TerminalQuarantined)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Secure-region bind lost backend continuity before binding publication.");
                }
                record.Regions.Add(mapping.MappingId, providerBinding);
                return KernelResult.Ok();
            }
        }
        finally
        {
            lock (_secureDomainLifecycleGate) record.RegionMutationsInFlight.Remove(mapping.MappingId);
        }
    }

    internal KernelResult UnbindSecureRegion(SecureDomainBinding binding, PlatformRegionMapping mapping)
    {
        SecureDomainRecord record;
        PlatformProviderSecureRegionBinding providerBinding;
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            var secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            record = secure.Value!;
            if (!record.Regions.TryGetValue(mapping.MappingId, out providerBinding))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Secure-region binding was not found.");
            if (!_mappings.TryGetValue(mapping.MappingId, out var mapped) || mapped.Mapping != mapping)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact secure-region mapping is absent or stale.");
            if (record.TerminalQuarantined)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
            if (record.TransitionInFlight || record.RevokeInFlight || record.RegionMutationsInFlight.Contains(mapping.MappingId))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain transition, revoke, or exact mapping mutation is in flight.");
            backendEpoch = BackendEpoch;
            record.RegionMutationsInFlight.Add(mapping.MappingId);
        }
        try
        {
            PlatformAuthorityResult<PlatformSecureRegionClosureReceipt> result;
            try { result = ((IPlatformSecureComputeProvider)_provider!).UnbindSecureRegion(providerBinding); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                }
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    $"Secure-region unbind may have taken effect without closure evidence: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate)
            {
                if (BackendEpoch != backendEpoch || record.TerminalQuarantined)
                {
                    record.TerminalQuarantined = true;
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Backend reset during secure-region unbind leaves closure uncertain.");
                }
                if (!result.IsSuccess)
                {
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return FromProviderFailure(result.Status, result.Message);
                }
                if (result.Value.Binding != providerBinding || !result.Value.Closed)
                {
                    record.QuarantinedRegions.Add(mapping.MappingId);
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-region closure receipt is malformed.");
                }
                record.Regions.Remove(mapping.MappingId);
                record.QuarantinedRegions.Remove(mapping.MappingId);
                return KernelResult.Ok();
            }
        }
        finally
        {
            lock (_secureDomainLifecycleGate) record.RegionMutationsInFlight.Remove(mapping.MappingId);
        }
    }

    private bool HasActiveSecureRegion(PlatformRegionMappingId mappingId)
    {
        lock (_secureDomainLifecycleGate)
            return _secureDomains.Values.Any(record => record.Regions.ContainsKey(mappingId) ||
                record.QuarantinedRegions.Contains(mappingId) || record.RegionMutationsInFlight.Contains(mappingId));
    }

    internal KernelResult TransitionSecureDomain(SecureDomainBinding binding, PlatformSecureDomainTransition transition)
    {
        SecureDomainRecord record;
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            var secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            if (secure.Value!.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
            if (!Enum.IsDefined(transition)) return KernelResult.Fail(KernelError.PlatformDenied, "Secure-domain transition is invalid.");
            if (secure.Value.TransitionInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain transition is already in flight.");
            if (secure.Value.RevokeInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain revoke is already in flight.");
            if (secure.Value.RegionMutationsInFlight.Count != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-region mutation must settle before domain transition.");
            record = secure.Value;
            backendEpoch = BackendEpoch;
            record.TransitionInFlight = true;
        }

        PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt> result;
        try { result = ((IPlatformSecureComputeProvider)_provider!).TransitionSecureDomain(record.ProviderLease, transition); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate)
            {
                record.TerminalQuarantined = true;
                record.TransitionInFlight = false;
            }
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Secure-domain transition may have taken effect without a receipt: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
        {
            record.TransitionInFlight = false;
            if (BackendEpoch != backendEpoch || record.TerminalQuarantined)
            {
                record.TerminalQuarantined = true;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Backend reset during secure-domain transition leaves the effect uncertain.");
            }
            if (!result.IsSuccess) { record.TerminalQuarantined = true; return FromProviderFailure(result.Status, result.Message); }
            if (result.Value.Domain != record.ProviderLease || result.Value.Transition != transition || !result.Value.Accepted)
            { record.TerminalQuarantined = true; return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-domain transition receipt is malformed."); }
            return KernelResult.Ok();
        }
    }

    internal KernelResult RevokeSecureDomain(SecureDomainBinding binding)
    {
        KernelResult<SecureDomainRecord> secure;
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            secure = ResolveSecure(binding);
            if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
            if (secure.Value!.Regions.Count != 0) return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure regions must close before secure-domain authority.");
            if (secure.Value.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
            if (secure.Value.TransitionInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain transition must settle before revoke.");
            if (secure.Value.RevokeInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-domain revoke is already in flight.");
            if (secure.Value.RegionMutationsInFlight.Count != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure-region mutation must settle before domain revoke.");
            secure.Value.RevokeInFlight = true;
            backendEpoch = BackendEpoch;
        }
        PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> result;
        try { result = ((IPlatformSecureComputeProvider)_provider!).RevokeSecureDomain(secure.Value.ProviderLease); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate)
            {
                secure.Value.TerminalQuarantined = true;
                secure.Value.RevokeInFlight = false;
            }
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Secure-domain revoke may have taken effect without closure evidence: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
        {
            secure.Value.RevokeInFlight = false;
            if (BackendEpoch != backendEpoch || secure.Value.Quarantined)
            {
                secure.Value.TerminalQuarantined = true;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Backend reset during secure-domain revoke leaves closure uncertain.");
            }
            if (!result.IsSuccess) { secure.Value.TerminalQuarantined = true; return FromProviderFailure(result.Status, result.Message); }
            if (!IsExactClosure(result, secure.Value.ProviderLease))
            { secure.Value.TerminalQuarantined = true; return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-domain closure receipt is malformed."); }
            _secureDomains.Remove(binding.BindingId);
            return KernelResult.Ok();
        }
    }

    private static bool IsExactClosure(
        PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> result,
        PlatformProviderSecureDomainLease lease) =>
        result.IsSuccess && result.Value.Domain == lease && result.Value.Closed;

    private KernelResult<SecureDomainRecord> ResolveSecure(SecureDomainBinding binding)
    {
        if (!_secureDomains.TryGetValue(binding.BindingId, out var record))
            return KernelResult<SecureDomainRecord>.Fail(KernelError.PlatformBindingNotFound, "Secure-domain binding was not found.");
        if (record.Binding != binding)
            return KernelResult<SecureDomainRecord>.Fail(KernelError.StaleGeneration, "Secure-domain binding is stale or belongs to another parent.");
        return KernelResult<SecureDomainRecord>.Ok(record);
    }
}
