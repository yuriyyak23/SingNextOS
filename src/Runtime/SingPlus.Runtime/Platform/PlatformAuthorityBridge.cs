using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformDomainBindingId(ulong Value);
public readonly record struct PlatformDomainBindingGeneration(ulong Value);
public readonly record struct PlatformRegionMappingId(ulong Value);
public readonly record struct PlatformRegionMappingGeneration(ulong Value);

public readonly record struct PlatformDomainBinding(
    PlatformDomainBindingId BindingId,
    PlatformDomainBindingGeneration Generation,
    PlatformDomainIdentity Subject);

public readonly record struct PlatformRegionMapping(
    PlatformRegionMappingId MappingId,
    PlatformRegionMappingGeneration Generation,
    PlatformDomainBinding DomainBinding,
    RegionHandle Region,
    PlatformMemoryAccess Access);

public sealed class PlatformAuthorityBridge
{
    private sealed class DomainRecord(
        PlatformDomainBinding binding,
        PlatformProviderDomainLease providerLease)
    {
        public PlatformDomainBinding Binding { get; } = binding;
        public PlatformProviderDomainLease ProviderLease { get; } = providerLease;
        public bool Revoked { get; set; }
    }

    private sealed class MappingRecord(
        PlatformRegionMapping mapping,
        PlatformProviderRegionMappingLease providerLease)
    {
        public PlatformRegionMapping Mapping { get; } = mapping;
        public PlatformProviderRegionMappingLease ProviderLease { get; } = providerLease;
        public bool Revoked { get; set; }
    }

    private readonly IPlatformAuthorityProvider? _provider;
    private readonly Dictionary<PlatformDomainBindingId, DomainRecord> _domains = [];
    private readonly Dictionary<PlatformRegionMappingId, MappingRecord> _mappings = [];
    private readonly Dictionary<PlatformDomainIdentity, PlatformDomainBindingId> _activeSubjects = [];
    private ulong _nextDomainBindingId = 1;
    private ulong _nextMappingId = 1;

    internal PlatformAuthorityBridge(IPlatformAuthorityProvider? provider)
    {
        _provider = provider;
    }

    public PlatformProviderDescriptor? ProviderDescriptor => _provider?.Descriptor;

    public bool IsAvailable => _provider is not null;

    internal KernelResult<PlatformDomainBinding> BindDomain(PlatformDomainIdentity subject)
    {
        if (_provider is null)
            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.PlatformUnavailable,
                "No platform authority provider is configured.");

        if (!Supports(PlatformAuthorityFeatures.NeutralDomainBinding))
            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not support neutral domain binding.");

