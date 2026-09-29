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

    internal KernelResult ValidateSecureDomainPublication(SecureDomainBinding binding, PlatformDomainIdentity subject)
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
                    "Secure-domain authority changed before local handle publication and remains pinned.");
            }
            return KernelResult.Ok();
        }
    }

    internal KernelResult BindSecureRegion(SecureDomainBinding binding, PlatformRegionMapping mapping, PlatformSecureRegionClass regionClass)
    {
        var secure = ResolveSecure(binding);
        if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
        if (!Enum.IsDefined(regionClass)) return KernelResult.Fail(KernelError.PlatformDenied, "Secure region class is invalid.");
        if (secure.Value!.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
        if (secure.Value.Regions.ContainsKey(mapping.MappingId)) return KernelResult.Fail(KernelError.PlatformDenied, "Mapping is already bound to this secure domain.");
        if (!_mappings.TryGetValue(mapping.MappingId, out var mapped) || mapped.Mapping != mapping || mapped.LocalAuthorizationRevoked)
            return KernelResult.Fail(KernelError.StaleGeneration, "Secure region mapping is absent, stale, or revoked.");
        if (mapping.DomainBinding != binding.Parent)
            return KernelResult.Fail(KernelError.WrongPlatformDomain, "Secure region belongs to another parent domain.");
        var backendEpoch = BackendEpoch;
        PlatformAuthorityResult<PlatformProviderSecureRegionBinding> result;
        try { result = ((IPlatformSecureComputeProvider)_provider!).BindSecureRegion(secure.Value.ProviderLease, mapped.ProviderLease, regionClass); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            secure.Value.TerminalQuarantined = true;
            secure.Value.QuarantinedRegions.Add(mapping.MappingId);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Secure-region bind may have taken effect without a lease: {exception.Message}");
        }
        if (BackendEpoch != backendEpoch || secure.Value.TerminalQuarantined)
        {
            secure.Value.TerminalQuarantined = true;
            secure.Value.QuarantinedRegions.Add(mapping.MappingId);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Backend reset during secure-region bind leaves the mapping pinned.");
        }
        if (!result.IsSuccess)
        {
            secure.Value.TerminalQuarantined = true;
            secure.Value.QuarantinedRegions.Add(mapping.MappingId);
            return FromProviderFailure(result.Status, result.Message);
        }
        var providerBinding = result.Value;
        if (providerBinding.BindingId.Value == 0 || providerBinding.Generation.Value == 0 || providerBinding.Domain != secure.Value.ProviderLease ||
            providerBinding.Mapping != mapped.ProviderLease || providerBinding.RegionClass != regionClass)
        {
            var exactlyClosed = false;
            try
            {
                var cleanup = ((IPlatformSecureComputeProvider)_provider!).UnbindSecureRegion(providerBinding);
                exactlyClosed = cleanup.IsSuccess && cleanup.Value.Binding == providerBinding && cleanup.Value.Closed &&
                    BackendEpoch == backendEpoch && !secure.Value.TerminalQuarantined;
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // The malformed provider binding may still exist after cleanup fault.
            }
            if (!exactlyClosed)
            {
                secure.Value.TerminalQuarantined = true;
                secure.Value.QuarantinedRegions.Add(mapping.MappingId);
            }
            return KernelResult.Fail(exactlyClosed ? KernelError.PlatformBindingRevoked : KernelError.PlatformFaulted, exactlyClosed
                ? "Provider secure-region binding was malformed and exactly compensated."
                : "Provider secure-region binding was malformed; exact compensation failed and the secure domain is quarantined.");
        }
        secure.Value.Regions.Add(mapping.MappingId, providerBinding);
        return KernelResult.Ok();
    }

    internal KernelResult UnbindSecureRegion(SecureDomainBinding binding, PlatformRegionMapping mapping)
    {
        var secure = ResolveSecure(binding);
        if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
        if (!secure.Value!.Regions.TryGetValue(mapping.MappingId, out var providerBinding))
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Secure-region binding was not found.");
        if (secure.Value.TerminalQuarantined)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
        var backendEpoch = BackendEpoch;
        PlatformAuthorityResult<PlatformSecureRegionClosureReceipt> result;
        try { result = ((IPlatformSecureComputeProvider)_provider!).UnbindSecureRegion(providerBinding); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            secure.Value.TerminalQuarantined = true;
            secure.Value.QuarantinedRegions.Add(mapping.MappingId);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Secure-region unbind may have taken effect without closure evidence: {exception.Message}");
        }
        if (BackendEpoch != backendEpoch || secure.Value.TerminalQuarantined)
        {
            secure.Value.TerminalQuarantined = true;
            secure.Value.QuarantinedRegions.Add(mapping.MappingId);
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Backend reset during secure-region unbind leaves closure uncertain.");
        }
        if (!result.IsSuccess) { secure.Value.QuarantinedRegions.Add(mapping.MappingId); return FromProviderFailure(result.Status, result.Message); }
        if (result.Value.Binding != providerBinding || !result.Value.Closed)
        { secure.Value.QuarantinedRegions.Add(mapping.MappingId); return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-region closure receipt is malformed."); }
        secure.Value.Regions.Remove(mapping.MappingId);
        secure.Value.QuarantinedRegions.Remove(mapping.MappingId);
        return KernelResult.Ok();
    }

    private bool HasActiveSecureRegion(PlatformRegionMappingId mappingId) =>
        _secureDomains.Values.Any(record => record.Regions.ContainsKey(mappingId) ||
            record.QuarantinedRegions.Contains(mappingId));

    internal KernelResult TransitionSecureDomain(SecureDomainBinding binding, PlatformSecureDomainTransition transition)
    {
        var secure = ResolveSecure(binding);
        if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
        if (secure.Value!.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
        if (!Enum.IsDefined(transition)) return KernelResult.Fail(KernelError.PlatformDenied, "Secure-domain transition is invalid.");
        var result = ((IPlatformSecureComputeProvider)_provider!).TransitionSecureDomain(secure.Value!.ProviderLease, transition);
        if (!result.IsSuccess) { secure.Value.TerminalQuarantined = true; return FromProviderFailure(result.Status, result.Message); }
        if (result.Value.Domain != secure.Value.ProviderLease || result.Value.Transition != transition || !result.Value.Accepted)
        { secure.Value.TerminalQuarantined = true; return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-domain transition receipt is malformed."); }
        return KernelResult.Ok();
    }

    internal KernelResult RevokeSecureDomain(SecureDomainBinding binding)
    {
        var secure = ResolveSecure(binding);
        if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
        if (secure.Value!.Regions.Count != 0) return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure regions must close before secure-domain authority.");
        if (secure.Value.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
        var backendEpoch = BackendEpoch;
        PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> result;
        try { result = ((IPlatformSecureComputeProvider)_provider!).RevokeSecureDomain(secure.Value.ProviderLease); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            secure.Value.TerminalQuarantined = true;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Secure-domain revoke may have taken effect without closure evidence: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
        {
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
