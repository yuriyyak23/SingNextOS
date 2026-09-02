using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed class HybridCpuPlatformAuthorityProvider :
    IPlatformAuthorityProvider,
    IPlatformFeatureProvider
{
    private sealed class DomainRecord(
        PlatformProviderDomainLease lease,
        NeutralDomainBindingLease hybridCpuLease)
    {
        public PlatformProviderDomainLease Lease { get; } = lease;
        public NeutralDomainBindingLease HybridCpuLease { get; } = hybridCpuLease;
        public bool Revoked { get; set; }
    }

    private readonly NeutralDomainRuntimeFacade _runtime;
    private readonly Dictionary<PlatformProviderDomainLeaseId, DomainRecord> _domains = [];
    private readonly Dictionary<PlatformDomainIdentity, PlatformProviderDomainLeaseId> _activeSubjects = [];
    private readonly PlatformFeatureManifest _featureManifest;
    private ulong _nextProviderDomainId = 1;

    public HybridCpuPlatformAuthorityProvider()
        : this(new NeutralDomainRuntimeFacade())
    {
    }

    public HybridCpuPlatformAuthorityProvider(NeutralDomainRuntimeFacade runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        Descriptor = new PlatformProviderDescriptor(
            new PlatformProviderId("hybridcpu-neutral"),
            3,
            PlatformAuthorityFeatures.NeutralDomainBinding);
        _featureManifest = new PlatformFeatureManifest(
            new[]
            {
                new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.NeutralDomains,
                    1,
                    PlatformFeatureAvailability.RuntimeAdmission),
            });
    }

    public PlatformProviderDescriptor Descriptor { get; }

    public PlatformFeatureManifest QueryFeatures() => _featureManifest;

    public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
        PlatformDomainIdentity subject)
    {
        if (subject.ProcessGeneration == 0)
        {
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
                PlatformAuthorityStatus.Denied,
                "Process generation zero is not a valid platform subject.");
        }

        if (_activeSubjects.ContainsKey(subject))
        {
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
                PlatformAuthorityStatus.Denied,
                "The platform subject already has an active HybridCPU binding.");
        }

        var external = _runtime.Bind(NeutralDomainProfile.OrdinaryService);
        if (!external.IsBound)
            return FromBindFailure(external);

        var lease = new PlatformProviderDomainLease(
            new PlatformProviderDomainLeaseId(NextNonZero(ref _nextProviderDomainId)),
            new PlatformProviderLeaseGeneration(1),
            subject);
        _domains.Add(lease.LeaseId, new DomainRecord(lease, external.Lease));
        _activeSubjects.Add(subject, lease.LeaseId);
        return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
    }

    public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
    {
        var validation = ValidateDomain(lease);
        if (!validation.IsSuccess) return validation;

        var record = _domains[lease.LeaseId];
        var external = _runtime.Close(record.HybridCpuLease);
        switch (external.Decision)
        {
            case NeutralDomainCloseDecision.Closed:
            case NeutralDomainCloseDecision.Revoked:
                MarkRevoked(record);
                return PlatformAuthorityResult.Ok();

            case NeutralDomainCloseDecision.Stale:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    "HybridCPU rejected the provider-owned domain lease as stale; closure is not proven.");

            case NeutralDomainCloseDecision.NotFound:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    "HybridCPU no longer recognizes the provider-owned domain lease; closure is not proven.");

            case NeutralDomainCloseDecision.Faulted:
            default:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    external.Reason);
        }
    }

    public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
        PlatformProviderDomainLease domainLease,
        PlatformRegionIdentity region,
        PlatformMemoryAccess access) =>
        PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
            PlatformAuthorityStatus.Unsupported,
            "HybridCPU owned-region mapping is intentionally outside the Phase-3 neutral-domain slice.");

    public PlatformAuthorityResult RevokeRegionMapping(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionRevocationPolicy policy) =>
        PlatformAuthorityResult.Fail(
            PlatformAuthorityStatus.Unsupported,
            "HybridCPU owned-region mapping is intentionally outside the Phase-3 neutral-domain slice.");

    private PlatformAuthorityResult ValidateDomain(PlatformProviderDomainLease lease)
    {
        if (!_domains.TryGetValue(lease.LeaseId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider domain lease does not exist.");
        }

        if (record.Lease.Generation != lease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider domain lease generation is stale.");
        }

        if (record.Lease.Subject != lease.Subject)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The provider domain lease belongs to a different Sing platform subject.");
        }

        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider domain lease has already been revoked.");
        }

        return PlatformAuthorityResult.Ok();
    }

    private static PlatformAuthorityResult<PlatformProviderDomainLease> FromBindFailure(
        NeutralDomainBindResult result)
    {
        var status = result.Decision switch
        {
            NeutralDomainBindDecision.UnsupportedProfile => PlatformAuthorityStatus.Unsupported,
            NeutralDomainBindDecision.Faulted => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };

        return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
            status,
            result.Reason);
    }

    private void MarkRevoked(DomainRecord record)
    {
        record.Revoked = true;
        _activeSubjects.Remove(record.Lease.Subject);
    }

    private static ulong NextNonZero(ref ulong next)
    {
        var value = next;
        unchecked { next++; }
        if (value == 0)
            throw new InvalidOperationException("Provider domain identity space is exhausted.");
        return value;
    }
}