        if (_activeSubjects.ContainsKey(subject))
            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.PlatformDenied,
                "The local subject already has an active platform binding.");

        var providerResult = _provider.BindDomain(subject);
        if (!providerResult.IsSuccess)
            return FromProviderFailure<PlatformDomainBinding>(providerResult.Status, providerResult.Message);

        var providerLease = providerResult.Value!;
        if (providerLease.Subject != subject)
        {
            _ = _provider.RevokeDomain(providerLease);
            return KernelResult<PlatformDomainBinding>.Fail(
                KernelError.PlatformFaulted,
                "The platform provider returned a domain binding for a different subject.");
        }

        var binding = new PlatformDomainBinding(
            new PlatformDomainBindingId(_nextDomainBindingId++),
            new PlatformDomainBindingGeneration(1),
            subject);

        _domains.Add(binding.BindingId, new DomainRecord(binding, providerLease));
        _activeSubjects.Add(subject, binding.BindingId);
        return KernelResult<PlatformDomainBinding>.Ok(binding);
    }

    internal KernelResult RevokeDomain(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateDomain(binding, expectedSubject);
        if (!validation.IsSuccess) return validation;

        if (_mappings.Values.Any(m => !m.Revoked && m.Mapping.DomainBinding.BindingId == binding.BindingId))
            return KernelResult.Fail(
                KernelError.PlatformBindingActive,
                "Active platform region mappings must be revoked before the domain binding.");

        var record = _domains[binding.BindingId];
        var providerResult = _provider!.RevokeDomain(record.ProviderLease);
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status is PlatformAuthorityStatus.Revoked or PlatformAuthorityStatus.Stale)
                MarkDomainRevoked(record);

            return FromProviderFailure(providerResult.Status, providerResult.Message);
        }

        MarkDomainRevoked(record);
        return KernelResult.Ok();
    }

    internal KernelResult ValidateDomain(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_domains.TryGetValue(binding.BindingId, out var record))
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The platform domain binding does not exist.");

        if (record.Binding.Generation != binding.Generation)
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform domain binding generation is stale.");

        if (record.Binding.Subject != binding.Subject || record.Binding.Subject != expectedSubject)
            return KernelResult.Fail(
                KernelError.WrongPlatformDomain,
                "The platform domain binding does not belong to the expected local subject.");

        if (record.Revoked)
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform domain binding has been revoked.");

        return KernelResult.Ok();
    }

    internal KernelResult<PlatformRegionMapping> MapOwnedRegion(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        PlatformRegionIdentity region,
        PlatformMemoryAccess access)
    {
        var bindingValidation = ValidateDomain(binding, expectedSubject);
        if (!bindingValidation.IsSuccess)
            return KernelResult<PlatformRegionMapping>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);

        if (_provider is null)
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformUnavailable,
                "No platform authority provider is configured.");

        if (!Supports(PlatformAuthorityFeatures.DirectOwnedRegionMapping))
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformUnsupported,
                "The platform provider does not support direct owned-region mapping.");

        if (!IsValidAccess(access))
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformDenied,
                "The requested platform memory access is invalid.");

        var domainRecord = _domains[binding.BindingId];
        var providerResult = _provider.MapOwnedRegion(domainRecord.ProviderLease, region, access);
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status is PlatformAuthorityStatus.Revoked or PlatformAuthorityStatus.Stale)
                MarkDomainRevoked(domainRecord);

            return FromProviderFailure<PlatformRegionMapping>(providerResult.Status, providerResult.Message);
        }

        var providerLease = providerResult.Value!;
        if (providerLease.DomainLease != domainRecord.ProviderLease ||
            providerLease.Region != region ||
            providerLease.Access != access)
        {
            _ = _provider.RevokeRegionMapping(providerLease);
            return KernelResult<PlatformRegionMapping>.Fail(
                KernelError.PlatformFaulted,
                "The platform provider returned a mapping identity that does not match the request.");
        }

        var mapping = new PlatformRegionMapping(
            new PlatformRegionMappingId(_nextMappingId++),
            new PlatformRegionMappingGeneration(1),
            binding,
            region.Handle,
            access);

        _mappings.Add(mapping.MappingId, new MappingRecord(mapping, providerLease));
        return KernelResult<PlatformRegionMapping>.Ok(mapping);
    }

    internal KernelResult RevokeRegionMapping(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateMapping(mapping, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _mappings[mapping.MappingId];
        var providerResult = _provider!.RevokeRegionMapping(record.ProviderLease);
        if (!providerResult.IsSuccess)
        {
            if (providerResult.Status is PlatformAuthorityStatus.Revoked or PlatformAuthorityStatus.Stale)
                record.Revoked = true;

            return FromProviderFailure(providerResult.Status, providerResult.Message);
        }

        record.Revoked = true;
        return KernelResult.Ok();
    }

    internal KernelResult ValidateMapping(
        PlatformRegionMapping mapping,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_mappings.TryGetValue(mapping.MappingId, out var record))
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The platform region mapping does not exist.");

        if (record.Mapping.Generation != mapping.Generation)
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform region mapping generation is stale.");

        if (record.Mapping != mapping)
            return KernelResult.Fail(
                KernelError.WrongPlatformDomain,
                "The platform region mapping identity does not match the active mapping.");

        var domainValidation = ValidateDomain(record.Mapping.DomainBinding, expectedSubject);
        if (!domainValidation.IsSuccess) return domainValidation;

        if (record.Revoked)
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform region mapping has been revoked.");

        return KernelResult.Ok();
    }

    internal bool HasActiveAuthority(PlatformDomainIdentity subject) =>
        (_activeSubjects.TryGetValue(subject, out var id) &&
         _domains.TryGetValue(id, out var domain) &&
         !domain.Revoked) ||
        _mappings.Values.Any(
            m => !m.Revoked && m.Mapping.DomainBinding.Subject == subject);

    internal bool HasActiveMapping(RegionHandle region) =>
        _mappings.Values.Any(m => !m.Revoked && m.Mapping.Region == region);

    private void MarkDomainRevoked(DomainRecord record)
    {
        record.Revoked = true;
        _activeSubjects.Remove(record.Binding.Subject);
    }

    private bool Supports(PlatformAuthorityFeatures feature) =>
        _provider is not null &&
        (_provider.Descriptor.Features & feature) == feature;

    private static bool IsValidAccess(PlatformMemoryAccess access) =>
        access != PlatformMemoryAccess.None &&
        (access & ~(PlatformMemoryAccess.Read | PlatformMemoryAccess.Write)) == 0;

    private static KernelResult FromProviderFailure(
        PlatformAuthorityStatus status,
        string? message) =>
        KernelResult.Fail(ToKernelError(status), message ?? $"Platform provider returned {status}.");

    private static KernelResult<T> FromProviderFailure<T>(
        PlatformAuthorityStatus status,
        string? message) =>
        KernelResult<T>.Fail(ToKernelError(status), message ?? $"Platform provider returned {status}.");

    private static KernelError ToKernelError(PlatformAuthorityStatus status) =>
        status switch
        {
            PlatformAuthorityStatus.Unavailable => KernelError.PlatformUnavailable,
            PlatformAuthorityStatus.Unsupported => KernelError.PlatformUnsupported,
            PlatformAuthorityStatus.Denied => KernelError.PlatformDenied,
            PlatformAuthorityStatus.Stale => KernelError.StaleGeneration,
            PlatformAuthorityStatus.Revoked => KernelError.PlatformBindingRevoked,
            PlatformAuthorityStatus.WrongDomain => KernelError.WrongPlatformDomain,
            PlatformAuthorityStatus.Faulted => KernelError.PlatformFaulted,
            _ => KernelError.PlatformFaulted
        };
}
