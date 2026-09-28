using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public enum HybridCpuChildDomainAdapterReadiness
{
    ExternalBlocked = 0,
    Ready,
}

/// <summary>
/// Non-authoritative integration readiness evidence for the external child-domain ABI.
/// Feature admission remains exclusively in <see cref="PlatformFeatureManifest"/>.
/// </summary>
public sealed record HybridCpuChildDomainAdapterStatus(
    HybridCpuChildDomainAdapterReadiness Readiness,
    string ExternalRequirement,
    uint PlatformContractVersion,
    bool HasVersionedExternalChildContract,
    bool HasParentChildSubset,
    bool HasGuestMemory,
    bool HasVirtualEvents,
    bool HasVirtualTraps,
    bool HasVirtualIo,
    bool HasDefinitiveClose);

public sealed partial class HybridCpuPlatformAuthorityProvider :
    IPlatformChildDomainProvider,
    IPlatformGuestMemoryProvider,
    IPlatformVirtualEventProvider,
    IPlatformVirtualIoProvider,
    IPlatformChildExecutionProvider
{
    private sealed record ChildRecord(PlatformProviderChildDomainLease Lease, NeutralChildDomainLease Neutral);
    private sealed record GuestRecord(PlatformProviderGuestRegionMappingLease Lease, NeutralGuestMappingLease Neutral);
    private sealed record IoRecord(PlatformProviderVirtualIoLease Lease, NeutralVirtualIoLease Neutral);
    private sealed record ArtifactRecord(PlatformExecutableArtifactReceipt Receipt, NeutralExecutableArtifactReceipt Neutral);
    private readonly Dictionary<PlatformProviderChildDomainLeaseId, ChildRecord> _providerChildren = [];
    private readonly Dictionary<PlatformProviderGuestRegionMappingLeaseId, GuestRecord> _providerGuestMappings = [];
    private readonly Dictionary<PlatformProviderVirtualIoLeaseId, IoRecord> _providerVirtualIo = [];
    private readonly Dictionary<PlatformExecutableArtifactId, ArtifactRecord> _providerArtifacts = [];
    private ulong _nextProviderChildId = 1;
    private ulong _nextProviderGuestId = 1;
    private ulong _nextProviderIoId = 1;
    private ulong _nextProviderArtifactId = 1;
    private static readonly HybridCpuChildDomainAdapterStatus BlockedChildDomainStatus = new(
        HybridCpuChildDomainAdapterReadiness.ExternalBlocked,
        "EXT-HCPU-006",
        PlatformChildDomainContract.ContractVersion,
        HasVersionedExternalChildContract: true,
        HasParentChildSubset: true,
        HasGuestMemory: true,
        HasVirtualEvents: true,
        HasVirtualTraps: true,
        HasVirtualIo: false,
        HasDefinitiveClose: true);

    public HybridCpuChildDomainAdapterStatus QueryChildDomainAdapterStatus() =>
        _childRuntime is null ? BlockedChildDomainStatus : BlockedChildDomainStatus with
        {
            Readiness = HybridCpuChildDomainAdapterReadiness.Ready,
            ExternalRequirement = string.Empty,
            HasVirtualIo = _childRuntime is INeutralVirtualIoProvider,
        };

    public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(
        PlatformProviderDomainLease parentLease,
        PlatformChildDomainIntent intent)
    {
        var validation = PlatformChildDomainContract.ValidateCreateRequest(parentLease, intent);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(
                validation.Status,
                validation.Message ?? "The child-domain request is invalid.");
        }

        if (_childRuntime is null)
            return ChildContourBlocked<PlatformProviderChildDomainLease>("create child domain");
        if (!_domains.TryGetValue(parentLease.LeaseId, out var parent))
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.WrongDomain, "Parent provider lease was not found.");
        var neutralIntent = ToNeutral(intent);
        var external = _childRuntime.CreateChildDomain(parent.HybridCpuLease, neutralIntent);
        if (!external.IsSuccess) return FromNeutral<PlatformProviderChildDomainLease>(external.Status, external.Reason);
        var lease = new PlatformProviderChildDomainLease(new(_nextProviderChildId++), new(1), parentLease, intent);
        _providerChildren.Add(lease.LeaseId, new(lease, external.Value));
        return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(lease);
    }

    public PlatformAuthorityResult TransitionChildDomain(
        PlatformProviderChildDomainLease lease,
        PlatformChildDomainTransition transition)
    {
        var validation = PlatformChildDomainContract.ValidateTransition(lease, transition);
        if (!validation.IsSuccess) return validation;
        if (_childRuntime is null) return ChildContourBlocked("transition child domain");
        if (!_providerChildren.TryGetValue(lease.LeaseId, out var child) || child.Lease != lease)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Child provider lease is absent or stale.");
        var external = _childRuntime.TransitionChildDomain(child.Neutral, transition switch
        {
            PlatformChildDomainTransition.Start => NeutralChildDomainTransition.Start,
            PlatformChildDomainTransition.Park => NeutralChildDomainTransition.Park,
            PlatformChildDomainTransition.Resume => NeutralChildDomainTransition.Resume,
            PlatformChildDomainTransition.BeginDrain => NeutralChildDomainTransition.BeginDrain,
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        });
        return external.IsSuccess ? PlatformAuthorityResult.Ok() : FromNeutral(external.Status, external.Reason);
    }

    public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(
        PlatformProviderChildDomainLease lease)
    {
        var validation = PlatformChildDomainContract.ValidateLease(
            lease.ParentDomainLease,
            lease.Intent,
            lease);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(
                validation.Status,
                validation.Message ?? "The child-domain lease is invalid.");
        }

        if (_childRuntime is null) return ChildContourBlocked<PlatformChildDomainClosureReceipt>("close child domain with definitive receipt");
        if (!_providerChildren.TryGetValue(lease.LeaseId, out var child) || child.Lease != lease)
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(PlatformAuthorityStatus.Stale, "Child provider lease is absent or stale.");
        var external = _childRuntime.CloseChildDomain(child.Neutral);
        if (!external.IsSuccess || !external.Value.IsTerminal)
            return FromNeutral<PlatformChildDomainClosureReceipt>(external.Status, external.Reason);
        return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
            lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation, PlatformChildDomainClosureDisposition.Closed));
    }

    public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(
        PlatformGuestRegionMappingRequest request)
    {
        var validation = PlatformGuestMemoryContract.ValidateRequest(request);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(
                validation.Status,
                validation.Message ?? "The guest-memory request is invalid.");
        }

        if (_childRuntime is not INeutralGuestMemoryProvider guest)
            return ChildContourBlocked<PlatformProviderGuestRegionMappingLease>("map guest memory");
        if (!_providerChildren.TryGetValue(request.ChildLease.LeaseId, out var child) ||
            !_providerMappings.TryGetValue(request.ParentMapping.Lease.MappingId, out var parentMapping))
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.WrongDomain, "Exact child or parent mapping was not found.");
        var neutralRequest = new NeutralGuestMappingRequest(child.Neutral, parentMapping.HybridCpuLease,
            new(request.GuestRange.GuestAddress, request.GuestRange.ByteLength),
            (NeutralGuestMemoryAccess)(int)request.Access);
        var external = guest.MapGuestRegion(neutralRequest);
        if (!external.IsSuccess) return FromNeutral<PlatformProviderGuestRegionMappingLease>(external.Status, external.Reason);
        var lease = new PlatformProviderGuestRegionMappingLease(new(_nextProviderGuestId++), new(1), request.ChildLease,
            request.ParentMapping.Lease, request.GuestRange, request.Access);
        _providerGuestMappings.Add(lease.LeaseId, new(lease, external.Value));
        return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(lease);
    }

    public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(
        PlatformProviderGuestRegionMappingLease lease)
    {
        if (_childRuntime is not INeutralGuestMemoryProvider guest)
            return ChildContourBlocked<PlatformGuestRegionMappingClosureReceipt>("unmap guest memory");
        if (!_providerGuestMappings.TryGetValue(lease.LeaseId, out var mapping) || mapping.Lease != lease)
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.Stale, "Guest mapping is absent or stale.");
        var external = guest.UnmapGuestRegion(mapping.Neutral);
        if (!external.IsSuccess || !external.Value.IsTerminal)
            return FromNeutral<PlatformGuestRegionMappingClosureReceipt>(external.Status, external.Reason);
        _providerGuestMappings.Remove(lease.LeaseId);
        return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
            lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
            lease.ChildLease.ParentDomainLease.Generation, true));
    }

    public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(
        PlatformVirtualEventRequest request)
    {
        var validation = PlatformVirtualEventContract.ValidateRequest(request);
        if (!validation.IsSuccess)
            return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(
                validation.Status,
                validation.Message ?? "The virtual-event request is invalid.");
        return ChildContourBlocked<PlatformVirtualEventReceipt>("inject virtual event");
    }

    public PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindVirtualIo(
        PlatformVirtualIoRequest request)
    {
        var validation = PlatformVirtualIoContract.ValidateRequest(request);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(
                validation.Status,
                validation.Message ?? "The virtual-I/O request is invalid.");
        }

        if (_childRuntime is not INeutralVirtualIoProvider io)
            return ChildContourBlocked<PlatformProviderVirtualIoLease>("bind bounded virtual I/O");
        if (!_providerChildren.TryGetValue(request.ChildLease.LeaseId, out var child) ||
            !_deviceLeases.TryGetValue(request.ParentDeviceLease.LeaseId, out var device))
            return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(PlatformAuthorityStatus.WrongDomain, "Exact child or parent device was not found.");
        var neutral = new NeutralVirtualIoRequest(child.Neutral, device.HybridCpuLease,
            new(ToNeutralDeviceRights(request.Profile.Rights), request.Profile.MaximumDmaTransferBytes));
        var external = io.BindVirtualIo(neutral);
        if (!external.IsSuccess) return FromNeutral<PlatformProviderVirtualIoLease>(external.Status, external.Reason);
        var lease = new PlatformProviderVirtualIoLease(new(_nextProviderIoId++), new(1), request.ChildLease,
            request.ParentDeviceLease, request.Profile);
        _providerVirtualIo.Add(lease.LeaseId, new(lease, external.Value));
        return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Ok(lease);
    }

    public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> RevokeVirtualIo(
        PlatformProviderVirtualIoLease lease)
    {
        if (_childRuntime is not INeutralVirtualIoProvider io)
            return ChildContourBlocked<PlatformVirtualIoClosureReceipt>("revoke bounded virtual I/O");
        if (!_providerVirtualIo.TryGetValue(lease.LeaseId, out var binding) || binding.Lease != lease)
            return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Stale, "Virtual-I/O binding is absent or stale.");
        var external = io.CloseVirtualIo(binding.Neutral);
        if (!external.IsSuccess || !external.Value.IsTerminal)
            return FromNeutral<PlatformVirtualIoClosureReceipt>(external.Status, external.Reason);
        _providerVirtualIo.Remove(lease.LeaseId);
        return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
            lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
            lease.ChildLease.ParentDomainLease.Generation, true));
    }

    public PlatformAuthorityResult<PlatformExecutableArtifactReceipt> BindExecutableArtifact(PlatformExecutableArtifactRequest request)
    {
        var valid = PlatformChildExecutionContract.ValidateRequest(request);
        if (!valid.IsSuccess) return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(valid.Status, valid.Message!);
        if (_childRuntime is not INeutralChildExecutionProvider execution ||
            !_providerChildren.TryGetValue(request.ChildLease.LeaseId, out var child) ||
            !_providerGuestMappings.TryGetValue(request.GuestMapping.LeaseId, out var mapping))
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Unsupported, "Executable child adapter or exact mapping is unavailable.");
        var external = execution.BindExecutableArtifact(new(child.Neutral, mapping.Neutral,
            request.ImmutablePackage, request.MaximumExecutionSteps));
        if (!external.IsSuccess) return FromNeutral<PlatformExecutableArtifactReceipt>(external.Status, external.Reason);
        var receipt = new PlatformExecutableArtifactReceipt(new(_nextProviderArtifactId++), new(1),
            request.ChildLease.LeaseId, request.ChildLease.Generation, request.GuestMapping.LeaseId,
            request.GuestMapping.Generation, request.ChildLease.ParentDomainLease.LeaseId,
            request.ChildLease.ParentDomainLease.Generation, external.Value.ContentDigest, request.MaximumExecutionSteps);
        _providerArtifacts.Add(receipt.ArtifactId, new(receipt, external.Value));
        return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Ok(receipt);
    }

    public PlatformAuthorityResult<PlatformChildExecutionReceipt> StartExecutableArtifact(
        PlatformChildExecutionStartRequest request)
    {
        var admission = PlatformChildExecutionContract.ValidateStart(request);
        if (!admission.IsSuccess)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(admission.Status, admission.Message!);
        if (_childRuntime is not INeutralChildExecutionProvider execution ||
            !_providerChildren.TryGetValue(request.ChildLease.LeaseId, out var child) ||
            !_providerArtifacts.TryGetValue(request.Artifact.ArtifactId, out var found) || found.Receipt != request.Artifact)
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(PlatformAuthorityStatus.Stale, "Executable artifact binding is absent or stale.");
        var neutralRequest = new NeutralChildExecutionStartRequest(child.Neutral, found.Neutral,
            new(request.OperationId.Value), new(request.OperationGeneration.Value));
        var external = execution.StartExecutableArtifact(neutralRequest);
        if (!external.IsSuccess) return FromNeutral<PlatformChildExecutionReceipt>(external.Status, external.Reason);
        var value = external.Value;
        var artifact = request.Artifact;
        var receipt = new PlatformChildExecutionReceipt(artifact.ArtifactId, artifact.ArtifactGeneration,
            artifact.ChildLeaseId, artifact.ChildGeneration, artifact.MappingLeaseId, artifact.MappingGeneration,
            artifact.ParentLeaseId, artifact.ParentGeneration, artifact.ContentDigest,
            request.OperationId, request.OperationGeneration,
            new(value.ExecutionGeneration.Value), value.RetiredWorkUnits, value.RetiredSequence,
            value.LastRetiredCodeOffset, value.IsTerminal);
        var exact = PlatformChildExecutionContract.ValidateExecution(request, receipt);
        return exact.IsSuccess ? PlatformAuthorityResult<PlatformChildExecutionReceipt>.Ok(receipt) :
            PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(exact.Status, exact.Message!);
    }

    private static NeutralChildDomainIntent ToNeutral(PlatformChildDomainIntent intent) => new(
        new(intent.Profile.VirtualProcessorCount, intent.Profile.MaximumGuestMemoryBytes),
        new((NeutralChildAuthorityClass)(int)intent.Authority.ParentAuthority,
            (NeutralChildAuthorityClass)(int)intent.Authority.ChildAuthority));

    private static PlatformAuthorityStatus ToPlatform(NeutralVirtualizationStatus status) => status switch
    {
        NeutralVirtualizationStatus.Unsupported or NeutralVirtualizationStatus.Unavailable => PlatformAuthorityStatus.Unsupported,
        NeutralVirtualizationStatus.Denied => PlatformAuthorityStatus.Denied,
        NeutralVirtualizationStatus.Stale => PlatformAuthorityStatus.Stale,
        NeutralVirtualizationStatus.Revoked => PlatformAuthorityStatus.Revoked,
        NeutralVirtualizationStatus.WrongParent or NeutralVirtualizationStatus.WrongOwner => PlatformAuthorityStatus.WrongDomain,
        NeutralVirtualizationStatus.Faulted or NeutralVirtualizationStatus.Ambiguous => PlatformAuthorityStatus.Faulted,
        _ => PlatformAuthorityStatus.Faulted,
    };

    private static PlatformAuthorityResult FromNeutral(NeutralVirtualizationStatus status, string reason) =>
        PlatformAuthorityResult.Fail(ToPlatform(status), reason);
    private static PlatformAuthorityResult<T> FromNeutral<T>(NeutralVirtualizationStatus status, string reason) =>
        PlatformAuthorityResult<T>.Fail(ToPlatform(status), reason);

    private static PlatformAuthorityResult ChildContourBlocked(string operation) =>
        PlatformAuthorityResult.Fail(
            PlatformAuthorityStatus.Unsupported,
            $"EXT-HCPU-006 ExternalBlocked: HybridCPU cannot {operation} because no executable child adapter was composed.");

    private static PlatformAuthorityResult<T> ChildContourBlocked<T>(string operation) =>
        PlatformAuthorityResult<T>.Fail(
            PlatformAuthorityStatus.Unsupported,
            $"EXT-HCPU-006 ExternalBlocked: HybridCPU cannot {operation} because no executable child adapter was composed.");
}
