using SingPlus.Platform;

namespace SingPlus.Platform.Host;

public sealed class HostPlatformAuthorityProvider : IPlatformAuthorityProvider
{
    private sealed class DomainRecord(PlatformProviderDomainLease lease)
    {
        public PlatformProviderDomainLease Lease { get; } = lease;
        public bool Revoked { get; set; }
    }

    private sealed class MappingRecord(PlatformProviderRegionMappingLease lease)
    {
        public PlatformProviderRegionMappingLease Lease { get; } = lease;
        public bool Revoked { get; set; }
    }

    private readonly Dictionary<PlatformProviderDomainLeaseId, DomainRecord> _domains = [];
    private readonly Dictionary<PlatformProviderRegionMappingId, MappingRecord> _mappings = [];
    private readonly Dictionary<PlatformDomainIdentity, PlatformProviderDomainLeaseId> _activeSubjects = [];
    private readonly Dictionary<PlatformRegionIdentity, PlatformProviderRegionMappingId> _activeRegions = [];
    private ulong _nextDomainId = 1;
    private ulong _nextMappingId = 1;

    public HostPlatformAuthorityProvider(
        PlatformAuthorityFeatures features =
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping)
    {
        Descriptor = new PlatformProviderDescriptor("host-test", 1, features);
    }

    public PlatformProviderDescriptor Descriptor { get; }

    public int BindDomainCallCount { get; private set; }
    public int RevokeDomainCallCount { get; private set; }
    public int MapOwnedRegionCallCount { get; private set; }
    public int RevokeRegionMappingCallCount { get; private set; }

    public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
    {
        BindDomainCallCount++;

        if (!Supports(PlatformAuthorityFeatures.NeutralDomainBinding))
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Neutral domain binding is not supported by this provider.");

        if (subject.ProcessGeneration == 0)
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
                PlatformAuthorityStatus.Denied,
                "Process generation zero is not a valid platform subject.");

        if (_activeSubjects.ContainsKey(subject))
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
                PlatformAuthorityStatus.Denied,
                "The platform subject already has an active binding.");

        var lease = new PlatformProviderDomainLease(
            new PlatformProviderDomainLeaseId(_nextDomainId++),
            new PlatformProviderLeaseGeneration(1),
            subject);

        _domains.Add(lease.LeaseId, new DomainRecord(lease));
        _activeSubjects.Add(subject, lease.LeaseId);
        return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
    }

    public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
    {
        RevokeDomainCallCount++;

        var validation = ValidateDomain(lease);
        if (!validation.IsSuccess) return validation;

        if (_mappings.Values.Any(m => !m.Revoked && m.Lease.DomainLease.LeaseId == lease.LeaseId))
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Active region mappings must be revoked before the domain binding.");

        var record = _domains[lease.LeaseId];
        record.Revoked = true;
        _activeSubjects.Remove(record.Lease.Subject);
        return PlatformAuthorityResult.Ok();
    }

    public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
        PlatformProviderDomainLease domainLease,
        PlatformRegionIdentity region,
        PlatformMemoryAccess access)
    {
        MapOwnedRegionCallCount++;

        if (!Supports(PlatformAuthorityFeatures.DirectOwnedRegionMapping))
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Direct owned-region mapping is not supported by this provider.");

        var domainValidation = ValidateDomain(domainLease);
        if (!domainValidation.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                domainValidation.Status,
                domainValidation.Message!);

        if (!IsValidAccess(access))
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Denied,
                "The requested platform memory access is invalid.");

        if (region.ByteLength <= 0)
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Denied,
                "The mapped region must have a positive byte length.");

        var expectedOwner = region.Owner;
        if (expectedOwner.DomainId != domainLease.Subject.DomainId ||
            expectedOwner.ProcessGeneration != domainLease.Subject.ProcessGeneration)
        {
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The region owner does not match the bound platform subject.");
        }

        if (_activeRegions.ContainsKey(region))
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Denied,
                "The exact owned region already has an active platform mapping.");

        var lease = new PlatformProviderRegionMappingLease(
            new PlatformProviderRegionMappingId(_nextMappingId++),
            new PlatformProviderLeaseGeneration(1),
            domainLease,
            region,
            access);

        _mappings.Add(lease.MappingId, new MappingRecord(lease));
        _activeRegions.Add(region, lease.MappingId);
        return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(lease);
    }

    public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping)
    {
        RevokeRegionMappingCallCount++;

        if (!_mappings.TryGetValue(mapping.MappingId, out var record))
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The platform mapping does not exist.");

        if (record.Lease.Generation != mapping.Generation)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The platform mapping generation is stale.");

        if (record.Lease != mapping)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The platform mapping identity does not match the active mapping.");

        if (record.Revoked)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The platform mapping has already been revoked.");

        record.Revoked = true;
        _activeRegions.Remove(record.Lease.Region);
        return PlatformAuthorityResult.Ok();
    }

    private PlatformAuthorityResult ValidateDomain(PlatformProviderDomainLease lease)
    {
        if (!_domains.TryGetValue(lease.LeaseId, out var record))
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The platform domain binding does not exist.");

        if (record.Lease.Generation != lease.Generation)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The platform domain binding generation is stale.");

        if (record.Lease.Subject != lease.Subject)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The platform domain subject does not match the active binding.");

        if (record.Revoked)
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The platform domain binding has been revoked.");

        return PlatformAuthorityResult.Ok();
    }

    private bool Supports(PlatformAuthorityFeatures feature) =>
        (Descriptor.Features & feature) == feature;

    private static bool IsValidAccess(PlatformMemoryAccess access) =>
        access != PlatformMemoryAccess.None &&
        (access & ~(PlatformMemoryAccess.Read | PlatformMemoryAccess.Write)) == 0;
}
