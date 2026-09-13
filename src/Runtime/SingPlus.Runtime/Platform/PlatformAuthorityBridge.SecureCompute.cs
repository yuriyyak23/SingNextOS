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
        public bool Quarantined { get; set; }
    }
    private readonly Dictionary<SecureDomainBindingId, SecureDomainRecord> _secureDomains = [];
    private ulong _nextSecureDomainBindingId = 1;

    internal KernelResult<SecureDomainBinding> CreateSecureDomain(PlatformDomainBinding parent, PlatformDomainIdentity subject, SecureDomainProfile profile)
    {
        var validParent = ValidateDomain(parent, subject);
        if (!validParent.IsSuccess) return KernelResult<SecureDomainBinding>.Fail(validParent.Error, validParent.Message!);
        if (_provider is not IPlatformSecureComputeProvider provider ||
            !_featureManifest.Supports(PlatformFeatureFamily.SecureDomains, PlatformSecureComputeContract.ContractVersion, PlatformFeatureAvailability.ProductionSecure))
            return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformUnsupported, "Every requested secure property requires a ProductionSecure provider claim.");
        if (profile.MaximumMemoryBytes <= 0 || profile.RequiredProperties is null || profile.RequiredProperties.Count == 0 ||
            profile.RequiredProperties.Any(property => !Enum.IsDefined(property)) || profile.RequiredProperties.Distinct().Count() != profile.RequiredProperties.Count)
            return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformDenied, "Secure-domain profile is malformed.");
        var result = provider.CreateSecureDomain(new(_domains[parent.BindingId].ProviderLease, profile));
        if (!result.IsSuccess) return FromProviderFailure<SecureDomainBinding>(result.Status, result.Message);
        var lease = result.Value;
        if (lease.DomainId.Value == 0 || lease.Generation.Value == 0 || lease.Parent != _domains[parent.BindingId].ProviderLease ||
            lease.ProvenProperties is null || lease.ProvenProperties.Any(property => !Enum.IsDefined(property)) ||
            profile.RequiredProperties.Any(property => !lease.ProvenProperties.Contains(property)))
        {
            var cleanup = provider.RevokeSecureDomain(lease);
            if (!IsExactClosure(cleanup, lease))
            {
                var quarantined = new SecureDomainBinding(new(_nextSecureDomainBindingId++), new(1), parent);
                _secureDomains.Add(quarantined.BindingId, new(quarantined, lease) { Quarantined = true });
            }
            return KernelResult<SecureDomainBinding>.Fail(KernelError.PlatformFaulted,
                IsExactClosure(cleanup, lease) ? "Provider did not prove every requested secure property." : "Malformed secure-domain admission could not be closed and remains quarantined.");
        }
        var binding = new SecureDomainBinding(new(_nextSecureDomainBindingId++), new(1), parent);
        _secureDomains.Add(binding.BindingId, new(binding, lease));
        return KernelResult<SecureDomainBinding>.Ok(binding);
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
        var result = ((IPlatformSecureComputeProvider)_provider!).BindSecureRegion(secure.Value.ProviderLease, mapped.ProviderLease, regionClass);
        if (!result.IsSuccess) return FromProviderFailure(result.Status, result.Message);
        var providerBinding = result.Value;
        if (providerBinding.BindingId.Value == 0 || providerBinding.Generation.Value == 0 || providerBinding.Domain != secure.Value.ProviderLease ||
            providerBinding.Mapping != mapped.ProviderLease || providerBinding.RegionClass != regionClass)
        { secure.Value.Quarantined = true; return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-region binding is malformed."); }
        secure.Value.Regions.Add(mapping.MappingId, providerBinding);
        return KernelResult.Ok();
    }

    internal KernelResult UnbindSecureRegion(SecureDomainBinding binding, PlatformRegionMapping mapping)
    {
        var secure = ResolveSecure(binding);
        if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
        if (!secure.Value!.Regions.TryGetValue(mapping.MappingId, out var providerBinding))
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Secure-region binding was not found.");
        if (secure.Value.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure-region authority is quarantined.");
        var result = ((IPlatformSecureComputeProvider)_provider!).UnbindSecureRegion(providerBinding);
        if (!result.IsSuccess) { secure.Value.Quarantined = true; return FromProviderFailure(result.Status, result.Message); }
        if (result.Value.Binding != providerBinding || !result.Value.Closed)
        { secure.Value.Quarantined = true; return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-region closure receipt is malformed."); }
        secure.Value.Regions.Remove(mapping.MappingId);
        return KernelResult.Ok();
    }

    private bool HasActiveSecureRegion(PlatformRegionMappingId mappingId) =>
        _secureDomains.Values.Any(record => record.Regions.ContainsKey(mappingId));

    internal KernelResult TransitionSecureDomain(SecureDomainBinding binding, PlatformSecureDomainTransition transition)
    {
        var secure = ResolveSecure(binding);
        if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
        if (secure.Value!.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
        if (!Enum.IsDefined(transition)) return KernelResult.Fail(KernelError.PlatformDenied, "Secure-domain transition is invalid.");
        var result = ((IPlatformSecureComputeProvider)_provider!).TransitionSecureDomain(secure.Value!.ProviderLease, transition);
        if (!result.IsSuccess) { secure.Value.Quarantined = true; return FromProviderFailure(result.Status, result.Message); }
        if (result.Value.Domain != secure.Value.ProviderLease || result.Value.Transition != transition || !result.Value.Accepted)
        { secure.Value.Quarantined = true; return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-domain transition receipt is malformed."); }
        return KernelResult.Ok();
    }

    internal KernelResult RevokeSecureDomain(SecureDomainBinding binding)
    {
        var secure = ResolveSecure(binding);
        if (!secure.IsSuccess) return KernelResult.Fail(secure.Error, secure.Message!);
        if (secure.Value!.Regions.Count != 0) return KernelResult.Fail(KernelError.PlatformBindingActive, "Secure regions must close before secure-domain authority.");
        if (secure.Value.Quarantined) return KernelResult.Fail(KernelError.PlatformFaulted, "Secure domain is quarantined.");
        var result = ((IPlatformSecureComputeProvider)_provider!).RevokeSecureDomain(secure.Value!.ProviderLease);
        if (!result.IsSuccess) { secure.Value.Quarantined = true; return FromProviderFailure(result.Status, result.Message); }
        if (!IsExactClosure(result, secure.Value.ProviderLease))
        { secure.Value.Quarantined = true; return KernelResult.Fail(KernelError.PlatformFaulted, "Provider secure-domain closure receipt is malformed."); }
        _secureDomains.Remove(binding.BindingId);
        return KernelResult.Ok();
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
