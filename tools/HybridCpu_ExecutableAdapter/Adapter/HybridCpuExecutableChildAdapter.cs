using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using System.Security.Cryptography;
using System.Text;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter.Backend;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

/// <summary>Exclusive translation boundary between neutral child authority and ExternalRuntime V3.</summary>
public sealed partial class HybridCpuExecutableChildAdapter : INeutralRuntimeFeatureProvider,
    INeutralChildDomainProvider, INeutralGuestMemoryProvider, INeutralVirtualEventProvider,
    INeutralVirtualIoProvider, INeutralChildExecutionProvider, INeutralExecutableArtifactReleaseProvider, INeutralOriginalArtifactBindProvider,
    INeutralExternalParentClosureProvider, INeutralExecutablePrivateDataQuotaProvider, INeutralExecutionTerminalProvider
{
    private sealed record Child(NeutralChildDomainLease Neutral, ExternalChildDomainLease External,
        ExternalDomainLease Parent, NeutralChildDomainState State)
    {
        private NeutralChildDomainState _state = State;
        private long _stateRevision;
        public long StateRevision => _stateRevision;
        public bool StateRevisionExhausted { get; private set; }
        public NeutralChildDomainState State
        {
            get => _state;
            set
            {
                // Revoke current machine admission before publishing a new
                // semantic child revision. No provider callbacks run here.
                foreach (var f in OriginalSoftwareAdmissions)
                    if (ReferenceEquals(f.OriginalOwner, f) && ReferenceEquals(f.Child, this) &&
                        f.CoreHost is { } host && ReferenceEquals(host.Session, f.Session) &&
                        f.Creation?.Receipt is { } created && ReferenceEquals(created.Request, f.Create) &&
                        ReferenceEquals(f.CanonicalCreation?.Receipt, created) && created.Request.Parent.Equals(Parent))
                        f.Session.RevokeLocalBinding(created.Domain);
                foreach (var entry in OriginalSoftwareArtifactEntries)
                    if (ReferenceEquals(entry.OriginalOwner, entry) && ReferenceEquals(entry.Child, this)) RevokeSoftwareArtifactEntry(entry);
                if (_stateRevision == long.MaxValue) StateRevisionExhausted = true; else _stateRevision++;
                _state = value;
            }
        }
        public OriginalChildCloseInvocation? OriginalCloseInvocation { get; set; }
        public List<SoftwareAdmission> OriginalSoftwareAdmissions { get; } = [];
        public ulong SoftwareAdmissionsIssued;
        public List<SoftwareArtifactEntry> OriginalSoftwareArtifactEntries { get; } = [];
        public ulong SoftwareArtifactEntriesIssued;
        public List<OriginalGuestMapInvocation> OriginalGuestMaps { get; } = [];
        public ulong OriginalGuestMapsIssued { get; set; }
        public List<OriginalIoFact> OriginalIo { get; } = [];
        public ulong OriginalIoIssued { get; set; }
        public List<OriginalEventFact> OriginalEvents { get; } = [];
        public ulong OriginalEventsIssued { get; set; }
        public bool AdmissionPublished { get; set; } = true;
        public ExternalChildDomainCreateReceipt? CreateReceipt { get; init; }
        public Child? NestedParent { get; init; }
        public OriginalNestedFact? NestedFactory { get; init; }
        public bool NestedCreationAllowed { get; init; }
        public List<OriginalNestedFact> OriginalNested { get; } = [];
        public ulong NestedIssued { get; set; }
        public bool GuestMapMayHaveEffect { get; set; }
        public bool CloseMayHaveEffect { get; set; }
        public ExternalChildDomainCloseReceipt? CloseReceipt { get; set; }
        public ExternalOperationIdentity CloseOperation { get; set; }
        public NeutralChildDomainLease CloseLease { get; set; }
        public Parent? ParentSource { get; init; }
        public bool Equals(Child? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
    private sealed record Mapping(NeutralGuestMappingLease Neutral, ExternalGuestMappingLease External)
    {
        public ExternalGuestMemoryMapReceipt? OriginalAdmission { get; init; }
        public OriginalGuestUnmapInvocation? OriginalUnmap { get; set; }
        public bool AdmissionPublished { get; init; }
        public bool ArtifactMayHaveEffect { get; set; }
        public OriginalArtifactBindFact? ArtifactBind { get; set; }
        public bool ClosureMayHaveEffect { get; set; }
        public ExternalGuestMemoryUnmapReceipt? ClosureReceipt { get; set; }
        public ExternalOperationIdentity ClosureOperation { get; set; }
        public NeutralGuestMappingLease ClosureLease { get; set; }
        public bool Equals(Mapping? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
    private sealed record Artifact(NeutralExecutableArtifactReceipt Neutral, ExternalChildArtifactBindReceipt External)
    {
        public SoftwareArtifactEntry? SoftwareEntry { get; set; }
        public bool AdmissionPublished { get; init; }
        public NeutralChildExecutionReceipt? ExecutionReceipt { get; set; }
        public ExternalChildExecutionReceipt? TupleMatchedExecutionObservation { get; set; }
        public OriginalStartInvocation? OriginalStart { get; set; }
        public List<OriginalStartInvocation> StartHistory { get; } = [];
        public ulong StartsIssued { get; set; }
        public Child? BindChild { get; init; }
        public Mapping? BindMapping { get; init; }
        public NeutralArtifactReleaseRequest? ReleaseRequest { get; set; }
        public ExternalArtifactReleaseRequest? ExternalReleaseRequest { get; set; }
        public ExternalChildResult<ExternalArtifactReleaseReceipt>? ReleaseObservation { get; set; }
        public ExternalArtifactReleaseReceipt? ReleaseResolutionObservation { get; set; }
        public (NeutralArtifactReleaseRequest Request, ExternalArtifactReleaseReceipt Receipt)? OriginalRelease { get; set; }
        public bool ReleaseInvoked { get; set; }
        public bool ReleaseInFlight { get; set; }
        public bool Released { get; set; }
        public bool Equals(Artifact? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
    private sealed record Io(NeutralVirtualIoLease Neutral, ExternalChildVirtualIoBindReceipt External);

    private readonly object sync = new();
    private readonly IHybridCpuExternalRuntime root;
    private readonly IHybridCpuChildDomainRuntimeV3 childRuntime;
    private readonly HybridCpuExternalFeatureManifest externalFeatures;
    private readonly ISemanticTraceSinkV1? traceSink;
    private readonly Dictionary<NeutralDomainBindingLease, Parent> parents = [];
    private readonly Dictionary<NeutralDomainBindingLease, Parent> originalParents = [];
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
    private readonly Dictionary<NeutralChildDomainHandle, Child> originalChildren = [];
    private readonly Dictionary<NeutralGuestMappingHandle, Mapping> mappings = [];
    private readonly Dictionary<NeutralGuestMappingHandle, Mapping> originalMappings = [];
    private readonly Dictionary<NeutralExecutableArtifactHandle, Artifact> artifacts = [];
    private readonly Dictionary<NeutralExecutableArtifactHandle, Artifact> originalArtifacts = [];
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

    public NeutralRuntimeFeatureManifest QueryNeutralFeatures()
    {
        List<NeutralRuntimeFeatureDescriptor> features =
    [
        new(NeutralRuntimeFeatureFamily.ChildDomainLifecycle, NeutralChildDomainContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
        new(NeutralRuntimeFeatureFamily.ChildGuestMemory, NeutralGuestMemoryContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
        new(NeutralRuntimeFeatureFamily.ChildEventDelivery, NeutralVirtualEventContract.ContractVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        new(NeutralRuntimeFeatureFamily.ChildExecutableArtifact, NeutralChildExecutionContract.ContractVersion, NeutralRuntimeFeatureAvailability.Executable),
    ];
        if (deviceLoans is not null && childRuntime is IHybridCpuOriginalVirtualIoRuntimeV1)
            features.Add(new(NeutralRuntimeFeatureFamily.BoundedVirtualIo, NeutralVirtualIoContract.ContractVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission));
        if (SupportsOriginalNestedDomains)
            features.Add(new(NeutralRuntimeFeatureFamily.NestedDomains, 1, NeutralRuntimeFeatureAvailability.RuntimeAdmission));
        return new(features);
    }

    public NeutralVirtualizationResult<NeutralChildDomainLease> CreateChildDomain(
        NeutralDomainBindingLease parentLease, NeutralChildDomainIntent intent)
        => SupportsOriginalChildCreateResolution ? CreateOriginalChild(new(parentLease, intent)) : CreateChildDomainCore(parentLease, intent, null);
    private NeutralVirtualizationResult<NeutralChildDomainLease> CreateChildDomainCore(
        NeutralDomainBindingLease parentLease, NeutralChildDomainIntent intent, NeutralOriginalChildCreateRequest? originalRequest)
    {
        NeutralVirtualizationResult valid = NeutralChildDomainContract.ValidateCreate(parentLease, intent);
        if (!valid.IsSuccess) return Fail<NeutralChildDomainLease>(valid);
        ExternalChildAuthority authority;
        ExternalDomainLease parent;
        Parent parentOwner = null!;
        OriginalChildCreateInvocation? original = null;
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
            if (authority == ExternalChildAuthority.None && !SupportsOriginalNestedDomains)
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Denied,
                    "No executable child authority was admitted.");
            if (nextIdentity == 0 || nextIdentity == ulong.MaxValue)
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.NotInvoked,
                    "Local child identity capacity is exhausted before parent/provider effects.");
            hasParent = parents.TryGetValue(parentLease, out parentOwner!);
            if (hasParent && parentOwner.OriginalChildCreates.Any(f => !f.Consumed || f.InFlight))
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Original child Create is pending or unresolved.");
            if (hasParent && (!originalParents.TryGetValue(parentLease, out Parent? originalParent) || !ReferenceEquals(originalParent, parentOwner) ||
                parentOwner.Admission is null || !HasOriginalParentBindCustody(parentOwner) || parentOwner.CloseRequest is not null || parentOwner.Closed))
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Original external parent is unknown, close-pending or foreign.");
            parent = hasParent ? parentOwner.External : default;
            ulong operationCount = hasParent ? 1UL : 2UL;
            if (nextOperation == 0 || nextOperation > ulong.MaxValue - operationCount)
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.NotInvoked,
                    "External create operation capacity is exhausted before parent/provider effects.");
            reservedIdentity = Next(); // consumed before any external effect; no rollback/recycle
            bindOperation = hasParent ? default : Op();
            createOperation = Op(); // applicable operation tuple reserved before first callback
            if (!hasParent) pendingParentBinds.Add(parentLease.Handle);
            if (!hasParent)
            {
                parentOwner = new(parentLease, new(Guid.NewGuid(), ExternalDomainProfile.IsolatedDomain, bindOperation));
                originalParents.Add(parentLease, parentOwner);
                parents.Add(parentLease, parentOwner); // Original intent before possible parent Bind effect.
            }
            if (originalRequest is not null)
            {
                original = new(parentOwner, originalRequest, reservedIdentity);
                parentOwner.OriginalChildCreates.Add(original);
            }
        }
            if (!hasParent)
            {
                try
                {
                ExternalDomainBindRequest bindRequest = parentOwner.BindRequest;
                ExternalDomainBindResult bound;
                try { bound = root is IHybridCpuOriginalDomainBindRuntimeV1 ? InvokeOriginalParentBind(parentOwner) : root.BindDomain(bindRequest); }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    lock (sync) parentCreateMayHaveEffect.Add(parentLease.Handle);
                    if (original is not null) lock (sync) original.LocalNoEffect = original.OwnPin = true;
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                        $"External parent bind may have taken effect without a receipt: {exception.Message}");
                }
                lock (sync)
                {
                parentOwner.BindObservation = bound; // Original raw before local checks.
                if (bound.Outcome != ExternalRuntimeOutcome.Bound || bound.Receipt is not { } bindReceipt ||
                    bindReceipt.Lease.Handle.Value == Guid.Empty || bindReceipt.Lease.Epoch.Value == 0 ||
                    bindReceipt.Operation != bindRequest.Operation ||
                    bindReceipt.ContractVersion != externalFeatures.ContractVersion ||
                    bindReceipt.ManifestGeneration != externalFeatures.Generation ||
                    bindReceipt.State != ExternalDomainState.Ready)
                {
                    parentCreateMayHaveEffect.Add(parentLease.Handle);
                    if (original is not null) original.LocalNoEffect = original.OwnPin = true;
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                        "External parent bind lacks exact effect evidence; parent remains pinned.");
                }
                parent = bindReceipt.Lease;
                parentOwner.Admission = bindReceipt; // Known original admission retained even when routing publication is refused.
                if (!parents.TryGetValue(parentLease, out Parent? parentRoute) || !ReferenceEquals(parentRoute, parentOwner))
                {
                    parentCreateMayHaveEffect.Add(parentLease.Handle);
                    if (original is not null) original.LocalNoEffect = original.OwnPin = true;
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Original external parent route changed after Bind.");
                }
                }
                }
                finally { lock (sync) pendingParentBinds.Remove(parentLease.Handle); }
            }
        ExternalChildDomainCreateRequest createRequest = new ExternalChildDomainCreateRequest(Guid.NewGuid(), authority,
                checked((ulong)intent.Profile.MaximumGuestMemoryBytes), createOperation);
            lock (sync)
            {
                if (parentCreateMayHaveEffect.Contains(parentLease.Handle) ||
                    !parents.TryGetValue(parentLease, out Parent? admittedParent) || !ReferenceEquals(admittedParent, parentOwner) ||
                    admittedParent.External != parent || admittedParent.CloseRequest is not null || admittedParent.Closed)
                {
                    if (original is not null) { original.LocalNoEffect = original.OwnPin = true; parentCreateMayHaveEffect.Add(parentLease.Handle); }
                    return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                        "Parent admission changed before child creation; provider was not invoked.");
                }
            }
            ExternalChildResult<ExternalChildDomainCreateReceipt> result;
            try { result = original is null ? childRuntime.CreateChildDomain(parent, createRequest) : InvokeOriginalChildCreate(original, createRequest); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (sync) parentCreateMayHaveEffect.Add(parentLease.Handle);
                if (original is not null) lock (sync) original.OwnPin = true;
                return Fail<NeutralChildDomainLease>(NeutralVirtualizationStatus.Ambiguous,
                    $"External child creation may have taken effect without a receipt: {exception.Message}");
            }
            lock (sync)
            {
            if (original?.Validated is { NoEffect: true })
            {
                original.Consumed = hasParent; original.OwnPin = !hasParent;
                if (!hasParent) parentCreateMayHaveEffect.Add(parentLease.Handle);
                return Fail<NeutralChildDomainLease>(hasParent ? NeutralVirtualizationStatus.NotInvoked : NeutralVirtualizationStatus.Ambiguous,
                    "Exact child Create no-effect retained; newly admitted external parent still needs cleanup.");
            }
            bool continuity = !parentCreateMayHaveEffect.Contains(parentLease.Handle) &&
                parents.TryGetValue(parentLease, out Parent? returnedParent) && ReferenceEquals(returnedParent, parentOwner) &&
                returnedParent.External == parent && returnedParent.CloseRequest is null && !returnedParent.Closed;
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
            NeutralChildDomainLease lease = new NeutralChildDomainLease(parentLease, new(reservedIdentity), new(1), intent);
            children.Add(lease.Handle, new(lease, createReceipt.Lease, parent,
                continuity ? NeutralChildDomainState.Created : NeutralChildDomainState.Faulted)
            { AdmissionPublished = continuity, CreateReceipt = createReceipt, ParentSource = parentOwner,
                NestedCreationAllowed = original?.Validated?.NestedCreationAllowed == true });
            originalChildren.Add(lease.Handle, children[lease.Handle]);
            if (original is not null) { original.Child = children[lease.Handle]; original.Consumed = continuity; original.OwnPin = !continuity; }
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
        originalArtifacts.Values.Any(artifact => !artifact.Released && (artifact.ReleaseInFlight || artifact.ReleaseInvoked || artifact.ReleaseRequest is not null || artifact.ExternalReleaseRequest is not null) && artifact.BindChild?.Neutral == child) ||
        pendingGuestMaps.ContainsKey(child.Handle) || pendingArtifactBinds.Any(handle =>
            mappings.TryGetValue(handle, out Mapping? mapping) && mapping.Neutral.ChildLease == child) ||
        pendingGuestUnmaps.Any(handle =>
            mappings.TryGetValue(handle, out Mapping? mapping) && mapping.Neutral.ChildLease == child);

    public NeutralVirtualizationResult TransitionChildDomain(NeutralChildDomainLease lease, NeutralChildDomainTransition transition)
        => TransitionChildDomainCore(lease, transition, null);

    private NeutralVirtualizationResult TransitionChildDomainCore(NeutralChildDomainLease lease, NeutralChildDomainTransition transition, OriginalDrain? originalDrain)
    {
        Child record;
        ExternalChildDomainTransition external;
        ExternalOperationIdentity operation;
        lock (sync)
        {
            if (!TryChild(lease, out Child? admittedChild, out NeutralVirtualizationResult failure, dependencyRefusalIsPreEffect: transition == NeutralChildDomainTransition.BeginDrain)) return failure;
            record = admittedChild!;
            if (transition == NeutralChildDomainTransition.BeginDrain && HasRetainedOriginalChildClosure(record))
                return Ok();
            OriginalDrain? precedingDrain = originalDrains.Values.FirstOrDefault(f => ReferenceEquals(f.Child, record) &&
                (f.OriginalRaw is null || f.OriginalRaw.Result.Status != NeutralVirtualizationStatus.NotInvoked));
            if (originalDrain is null && precedingDrain is not null)
                return transition == NeutralChildDomainTransition.BeginDrain && record.State == NeutralChildDomainState.Draining &&
                    ResolveOriginalChildDrain(precedingDrain.Request) is { Result.IsSuccess: true }
                    ? Ok() : Fail(NeutralVirtualizationStatus.Ambiguous, "Original drain forbids another transition effect; use same-original lookup.");
            if(transition == NeutralChildDomainTransition.BeginDrain && record.State == NeutralChildDomainState.Closed &&
                record.OriginalCloseInvocation is {OriginalCanonical.NoFurtherUse:true} closedOriginal && HasIntactChildCloseCanonical(closedOriginal) && HasOriginalIoPins(record))
                return Fail(NeutralVirtualizationStatus.NotInvoked,"Original IO inventory pins consumed child cleanup; no BeginDrain effect was entered.");
            if (HasSoftwareAdmissionPins(record)) return Fail(NeutralVirtualizationStatus.Ambiguous, "Original software admission custody remains pinned.");
            if (record!.State == NeutralChildDomainState.Closed)
                return Fail(NeutralVirtualizationStatus.Stale, "Exact child is already closed.");
            if (record!.State == NeutralChildDomainState.Faulted &&
                !(transition == NeutralChildDomainTransition.BeginDrain && !record.GuestMapMayHaveEffect && !record.CloseMayHaveEffect &&
                  originalArtifacts.Values.Any(x => ReferenceEquals(x.BindChild, record) && x.Released) &&
                  !originalArtifacts.Values.Any(x => ReferenceEquals(x.BindChild, record) && !x.Released)))
                return Fail(NeutralVirtualizationStatus.Ambiguous,
                    "Child transition outcome is quarantined pending external reconciliation.");
            if (pendingChildTransitions.Contains(lease.Handle) || pendingChildStarts.Contains(lease.Handle) || HasPendingGuestAdmission(lease))
                return Fail(NeutralVirtualizationStatus.NotInvoked, "Child provider transition or executable start is already in flight.");
            if (transition == NeutralChildDomainTransition.BeginDrain)
            {
                record!.State = NeutralChildDomainState.Draining;
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
            if (!children.TryGetValue(lease.Handle, out Child? returnedChild) || returnedChild != record ||
                returnedChild.State is NeutralChildDomainState.Draining or NeutralChildDomainState.Faulted or NeutralChildDomainState.Closed ||
                returnedChild.GuestMapMayHaveEffect)
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
                ExternalChildDomainState expectedState = transition == NeutralChildDomainTransition.Park
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
                NeutralChildDomainState state = transition == NeutralChildDomainTransition.Park ? NeutralChildDomainState.Parked : NeutralChildDomainState.Running;
            record.State = state;
            return Ok();
            }
        }
        finally { lock (sync) pendingChildTransitions.Remove(lease.Handle); }
    }

    private void FaultTransitionChild(NeutralChildDomainHandle handle)
    {
        if (children.TryGetValue(handle, out Child? currentChild))
            currentChild.State = NeutralChildDomainState.Faulted;
    }

    private NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> CloseChildDomainLegacy(NeutralChildDomainLease lease)
    {
        Child record;
        ExternalOperationIdentity operation;
        lock (sync)
        {
            if (!TryChild(lease, out Child? admittedChild, out NeutralVirtualizationResult failure, allowCleanup: true)) return Fail<NeutralChildDomainCloseReceipt>(failure);
            record = admittedChild!;
            if (HasSoftwareAdmissionPins(record)) return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original software admission custody remains pinned.");
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
            if (HasOriginalIoPins(record) || HasOriginalEventPins(record) || ioBindings.Values.Any(io => io.Neutral.ChildLease == lease))
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
                record.State = NeutralChildDomainState.Closed;
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
                bool continuity = children.TryGetValue(lease.Handle, out Child? returnedChild) && returnedChild == record && !HasSoftwareAdmissionPins(record);
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
                record.CloseReceipt = receipt; // Original terminal fact before publication, including the normal path.
                record.CloseOperation = operation;
                record.CloseLease = record.Neutral;
                if (!continuity)
                {
                    record.CloseMayHaveEffect = true;
                    PinChildCloseUncertainty(lease.Handle);
                    return Fail<NeutralChildDomainCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                        "External child close owner continuity changed; exact closure retained when the original tuple remains, publication refused.");
                }
                record.State = NeutralChildDomainState.Closed;
                return Ok(new NeutralChildDomainCloseReceipt(lease.Handle, lease.Epoch, lease.ParentLease.Handle, lease.ParentLease.Epoch,
                    NeutralChildDomainState.Closed, true));
                }
        }
        finally { lock (sync) pendingChildCloses.Remove(lease.Handle); }
    }

    private void PinChildCloseUncertainty(NeutralChildDomainHandle handle)
    {
        if (originalChildren.TryGetValue(handle, out Child? currentChild))
        {
            currentChild.State = NeutralChildDomainState.Faulted;
            currentChild.CloseMayHaveEffect = true;
        }
    }

    private NeutralVirtualizationResult<NeutralGuestMappingLease> MapGuestRegionLegacy(NeutralGuestMappingRequest request)
    {
        NeutralVirtualizationResult valid = NeutralGuestMemoryContract.ValidateMap(request, NeutralChildDomainState.Created);
        if (!valid.IsSuccess) return Fail<NeutralGuestMappingLease>(valid);
        Child child;
        ulong reservedIdentity;
        ExternalGuestMemoryMapRequest externalRequest;
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? admittedChild, out NeutralVirtualizationResult failure)) return Fail<NeutralGuestMappingLease>(failure);
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
            bool continuity = children.TryGetValue(request.ChildLease.Handle, out Child? returnedChild) && returnedChild == child &&
                returnedChild.State is not (NeutralChildDomainState.Draining or NeutralChildDomainState.Faulted or NeutralChildDomainState.Closed) &&
                !returnedChild.GuestMapMayHaveEffect;
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
                ExternalGuestMemoryMapReceipt externalReceipt = result.Receipt;
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
                NeutralGuestMappingLease lease = new NeutralGuestMappingLease(request.ChildLease, request.ParentMapping, request.GuestRange,
                request.Access, new(reservedIdentity), new(1));
            mappings.Add(lease.Handle, new(lease, externalReceipt.Mapping) { AdmissionPublished = continuity, OriginalAdmission = externalReceipt });
            originalMappings.Add(lease.Handle, mappings[lease.Handle]);
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

    private NeutralVirtualizationResult<NeutralGuestMappingCloseReceipt> UnmapGuestRegionLegacy(NeutralGuestMappingLease lease)
    {
        Mapping mapping;
        ExternalOperationIdentity operation;
        lock (sync)
        {
            if (originalChildren.TryGetValue(lease.ChildLease.Handle, out var sourceChild) && HasSoftwareMemoryPins(sourceChild))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original private software memory source remains pinned.");
            if (!mappings.TryGetValue(lease.Handle, out Mapping? admittedMapping) || admittedMapping.Neutral != lease)
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Stale, "Guest mapping is absent or stale.");
            if (!originalMappings.TryGetValue(lease.Handle, out Mapping? originalMapping) || !ReferenceEquals(originalMapping, admittedMapping))
                return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous, "Guest mapping route is not its original owner.");
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
                originalArtifacts.Values.Any(artifact => !artifact.Released && artifact.Neutral.MappingHandle == lease.Handle &&
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
                    mapping.ClosureReceipt = receipt;
                    mapping.ClosureOperation = operation;
                    mapping.ClosureLease = mapping.Neutral;
                    mapping.ClosureMayHaveEffect = true;
                    PinGuestUnmapUncertainty(mapping);
                    return Fail<NeutralGuestMappingCloseReceipt>(NeutralVirtualizationStatus.Ambiguous,
                        "External guest unmap owner continuity changed; exact closure retained when the original tuple remains, publication refused.");
                }
                mapping.ClosureReceipt = receipt;
                mapping.ClosureOperation = operation;
                mapping.ClosureLease = mapping.Neutral;
                mappings.Remove(lease.Handle);
                return Ok(new NeutralGuestMappingCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle, lease.ChildLease.Epoch,
                    lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true));
                }
        }
        finally { lock (sync) pendingGuestUnmaps.Remove(lease.Handle); }
    }

    private void PinGuestMapUncertainty(NeutralChildDomainHandle handle)
    {
        if (originalChildren.TryGetValue(handle, out Child? child))
        {
            child.State = NeutralChildDomainState.Faulted;
            child.GuestMapMayHaveEffect = true;
        }
    }

    private void PinGuestUnmapUncertainty(Mapping mapping)
    {
        mapping.ClosureMayHaveEffect = true;
        if (originalChildren.TryGetValue(mapping.Neutral.ChildLease.Handle, out Child? child))
            child.State = NeutralChildDomainState.Faulted;
    }

    public NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> BindExecutableArtifact(NeutralExecutableArtifactRequest request)
    {
        if (request.MaximumPrivateDataBytes != 0 && childRuntime is not IHybridCpuPrivateExecutableDataQuotaRuntimeV1)
            return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.NotInvoked, "Native private data quota producer unavailable before Bind.");
        NeutralVirtualizationResult valid = NeutralChildExecutionContract.ValidateAdmission(request);
        if (!valid.IsSuccess) return Fail<NeutralExecutableArtifactReceipt>(valid);
        Child child;
        Mapping mapping;
        ulong reservedIdentity;
        ExternalChildArtifactBindRequest externalRequest;
        OriginalArtifactBindFact bindFact;
        Child[] artifactLineage;
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? admittedChild, out NeutralVirtualizationResult failure)) return Fail<NeutralExecutableArtifactReceipt>(failure);
            child = admittedChild!;
            if (child.NestedParent is not null && !NestedIdentity(child))
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original artifact ancestor factory/history unavailable.");
            artifactLineage = child.NestedParent is null ? [] : GuestLineage(child).ToArray();
            if (artifactLineage.Any(source => pendingNestedSources.Contains(source) || pendingChildCloses.Contains(source.Neutral.Handle) ||
                pendingChildStarts.Contains(source.Neutral.Handle) || pendingChildTransitions.Contains(source.Neutral.Handle) ||
                source.StateRevisionExhausted || source.State is NeutralChildDomainState.Draining or NeutralChildDomainState.Faulted or NeutralChildDomainState.Closed))
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.NotInvoked, "Artifact ancestor source is unavailable or in flight before Bind.");
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
            if (!originalMappings.TryGetValue(request.GuestMapping.Handle, out Mapping? originalMapping) || !ReferenceEquals(originalMapping, mapping) || mapping.ArtifactMayHaveEffect)
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original artifact mapping owner is foreign or an earlier bind remains unknown.");
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
                request.ImmutablePackage.ToArray(), request.MaximumExecutionSteps, Op()) { MaximumPrivateDataBytes = request.MaximumPrivateDataBytes };
            bindFact = new(request, externalRequest, child, mapping, reservedIdentity);
            bindFact.EstablishSelf();
            foreach (Child? ancestor in artifactLineage.Skip(1)) bindFact.Ancestors.Add(ancestor, ancestor.StateRevision);
            mapping.ArtifactBind = bindFact;
            pendingArtifactBinds.Add(request.GuestMapping.Handle);
            foreach (Child source in artifactLineage) pendingNestedSources.Add(source);
        }
        try
        {
            if (child.NestedParent is not null)
            {
                HybridCpuExternalFeatureManifest before;
                try { before = root.QueryFeatures(); }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    lock (sync) bindFact.NoEffectConsumed = true;
                    return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.NotInvoked, "Original nested Bind source unavailable before native invocation.");
                }
                lock (sync)
                {
                    if (!HasOriginalBindSources(bindFact) || child.StateRevisionExhausted || child.StateRevision != bindFact.AdmissionRevision ||
                        before.ContractVersion != externalFeatures.ContractVersion || before.Generation != externalFeatures.Generation)
                    {
                        bindFact.NoEffectConsumed = true;
                        return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.NotInvoked, "Original nested Bind source changed before native invocation.");
                    }
                }
            }
            ExternalChildResult<ExternalChildArtifactBindReceipt> result;
            try { result = childRuntime.BindChildExecutableArtifact(child!.External, externalRequest); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (sync) PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External artifact bind may have taken effect without a receipt: {exception.Message}");
            }
            lock (sync) bindFact.Observation = result;
            if (child.NestedParent is not null && result is { Outcome: ExternalRuntimeOutcome.Succeeded, Receipt: { } originalReceipt })
            {
                ExternalChildArtifactBindReceipt? canonical;
                try { canonical = (childRuntime as IHybridCpuOriginalArtifactBindRuntimeV1)?.ResolveChildExecutableArtifactBind(child.External, externalRequest); }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    lock (sync) PinArtifactBindUncertainty(child, mapping);
                    return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original nested Bind confirmation reply unavailable; raw retained.");
                }
                lock (sync)
                {
                    bindFact.ResolutionObservation = canonical;
                    if (!ReferenceEquals(canonical, originalReceipt))
                    {
                        PinArtifactBindUncertainty(child, mapping);
                        return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original nested Bind confirmation is absent or foreign; backing remains pinned.");
                    }
                }
            }
            HybridCpuExternalFeatureManifest finalSource = externalFeatures;
            if (child.NestedParent is not null)
            {
                try { finalSource = root.QueryFeatures(); }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    lock (sync) { bindFact.SourceChanged = true; PinArtifactBindUncertainty(child, mapping); }
                    return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original nested Bind source unavailable after effect; raw retained.");
                }
                lock (sync) if (finalSource.ContractVersion != externalFeatures.ContractVersion || finalSource.Generation != externalFeatures.Generation)
                    bindFact.SourceChanged = true;
            }
            lock (sync)
            {
            bindFact.Observation = result; // Raw retained on captured original fact before local validation.
            bool continuity = (child.NestedParent is null || HasOriginalBindSources(bindFact) && !child.StateRevisionExhausted && child.StateRevision == bindFact.AdmissionRevision) && children.TryGetValue(request.ChildLease.Handle, out Child? returnedChild) && returnedChild == child &&
                returnedChild.State is not (NeutralChildDomainState.Draining or NeutralChildDomainState.Faulted or NeutralChildDomainState.Closed) &&
                !returnedChild.GuestMapMayHaveEffect &&
                mappings.TryGetValue(request.GuestMapping.Handle, out Mapping? returnedMapping) && returnedMapping == mapping &&
                ReferenceEquals(mapping.ArtifactBind, bindFact);
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
                ExternalChildArtifactBindReceipt externalReceipt = result.Receipt;
            if (externalReceipt.ArtifactHandle.Value == Guid.Empty || externalReceipt.ArtifactEpoch.Value == 0 ||
                externalReceipt.ChildHandle != child.External.Handle || externalReceipt.ChildEpoch != child.External.Epoch ||
                externalReceipt.MappingHandle != mapping.External.Handle || externalReceipt.MappingEpoch != mapping.External.Epoch ||
                externalReceipt.Parent != child.External.Parent || externalReceipt.Operation != externalRequest.Operation ||
                externalReceipt.ContractVersion != externalFeatures.ContractVersion ||
                externalReceipt.ManifestGeneration != externalFeatures.Generation ||
                externalReceipt.MaximumPipelineCycles != externalRequest.MaximumPipelineCycles ||
                externalReceipt.MaximumPrivateDataBytes != externalRequest.MaximumPrivateDataBytes ||
                !string.Equals(externalReceipt.PackageSha256,
                    Convert.ToHexStringLower(SHA256.HashData(externalRequest.PackageBytes)), StringComparison.Ordinal))
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External artifact receipt does not match the exact child, mapping, operation, or provider generation.");
            }
                NeutralExecutableArtifactReceipt receipt = new NeutralExecutableArtifactReceipt(new(reservedIdentity), new(1), request.ChildLease.Handle,
                request.ChildLease.Epoch, request.GuestMapping.Handle, request.GuestMapping.Epoch,
                request.ChildLease.ParentLease.Handle, request.ChildLease.ParentLease.Epoch,
                externalReceipt.PackageSha256, request.MaximumExecutionSteps) { MaximumPrivateDataBytes = request.MaximumPrivateDataBytes };
                NeutralVirtualizationResult exact = NeutralChildExecutionContract.ValidateAdmissionReceipt(request, receipt);
            if (!exact.IsSuccess)
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External artifact receipt failed neutral admission validation; dependencies remain pinned.");
            }
            artifacts.Add(receipt.ArtifactHandle, new(receipt, externalReceipt) { AdmissionPublished = continuity, BindChild = child, BindMapping = mapping });
            originalArtifacts.Add(receipt.ArtifactHandle, artifacts[receipt.ArtifactHandle]);
            if (!continuity)
            {
                PinArtifactBindUncertainty(child, mapping);
                return Fail<NeutralExecutableArtifactReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External artifact bind owner continuity changed; exact receipt retained for cleanup, publication refused.");
            }
            return Ok(receipt);
            }
        }
        finally
        {
            lock (sync)
            {
                pendingArtifactBinds.Remove(request.GuestMapping.Handle);
                foreach (Child source in artifactLineage) pendingNestedSources.Remove(source);
            }
        }
    }

    private void PinArtifactBindUncertainty(Child child, Mapping mapping)
    {
        if (child.NestedParent is not null && mapping.ArtifactBind is { } original &&
            (child.StateRevisionExhausted || child.StateRevision != original.AdmissionRevision &&
                !(original.OwnFailure && child.StateRevision == original.FailureRevision)))
            original.SourceChanged = true; // Independent child ABA/exhaustion cannot become this invocation's recoverable fault.
        if (originalChildren.TryGetValue(child.Neutral.Handle, out Child? currentChild) && ReferenceEquals(currentChild, child))
        {
            currentChild.State = NeutralChildDomainState.Faulted;
            currentChild.GuestMapMayHaveEffect |= !mappings.ContainsKey(mapping.Neutral.Handle);
        }
        if (originalMappings.TryGetValue(mapping.Neutral.Handle, out Mapping? currentMapping) && ReferenceEquals(currentMapping, mapping))
            currentMapping.ArtifactMayHaveEffect = true;
        if (mapping.ArtifactBind is { } fact && ReferenceEquals(fact.Child, child) && ReferenceEquals(fact.Mapping, mapping))
        { fact.OwnFailure = true; fact.FailureRevision = child.StateRevision; }
    }

    public NeutralVirtualizationResult<NeutralChildExecutionReceipt> StartExecutableArtifact(
        NeutralChildExecutionStartRequest request)
    {
        List<SemanticTraceEventV1> events = new List<SemanticTraceEventV1>(3);
        try { return StartExecutableArtifactCore(request, events); }
        finally
        {
            // Deliver committed observations after releasing the owner lock.
            // Sink failure and reentry cannot participate in start admission.
            foreach (SemanticTraceEventV1 item in events)
            {
                try { _ = traceSink?.TryRecord(item); }
                catch { /* Observation is not execution authority. */ }
            }
        }
    }

    private NeutralVirtualizationResult<NeutralChildExecutionReceipt> StartExecutableArtifactCore(
        NeutralChildExecutionStartRequest request, List<SemanticTraceEventV1> events)
    {
        NeutralVirtualizationResult admission = NeutralChildExecutionContract.ValidateStart(request);
        if (!admission.IsSuccess) return Fail<NeutralChildExecutionReceipt>(admission);
        Child child; Artifact found; Mapping backing;
        ExternalOperationIdentity operation;
        string correlation, generationDigest;
        OriginalStartInvocation invocation;
        Child[] startLineage;
        lock (sync)
        {
            if (!TryChild(request.ChildLease, out Child? admittedChild, out NeutralVirtualizationResult failure)) return Fail<NeutralChildExecutionReceipt>(failure);
            child = admittedChild!;
            if (HasSoftwareArtifactEntryHistory(child)) return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Denied, "Original secure artifact entry is one-shot; ordinary start is unavailable.");
            if (child.NestedParent is not null && !NestedIdentity(child))
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original Start ancestor identity/history unavailable.");
            startLineage = child.NestedParent is null ? [] : GuestLineage(child).ToArray();
            if (startLineage.Any(source => pendingNestedSources.Contains(source) || pendingChildCloses.Contains(source.Neutral.Handle) ||
                pendingChildStarts.Contains(source.Neutral.Handle) || pendingChildTransitions.Contains(source.Neutral.Handle) ||
                source.StateRevisionExhausted || source.State is NeutralChildDomainState.Draining or NeutralChildDomainState.Faulted or NeutralChildDomainState.Closed))
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.NotInvoked, "Original Start ancestor admission is unavailable or pending.");
            if (child.State == NeutralChildDomainState.Faulted)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "Child execution outcome is quarantined pending external reconciliation.");
            if (child.State != NeutralChildDomainState.Created)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable start requires a Created child.");
            if (!artifacts.TryGetValue(request.Artifact.ArtifactHandle, out found!) || found.Neutral != request.Artifact)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Stale, "Artifact admission is absent or stale.");
            if (!originalArtifacts.TryGetValue(request.Artifact.ArtifactHandle, out Artifact? original) || !ReferenceEquals(original, found) ||
                found.ReleaseRequest is not null || !ReferenceEquals(found.BindChild, child) ||
                found.BindMapping is not { } originalBacking || !mappings.TryGetValue(originalBacking.Neutral.Handle, out Mapping? currentBacking) ||
                !ReferenceEquals(currentBacking, originalBacking))
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original artifact source is changed or release is staged.");
            backing = originalBacking;
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
            if (!OriginalStartHistoryIntact(found) || found.StartHistory.Any(previous => previous.Invoked && !previous.NoEffectConsumed))
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,"Original Start history unresolved; repeat denied.");
            if (found.StartsIssued == ulong.MaxValue || child.StateRevisionExhausted)
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.NotInvoked,"Original Start history/source capacity exhausted before effect.");
            found.StartHistory.EnsureCapacity(found.StartHistory.Count + 1);
            pendingChildStarts.EnsureCapacity(pendingChildStarts.Count + 1);
            operation = Op(); // reserve the exact usable generation before trace/pending/effect
            correlation = $"hybridcpu-child-start:{request.OperationId.Value}:{request.OperationGeneration.Value}";
            generationDigest = Digest(FormattableString.Invariant(
                $"child-start-generation/v2|{request.ChildLease.ParentLease.Handle.Value}|{request.ChildLease.ParentLease.Epoch.Value}|{request.ChildLease.Handle.Value}|{request.ChildLease.Epoch.Value}|{request.Artifact.MappingHandle.Value}|{request.Artifact.MappingEpoch.Value}|{request.Artifact.ArtifactHandle.Value}|{request.Artifact.ArtifactEpoch.Value}|{request.OperationId.Value}|{request.OperationGeneration.Value}|{externalFeatures.Generation}"));
            RecordTrace(events, correlation, 1, SemanticTraceEventKindV1.Submit, generationDigest,
                Digest($"{request.Artifact.ContentDigest}|{request.Artifact.MaximumExecutionSteps}"));
            RecordTrace(events, correlation, 2, SemanticTraceEventKindV1.EffectPossible, generationDigest,
                Digest($"{found.External.ArtifactHandle}|{found.External.ArtifactEpoch}"));
            if (!TryChild(request.ChildLease, out Child? currentChild, out failure) ||
                currentChild!.State != NeutralChildDomainState.Created ||
                !artifacts.TryGetValue(request.Artifact.ArtifactHandle, out Artifact? currentArtifact) ||
                currentArtifact != found)
            {
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("admission-changed-during-observation"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Denied,
                    "Executable admission changed during trace observation.");
            }
            invocation = new(request,found,child,backing,new(found.External.ArtifactHandle,found.External.ArtifactEpoch,operation));
            invocation.EstablishSelf(); found.StartHistory.Add(invocation); found.StartsIssued++; found.OriginalStart = invocation;
            foreach (Child source in startLineage) pendingNestedSources.Add(source);
            pendingChildStarts.Add(request.ChildLease.Handle);
        }
        try
        {
            HybridCpuExternalFeatureManifest before;
            try { before = root.QueryFeatures(); }
            catch
            {
                lock (sync) invocation.NoEffectConsumed = true;
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.NotInvoked,"Original Start source lookup failed before native invocation.");
            }
            lock (sync)
            {
                if (!CurrentOriginalStart(invocation,before))
                {
                    invocation.NoEffectConsumed = true;
                    return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.NotInvoked,"Original Start sources changed before invocation.");
                }
                invocation.Invoked = true;
            }
            ExternalChildResult<ExternalChildExecutionReceipt> result;
            try
            {
                if (childRuntime is IHybridCpuOriginalChildExecutionStartRuntimeV1 originalStart)
                {
                    ExternalOriginalChildExecutionStartObservation? observation = originalStart.StartOriginalChildExecution(invocation.OriginalExternalRequest);
                    lock (sync) invocation.OriginalObservation = observation; // Raw typed fact before callbacks/validation.
                    if (observation is null || !ReferenceEquals(observation.Request, invocation.OriginalExternalRequest))
                        throw new InvalidOperationException("Original native Start response is absent or foreign.");
                    result = observation.Raw;
                }
                else result = childRuntime.StartChildExecution(child.External,invocation.ExternalRequest);
                lock (sync) invocation.Observation = result; // Raw before source/trace callbacks.
                if (child.NestedParent is not null && invocation.OriginalObservation is { } originalObservation &&
                    childRuntime is IHybridCpuOriginalChildExecutionStartRuntimeV1 originalConfirmation)
                {
                    ExternalOriginalChildExecutionStartObservation? canonical = originalConfirmation.ResolveOriginalChildExecutionStart(invocation.OriginalExternalRequest);
                    lock (sync)
                    {
                        invocation.ResolutionObservation = canonical;
                        if (canonical is null || canonical.NoEffect != originalObservation.NoEffect || !ReferenceEquals(canonical.Request, invocation.OriginalExternalRequest) ||
                            !ReferenceEquals(canonical.Raw, result))
                        {
                            invocation.SourceChanged = true;
                            throw new InvalidOperationException("Original nested Start raw/history confirmation unavailable; retained response cannot replace original fact.");
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // Pure cleanup reconciliation may retain the original native result; it cannot publish Start.
                if (childRuntime is IHybridCpuOriginalChildExecutionStartRuntimeV1 originalStart)
                {
                    try
                    {
                        ExternalOriginalChildExecutionStartObservation? resolved = originalStart.ResolveOriginalChildExecutionStart(invocation.OriginalExternalRequest);
                        lock (sync) invocation.ResolutionObservation = resolved;
                    }
                    catch { /* Unknown remains retained and no-repeat. */ }
                }
                HybridCpuExternalFeatureManifest? reconciliationSource = null;
                try { reconciliationSource = root.QueryFeatures(); } catch { /* No-effect proof retained; source unknown stays pinned. */ }
                lock (sync)
                {
                    if (invocation.ResolutionObservation is { NoEffect: true } refusal &&
                        ReferenceEquals(refusal.Request, invocation.OriginalExternalRequest) &&
                        refusal.Raw is { Receipt: null } refusalRaw && refusalRaw.Outcome != ExternalRuntimeOutcome.Succeeded &&
                        reconciliationSource is not null && MatchesOriginalStartManifest(invocation, reconciliationSource) &&
                        ReferenceEquals(invocation.OwnerSelf, invocation) && ReferenceEquals(found.OriginalStart, invocation) &&
                        HasOriginalArtifactReleaseSources(found) && !child.StateRevisionExhausted && child.StateRevision == invocation.ChildRevision)
                        invocation.NoEffectConsumed = true; // Completed invocation only; no Start publication/permission or backing release.
                }
                lock (sync) child.State = NeutralChildDomainState.Faulted; // Original owner, never foreign-route compensation.
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("external-execution-callback-failed"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    $"External execution may have had an effect without a receipt: {exception.Message}");
            }
            HybridCpuExternalFeatureManifest? after = null;
            try { after = root.QueryFeatures(); }
            catch
            {
                lock (sync) if (child.NestedParent is not null) invocation.SourceChanged = true;
                // Raw retained; a nested independent source fault cannot be consumed as an owned execution fault.
            }
            lock (sync)
            {
            bool continuity = after is not null && CurrentOriginalStart(invocation,after);
            if (continuity && invocation.OriginalObservation is { NoEffect: true } noEffect &&
                ReferenceEquals(noEffect.Request, invocation.OriginalExternalRequest) && result.Outcome != ExternalRuntimeOutcome.Succeeded && result.Receipt is null)
            {
                invocation.NoEffectConsumed = true;
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.NotInvoked,"Exact original native Start refused before effect.");
            }
            if (!continuity && (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null))
            {
                child.State = NeutralChildDomainState.Faulted;
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("external-start-owner-continuity-lost"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External start owner continuity changed during the provider callback; effect remains quarantined.");
            }
            if (result.Outcome != ExternalRuntimeOutcome.Succeeded || result.Receipt is null)
            {
                child.State = NeutralChildDomainState.Faulted;
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest($"{result.Outcome}|{result.Reason}"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External execution may have had an effect; result requires reconciliation.");
            }
                ExternalChildExecutionReceipt externalReceipt = result.Receipt;
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
                child.State = NeutralChildDomainState.Faulted;
                RecordTrace(events, correlation, 3, SemanticTraceEventKindV1.Quarantined, generationDigest,
                    Digest("external-execution-receipt-mismatch"));
                return Fail<NeutralChildExecutionReceipt>(NeutralVirtualizationStatus.Ambiguous,
                    "External execution receipt does not match the admitted tuple; child is quarantined.");
            }
                NeutralChildExecutionReceipt receipt = ToNeutral(externalReceipt, found, request);
            found.TupleMatchedExecutionObservation = externalReceipt; // raw original outcome, not accepted completion/charge/closure
                NeutralVirtualizationResult exact = NeutralChildExecutionContract.ValidateExecutionReceipt(request, receipt);
            if (exact.IsSuccess) found.ExecutionReceipt = receipt; // provider fact before trace delivery, not permission/release
            if (!exact.IsSuccess)
                child.State = NeutralChildDomainState.Faulted;
            if (!continuity)
            {
                child.State = NeutralChildDomainState.Faulted;
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
        finally
        {
            lock (sync)
            {
                pendingChildStarts.Remove(request.ChildLease.Handle);
                foreach (Child source in startLineage) pendingNestedSources.Remove(source);
            }
        }
    }

    public NeutralVirtualizationResult<NeutralVirtualIoLease> BindVirtualIo(NeutralVirtualIoRequest request)
    {
        NeutralVirtualizationResult valid = NeutralVirtualIoContract.ValidateBind(request, NeutralChildDomainState.Created);
        if (!valid.IsSuccess) return Fail<NeutralVirtualIoLease>(valid);
        return Fail<NeutralVirtualIoLease>(NeutralVirtualizationStatus.Unsupported,
            "No provider-owned external parent device identity is bound to the exact neutral device lease.");
    }

    public NeutralVirtualizationResult<NeutralVirtualIoCloseReceipt> CloseVirtualIo(NeutralVirtualIoLease lease)
    {
        OriginalIoFact? original;
        lock (sync) originalIo.TryGetValue(lease.Handle, out original);
        if (original is not null) return CloseOriginalIo(original, lease);
        lock (sync)
        {
            if (!ioBindings.TryGetValue(lease.Handle, out Io? io) || io.Neutral != lease)
                return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Stale, "I/O binding is absent or stale.");
            if (!TryChild(lease.ChildLease, out Child? child, out NeutralVirtualizationResult failure)) return Fail<NeutralVirtualIoCloseReceipt>(failure);
            if (nextOperation == 0 || nextOperation == ulong.MaxValue)
                return Fail<NeutralVirtualIoCloseReceipt>(NeutralVirtualizationStatus.Denied,
                    "External I/O-close operation capacity is exhausted; binding remains pinned.");
            ExternalOperationIdentity operation = Op();
            ExternalChildResult<ExternalChildVirtualIoCloseReceipt> result = childRuntime.CloseChildVirtualIo(child!.External, io.External.IoHandle, io.External.IoEpoch, operation);
            if (result.Outcome != ExternalRuntimeOutcome.Closed || result.Receipt is null || !result.Receipt.IsTerminal)
                return Fail<NeutralVirtualIoCloseReceipt>(result.Outcome, result.Reason);
            ioBindings.Remove(lease.Handle);
            return Ok(new NeutralVirtualIoCloseReceipt(lease.Handle, lease.Epoch, lease.ChildLease.Handle, lease.ChildLease.Epoch,
                lease.ChildLease.ParentLease.Handle, lease.ChildLease.ParentLease.Epoch, true));
        }
    }

    public NeutralVirtualizationResult<NeutralVirtualEventReceipt> InjectVirtualEvent(NeutralVirtualEventRequest request) =>
        InjectOriginalVirtualEvent(new(request));

    private bool TryChild(NeutralChildDomainLease lease, out Child? child, out NeutralVirtualizationResult failure,
        bool allowCleanup = false, bool dependencyRefusalIsPreEffect = false)
    {
        if (!children.TryGetValue(lease.Handle, out child)) { failure = Fail(NeutralVirtualizationStatus.Denied, "Child was not found."); return false; }
        if (!originalChildren.TryGetValue(lease.Handle, out Child? original) || !ReferenceEquals(original, child))
        { failure = Fail(NeutralVirtualizationStatus.Ambiguous, "Child route is not its original owner."); return false; }
        if (child.Neutral.Epoch != lease.Epoch) { failure = Fail(NeutralVirtualizationStatus.Stale, "Child epoch is stale."); return false; }
        if (child.Neutral != lease) { failure = Fail(NeutralVirtualizationStatus.WrongParent, "Child parent or intent differs."); return false; }
        if (!allowCleanup && !GuestMapHistory(child))
        { failure = Fail(NeutralVirtualizationStatus.Ambiguous,"Original guest Map history is incomplete; new child effect prohibited."); return false; }
        if (!allowCleanup && !child.AdmissionPublished)
        {
            failure = Fail(NeutralVirtualizationStatus.Ambiguous, "Exact child is retained for cleanup; admission publication was refused.");
            return false;
        }
        if (!allowCleanup && ((ulong)child.OriginalIo.Count != child.OriginalIoIssued || child.OriginalIo.Any(f => f.InFlight || !f.Published && !f.Consumed)))
        {
            failure = Fail(dependencyRefusalIsPreEffect ? NeutralVirtualizationStatus.NotInvoked : NeutralVirtualizationStatus.Ambiguous,
                "Original I/O admission/cleanup is pending; child effect prohibited.");
            return false;
        }
        if (!allowCleanup && HasOriginalEventPins(child))
        {
            failure = Fail(dependencyRefusalIsPreEffect ? NeutralVirtualizationStatus.NotInvoked : NeutralVirtualizationStatus.Ambiguous,
                "Original event delivery/cleanup remains pinned; child effect was not invoked.");
            return false;
        }
        if (pendingNestedSources.Contains(child) || !allowCleanup && !NestedIdentity(child))
        { failure = Fail(NeutralVirtualizationStatus.Ambiguous, "Original nested lineage/factory is unavailable or pending."); return false; }
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
        SemanticTraceEventV1 traceEvent = new SemanticTraceEventV1(SemanticTraceEventV1.CurrentVersion, correlation, sequence,
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






