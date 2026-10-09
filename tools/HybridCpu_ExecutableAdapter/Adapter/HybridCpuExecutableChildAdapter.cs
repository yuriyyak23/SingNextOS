using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using System.Security.Cryptography;
using System.Text;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter.Backend;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

/// <summary>Exclusive translation boundary between neutral child authority and ExternalRuntime V3.</summary>
public sealed class HybridCpuExecutableChildAdapter : INeutralRuntimeFeatureProvider,
    INeutralChildDomainProvider, INeutralGuestMemoryProvider, INeutralVirtualEventProvider,
    INeutralVirtualIoProvider, INeutralChildExecutionProvider
{
    private sealed record Child(NeutralChildDomainLease Neutral, ExternalChildDomainLease External,
        ExternalDomainLease Parent, NeutralChildDomainState State)
    {
        public bool AdmissionPublished { get; init; } = true;
        public ExternalChildDomainCreateReceipt? CreateReceipt { get; init; }
        public bool GuestMapMayHaveEffect { get; init; }
        public bool CloseMayHaveEffect { get; init; }
        public ExternalChildDomainCloseReceipt? CloseReceipt { get; init; }
        public ExternalOperationIdentity CloseOperation { get; init; }
        public NeutralChildDomainLease CloseLease { get; init; }
    }
    private sealed record Mapping(NeutralGuestMappingLease Neutral, ExternalGuestMappingLease External)
    {
        public bool AdmissionPublished { get; init; }
        public bool ArtifactMayHaveEffect { get; init; }
        public bool ClosureMayHaveEffect { get; init; }
        public ExternalGuestMemoryUnmapReceipt? ClosureReceipt { get; init; }
        public ExternalOperationIdentity ClosureOperation { get; init; }
        public NeutralGuestMappingLease ClosureLease { get; init; }
    }
    private sealed record Artifact(NeutralExecutableArtifactReceipt Neutral, ExternalChildArtifactBindReceipt External)
    {
        public bool AdmissionPublished { get; init; }
        public NeutralChildExecutionReceipt? ExecutionReceipt { get; set; }
        public ExternalChildExecutionReceipt? TupleMatchedExecutionObservation { get; set; }
    }
    private sealed record Io(NeutralVirtualIoLease Neutral, ExternalChildVirtualIoBindReceipt External);

    private readonly object sync = new();
    private readonly IHybridCpuExternalRuntime root;
    private readonly IHybridCpuChildDomainRuntimeV3 childRuntime;
    private readonly HybridCpuExternalFeatureManifest externalFeatures;
    private readonly ISemanticTraceSinkV1? traceSink;
    private readonly Dictionary<NeutralDomainBindingLease, ExternalDomainLease> parents = [];
    // Admission interlock only; durable dependency pins remain in mappings/artifacts.
    private readonly HashSet<NeutralGuestMappingHandle> pendingArtifactBinds = [];
    private readonly HashSet<NeutralGuestMappingHandle> pendingGuestUnmaps = [];
    private readonly HashSet<NeutralChildDomainHandle> pendingChildCloses = [];
    private readonly Dictionary<NeutralChildDomainHandle, int> pendingGuestMaps = [];
    private readonly HashSet<NeutralChildDomainHandle> pendingChildTransitions = [];
    private readonly HashSet<NeutralChildDomainHandle> pendingChildStarts = [];
    private readonly HashSet<NeutralDomainBindingHandle> pendingParentBinds = [];
    private readonly HashSet<NeutralDomainBindingHandle> parentCreateMayHaveEffect = [];
    private readonly Dictionary<NeutralChildDomainHandle, Child> children = [];
    private readonly Dictionary<NeutralGuestMappingHandle, Mapping> mappings = [];
    private readonly Dictionary<NeutralExecutableArtifactHandle, Artifact> artifacts = [];
    private readonly Dictionary<NeutralVirtualIoHandle, Io> ioBindings = [];
    private ulong nextIdentity = 1;
    private ulong nextOperation = 1;

    public HybridCpuExecutableChildAdapter() : this(new HybridCpuExternalRuntime(), null) { }

    public HybridCpuExecutableChildAdapter(ISemanticTraceSinkV1 traceSink) :
        this(new HybridCpuExternalRuntime(), traceSink ?? throw new ArgumentNullException(nameof(traceSink))) { }

    internal HybridCpuExecutableChildAdapter(HybridCpuExternalRuntime runtime, ISemanticTraceSinkV1? traceSink = null)
        : this(runtime, runtime, traceSink) { }

    internal HybridCpuExecutableChildAdapter(IHybridCpuExternalRuntime runtime,
        IHybridCpuChildDomainRuntimeV3 childRuntime, ISemanticTraceSinkV1? traceSink = null)
    {
        root = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.childRuntime = childRuntime ?? throw new ArgumentNullException(nameof(childRuntime));
        this.traceSink = traceSink;
        externalFeatures = root.QueryFeatures();
        if (!ExternalChildContractQualification.CanAdvertiseExecutable(externalFeatures))
            throw new InvalidOperationException("ExternalRuntime V3 executable artifact and bounded-I/O qualification is required.");
    }

    public NeutralRuntimeFeatureManifest QueryNeutralFeatures() => new(
    [
        new(NeutralRuntimeFeatureFamily.ChildDomainLifecycle, NeutralChildDomainContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
        new(NeutralRuntimeFeatureFamily.ChildGuestMemory, NeutralGuestMemoryContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
        new(NeutralRuntimeFeatureFamily.ChildEventDelivery, NeutralVirtualEventContract.ContractVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        new(NeutralRuntimeFeatureFamily.ChildExecutableArtifact, NeutralChildExecutionContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
    ]);

    public NeutralVirtualizationResult<NeutralChildDomainLease> CreateChildDomain(
        NeutralDomainBindingLease parentLease, NeutralChildDomainIntent intent)
    {
        var valid = NeutralChildDomainContract.ValidateCreate(parentLease, intent);
        if (!valid.IsSuccess) return Fail<NeutralChildDomainLease>(valid);
        ExternalChildAuthority authority;
        ExternalDomainLease parent;
        bool hasParent;
        ulong reservedIdentity;
        ExternalOperationIdentity bindOperation, createOperation;
        lock (sync)
        {
            if (parentCreateMayHaveEffect.Contains(parentLease.Handle))
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                    "Parent bind or child creation may have taken effect without exact evidence.");
            if (pendingParentBinds.Contains(parentLease.Handle))
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.NotInvoked,
                    "Parent bind admission is already in flight for this handle.");
            if (parents.Keys.Any(existing => existing.Handle == parentLease.Handle && existing != parentLease))
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Stale,
                    "Parent binding epoch changed without exact external rebind or closure.");
            authority = ToExternal(intent.Authority.ChildAuthority);
            if (authority == ExternalChildAuthority.None)
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Denied,
                    "No executable child authority was admitted.");
            if (nextIdentity == 0 || nextIdentity == ulong.MaxValue)
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.NotInvoked,
                    "Local child identity capacity is exhausted before parent/provider effects.");
            hasParent = parents.TryGetValue(parentLease, out parent);
            ulong operationCount = hasParent ? 1UL : 2UL;
            if (nextOperation == 0 || nextOperation > ulong.MaxValue - operationCount)
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.NotInvoked,
                    "External create operation capacity is exhausted before parent/provider effects.");
            reservedIdentity = Next(); // consumed before any external effect; no rollback/recycle
            bindOperation = hasParent ? default : Op();
            createOperation = Op(); // applicable operation tuple reserved before first callback
            if (!hasParent) pendingParentBinds.Add(parentLease.Handle);
        }
            if (!hasParent)
            {
                try
                {
                var bindRequest = new ExternalDomainBindRequest(Guid.NewGuid(), ExternalDomainProfile.IsolatedDomain, bindOperation);
                ExternalDomainBindResult bound;
                try { bound = root.BindDomain(bindRequest); }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    lock (sync) parentCreateMayHaveEffect.Add(parentLease.Handle);
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                        $"External parent bind may have taken effect without a receipt: {exception.Message}");
                }
                lock (sync)
                {
                if (bound.Outcome != ExternalRuntimeOutcome.Bound || bound.Receipt is not { } bindReceipt ||
                    bindReceipt.Lease.Handle.Value == Guid.Empty || bindReceipt.Lease.Epoch.Value == 0 ||
                    bindReceipt.Operation != bindRequest.Operation ||
                    bindReceipt.ContractVersion != externalFeatures.ContractVersion ||
                    bindReceipt.ManifestGeneration != externalFeatures.Generation ||
                    bindReceipt.State != ExternalDomainState.Ready)
                {
                    parentCreateMayHaveEffect.Add(parentLease.Handle);
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                        "External parent bind lacks exact effect evidence; parent remains pinned.");
                }
                parent = bindReceipt.Lease;
                parents.Add(parentLease, parent);
                }
                }
                finally { lock (sync) pendingParentBinds.Remove(parentLease.Handle); }
            }
            var createRequest = new ExternalChildDomainCreateRequest(Guid.NewGuid(), authority,
                checked((ulong)intent.Profile.MaximumGuestMemoryBytes), createOperation);
            lock (sync)
            {
                if (parentCreateMayHaveEffect.Contains(parentLease.Handle) ||
                    !parents.TryGetValue(parentLease, out var admittedParent) || admittedParent != parent)
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                        "Parent admission changed before child creation; provider was not invoked.");
            }
            ExternalChildResult<ExternalChildDomainCreateReceipt> result;
            try { result = childRuntime.CreateChildDomain(parent, createRequest); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (sync) parentCreateMayHaveEffect.Add(parentLease.Handle);
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                    $"External child creation may have taken effect without a receipt: {exception.Message}");
            }
            lock (sync)
            {
            bool continuity = !parentCreateMayHaveEffect.Contains(parentLease.Handle) &&
                parents.TryGetValue(parentLease, out ExternalDomainLease returnedParent) && returnedParent == parent;
            if (!continuity && (result.Outcome != ExternalRuntimeOutcome.Bound || result.Receipt is null))
            {
                parentCreateMayHaveEffect.Add(parentLease.Handle);
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                    "External child creation parent continuity changed during provider callback; parent remains pinned.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Bound || result.Receipt is not { } createReceipt ||
                createReceipt.Lease.Handle.Value == Guid.Empty || createReceipt.Lease.Epoch.Value == 0 ||
                createReceipt.Lease.Parent != parent || createReceipt.GrantedAuthority != authority ||
                createReceipt.GuestMemoryLimitBytes != createRequest.GuestMemoryLimitBytes ||
                createReceipt.Operation != createRequest.Operation ||
                createReceipt.ContractVersion != externalFeatures.ContractVersion ||
                createReceipt.ManifestGeneration != externalFeatures.Generation ||
                createReceipt.State != ExternalChildDomainState.Ready)
            {
                parentCreateMayHaveEffect.Add(parentLease.Handle);
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                    "External child creation lacks exact effect evidence; parent remains pinned.");
            }
            var lease = new NeutralChildDomainLease(parentLease, new(reservedIdentity), new(1), intent);
            children.Add(lease.Handle, new(lease, createReceipt.Lease, parent,
                continuity ? NeutralChildDomainState.Created : NeutralChildDomainState.Faulted)
            { AdmissionPublished = continuity, CreateReceipt = createReceipt });
            if (!continuity)
            {
                parentCreateMayHaveEffect.Add(parentLease.Handle);
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                    "External child creation parent continuity changed; exact child retained for cleanup, publication refused.");
            }
            return Ok(lease);
        }
    }

    // Called only under sync; facts remain owned by the existing admission sets/mappings.
    private bool HasPendingGuestAdmission(NeutralChildDomainLease child) =>
        pendingGuestMaps.ContainsKey(child.Handle) || pendingArtifactBinds.Any(handle =>
            mappings.TryGetValue(handle, out var mapping) && mapping.Neutral.ChildLease == child) ||
        pendingGuestUnmaps.Any(handle =>
            mappings.TryGetValue(handle, out var mapping) && mapping.Neutral.ChildLease == child);

    public NeutralVirtualizationResult TransitionChildDomain(NeutralChildDomainLease lease, NeutralChildDomainTransition transition)
    {
        Child record;
        ExternalChildDomainTransition external;
        ExternalOperationIdentity operation;
        lock (sync)
        {
            if (!TryChild(lease, out Child? admittedChild, out var failure)) return failure;
            record = admittedChild!;
            if (record!.State == NeutralChildDomainState.Closed)
                return Fail(NeutralVirtualizationStatus.Stale, "Exact child is already closed.");
            if (record!.State == NeutralChildDomainState.Faulted)
                return Fail(NeutralVirtualizationStatus.Ambiguous,
                    "Child transition outcome is quarantined pending external reconciliation.");
            if (pendingChildTransitions.Contains(lease.Handle) || pendingChildStarts.Contains(lease.Handle) || HasPendingGuestAdmission(lease))
                return Fail(NeutralVirtualizationStatus.NotInvoked, "Child provider transition or executable start is already in flight.");
            if (transition == NeutralChildDomainTransition.BeginDrain)
            {
                children[lease.Handle] = record! with { State = NeutralChildDomainState.Draining };
                return Ok();
            }
            if (transition == NeutralChildDomainTransition.Start)
                return Fail(NeutralVirtualizationStatus.NotInvoked,
                    "Executable Start requires the exact artifact/start operation contract.");
            external = transition switch
            {
                NeutralChildDomainTransition.Park => ExternalChildDomainTransition.Park,
                NeutralChildDomainTransition.Resume => ExternalChildDomainTransition.Resume,
                _ => throw new ArgumentOutOfRangeException(nameof(transition)),
            };
            if (nextOperation == 0 || nextOperation == ulong.MaxValue)
                return Fail(NeutralVirtualizationStatus.NotInvoked,
                    "External transition operation capacity is exhausted before provider invocation.");
            operation = Op();
            pendingChildTransitions.Add(lease.Handle);
        }
        try
        {
            ExternalChildResult<ExternalChildDomainTransitionReceipt> result;
            try { result = childRuntime.TransitionChildDomain(record!.External, external, operation); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (sync) FaultTransitionChild(lease.Handle);
                return Fail(NeutralVirtualizationStatus.Ambiguous,
                    $"External child transition may have taken effect without a receipt: {exception.Message}");
            }
            lock (sync)
            {
            if (!children.TryGetValue(lease.Handle, out Child? returnedChild) || returnedChild != record)
            {
                FaultTransitionChild(lease.Handle);
                return Fail(NeutralVirtualizationStatus.Ambiguous,
                    "External child transition owner continuity changed during provider callback; child remains quarantined.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded)
            {
                FaultTransitionChild(lease.Handle);
                return Fail(NeutralVirtualizationStatus.Ambiguous,
                    $"External child transition outcome requires reconciliation: {result.Outcome}: {result.Reason}");
            }
            var expectedState = transition == NeutralChildDomainTransition.Park
                ? ExternalChildDomainState.Parked : ExternalChildDomainState.Running;
            if (result.Receipt is not { } receipt ||
                receipt.Lease != record.External || receipt.Operation != operation ||
                receipt.ContractVersion != externalFeatures.ContractVersion ||
                receipt.ManifestGeneration != externalFeatures.Generation ||
                receipt.Transition != external || receipt.ResultingState != expectedState)
            {
                FaultTransitionChild(lease.Handle);
                return Fail(NeutralVirtualizationStatus.Ambiguous,
                    "External child transition may have occurred but its receipt is invalid; child is quarantined.");
            }
            var state = transition == NeutralChildDomainTransition.Park ? NeutralChildDomainState.Parked : NeutralChildDomainState.Running;
            children[lease.Handle] = record with { State = state };
            return Ok();
            }
        }
        finally { lock (sync) pendingChildTransitions.Remove(lease.Handle); }
    }

    private void FaultTransitionChild(NeutralChildDomainHandle handle)
    {
        if (children.TryGetValue(handle, out Child? currentChild))
            children[handle] = currentChild with { State = NeutralChildDomainState.Faulted };
    }

    public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> CloseChildDomain(NeutralChildDomainLease lease)
    {
        Child record;
        ExternalOperationIdentity operation;
        lock (sync)
        {
            if (!TryChild(lease, out Child? admittedChild, out var failure, allowCleanup: true)) return Fail<NeutralChildDomainCloseReceipt>(failure);
            record = admittedChild!;
            if (record!.State == NeutralChildDomainState.Closed)
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Stale,
                    "Exact child is already closed.");
            if (pendingGuestMaps.ContainsKey(lease.Handle))
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.NotInvoked,
                    "Guest mapping admission is in flight; child closure is prohibited.");
            if (pendingChildTransitions.Contains(lease.Handle))
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.NotInvoked,
                    "Child provider transition is in flight; child closure is prohibited.");
            if (pendingChildStarts.Contains(lease.Handle))
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.NotInvoked,
                    "Executable Start is in flight; child closure is prohibited.");
            if (record!.GuestMapMayHaveEffect)
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Guest mapping may exist without an exact lease or closure receipt; child remains pinned.");
            if (record.CloseMayHaveEffect && record.CloseReceipt is null)
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External child close may have taken effect without exact evidence; retry is prohibited.");
            if (mappings.Values.Any(mapping => mapping.Neutral.ChildLease == lease))
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Exact guest mappings must close before external child closure.");
            if (ioBindings.Values.Any(io => io.Neutral.ChildLease == lease))
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Virtual-I/O bindings must close before external child closure.");
            if (record.CloseReceipt is { } knownClose)
            {
                if (record.Neutral != record.CloseLease || knownClose.Lease != record.External ||
                    knownClose.Lease.Parent != record.Parent || knownClose.Operation != record.CloseOperation ||
                    knownClose.ContractVersion != externalFeatures.ContractVersion ||
                    knownClose.ManifestGeneration != externalFeatures.Generation ||
                    knownClose.ResultingState != ExternalChildDomainState.Closed || !knownClose.IsTerminal)
                    return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                        "Retained child closure no longer matches the original child/parent/operation/provider tuple.");
                children[lease.Handle] = record with { State = NeutralChildDomainState.Closed };
                return Ok(new NeutralChildDomainCloseReceipt(lease.Handle, lease.Epoch, lease.ParentLease.Handle, lease.ParentLease.Epoch,
                    NeutralChildDomainState.Closed, true)); // local cleanup; no repeated provider close
            }
            if (nextOperation == 0 || nextOperation == ulong.MaxValue)
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.NotInvoked,
                    "External child-close operation capacity is exhausted; child remains pinned.");
            operation = Op();
            pendingChildCloses.Add(lease.Handle);
        }
        try
        {
            ExternalChildResult<ExternalChildDomainCloseReceipt> result;
            try { result = childRuntime.CloseChildDomain(record.External, operation); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (sync) PinChildCloseUncertainty(lease.Handle);
                return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External child close may have taken effect without a receipt: {exception.Message}");
            }
            lock (sync)
            {
                bool continuity = children.TryGetValue(lease.Handle, out Child? returnedChild) && returnedChild == record;
                if (result.Outcome != ExternalRuntimeOutcome.Closed || result.Receipt is not { } receipt ||
                    receipt.Lease != record.External || receipt.Operation != operation ||
                    receipt.ContractVersion != externalFeatures.ContractVersion ||
                    receipt.ManifestGeneration != externalFeatures.Generation ||
                    receipt.ResultingState != ExternalChildDomainState.Closed || !receipt.IsTerminal)
                {
                    PinChildCloseUncertainty(lease.Handle);
                    return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                        "External child close lacks exact terminal evidence; child remains quarantined.");
                }
                if (!continuity)
                {
                    if (returnedChild is not null && returnedChild.Neutral == record.Neutral &&
                        returnedChild.External == record.External && returnedChild.Parent == record.Parent)
                        children[lease.Handle] = returnedChild with { CloseReceipt = receipt, CloseOperation = operation,
                            CloseLease = record.Neutral };
                    PinChildCloseUncertainty(lease.Handle);
                    return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                        "External child close owner continuity changed; exact closure retained when the original tuple remains, publication refused.");
                }
                children[lease.Handle] = record with { State = NeutralChildDomainState.Closed };
                return Ok(new NeutralChildDomainCloseReceipt(lease.Handle, lease.Epoch, lease.ParentLease.Handle, lease.ParentLease.Epoch,
                    NeutralChildDomainState.Closed, true));
                }
        }
        finally { lock (sync) pendingChildCloses.Remove(lease.Handle); }
    }

    private void PinChildCloseUncertainty(NeutralChildDomainHandle handle)
    {
        if (children.TryGetValue(handle, out Child? currentChild))
            children[handle] = currentChild with { State = NeutralChildDomainState.Faulted, CloseMayHaveEffect = true };
    }

    public NeutralVirtualizationResult<NeutralGuestMappingLease> MapGuestRegion(NeutralGuestMappingRequest request)
    {
        var valid = NeutralGuestMemoryContract.ValidateMap(request, NeutralChildDomainState.Created);
        if (!valid.IsSuccess) return Fail<NeutralGuestMappingLease>(valid);
        Child child;
        ulong reservedIdentity;
        ExternalGuestMemoryMapRequest externalRequest;
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? admittedChild, out var failure)) return Fail<NeutralGuestMappingLease>(failure);
            child = admittedChild!;
            if (pendingChildStarts.Contains(request.ChildLease.Handle) || pendingChildTransitions.Contains(request.ChildLease.Handle))
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.NotInvoked,
                    "Child execution or transition is in flight; guest map provider was not invoked.");
            if (child!.State != NeutralChildDomainState.Created)
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Denied,
                    "Child lifecycle must be Created for guest mapping admission.");
            if (nextIdentity == 0 || nextIdentity == ulong.MaxValue)
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.NotInvoked,
                    "Local guest mapping identity capacity is exhausted before provider invocation.");
            if (nextOperation == 0 || nextOperation == ulong.MaxValue)
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.NotInvoked,
                    "External guest-map operation capacity is exhausted before provider invocation.");
            reservedIdentity = Next(); // consumed before possible effect; never recycled
            externalRequest = new ExternalGuestMemoryMapRequest(request.GuestRange.Offset,
                checked((ulong)request.GuestRange.Length), Op());
            pendingGuestMaps.TryGetValue(request.ChildLease.Handle, out int pendingMaps);
            pendingGuestMaps[request.ChildLease.Handle] = checked(pendingMaps + 1);
        }
        try
        {
            ExternalChildResult<ExternalGuestMemoryMapReceipt> result;
            try { result = childRuntime.MapChildGuestMemory(child.External, externalRequest); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (sync) PinGuestMapUncertainty(request.ChildLease.Handle);
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous,
                    $"External guest map may have taken effect without a receipt: {exception.Message}");
            }
            lock (sync)
            {
            bool continuity = children.TryGetValue(request.ChildLease.Handle, out Child? returnedChild) && returnedChild == child;
            if (!continuity && (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null))
            {
                PinGuestMapUncertainty(request.ChildLease.Handle);
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous,
                    "External guest map owner continuity changed during provider callback; mapping remains uncertain.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null)
            {
                PinGuestMapUncertainty(request.ChildLease.Handle);
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous,
                    $"External guest map lacks exact effect evidence: {result.Outcome}: {result.Reason}");
            }
            var externalReceipt = result.Receipt;
            if (externalReceipt.Mapping.Handle.Value == Guid.Empty || externalReceipt.Mapping.Epoch.Value == 0 ||
                externalReceipt.Mapping.Child != child.External ||
                externalReceipt.ChildOffsetBytes != externalRequest.ChildOffsetBytes ||
                externalReceipt.LengthBytes != externalRequest.LengthBytes ||
                externalReceipt.Operation != externalRequest.Operation ||
                externalReceipt.ContractVersion != externalFeatures.ContractVersion ||
                externalReceipt.ManifestGeneration != externalFeatures.Generation)
            {
                PinGuestMapUncertainty(request.ChildLease.Handle);
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous,
                    "External guest map receipt does not match the exact child, range, operation, or provider generation.");
            }
            var lease = new NeutralGuestMappingLease(request.ChildLease, request.ParentMapping, request.GuestRange,
                request.Access, new(reservedIdentity), new(1));
            mappings.Add(lease.Handle, new(lease, externalReceipt.Mapping) { AdmissionPublished = continuity });
            if (!continuity)
            {
                FaultTransitionChild(request.ChildLease.Handle); // preserve independent unknown invocation pins
                return Fail<NeutralGuestMappingLease>(NeutralVirtualizationStatus.Ambiguous,
                    "External guest map owner continuity changed; exact mapping retained for cleanup, publication refused.");
            }
            return Ok(lease);
            }
        }
        finally
        {
            lock (sync)
            {
                int remaining = pendingGuestMaps[request.ChildLease.Handle] - 1;
                if (remaining == 0) pendingGuestMaps.Remove(request.ChildLease.Handle);
                else pendingGuestMaps[request.ChildLease.Handle] = remaining;
            }
        }
    }

    public NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> UnmapGuestRegion(NeutralGuestMappingLease lease)
    {
        Mapping mapping;
        ExternalOperationIdentity operation;
        lock (sync)
        {
            if (!mappings.TryGetValue(lease.Handle, out Mapping? admittedMapping) || admittedMapping.Neutral != lease)
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Stale, "Guest mapping is absent or stale.");
            mapping = admittedMapping;
            if (pendingChildStarts.Contains(lease.ChildLease.Handle) || pendingChildTransitions.Contains(lease.ChildLease.Handle))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.NotInvoked,
                    "Child execution or transition is in flight; guest unmap provider was not invoked.");
            if (pendingGuestUnmaps.Contains(lease.Handle))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Denied,
                    "Guest unmap admission is already in flight for this mapping.");
            if (pendingArtifactBinds.Contains(lease.Handle))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable artifact bind admission is in flight for this mapping.");
            if (mapping.ClosureMayHaveEffect && mapping.ClosureReceipt is null)
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Guest unmap may have taken effect without exact closure; retry is prohibited.");
            if (mapping.ArtifactMayHaveEffect ||
                artifacts.Values.Any(artifact => artifact.Neutral.MappingHandle == lease.Handle &&
                    artifact.Neutral.MappingEpoch == lease.Epoch))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable artifact effect has no exact release evidence for this guest mapping.");
            if (mapping.ClosureReceipt is { } knownClosure)
            {
                if (mapping.Neutral != mapping.ClosureLease || knownClosure.Mapping != mapping.External ||
                    knownClosure.Operation != mapping.ClosureOperation || !knownClosure.IsTerminal ||
                    knownClosure.ContractVersion != externalFeatures.ContractVersion ||
                    knownClosure.ManifestGeneration != externalFeatures.Generation)
                    return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                        "Retained guest closure no longer matches the exact mapping/provider generation.");
                mappings.Remove(lease.Handle); // local cleanup only; original provider effect is not repeated
                return Ok(new NeutralGuestMappingCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle, lease.ChildLease.Epoch,
                    lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true));
            }
            if (nextOperation == 0 || nextOperation == ulong.MaxValue)
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.NotInvoked,
                    "External guest-unmap operation capacity is exhausted; mapping remains pinned.");
            operation = Op();
            pendingGuestUnmaps.Add(lease.Handle);
        }
        try
        {
            ExternalChildResult<ExternalGuestMemoryUnmapReceipt> result;
            try { result = childRuntime.UnmapChildGuestMemory(mapping.External, operation); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (sync) PinGuestUnmapUncertainty(mapping);
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External guest unmap may have taken effect without a closure receipt: {exception.Message}");
            }
            lock (sync)
            {
                bool continuity = mappings.TryGetValue(lease.Handle, out Mapping? returnedMapping) && returnedMapping == mapping;
                if (result.Outcome != ExternalRuntimeOutcome.Closed || result.Receipt is not { } receipt ||
                    receipt.Mapping != mapping.External || receipt.Operation != operation ||
                    receipt.ContractVersion != externalFeatures.ContractVersion ||
                    receipt.ManifestGeneration != externalFeatures.Generation || !receipt.IsTerminal)
                {
                    PinGuestUnmapUncertainty(mapping);
                    return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                        "External guest unmap lacks exact closure evidence; mapping remains pinned.");
                }
                if (!continuity)
                {
                    // Preserve the validated original closure only on its exact existing owner tuple.
                    // A replacement tuple cannot inherit this fact or authorize cleanup.
                    if (returnedMapping is not null && returnedMapping.Neutral == mapping.Neutral &&
                        returnedMapping.External == mapping.External)
                        mappings[lease.Handle] = returnedMapping with { ClosureReceipt = receipt, ClosureOperation = operation,
                            ClosureLease = mapping.Neutral };
                    PinGuestUnmapUncertainty(mapping);
                    return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                        "External guest unmap owner continuity changed; exact closure retained when the original tuple remains, publication refused.");
                }
                mappings.Remove(lease.Handle);
                return Ok(new NeutralGuestMappingCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle, lease.ChildLease.Epoch,
                    lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true));
                }
        }
        finally { lock (sync) pendingGuestUnmaps.Remove(lease.Handle); }
    }

    private void PinGuestMapUncertainty(NeutralChildDomainHandle handle)
    {
        if (children.TryGetValue(handle, out Child? child))
            children[handle] = child with { State = NeutralChildDomainState.Faulted, GuestMapMayHaveEffect = true };
    }

    private void PinGuestUnmapUncertainty(Mapping mapping)
    {
        if (mappings.TryGetValue(mapping.Neutral.Handle, out Mapping? currentMapping))
            mappings[mapping.Neutral.Handle] = currentMapping with { ClosureMayHaveEffect = true };
        if (children.TryGetValue(mapping.Neutral.ChildLease.Handle, out Child? child))
            children[child.Neutral.Handle] = child with { State = NeutralChildDomainState.Faulted };
    }

    public NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> BindExecutableArtifact(NeutralExecutableArtifactRequest request)
    {
        var valid = NeutralChildExecutionContract.ValidateAdmission(request);
        if (!valid.IsSuccess) return Fail<NeutralExecutableArtifactReceipt>(valid);
        Child child;
        Mapping mapping;
        ulong reservedIdentity;
        ExternalChildArtifactBindRequest externalRequest;
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? admittedChild, out var failure)) return Fail<NeutralExecutableArtifactReceipt>(failure);
            child = admittedChild!;
            if (child!.State == NeutralChildDomainState.Faulted)
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Child execution outcome is quarantined pending external reconciliation.");
            if (pendingChildStarts.Contains(request.ChildLease.Handle) || pendingChildTransitions.Contains(request.ChildLease.Handle))
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.NotInvoked,
                    "Child execution or transition is in flight; artifact bind provider was not invoked.");
            if (child.State != NeutralChildDomainState.Created)
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable artifact admission requires a Created child.");
            if (!mappings.TryGetValue(request.GuestMapping.Handle, out Mapping? admittedMapping) || admittedMapping.Neutral != request.GuestMapping)
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Stale, "Exact guest mapping is absent or stale.");
            mapping = admittedMapping;
            if (!mapping.AdmissionPublished)
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Denied,
                    "Exact mapping is retained for cleanup; admission publication was refused.");
            if (pendingGuestUnmaps.Contains(request.GuestMapping.Handle))
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Denied,
                    "Guest unmap admission is in flight for this mapping.");
            if (pendingArtifactBinds.Contains(request.GuestMapping.Handle))
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable artifact bind admission is already in flight for this mapping.");
            if (nextIdentity == 0 || nextIdentity == ulong.MaxValue)
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.NotInvoked,
                    "Local artifact identity capacity is exhausted before provider invocation.");
            if (nextOperation == 0 || nextOperation == ulong.MaxValue)
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.NotInvoked,
                    "External artifact-bind operation capacity is exhausted before provider invocation.");
            reservedIdentity = Next(); // original owner allocation before possible effect, never recycled
            externalRequest = new ExternalChildArtifactBindRequest(mapping.External,
                request.ImmutablePackage.ToArray(), request.MaximumExecutionSteps, Op());
            pendingArtifactBinds.Add(request.GuestMapping.Handle);
        }
        try
        {
            ExternalChildResult<ExternalChildArtifactBindReceipt> result;
            try { result = childRuntime.BindChildExecutableArtifact(child!.External, externalRequest); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (sync) PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External artifact bind may have taken effect without a receipt: {exception.Message}");
            }
            lock (sync)
            {
            bool continuity = children.TryGetValue(request.ChildLease.Handle, out Child? returnedChild) && returnedChild == child &&
                mappings.TryGetValue(request.GuestMapping.Handle, out Mapping? returnedMapping) && returnedMapping == mapping;
            if (!continuity && (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null))
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External artifact bind owner continuity changed during provider callback; dependencies remain pinned.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null)
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External artifact bind lacks exact effect evidence: {result.Outcome}: {result.Reason}");
            }
            var externalReceipt = result.Receipt;
            if (externalReceipt.ArtifactHandle.Value == Guid.Empty || externalReceipt.ArtifactEpoch.Value == 0 ||
                externalReceipt.ChildHandle != child.External.Handle || externalReceipt.ChildEpoch != child.External.Epoch ||
                externalReceipt.MappingHandle != mapping.External.Handle || externalReceipt.MappingEpoch != mapping.External.Epoch ||
                externalReceipt.Parent != child.External.Parent || externalReceipt.Operation != externalRequest.Operation ||
                externalReceipt.ContractVersion != externalFeatures.ContractVersion ||
                externalReceipt.ManifestGeneration != externalFeatures.Generation ||
                externalReceipt.MaximumPipelineCycles != externalRequest.MaximumPipelineCycles ||
                !string.Equals(externalReceipt.PackageSha256,
                    Convert.ToHexStringLower(SHA256.HashData(externalRequest.PackageBytes)), StringComparison.Ordinal))
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External artifact receipt does not match the exact child, mapping, operation, or provider generation.");
            }
            var receipt = new NeutralExecutableArtifactReceipt(new(reservedIdentity), new(1), request.ChildLease.Handle,
                request.ChildLease.Epoch, request.GuestMapping.Handle, request.GuestMapping.Epoch,
                request.ChildLease.ParentLease.Handle, request.ChildLease.ParentLease.Epoch,
                externalReceipt.PackageSha256, request.MaximumExecutionSteps);
            var exact = NeutralChildExecutionContract.ValidateAdmissionReceipt(request, receipt);
            if (!exact.IsSuccess)
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External artifact receipt failed neutral admission validation; dependencies remain pinned.");
            }
            artifacts.Add(receipt.ArtifactHandle, new(receipt, externalReceipt) { AdmissionPublished = continuity });
            if (!continuity)
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External artifact bind owner continuity changed; exact receipt retained for cleanup, publication refused.");
            }
            return Ok(receipt);
            }
        }
        finally { lock (sync) pendingArtifactBinds.Remove(request.GuestMapping.Handle); }
    }

    private void PinArtifactBindUncertainty(Child child, Mapping mapping)
    {
        if (children.TryGetValue(child.Neutral.Handle, out Child? currentChild))
            children[child.Neutral.Handle] = currentChild with { State = NeutralChildDomainState.Faulted,
                GuestMapMayHaveEffect = currentChild.GuestMapMayHaveEffect || !mappings.ContainsKey(mapping.Neutral.Handle) };
        if (mappings.TryGetValue(mapping.Neutral.Handle, out Mapping? currentMapping))
            mappings[mapping.Neutral.Handle] = currentMapping with { ArtifactMayHaveEffect = true };
    }

    public NeutralVirtualizationResult<NeutralChildExecutionReceipt> StartExecutableArtifact(
        NeutralChildExecutionStartRequest request)
    {
        var events = new List<SemanticTraceEventV1>(3);
        try { return StartExecutableArtifactCore(request, events); }
        finally
        {
            // Deliver committed observations after releasing the owner lock.
            // Sink failure and reentry cannot participate in start admission.
            foreach (var item in events)
            {
                try { _ = traceSink?.TryRecord(item); }
                catch { /* Observation is not execution authority. */ }
            }
        }
    }

    private NeutralVirtualizationResult<NeutralChildExecutionReceipt> StartExecutableArtifactCore(
        NeutralChildExecutionStartRequest request, List<SemanticTraceEventV1> events)
    {
        var admission = NeutralChildExecutionContract.ValidateStart(request);
        if (!admission.IsSuccess) return Fail<NeutralChildExecutionReceipt>(admission);
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? child, out var failure)) return Fail<NeutralChildExecutionReceipt>(failure);
            if (child!.State == NeutralChildDomainState.Faulted)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Child execution outcome is quarantined pending external reconciliation.");
            if (child.State != NeutralChildDomainState.Created)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable start requires a Created child.");
            if (!artifacts.TryGetValue(request.Artifact.ArtifactHandle, out Artifact? found) || found.Neutral != request.Artifact)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Stale, "Artifact admission is absent or stale.");
            if (!found.AdmissionPublished)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Denied,
                    "Exact artifact receipt is retained for cleanup; admission publication was refused.");
            if (found.ExecutionReceipt is not null)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Denied,
                    "The exact artifact already has an accepted execution receipt; a fresh operation cannot repeat its effect.");
            if (pendingChildStarts.Contains(request.ChildLease.Handle) ||
                pendingChildTransitions.Contains(request.ChildLease.Handle) || HasPendingGuestAdmission(request.ChildLease))
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.NotInvoked,
                    "Executable start or provider transition admission is already in flight for this child.");
            if (nextOperation == 0 || nextOperation == ulong.MaxValue)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.NotInvoked,
                    "External start operation capacity is exhausted before submission or provider invocation.");
            var operation = Op(); // reserve the exact usable generation before trace/pending/effect
            string correlation = $"hybridcpu-child-start:{request.OperationId.Value}:{request.OperationGeneration.Value}";
            string generationDigest = Digest(FormattableString.Invariant(
                $"child-start-generation/v2|{request.ChildLease.ParentLease.Handle.Value}|{request.ChildLease.ParentLease.Epoch.Value}|{request.ChildLease.Handle.Value}|{request.ChildLease.Epoch.Value}|{request.Artifact.MappingHandle.Value}|{request.Artifact.MappingEpoch.Value}|{request.Artifact.ArtifactHandle.Value}|{request.Artifact.ArtifactEpoch.Value}|{request.OperationId.Value}|{request.OperationGeneration.Value}|{externalFeatures.Generation}"));
            RecordTrace(events, correlation, 1, SemanticTraceEventKindV1.Submit, generationDigest,
                Digest($"{request.Artifact.ContentDigest}|{request.Artifact.MaximumExecutionSteps}"));
            RecordTrace(events, correlation, 2, SemanticTraceEventKindV1.EffectPossible, generationDigest,
                Digest($"{found.External.ArtifactHandle}|{found.External.ArtifactEpoch}"));
            if (!TryChild(request.ChildLease, out var currentChild, out failure) ||
                currentChild!.State != NeutralChildDomainState.Created ||
                !artifacts.TryGetValue(request.Artifact.ArtifactHandle, out var currentArtifact) ||
                currentArtifact != found)
            {
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("admission-changed-during-observation"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable admission changed during trace observation.");
            }
            ExternalChildResult<ExternalChildExecutionReceipt> result;
            pendingChildStarts.Add(request.ChildLease.Handle);
            try
            {
                result = childRuntime.StartChildExecution(child.External,
                    new(found.External.ArtifactHandle, found.External.ArtifactEpoch, operation));
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                if (children.TryGetValue(request.ChildLease.Handle, out var failedChild))
                    children[request.ChildLease.Handle] = failedChild with { State = NeutralChildDomainState.Faulted };
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("external-execution-callback-failed"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External execution may have had an effect without a receipt: {exception.Message}");
            }
            finally { pendingChildStarts.Remove(request.ChildLease.Handle); }
            bool continuity = children.TryGetValue(request.ChildLease.Handle, out var returnedChild) &&
                returnedChild == child &&
                artifacts.TryGetValue(request.Artifact.ArtifactHandle, out var returnedArtifact) &&
                returnedArtifact == found;
            if (!continuity && (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null))
            {
                if (returnedChild is not null)
                    children[request.ChildLease.Handle] = returnedChild with { State = NeutralChildDomainState.Faulted };
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("external-start-owner-continuity-lost"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External start owner continuity changed during the provider callback; effect remains quarantined.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null)
            {
                children[request.ChildLease.Handle] = child with { State = NeutralChildDomainState.Faulted };
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest($"{result.Outcome}|{result.Reason}"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External execution may have had an effect; result requires reconciliation.");
            }
            var externalReceipt = result.Receipt;
            if (externalReceipt.Operation != operation ||
                externalReceipt.ContractVersion != externalFeatures.ContractVersion ||
                externalReceipt.ManifestGeneration != externalFeatures.Generation ||
                externalReceipt.ChildHandle != child.External.Handle ||
                externalReceipt.ChildEpoch != child.External.Epoch ||
                externalReceipt.Parent != child.External.Parent ||
                externalReceipt.ArtifactHandle != found.External.ArtifactHandle ||
                externalReceipt.ArtifactEpoch != found.External.ArtifactEpoch ||
                externalReceipt.MappingHandle != found.External.MappingHandle ||
                externalReceipt.MappingEpoch != found.External.MappingEpoch ||
                externalReceipt.PackageSha256 != found.External.PackageSha256 ||
                !externalReceipt.IsTerminal)
            {
                FaultTransitionChild(request.ChildLease.Handle);
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("external-execution-receipt-mismatch"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External execution receipt does not match the admitted tuple; child is quarantined.");
            }
            var receipt = ToNeutral(externalReceipt, found, request);
            found.TupleMatchedExecutionObservation = externalReceipt; // raw original outcome, not accepted completion/charge/closure
            var exact = NeutralChildExecutionContract.ValidateExecutionReceipt(request, receipt);
            if (exact.IsSuccess) found.ExecutionReceipt = receipt; // provider fact before trace delivery, not permission/release
            if (!exact.IsSuccess)
                FaultTransitionChild(request.ChildLease.Handle);
            if (!continuity)
            {
                FaultTransitionChild(request.ChildLease.Handle);
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("external-start-owner-continuity-lost"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External start owner continuity changed; validated receipt retained when exact, publication refused.");
            }
            RecordTrace(events, correlation, 3,
                exact.IsSuccess ? SemanticTraceEventKindV1.RetireOrComplete : SemanticTraceEventKindV1.Quarantined,
                generationDigest,
                Digest($"{receipt.ExecutionGeneration.Value}|{receipt.RetiredWorkUnits}|{receipt.RetiredSequence}|{receipt.LastRetiredCodeOffset}|{receipt.IsTerminal}"));
            return exact.IsSuccess ? Ok(receipt) : Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                "Execution receipt failed neutral validation; child is quarantined.");
        }
    }

    public NeutralVirtualizationResult<NeutralVirtualIoLease> BindVirtualIo(NeutralVirtualIoRequest request)
    {
        var valid = NeutralVirtualIoContract.ValidateBind(request, NeutralChildDomainState.Created);
        if (!valid.IsSuccess) return Fail<NeutralVirtualIoLease>(valid);
        return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Unsupported,
            "No provider-owned external parent device identity is bound to the exact neutral device lease.");
    }

    public NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> CloseVirtualIo(NeutralVirtualIoLease lease)
    {
        lock (sync)
        {
            if (!ioBindings.TryGetValue(lease.Handle, out Io? io) || io.Neutral != lease)
                return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Stale, "I/O binding is absent or stale.");
            if (!TryChild(lease.ChildLease, out Child? child, out var failure)) return Fail<NeutralVirtualIoCloseReceipt>(failure);
            if (nextOperation == 0 || nextOperation == ulong.MaxValue)
                return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Denied,
                    "External I/O-close operation capacity is exhausted; binding remains pinned.");
            var operation = Op();
            var result = childRuntime.CloseChildVirtualIo(child!.External, io.External.IoHandle, io.External.IoEpoch, operation);
            if (result.Outcome != ExternalRuntimeOutcome.Closed || result.Receipt is null || !result.Receipt.IsTerminal)
                return Fail<NeutralVirtualIoCloseReceipt>(result.Outcome, result.Reason);
            ioBindings.Remove(lease.Handle);
            return Ok(new NeutralVirtualIoCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle, lease.ChildLease.Epoch,
                lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true));
        }
    }

    public NeutralVirtualizationResult<NeutralVirtualEventReceipt> InjectVirtualEvent(NeutralVirtualEventRequest request) =>
        Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Unsupported, "Executable event injection is not yet owned by the V3 runner.");

    private bool TryChild(NeutralChildDomainLease lease, out Child? child, out NeutralVirtualizationResult failure,
        bool allowCleanup = false)
    {
        if (!children.TryGetValue(lease.Handle, out child)) { failure = Fail(NeutralVirtualizationStatus.Denied, "Child was not found."); return false; }
        if (child.Neutral.Epoch != lease.Epoch) { failure = Fail(NeutralVirtualizationStatus.Stale, "Child epoch is stale."); return false; }
        if (child.Neutral != lease) { failure = Fail(NeutralVirtualizationStatus.WrongParent, "Child parent or intent differs."); return false; }
        if (!allowCleanup && !child.AdmissionPublished)
        {
            failure = Fail(NeutralVirtualizationStatus.Ambiguous, "Exact child is retained for cleanup; admission publication was refused.");
            return false;
        }
        if (pendingChildCloses.Contains(lease.Handle))
        {
            failure = Fail(NeutralVirtualizationStatus.Denied, "Child close admission is in flight.");
            return false;
        }
        failure = default; return true;
    }

    private NeutralChildExecutionReceipt ToNeutral(
        ExternalChildExecutionReceipt receipt, Artifact artifact, NeutralChildExecutionStartRequest request)
    {
        return new(artifact.Neutral.ArtifactHandle, artifact.Neutral.ArtifactEpoch, artifact.Neutral.ChildHandle,
            artifact.Neutral.ChildEpoch, artifact.Neutral.MappingHandle, artifact.Neutral.MappingEpoch,
            artifact.Neutral.ParentHandle, artifact.Neutral.ParentEpoch, artifact.Neutral.ContentDigest,
            request.OperationId, request.OperationGeneration,
            new(receipt.ExecutionGeneration.Value), receipt.RetiredPipelineCycles, receipt.LastRetireSequence,
            receipt.LastRetiredBundleOffset, receipt.IsTerminal);
    }

    private ExternalOperationIdentity Op() => new(new(Guid.NewGuid()), new(nextOperation++));
    private void RecordTrace(List<SemanticTraceEventV1> events, string correlation, ulong sequence, SemanticTraceEventKindV1 kind,
        string generationDigest, string evidenceDigest)
    {
        if (traceSink is null) return;
        var traceEvent = new SemanticTraceEventV1(SemanticTraceEventV1.CurrentVersion, correlation, sequence,
            kind, "HybridCPU.ExternalRuntime.V3", generationDigest, evidenceDigest).Validate();
        events.Add(traceEvent);
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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
