using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter.Backend;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

/// <summary>Exclusive translation boundary between neutral child authority and ExternalRuntime V3.</summary>
public sealed class HybridCpuExecutableChildAdapter : INeutralRuntimeFeatureProvider,
    INeutralChildDomainProvider, INeutralGuestMemoryProvider, INeutralVirtualEventProvider,
    INeutralVirtualIoProvider, INeutralChildExecutionProvider
{
    private sealed record Child(NeutralChildDomainLease Neutral, ExternalChildDomainLease External,
        ExternalDomainLease Parent, NeutralChildDomainState State);
    private sealed record Mapping(NeutralGuestMappingLease Neutral, ExternalGuestMappingLease External);
    private sealed record Artifact(NeutralExecutableArtifactReceipt Neutral, ExternalChildArtifactBindReceipt External);
    private sealed record Io(NeutralVirtualIoLease Neutral, ExternalChildVirtualIoBindReceipt External);

    private readonly object sync = new();
    private readonly IHybridCpuExternalRuntime root;
    private readonly IHybridCpuChildDomainRuntimeV3 childRuntime;
    private readonly HybridCpuExternalFeatureManifest externalFeatures;
    private readonly Dictionary<NeutralDomainBindingLease, ExternalDomainLease> parents = [];
    private readonly Dictionary<NeutralChildDomainHandle, Child> children = [];
    private readonly Dictionary<NeutralGuestMappingHandle, Mapping> mappings = [];
    private readonly Dictionary<NeutralExecutableArtifactHandle, Artifact> artifacts = [];
    private readonly Dictionary<NeutralVirtualIoHandle, Io> ioBindings = [];
    private ulong nextIdentity = 1;
    private ulong nextOperation = 1;

    public HybridCpuExecutableChildAdapter() : this(new HybridCpuExternalRuntime()) { }

    internal HybridCpuExecutableChildAdapter(HybridCpuExternalRuntime runtime)
    {
        root = runtime;
        childRuntime = runtime;
        externalFeatures = root.QueryFeatures();
        if (!ExternalChildContractQualification.CanAdvertiseExecutable(externalFeatures))
            throw new InvalidOperationException("ExternalRuntime V3 executable artifact and bounded-I/O qualification is required.");
    }

    public NeutralRuntimeFeatureManifest QueryNeutralFeatures() => new(
    [
        new(NeutralRuntimeFeatureFamily.ChildDomainLifecycle, NeutralChildDomainContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
        new(NeutralRuntimeFeatureFamily.ChildGuestMemory, NeutralGuestMemoryContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
        new(NeutralRuntimeFeatureFamily.ChildEventDelivery, NeutralVirtualEventContract.ContractVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        new(NeutralRuntimeFeatureFamily.BoundedVirtualIo, NeutralVirtualIoContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
        new(NeutralRuntimeFeatureFamily.ChildExecutableArtifact, NeutralChildExecutionContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
    ]);

    public NeutralVirtualizationResult<NeutralChildDomainLease> CreateChildDomain(
        NeutralDomainBindingLease parentLease, NeutralChildDomainIntent intent)
    {
        var valid = NeutralChildDomainContract.ValidateCreate(parentLease, intent);
        if (!valid.IsSuccess) return Fail<NeutralChildDomainLease>(valid);
        lock (sync)
        {
            if (!parents.TryGetValue(parentLease, out ExternalDomainLease parent))
            {
                ExternalDomainBindResult bound = root.BindDomain(new(Guid.NewGuid(), ExternalDomainProfile.IsolatedDomain, Op()));
                if (bound.Outcome != ExternalRuntimeOutcome.Bound || bound.Receipt is null)
                    return Fail<NeutralChildDomainLease>(bound.Outcome, bound.Reason);
                parent = bound.Receipt.Lease;
                parents.Add(parentLease, parent);
            }
            ExternalChildAuthority authority = ToExternal(intent.Authority.ChildAuthority);
            if (authority == ExternalChildAuthority.None)
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Denied, "No executable child authority was admitted.");
            var result = childRuntime.CreateChildDomain(parent,
                new(Guid.NewGuid(), authority, checked((ulong)intent.Profile.MaximumGuestMemoryBytes), Op()));
            if (result.Outcome != ExternalRuntimeOutcome.Bound || result.Receipt is null)
                return Fail<NeutralChildDomainLease>(result.Outcome, result.Reason);
            var lease = new NeutralChildDomainLease(parentLease, new(Next()), new(1), intent);
            children.Add(lease.Handle, new(lease, result.Receipt.Lease, parent, NeutralChildDomainState.Created));
            return Ok(lease);
        }
    }

    public NeutralVirtualizationResult TransitionChildDomain(NeutralChildDomainLease lease, NeutralChildDomainTransition transition)
    {
        lock (sync)
        {
            if (!TryChild(lease, out Child? record, out var failure)) return failure;
            if (transition == NeutralChildDomainTransition.BeginDrain)
            {
                children[lease.Handle] = record! with { State = NeutralChildDomainState.Draining };
                return Ok();
            }
            if (transition == NeutralChildDomainTransition.Start)
                return Fail(NeutralVirtualizationStatus.Denied,
                    "Executable Start requires the exact artifact/start operation contract.");
            ExternalChildDomainTransition external = transition switch
            {
                NeutralChildDomainTransition.Park => ExternalChildDomainTransition.Park,
                NeutralChildDomainTransition.Resume => ExternalChildDomainTransition.Resume,
                _ => throw new ArgumentOutOfRangeException(nameof(transition)),
            };
            var result = childRuntime.TransitionChildDomain(record!.External, external, Op());
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded) return Fail(result.Outcome, result.Reason);
            var state = transition == NeutralChildDomainTransition.Park ? NeutralChildDomainState.Parked : NeutralChildDomainState.Running;
            children[lease.Handle] = record with { State = state };
            return Ok();
        }
    }

    public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> CloseChildDomain(NeutralChildDomainLease lease)
    {
        lock (sync)
        {
            if (!TryChild(lease, out Child? record, out var failure)) return Fail<NeutralChildDomainCloseReceipt>(failure);
            var result = childRuntime.CloseChildDomain(record!.External, Op());
            if (result.Outcome != ExternalRuntimeOutcome.Closed || result.Receipt is null || !result.Receipt.IsTerminal)
                return Fail<NeutralChildDomainCloseReceipt>(result.Outcome, result.Reason);
            children[lease.Handle] = record with { State = NeutralChildDomainState.Closed };
            return Ok(new NeutralChildDomainCloseReceipt(lease.Handle, lease.Epoch, lease.ParentLease.Handle, lease.ParentLease.Epoch,
                NeutralChildDomainState.Closed, true));
        }
    }

    public NeutralVirtualizationResult<NeutralGuestMappingLease> MapGuestRegion(NeutralGuestMappingRequest request)
    {
        var valid = NeutralGuestMemoryContract.ValidateMap(request, NeutralChildDomainState.Created);
        if (!valid.IsSuccess) return Fail<NeutralGuestMappingLease>(valid);
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? child, out var failure)) return Fail<NeutralGuestMappingLease>(failure);
            var result = childRuntime.MapChildGuestMemory(child!.External,
                new(request.GuestRange.Offset, checked((ulong)request.GuestRange.Length), Op()));
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null)
                return Fail<NeutralGuestMappingLease>(result.Outcome, result.Reason);
            var lease = new NeutralGuestMappingLease(request.ChildLease, request.ParentMapping, request.GuestRange,
                request.Access, new(Next()), new(1));
            mappings.Add(lease.Handle, new(lease, result.Receipt.Mapping));
            return Ok(lease);
        }
    }

    public NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> UnmapGuestRegion(NeutralGuestMappingLease lease)
    {
        lock (sync)
        {
            if (!mappings.TryGetValue(lease.Handle, out Mapping? mapping) || mapping.Neutral != lease)
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Stale, "Guest mapping is absent or stale.");
            var result = childRuntime.UnmapChildGuestMemory(mapping.External, Op());
            if (result.Outcome != ExternalRuntimeOutcome.Closed || result.Receipt is null || !result.Receipt.IsTerminal)
                return Fail<NeutralGuestMappingCloseReceipt>(result.Outcome, result.Reason);
            mappings.Remove(lease.Handle);
            return Ok(new NeutralGuestMappingCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle, lease.ChildLease.Epoch,
                lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true));
        }
    }

    public NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> BindExecutableArtifact(NeutralExecutableArtifactRequest request)
    {
        var valid = NeutralChildExecutionContract.ValidateAdmission(request);
        if (!valid.IsSuccess) return Fail<NeutralExecutableArtifactReceipt>(valid);
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? child, out var failure)) return Fail<NeutralExecutableArtifactReceipt>(failure);
            if (!mappings.TryGetValue(request.GuestMapping.Handle, out Mapping? mapping) || mapping.Neutral != request.GuestMapping)
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Stale, "Exact guest mapping is absent or stale.");
            var result = childRuntime.BindChildExecutableArtifact(child!.External,
                new(mapping.External, request.ImmutablePackage.ToArray(), request.MaximumExecutionSteps, Op()));
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null)
                return Fail<NeutralExecutableArtifactReceipt>(result.Outcome, result.Reason);
            var receipt = new NeutralExecutableArtifactReceipt(new(Next()), new(1), request.ChildLease.Handle,
                request.ChildLease.Epoch, request.GuestMapping.Handle, request.GuestMapping.Epoch,
                request.ChildLease.ParentLease.Handle, request.ChildLease.ParentLease.Epoch,
                result.Receipt.PackageSha256, request.MaximumExecutionSteps);
            var exact = NeutralChildExecutionContract.ValidateAdmissionReceipt(request, receipt);
            if (!exact.IsSuccess) return Fail<NeutralExecutableArtifactReceipt>(exact);
            artifacts.Add(receipt.ArtifactHandle, new(receipt, result.Receipt));
            return Ok(receipt);
        }
    }

    public NeutralVirtualizationResult<NeutralChildExecutionReceipt> StartExecutableArtifact(
        NeutralChildExecutionStartRequest request)
    {
        var admission = NeutralChildExecutionContract.ValidateStart(request);
        if (!admission.IsSuccess) return Fail<NeutralChildExecutionReceipt>(admission);
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? child, out var failure)) return Fail<NeutralChildExecutionReceipt>(failure);
            if (!artifacts.TryGetValue(request.Artifact.ArtifactHandle, out Artifact? found) || found.Neutral != request.Artifact)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Stale, "Artifact admission is absent or stale.");
            var result = childRuntime.StartChildExecution(child!.External,
                new(found.External.ArtifactHandle, found.External.ArtifactEpoch, Op()));
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null)
                return Fail<NeutralChildExecutionReceipt>(result.Outcome, result.Reason);
            var receipt = ToNeutral(result.Receipt, request);
            var exact = NeutralChildExecutionContract.ValidateExecutionReceipt(request, receipt);
            return exact.IsSuccess ? Ok(receipt) : Fail<NeutralChildExecutionReceipt>(exact);
        }
    }

    public NeutralVirtualizationResult<NeutralVirtualIoLease> BindVirtualIo(NeutralVirtualIoRequest request)
    {
        var valid = NeutralVirtualIoContract.ValidateBind(request, NeutralChildDomainState.Created);
        if (!valid.IsSuccess) return Fail<NeutralVirtualIoLease>(valid);
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? child, out var failure)) return Fail<NeutralVirtualIoLease>(failure);
            var rights = (ExternalChildVirtualIoRights)(uint)request.Profile.Rights;
            var result = childRuntime.BindChildVirtualIo(child!.External,
                new(new(Guid.NewGuid()), new(request.ParentDeviceLease.Epoch.Value), rights,
                    checked((ulong)request.Profile.MaximumTransferBytes), Op()));
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null)
                return Fail<NeutralVirtualIoLease>(result.Outcome, result.Reason);
            var lease = new NeutralVirtualIoLease(request.ChildLease, request.ParentDeviceLease, request.Profile,
                new(Next()), new(1));
            ioBindings.Add(lease.Handle, new(lease, result.Receipt));
            return Ok(lease);
        }
    }

    public NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> CloseVirtualIo(NeutralVirtualIoLease lease)
    {
        lock (sync)
        {
            if (!ioBindings.TryGetValue(lease.Handle, out Io? io) || io.Neutral != lease)
                return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Stale, "I/O binding is absent or stale.");
            if (!TryChild(lease.ChildLease, out Child? child, out var failure)) return Fail<NeutralVirtualIoCloseReceipt>(failure);
            var result = childRuntime.CloseChildVirtualIo(child!.External, io.External.IoHandle, io.External.IoEpoch, Op());
            if (result.Outcome != ExternalRuntimeOutcome.Closed || result.Receipt is null || !result.Receipt.IsTerminal)
                return Fail<NeutralVirtualIoCloseReceipt>(result.Outcome, result.Reason);
            ioBindings.Remove(lease.Handle);
            return Ok(new NeutralVirtualIoCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle, lease.ChildLease.Epoch,
                lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true));
        }
    }

    public NeutralVirtualizationResult<NeutralVirtualEventReceipt> InjectVirtualEvent(NeutralVirtualEventRequest request) =>
        Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Unsupported, "Executable event injection is not yet owned by the V3 runner.");

    private bool TryChild(NeutralChildDomainLease lease, out Child? child, out NeutralVirtualizationResult failure)
    {
        if (!children.TryGetValue(lease.Handle, out child)) { failure = Fail(NeutralVirtualizationStatus.Denied, "Child was not found."); return false; }
        if (child.Neutral.Epoch != lease.Epoch) { failure = Fail(NeutralVirtualizationStatus.Stale, "Child epoch is stale."); return false; }
        if (child.Neutral != lease) { failure = Fail(NeutralVirtualizationStatus.WrongParent, "Child parent or intent differs."); return false; }
        failure = default; return true;
    }

    private NeutralChildExecutionReceipt ToNeutral(
        ExternalChildExecutionReceipt receipt, NeutralChildExecutionStartRequest request)
    {
        Artifact artifact = artifacts.Values.Single(x => x.External.ArtifactHandle == receipt.ArtifactHandle);
        return new(artifact.Neutral.ArtifactHandle, artifact.Neutral.ArtifactEpoch, artifact.Neutral.ChildHandle,
            artifact.Neutral.ChildEpoch, artifact.Neutral.MappingHandle, artifact.Neutral.MappingEpoch,
            artifact.Neutral.ParentHandle, artifact.Neutral.ParentEpoch, artifact.Neutral.ContentDigest,
            request.OperationId, request.OperationGeneration,
            new(receipt.ExecutionGeneration.Value), receipt.RetiredPipelineCycles, receipt.LastRetireSequence,
            receipt.LastRetiredBundleOffset, receipt.IsTerminal);
    }

    private ExternalOperationIdentity Op() => new(new(Guid.NewGuid()), new(nextOperation++));
    private ulong Next() => nextIdentity++;
    private static ExternalChildAuthority ToExternal(NeutralChildAuthorityClass value) =>
        (value.HasFlag(NeutralChildAuthorityClass.Execution) ? ExternalChildAuthority.Execute : 0) |
        (value.HasFlag(NeutralChildAuthorityClass.GuestMemory) ? ExternalChildAuthority.GuestMemory : 0) |
        (value.HasFlag(NeutralChildAuthorityClass.Events) ? ExternalChildAuthority.EventInjection : 0) |
        (value.HasFlag(NeutralChildAuthorityClass.Traps) ? ExternalChildAuthority.TrapDelivery : 0) |
        (value.HasFlag(NeutralChildAuthorityClass.Io) ? ExternalChildAuthority.VirtualIo : 0);
    private static NeutralVirtualizationStatus Status(ExternalRuntimeOutcome outcome) => outcome switch
    {
        ExternalRuntimeOutcome.Unsupported => NeutralVirtualizationStatus.Unsupported,
        ExternalRuntimeOutcome.Denied => NeutralVirtualizationStatus.Denied,
        ExternalRuntimeOutcome.NotFound => NeutralVirtualizationStatus.Denied,
        ExternalRuntimeOutcome.Stale => NeutralVirtualizationStatus.Stale,
        ExternalRuntimeOutcome.Revoked => NeutralVirtualizationStatus.Revoked,
        ExternalRuntimeOutcome.Faulted => NeutralVirtualizationStatus.Faulted,
        _ => NeutralVirtualizationStatus.Ambiguous,
    };
    private static NeutralVirtualizationResult Ok() => new(NeutralVirtualizationStatus.Success, string.Empty);
    private static NeutralVirtualizationResult<T> Ok<T>(T value) => new(NeutralVirtualizationStatus.Success, value, string.Empty);
    private static NeutralVirtualizationResult Fail(NeutralVirtualizationStatus status, string reason) => new(status, reason);
    private static NeutralVirtualizationResult Fail(ExternalRuntimeOutcome outcome, string reason) => new(Status(outcome), reason);
    private static NeutralVirtualizationResult<T> Fail<T>(NeutralVirtualizationStatus status, string reason) => new(status, default!, reason);
    private static NeutralVirtualizationResult<T> Fail<T>(ExternalRuntimeOutcome outcome, string reason) => Fail<T>(Status(outcome), reason);
    private static NeutralVirtualizationResult<T> Fail<T>(NeutralVirtualizationResult failure) => new(failure.Status, default!, failure.Reason);
}
