using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider :
    IPlatformAuthorityProvider,
    IPlatformFeatureProvider,
    IPlatformDomainExecutionProvider
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
            4,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);
        _featureManifest = new PlatformFeatureManifest(
            new[]
            {
                new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.NeutralDomains,
                    1,
                    PlatformFeatureAvailability.Executable),
                new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.OwnedRegionMapping,
                    PlatformOwnedRegionMappingContract.ContractVersion,
                    PlatformFeatureAvailability.Executable),
                new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.ExplicitMemoryVisibility,
                    PlatformRegionVisibilityContract.ContractVersion,
                    PlatformFeatureAvailability.Executable),
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

    public PlatformAuthorityResult<PlatformDomainExecutionTransitionResult> TransitionDomainExecution(
        PlatformProviderDomainLease domainLease,
        PlatformDomainExecutionTransition transition)
    {
        var validation = ValidateDomain(domainLease);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(
                validation.Status,
                validation.Message ?? "The provider domain lease is not live.");
        }

        var transitionValidation = PlatformDomainExecutionContract.ValidateTransition(transition);
        if (!transitionValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(
                transitionValidation.Status,
                transitionValidation.Message ?? "The platform execution transition is invalid.");
        }

        var record = _domains[domainLease.LeaseId];
        var neutralTransition = ToNeutralTransition(transition);
        var external = _runtime.TransitionExecution(record.HybridCpuLease, neutralTransition);
        if (!external.IsTransitioned)
        {
            if (external.Decision == NeutralExecutionTransitionDecision.Revoked)
                MarkRevoked(record);

            var status = external.Decision switch
            {
                NeutralExecutionTransitionDecision.InvalidTransition => PlatformAuthorityStatus.Denied,
                NeutralExecutionTransitionDecision.Revoked => PlatformAuthorityStatus.Revoked,
                NeutralExecutionTransitionDecision.Stale => PlatformAuthorityStatus.Faulted,
                NeutralExecutionTransitionDecision.NotFound => PlatformAuthorityStatus.Faulted,
                NeutralExecutionTransitionDecision.Faulted => PlatformAuthorityStatus.Faulted,
                _ => PlatformAuthorityStatus.Faulted,
            };

            return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(
                status,
                external.Reason);
        }

        if (external.Lease != record.HybridCpuLease ||
            external.Transition != neutralTransition ||
            external.State != ToNeutralState(PlatformDomainExecutionContract.ExpectedState(transition)))
        {
            return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Fail(
                PlatformAuthorityStatus.Faulted,
                "HybridCPU returned execution evidence that does not match the provider-owned transition.");
        }

        return PlatformAuthorityResult<PlatformDomainExecutionTransitionResult>.Ok(
            new PlatformDomainExecutionTransitionResult(
                record.Lease,
                transition,
                FromNeutralState(external.State)));
    }

    public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
    {
        var validation = ValidateDomain(lease);
        if (!validation.IsSuccess) return validation;

        if (HasActiveProviderMappings(lease))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "HybridCPU owned-region mappings must close before the provider domain lease.");
        }

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
        PlatformMemoryAccess access)
    {
        var mapped = MapOwnedRegionSlice(
            domainLease,
            new PlatformRegionSlice(region, 0, region.ByteLength, access));
        return mapped.IsSuccess
            ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(mapped.Value!.Lease)
            : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                mapped.Status,
                mapped.Message ?? "HybridCPU whole-region compatibility mapping failed.");
    }

    public PlatformAuthorityResult RevokeRegionMapping(
        PlatformProviderRegionMappingLease mapping,
        PlatformRegionRevocationPolicy policy)
    {
        if (!Enum.IsDefined(policy) || policy != PlatformRegionRevocationPolicy.DrainBeforeRevoke)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Only drain-before-revoke is supported for HybridCPU region mappings.");
        }

        return CloseProviderRegionMapping(mapping);
    }

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

    private static NeutralExecutionTransition ToNeutralTransition(
        PlatformDomainExecutionTransition transition) =>
        transition switch
        {
            PlatformDomainExecutionTransition.Start => NeutralExecutionTransition.Start,
            PlatformDomainExecutionTransition.Park => NeutralExecutionTransition.Park,
            PlatformDomainExecutionTransition.Resume => NeutralExecutionTransition.Resume,
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };

    private static NeutralExecutionState ToNeutralState(PlatformDomainExecutionState state) =>
        state switch
        {
            PlatformDomainExecutionState.Ready => NeutralExecutionState.Ready,
            PlatformDomainExecutionState.Running => NeutralExecutionState.Running,
            PlatformDomainExecutionState.Parked => NeutralExecutionState.Parked,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static PlatformDomainExecutionState FromNeutralState(NeutralExecutionState state) =>
        state switch
        {
            NeutralExecutionState.Ready => PlatformDomainExecutionState.Ready,
            NeutralExecutionState.Running => PlatformDomainExecutionState.Running,
            NeutralExecutionState.Parked => PlatformDomainExecutionState.Parked,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    private static ulong NextNonZero(ref ulong next)
    {
        var value = next;
        unchecked { next++; }
        if (value == 0)
            throw new InvalidOperationException("Provider domain identity space is exhausted.");
        return value;
    }
}
