using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformChildBindingId(ulong Value);
public readonly record struct PlatformChildBindingGeneration(ulong Value);
public readonly record struct PlatformChildBinding(
    PlatformChildBindingId BindingId,
    PlatformChildBindingGeneration Generation,
    PlatformDomainBinding ParentBinding,
    ProcessHandle Owner);
public readonly record struct PlatformGuestMappingId(ulong Value);
public readonly record struct PlatformGuestMappingGeneration(ulong Value);
public readonly record struct PlatformGuestMapping(
    PlatformGuestMappingId MappingId,
    PlatformGuestMappingGeneration Generation,
    PlatformChildBinding Child,
    PlatformRegionMapping ParentMapping);
public readonly record struct PlatformVirtualIoBindingId(ulong Value);
public readonly record struct PlatformVirtualIoBindingGeneration(ulong Value);
public readonly record struct PlatformVirtualIoBinding(
    PlatformVirtualIoBindingId BindingId,
    PlatformVirtualIoBindingGeneration Generation,
    PlatformChildBinding Child,
    PlatformDeviceLease ParentDevice);
public readonly record struct PlatformExecutableArtifactBindingId(ulong Value);
public readonly record struct PlatformExecutableArtifactBindingGeneration(ulong Value);
public readonly record struct PlatformExecutableArtifactBinding(
    PlatformExecutableArtifactBindingId BindingId,
    PlatformExecutableArtifactBindingGeneration Generation,
    PlatformChildBinding Child,
    PlatformGuestMapping GuestMapping);

public sealed partial class PlatformAuthorityBridge
{
    private sealed class ChildCreateInvocationFact
    {
        public ChildCreateInvocationFact(DomainRecord source, PlatformChildDomainIntent intent, PlatformChildBindingId identity)
        { OriginalOwner = this; Source = source; Intent = intent; ReservedIdentity = identity; OriginalRequest = new(source.ProviderLease, intent); Revision = source.AuthorityRevision; }
        public ChildCreateInvocationFact OriginalOwner { get; }
        public DomainRecord Source { get; }
        public PlatformChildDomainIntent Intent { get; }
        public PlatformChildBindingId ReservedIdentity { get; }
        public PlatformOriginalChildCreateRequest OriginalRequest { get; }
        public PlatformAuthorityResult<PlatformProviderChildDomainLease>? Observation { get; set; }
        public PlatformAuthorityResult<PlatformProviderChildDomainLease>? Resolution { get; set; }
        public ChildBindingRecord? Child { get; set; }
        public long Revision { get; }
        public bool OwnFault, InFlight;
        public long FaultRevision;
    }
    private sealed class NestedCreateInvocationFact
    {
        public NestedCreateInvocationFact(ChildBindingRecord source, PlatformNestedDomainRequest request)
        { OriginalOwner = this; Source = source; Request = request; OriginalRequest = new(request); AdmittedState = source.State; AdmittedRevision = source.LifecycleRevision; OriginalRoot = source.CreateRoot; AdmissionEpoch = source.CreateRoot?.BackendEpoch ?? default; }
        public NestedCreateInvocationFact OriginalOwner { get; }
        public ChildBindingRecord Source { get; }
        public PlatformNestedDomainRequest Request { get; }
        public PlatformOriginalNestedDomainRequest OriginalRequest { get; }
        public PlatformChildDomainState AdmittedState { get; }
        public long AdmittedRevision { get; }
        public long? OwnFaultRevision { get; set; }
        public PlatformOriginalNestedDomainClosure? CleanupRaw { get; set; }
        public bool CleanupInFlight { get; set; }
        public DomainRecord? OriginalRoot { get; }
        public PlatformBackendEpoch AdmissionEpoch { get; }
        public PlatformAuthorityResult<PlatformProviderNestedDomainLease>? Observation { get; set; }
    }
    private sealed class ChildBindingRecord(
        PlatformChildBinding binding,
        PlatformProviderChildDomainLease providerLease,
        PlatformChildBinding? parentChild = null)
    {
        public DomainRecord? CreateRoot { get; init; }
        public ChildBindingRecord? CreateParentChild { get; init; }
        public PlatformChildBinding Binding { get; } = binding;
        public PlatformProviderChildDomainLease ProviderLease { get; } = providerLease;
        public PlatformChildBinding? ParentChild { get; } = parentChild;
        private PlatformChildDomainState _state = PlatformChildDomainState.Created;
        private long _lifecycleRevision;
        private int _lifecycleRevisionExhausted;
        public long LifecycleRevision => Volatile.Read(ref _lifecycleRevision);
        public bool LifecycleRevisionExhausted => Volatile.Read(ref _lifecycleRevisionExhausted) != 0;
        public PlatformChildDomainState State
        {
            get => _state;
            set { AdvanceExecutableSourceRevision(ref _lifecycleRevision, ref _lifecycleRevisionExhausted); _state = value; }
        }
        public bool CreationPublished { get; set; } = true;
        public PlatformAuthorityResult<PlatformProviderChildDomainLease>? CreateObservation { get; set; }
        public ulong LastEventSequence { get; set; }
        public ulong LastTrapSequence { get; set; }
        // Original provider outcomes, not permission, sequence validation or closure truth.
        public List<ChildDeliveryInvocation<PlatformVirtualEventRequest, PlatformVirtualEventReceipt>> EventInvocations { get; } = [];
        public ulong EventsIssued { get; set; }
        public ulong EventsConsumed { get; set; }
        public bool EventCleanupOnly { get; set; }
        public bool NestedCleanupOnly { get; set; }
        public List<ChildDeliveryInvocation<PlatformProviderChildDomainLease, PlatformVirtualTrapEvidence>> TrapInvocations { get; } = [];
        public bool NestedCreateMayHaveEffect { get; set; }
        public List<NestedCreateInvocationFact> NestedCreateInvocations { get; } = [];
        public ulong NestedCreatesIssued { get; set; }
        public ulong NestedCreatesConsumed { get; set; }
        public PlatformAuthorityResult<PlatformProviderNestedDomainLease>? NestedCreateObservation { get; set; }
        public List<UnpublishedNestedCreateReceipt> UnpublishedNestedCreates { get; } = [];
        public bool TransitionInFlight { get; set; }
        public PlatformChildDomainTransition? TransitionIntent { get; set; }
        public PlatformAuthorityResult? TransitionObservation { get; set; }
        public bool TransitionPublicationRefused { get; set; }
        public bool CloseInvocationAttempted { get; set; }
        public OriginalChildCloseInvocation? OriginalCloseInvocation { get; set; }
        public PlatformBackendEpoch TransitionEpoch { get; set; }
        public PlatformChildDomainState TransitionAdmittedState { get; set; }
        public bool CloseInFlight { get; set; }
        public int PendingChildEffects { get; set; }
        public List<GuestMapInvocationFact> GuestMapInvocations { get; } = [];
        public ChildClosureReceiptFact? LastClosureReceipt { get; set; }
    }

    // Returned cleanup evidence only. Closed still requires exact validation and continuity.
    private sealed record ChildClosureReceiptFact(PlatformProviderChildDomainLease AdmittedLease,
        PlatformBackendEpoch AdmissionEpoch, PlatformChildDomainClosureReceipt Receipt);

    private sealed record GuestAdmissionSource(ChildBindingRecord Child, MappingRecord Parent, DomainRecord Root)
    {
        // Reference to the existing local fact owner, not copied permission or a ledger.
        public VirtualDomainAuthority.Record? LocalMappingOwner { get; init; }
        public GuestRegionMappingHandle? LocalMapping { get; init; }
    }

    private sealed class GuestBindingRecord(
        PlatformGuestMapping mapping,
        PlatformProviderGuestRegionMappingLease providerLease)
    {
        public OriginalGuestUnmapInvocation? OriginalUnmap { get; set; }
        public GuestAdmissionSource? AdmissionSource { get; init; }
        public bool AdmissionPublished { get; set; }
        public PlatformGuestMapping Mapping { get; } = mapping;
        public PlatformProviderGuestRegionMappingLease ProviderLease { get; } = providerLease;
        private PlatformExternalClosureState _closure = PlatformExternalClosureState.Active;
        private long _lifecycleRevision;
        private int _lifecycleRevisionExhausted;
        public long LifecycleRevision => Volatile.Read(ref _lifecycleRevision);
        public bool LifecycleRevisionExhausted => Volatile.Read(ref _lifecycleRevisionExhausted) != 0;
        public PlatformExternalClosureState Closure
        {
            get => _closure;
            set { AdvanceExecutableSourceRevision(ref _lifecycleRevision, ref _lifecycleRevisionExhausted); _closure = value; }
        }
        public int PendingExecutableEffects { get; set; }
        public int PendingSecureRegionEffects { get; set; }
        public GuestClosureReceiptFact? LastClosureReceipt { get; set; }
        public GuestUnmapRefusalFact? LastUnmapRefusal { get; set; }
        public bool ClosureInFlight { get; set; }
    }

    // Cleanup evidence only; it does not establish continuity or authorize reclaim.
    private sealed record GuestClosureReceiptFact(PlatformProviderGuestRegionMappingLease AdmittedLease,
        PlatformBackendEpoch AdmissionEpoch, PlatformGuestRegionMappingClosureReceipt Receipt);

    // Current invocation did not execute. This is neither original closure nor reclaim evidence.
    private sealed record GuestUnmapRefusalFact(PlatformProviderGuestRegionMappingLease AdmittedLease,
        PlatformBackendEpoch AdmissionEpoch, ChildBindingRecord Child, MappingRecord Parent);

    private sealed class VirtualIoBindInvocationFact
    {
        public VirtualIoBindInvocationFact(ChildBindingRecord child, DeviceLeaseRecord device, DomainRecord root,
            PlatformVirtualIoRequest request, PlatformBackendEpoch epoch)
        { OriginalOwner = this; Child = child; Device = device; Root = root; Request = request; Epoch = epoch; OriginalRequest = new(Guid.NewGuid(), request); }
        public VirtualIoBindInvocationFact OriginalOwner { get; }
        public ChildBindingRecord Child { get; }
        public DeviceLeaseRecord Device { get; }
        public DomainRecord Root { get; }
        public PlatformVirtualIoRequest Request { get; }
        public PlatformBackendEpoch Epoch { get; }
        public PlatformAuthorityResult<PlatformProviderVirtualIoLease>? Observation { get; set; }
        public PlatformOriginalVirtualIoBindRequest OriginalRequest { get; }
        public IPlatformOriginalVirtualIoBindProvider? OriginalProvider { get; set; }
        public PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure>? CleanupRaw { get; set; }
        public bool CleanupInFlight { get; set; }
        public bool Consumed { get; set; }
    }
    private sealed class VirtualIoBindingRecord
    {
        public VirtualIoBindingRecord(PlatformVirtualIoBinding binding, PlatformProviderVirtualIoLease providerLease, VirtualIoBindInvocationFact admission)
        { OriginalOwner = this; Binding = binding; ProviderLease = providerLease; Admission = admission; }
        public VirtualIoBindingRecord OriginalOwner { get; }
        public PlatformVirtualIoBinding Binding { get; }
        public PlatformProviderVirtualIoLease ProviderLease { get; }
        public VirtualIoBindInvocationFact Admission { get; }
        public PlatformExternalClosureState Closure { get; set; } = PlatformExternalClosureState.Active;
        public VirtualIoClosureReceiptFact? LastClosureReceipt { get; set; }
        public bool TransferInFlight { get; set; }
        public List<BridgeIoTransferFact> Transfers { get; } = [];
        public ulong TransfersIssued { get; set; }
        public bool ClosureInFlight { get; set; }
        public bool CloseInvoked { get; set; }
        public bool ClosureConsumed { get; set; }
        public VirtualIoCloseInvocationFact? CloseInvocation { get; set; }
    }
    private sealed class VirtualIoCloseInvocationFact
    {
        public VirtualIoCloseInvocationFact(VirtualIoBindingRecord source, PlatformBackendEpoch epoch)
        { OriginalOwner = this; Source = source; Epoch = epoch; AdmittedLease = source.ProviderLease; Request = new(Guid.NewGuid(), source.ProviderLease); }
        public VirtualIoCloseInvocationFact OriginalOwner { get; }
        public VirtualIoBindingRecord Source { get; }
        public PlatformBackendEpoch Epoch { get; }
        public PlatformProviderVirtualIoLease AdmittedLease { get; }
        public PlatformOriginalVirtualIoCloseRequest Request { get; }
        public IPlatformOriginalVirtualIoCloseProvider? ContinuationProvider { get; set; }
        public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>? Observation { get; set; }
        public bool ValidatedClosed { get; set; }
    }
    // Returned cleanup fact only; exact validation and continuity still govern closure.
    private sealed record VirtualIoClosureReceiptFact(PlatformProviderVirtualIoLease AdmittedLease,
        PlatformBackendEpoch AdmissionEpoch, PlatformVirtualIoClosureReceipt Receipt);
    private sealed record ExecutableArtifactSource(ChildBindingRecord Child, GuestBindingRecord Guest, MappingRecord Parent, DomainRecord Root);

    private sealed class ExecutableArtifactRecord(
        PlatformExecutableArtifactBinding binding,
        PlatformExecutableArtifactReceipt providerReceipt)
    {
        public PlatformExecutableArtifactBinding Binding { get; } = binding;
        public PlatformExecutableArtifactReceipt ProviderReceipt { get; set; } = providerReceipt;
        public bool AdmissionPublished { get; set; }
        public ExecutableBindInvocationFact? BindInvocation { get; set; }
        public ExecutableArtifactSource? AdmissionSource { get; init; }
        public bool Started { get; set; }
        public PlatformChildExecutionReceipt? ExecutionReceipt { get; set; }
        public PlatformAuthorityResult<PlatformExecutionTerminalObservation>? TerminalObservation { get; set; }
        public bool StartInFlight { get; set; }
        public ExecutableStartInvocationFact? StartInvocation { get; set; }
        public PlatformArtifactReleaseRequest? ReleaseRequest { get; set; }
        public PlatformAuthorityResult<PlatformArtifactReleaseReceipt>? ReleaseObservation { get; set; }
        public PlatformAuthorityResult<PlatformArtifactReleaseReceipt>? ReleaseResolutionObservation { get; set; }
        public bool ReleaseInFlight { get; set; }
        public bool ReleaseInvoked { get; set; }
        public bool Released { get; set; }
    }

    private readonly Dictionary<PlatformChildBindingId, ChildBindingRecord> _childBindings = [];
    private readonly Dictionary<PlatformChildBindingId, ChildBindingRecord> _originalChildOwners = [];
    private readonly Dictionary<PlatformGuestMappingId, GuestBindingRecord> _guestBindings = [];
    // Retains the same authoritative lifetime record independently of routing.
    private readonly Dictionary<PlatformGuestMappingId, GuestBindingRecord> _originalGuestOwners = [];
    private readonly Dictionary<PlatformVirtualIoBindingId, VirtualIoBindingRecord> _virtualIoBindings = [];
    private readonly List<VirtualIoBindingRecord> _virtualIoOwners = [];
    private bool IsOriginalVirtualIo(VirtualIoBindingRecord record) => ReferenceEquals(record.OriginalOwner, record) &&
        _virtualIoOwners.Any(x => ReferenceEquals(x, record)) &&
        _virtualIoBindings.TryGetValue(record.Binding.BindingId, out var route) && ReferenceEquals(route, record);
    private static bool HasConsumedVirtualIo(VirtualIoBindingRecord record) => ReferenceEquals(record.OriginalOwner, record) && record.ClosureConsumed;
    private readonly Dictionary<PlatformExecutableArtifactBindingId, ExecutableArtifactRecord> _executableArtifacts = [];
    private readonly Dictionary<PlatformExecutableArtifactBindingId, ExecutableArtifactRecord> _originalExecutableArtifactOwners = [];
    private ulong _nextChildBindingId = 1;
    private ulong _nextGuestBindingId = 1;
    private ulong _nextVirtualIoBindingId = 1;
    private ulong _nextExecutableArtifactBindingId = 1;
    private ulong _nextChildStartOperationId = 1;

    private KernelResult ValidateExecutableInputs(ChildBindingRecord child, GuestBindingRecord guest, MappingRecord parent)
    {
        if (!_childBindings.TryGetValue(child.Binding.BindingId, out var currentChild) || !ReferenceEquals(currentChild, child) ||
            !_guestBindings.TryGetValue(guest.Mapping.MappingId, out var currentGuest) || !ReferenceEquals(currentGuest, guest) ||
            !_mappings.TryGetValue(parent.Mapping.MappingId, out var currentParent) || !ReferenceEquals(currentParent, parent) ||
            guest.Mapping.Child != child.Binding || guest.Mapping.ParentMapping != parent.Mapping || !HasCurrentGuestAdmissionSources(guest))
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact executable dependency owner changed.");
        if (child.TransitionInFlight || child.CloseInFlight)
            return KernelResult.Fail(KernelError.PlatformBindingActive, "Child lifecycle is pending.");
        if (child.State is PlatformChildDomainState.Faulted or PlatformChildDomainState.Closed or PlatformChildDomainState.Draining ||
            guest.Closure != PlatformExternalClosureState.Active || !guest.AdmissionPublished || parent.BackendEpoch != BackendEpoch || _backendEpochExhausted)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Executable dependencies are not active.");
        var lineage = ValidateActiveChildLineage(child);
        if (!lineage.IsSuccess) return lineage;
        return ValidateMapping(parent.Mapping, child.Binding.ParentBinding.Subject);
    }

    internal KernelResult<PlatformExecutableArtifactBinding> BindChildExecutableArtifact(
        PlatformChildBinding childBinding, PlatformGuestMapping guestMapping,
        ReadOnlyMemory<byte> immutablePackage, int maximumExecutionSteps,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<PlatformExecutableArtifactBinding, Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null,
        ulong maximumPrivateDataBytes = 0)
    {
        if (_provider is not IPlatformChildExecutionProvider provider)
            return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.PlatformUnsupported, "Executable artifact provider is unavailable.");
        if (maximumPrivateDataBytes != 0 && _provider is not IPlatformExecutablePrivateDataQuotaProvider)
            return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.PlatformUnsupported, "Private data quota provider unavailable before artifact admission.");
        ChildBindingRecord child;
        GuestBindingRecord guest;
        MappingRecord parent;
        lock (_secureDomainLifecycleGate)
        {
            var resolved = ResolveChild(childBinding);
            if (!resolved.IsSuccess) return KernelResult<PlatformExecutableArtifactBinding>.Fail(resolved.Error, resolved.Message!);
            child = resolved.Value!;
            if (!_guestBindings.TryGetValue(guestMapping.MappingId, out guest!) || guest.Mapping != guestMapping ||
                guestMapping.Child != childBinding || !_mappings.TryGetValue(guestMapping.ParentMapping.MappingId, out parent!))
                return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.StaleGeneration, "Exact executable guest mapping is absent.");
        }
        var request = new PlatformExecutableArtifactRequest(child.ProviderLease, guest.ProviderLease, immutablePackage, maximumExecutionSteps) { MaximumPrivateDataBytes = maximumPrivateDataBytes };
        var valid = PlatformChildExecutionContract.ValidateRequest(request);
        if (!valid.IsSuccess) return FromProviderFailure<PlatformExecutableArtifactBinding>(valid.Status, valid.Message);
        // Capture caller memory outside owners; provider receives the exact private package.
        request = request with { ImmutablePackage = immutablePackage.ToArray() };
        var digest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(request.ImmutablePackage.Span));
        DomainRecord originalRoot = null!;

        PlatformBackendEpoch backendEpoch = default;
        PlatformExecutableArtifactBinding binding = default;
        ExecutableArtifactRecord artifact = null!;
        ExecutableBindInvocationFact bindInvocation = null!;
        Dictionary<ChildBindingRecord, long> artifactLineage = null!;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                var current = ValidateExecutableInputs(child, guest, parent);
                if (!current.IsSuccess) return current;
                var captured = CaptureGuestEffectLineage(child);
                if (!captured.IsSuccess) return KernelResult.Fail(captured.Error, captured.Message!);
                artifactLineage = captured.Value!;
                if (artifactLineage.Keys.Any(source => source.PendingChildEffects != 0))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Artifact ancestor effect is already pending.");
                if (child.PendingChildEffects != 0 || guest.PendingExecutableEffects != 0 ||
                    _executableArtifacts.Values.Any(x => x.Binding.GuestMapping == guestMapping))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Executable guest already has pending or retained artifact admission.");
                if (_nextExecutableArtifactBindingId == ulong.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Executable artifact identity space is exhausted.");
                KernelResult Commit()
                {
                    originalRoot = _domains[childBinding.ParentBinding.BindingId];
                    binding = new(new(_nextExecutableArtifactBindingId++), new(1), childBinding, guestMapping);
                    var admittedSource = new ExecutableArtifactSource(child, guest, parent, originalRoot);
                    bindInvocation = new(request, admittedSource);
                    foreach (var pair in artifactLineage.Where(pair => !ReferenceEquals(pair.Key, child))) bindInvocation.Ancestors.Add(pair.Key, pair.Value);
                    artifact = new(binding, default) { AdmissionSource = admittedSource, BindInvocation = bindInvocation };
                    _originalExecutableArtifactOwners.Add(binding.BindingId, artifact);
                    _executableArtifacts.Add(binding.BindingId, artifact);
                    guest.PendingExecutableEffects++;
                    foreach (var source in artifactLineage.Keys) source.PendingChildEffects++;
                    backendEpoch = BackendEpoch;
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformExecutableArtifactBinding>.Fail(admitted.Error, admitted.Message!);
        try
        {
            KernelResult ValidateSource(PlatformProviderIncarnation source)
            {
                if (!GuestEffectLineageCurrent(artifactLineage))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original artifact lineage changed before effect/publication.");
                if (source.Value == 0 || source != parent.ProviderIncarnation || source != originalRoot.ProviderIncarnation ||
                    BackendEpoch != backendEpoch || parent.BackendEpoch != backendEpoch || originalRoot.BackendEpoch != backendEpoch ||
                    _backendEpochExhausted || !TryGetCanonicalRootRecord(childBinding.ParentBinding.BindingId, out var root) ||
                    !ReferenceEquals(root, originalRoot) || root.Binding != childBinding.ParentBinding ||
                    root.ProviderLease != request.ChildLease.ParentDomainLease || child.ProviderLease != request.ChildLease ||
                    guest.ProviderLease != request.GuestMapping || parent.Mapping != guestMapping.ParentMapping)
                {
                    FaultPinExecutableArtifactDependencies(child, guest, parent);
                    parent.ClosureState = PlatformExternalClosureState.Faulted;
                    QuarantineDomain(originalRoot);
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original executable source changed; dependencies remain pinned.");
                }
                return ValidateExecutableInputs(child, guest, parent);
            }
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.PlatformFaulted, "Executable source unavailable before invocation."); }
            KernelResult FinalAdmission()
            {
                lock (_secureDomainLifecycleGate)
                {
                    var current = ValidateSource(source);
                    if (!current.IsSuccess) return current;
                    return sourceCommitConsumer is null ? KernelResult.Ok() : sourceCommitConsumer(KernelResult.Ok);
                }
            }
            var final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
            if (!final.IsSuccess) return KernelResult<PlatformExecutableArtifactBinding>.Fail(final.Error, final.Message!);
            lock (_secureDomainLifecycleGate)
            {
                if (!ReferenceEquals(artifact.BindInvocation, bindInvocation))
                    return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.PlatformFaulted, "Original Bind invocation owner changed before effect.");
                bindInvocation.Invoked = true;
            }
            var result = provider.BindExecutableArtifact(request);
            lock (_secureDomainLifecycleGate)
            {
                bindInvocation.Observation = result;
                if (result.Status == PlatformAuthorityStatus.NotAccepted) bindInvocation.Invoked = false;
            }
            if (!result.IsSuccess)
            {
                if (result.Status != PlatformAuthorityStatus.NotAccepted) FaultPinExecutableArtifactDependencies(child, guest, parent, artifact);
                return FromProviderFailure<PlatformExecutableArtifactBinding>(result.Status, result.Message);
            }
            var exact = PlatformChildExecutionContract.ValidateAdmissionReceipt(request, result.Value!);
            if (!exact.IsSuccess || !string.Equals(result.Value!.ContentDigest, digest, StringComparison.Ordinal))
            {
                FaultPinExecutableArtifactDependencies(child, guest, parent, artifact);
                return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.PlatformFaulted, "Executable receipt does not identify the exact captured package/dependency tuple.");
            }
            lock (_secureDomainLifecycleGate)
            {
                // Known source-tuple receipt is retained before mutable permission checks.
                artifact.ProviderReceipt = result.Value;
            }
            KernelResult continuity;
            try
            {
                source = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    continuity = ValidateSource(source);
                    if (!continuity.IsSuccess)
                    {
                        FaultPinExecutableArtifactDependencies(child, guest, parent, artifact);
                        continuity = KernelResult.Fail(KernelError.PlatformFaulted, "Executable admission lost exact dependency continuity; receipt retained.");
                    }
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                continuity = KernelResult.Fail(KernelError.PlatformFaulted, "Known executable source unavailable; receipt retained without publication.");
            }
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!continuity.IsSuccess) return continuity;
                    var current = ValidateSource(source);
                    if (!current.IsSuccess) return current;
                    if (BackendEpoch != backendEpoch || !_executableArtifacts.TryGetValue(binding.BindingId, out var saved) ||
                        !ReferenceEquals(saved, artifact))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Exact executable receipt changed before local publication.");
                    return commit();
                }
            }
            var completed = completionConsumer is null ? continuity : completionConsumer(binding, CommitCurrent);
            lock (_secureDomainLifecycleGate) artifact.AdmissionPublished = completed.IsSuccess;
            return completed.IsSuccess ? KernelResult<PlatformExecutableArtifactBinding>.Ok(binding) :
                KernelResult<PlatformExecutableArtifactBinding>.Fail(completed.Error, completed.Message!);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            FaultPinExecutableArtifactDependencies(child, guest, parent, artifact);
            return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.PlatformFaulted, $"Executable admission may have taken effect without exact publication: {exception.Message}");
        }
        finally
        {
            lock (_secureDomainLifecycleGate)
            {
                guest.PendingExecutableEffects--;
                foreach (var source in artifactLineage.Keys) source.PendingChildEffects--;
                if (!bindInvocation.Invoked)
                {
                    _originalExecutableArtifactOwners.Remove(binding.BindingId);
                    if (_executableArtifacts.TryGetValue(binding.BindingId, out var route) && ReferenceEquals(route, artifact)) _executableArtifacts.Remove(binding.BindingId);
                }
            }
        }
    }

    internal KernelResult StartChildExecutableArtifact(PlatformExecutableArtifactBinding binding,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null,
        Func<PlatformExecutionTerminalKind?, Func<Func<KernelResult>, KernelResult>, KernelResult>? terminalCompletionConsumer = null)
    {
        if (_provider is not IPlatformChildExecutionProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Executable artifact provider is unavailable.");
        ExecutableArtifactRecord artifact;
        ChildBindingRecord child;
        GuestBindingRecord guest;
        MappingRecord parent;
        lock (_secureDomainLifecycleGate)
        {
            if (!_executableArtifacts.TryGetValue(binding.BindingId, out artifact!))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Executable artifact binding was not found.");
            if (artifact.Binding.Generation != binding.Generation)
                return KernelResult.Fail(KernelError.StaleGeneration, "Executable artifact binding generation is stale.");
            if (artifact.Binding != binding)
                return KernelResult.Fail(KernelError.PlatformDenied, "Executable artifact belongs to another tuple.");
            var resolved = ResolveChild(binding.Child);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            child = resolved.Value!;
            if (!_guestBindings.TryGetValue(binding.GuestMapping.MappingId, out guest!) || guest.Mapping != binding.GuestMapping ||
                !_mappings.TryGetValue(binding.GuestMapping.ParentMapping.MappingId, out parent!))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Exact executable mapping is unavailable.");
            if (artifact.AdmissionSource is not { } capturedSource || !ReferenceEquals(child, capturedSource.Child) ||
                !ReferenceEquals(guest, capturedSource.Guest) || !ReferenceEquals(parent, capturedSource.Parent) ||
                !TryGetCanonicalRootRecord(binding.Child.ParentBinding.BindingId, out var root) || !ReferenceEquals(root, capturedSource.Root))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original artifact admission owners changed; start refused before reservation or effect.");
        }
        DomainRecord originalRoot = null!;
        Dictionary<ChildBindingRecord, long> startLineage = [];

        PlatformChildExecutionStartRequest request = default;
        PlatformBackendEpoch backendEpoch = default;
        bool startPending = false;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                if (startPending) return KernelResult.Fail(KernelError.PlatformBindingActive, "Original Start admission cannot reenter.");
                var current = ValidateExecutableInputs(child, guest, parent);
                if (!current.IsSuccess) return current;
                if (!_executableArtifacts.TryGetValue(binding.BindingId, out var saved) || !ReferenceEquals(saved, artifact) ||
                    artifact.AdmissionSource is not { } capturedSource || !ReferenceEquals(child, capturedSource.Child) ||
                    !ReferenceEquals(guest, capturedSource.Guest) || !ReferenceEquals(parent, capturedSource.Parent) ||
                    !TryGetCanonicalRootRecord(binding.Child.ParentBinding.BindingId, out var root) || !ReferenceEquals(root, capturedSource.Root))
                    return KernelResult.Fail(KernelError.StaleGeneration, "Original executable artifact owners changed.");
                if (!artifact.AdmissionPublished || artifact.Started || artifact.StartInvocation is { Invoked: true })
                    return KernelResult.Fail(KernelError.PlatformDenied, "Executable artifact start was already invoked or remains uncertain.");
                if (artifact.ReleaseRequest is not null || artifact.Released ||
                    !_originalExecutableArtifactOwners.TryGetValue(binding.BindingId, out var originalArtifact) || !ReferenceEquals(originalArtifact, artifact))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original artifact is released, release-pending or routed to a foreign owner.");
                var capturedLineage = CaptureGuestEffectLineage(child);
                if (!capturedLineage.IsSuccess) return KernelResult.Fail(capturedLineage.Error, capturedLineage.Message!);
                startLineage = capturedLineage.Value!;
                if (startLineage.Keys.Any(source => source.PendingChildEffects != 0))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Original Start ancestor effect is pending.");
                if (artifact.StartInFlight || child.PendingChildEffects != 0 || guest.PendingExecutableEffects != 0)
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Executable artifact was started or an effect is pending.");
                if (_nextChildStartOperationId == ulong.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Executable start identity space is exhausted.");
                request = new(child.ProviderLease, artifact.ProviderReceipt, new(_nextChildStartOperationId), new(1));
                var valid = PlatformChildExecutionContract.ValidateStart(request);
                if (!valid.IsSuccess) return FromProviderFailure(valid.Status, valid.Message);
                KernelResult Commit()
                {
                    if (startPending) return KernelResult.Fail(KernelError.PlatformBindingActive, "Original Start admission is already committed.");
                    originalRoot = _domains[binding.Child.ParentBinding.BindingId];
                    _nextChildStartOperationId++;
                    guest.PendingExecutableEffects++;
                    foreach (var source in startLineage.Keys) source.PendingChildEffects++;
                    artifact.StartInFlight = true;
                    artifact.StartInvocation = new(request, capturedSource);
                    foreach (var pair in startLineage.Where(pair => !ReferenceEquals(pair.Key, child))) artifact.StartInvocation.Ancestors.Add(pair.Key, pair.Value);
                    backendEpoch = BackendEpoch;
                    startPending = true;
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        void EndStart()
        {
            lock (_secureDomainLifecycleGate)
            {
                if (!startPending) return;
                guest.PendingExecutableEffects--;
                foreach (var source in startLineage.Keys) source.PendingChildEffects--;
                artifact.StartInFlight = false;
                startPending = false;
            }
        }
        KernelResult admitted;
        try { admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            EndStart();
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original Start admission callback failed before invocation: " + exception.GetType().Name);
        }
        if (!admitted.IsSuccess || !startPending)
        {
            EndStart();
            return admitted.IsSuccess ? KernelResult.Fail(KernelError.PlatformFaulted, "Original Start admission was not committed.") : admitted;
        }
        try
        {
            KernelResult ValidateSource(PlatformProviderIncarnation source)
            {
                if (!GuestEffectLineageCurrent(startLineage) || source.Value == 0 || source != parent.ProviderIncarnation || source != originalRoot.ProviderIncarnation ||
                    BackendEpoch != backendEpoch || parent.BackendEpoch != backendEpoch || originalRoot.BackendEpoch != backendEpoch ||
                    _backendEpochExhausted || !TryGetCanonicalRootRecord(binding.Child.ParentBinding.BindingId, out var root) ||
                    !ReferenceEquals(root, originalRoot) || root.Binding != binding.Child.ParentBinding ||
                    root.ProviderLease != request.ChildLease.ParentDomainLease || child.ProviderLease != request.ChildLease ||
                    parent.Mapping != binding.GuestMapping.ParentMapping || guest.Mapping != binding.GuestMapping ||
                    !_executableArtifacts.TryGetValue(binding.BindingId, out var saved) || !ReferenceEquals(saved, artifact) ||
                    artifact.Binding != binding || artifact.ProviderReceipt != request.Artifact || !artifact.StartInFlight)
                {
                    FaultPinExecutableArtifactDependencies(child, guest, parent, artifact);
                    QuarantineDomain(originalRoot);
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original executable start source changed; dependencies remain pinned.");
                }
                return ValidateExecutableInputs(child, guest, parent);
            }
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Executable start source unavailable before invocation."); }
            KernelResult FinalAdmission()
            {
                lock (_secureDomainLifecycleGate)
                {
                    var current = ValidateSource(source);
                    if (!current.IsSuccess) return current;
                    return sourceCommitConsumer is null ? KernelResult.Ok() : sourceCommitConsumer(KernelResult.Ok);
                }
            }
            var final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
            if (!final.IsSuccess) return final;
            lock (_secureDomainLifecycleGate) artifact.StartInvocation!.Invoked = true;
            var result = provider.StartExecutableArtifact(request);
            lock (_secureDomainLifecycleGate)
            {
                artifact.StartInvocation!.Observation = result; // Original raw before validation/callbacks.
                if (result.Status == PlatformAuthorityStatus.NotAccepted)
                {
                    artifact.StartInvocation.Invoked = false;
                    artifact.StartInvocation.NoEffectConsumed = true;
                }
            }
            if (!result.IsSuccess)
            {
                if (result.Status != PlatformAuthorityStatus.NotAccepted) FaultPinExecutableArtifactDependencies(child, guest, parent, artifact);
                return FromProviderFailure(result.Status, result.Message);
            }
            var exact = PlatformChildExecutionContract.ValidateExecution(request, result.Value!);
            if (!exact.IsSuccess) { FaultPinExecutableArtifactDependencies(child, guest, parent, artifact); return KernelResult.Fail(KernelError.PlatformFaulted, exact.Message!); }
            lock (_secureDomainLifecycleGate)
            {
                artifact.ExecutionReceipt = result.Value;
                artifact.Started = true; // exact returned provider fact, never local permission or release
            }
            PlatformExecutionTerminalKind? terminalKind = null;
            if (terminalCompletionConsumer is not null && _provider is IPlatformExecutionTerminalProvider terminalProvider)
            {
                var terminal = terminalProvider.ResolveExecutionTerminal(child.ProviderLease);
                lock (_secureDomainLifecycleGate) artifact.TerminalObservation = terminal; // Raw before source callbacks.
                if (!terminal.IsSuccess || !Enum.IsDefined(terminal.Value.Kind) || terminal.Value.Receipt != result.Value)
                {
                    FaultPinExecutableArtifactDependencies(child, guest, parent, artifact);
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original Start terminal outcome is unknown or foreign; execution retained without publication.");
                }
                terminalKind = terminal.Value.Kind;
            }
            KernelResult continuity;
            try
            {
                source = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    continuity = ValidateSource(source);
                    if (!continuity.IsSuccess)
                    {
                        FaultPinExecutableArtifactDependencies(child, guest, parent, artifact);
                        continuity = KernelResult.Fail(KernelError.PlatformFaulted, "Executable receipt retained without current dependency continuity.");
                    }
                    else
                    {
                        child.State = PlatformChildDomainState.Running;
                        artifact.StartInvocation!.PublishedChildRevision = child.LifecycleRevision;
                        startLineage[child] = child.LifecycleRevision; // Only this validated owned publication advances the captured leaf.
                    }
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                continuity = KernelResult.Fail(KernelError.PlatformFaulted, "Known execution source unavailable; receipt retained without publication.");
            }
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!continuity.IsSuccess) return continuity;
                    var current = ValidateSource(source);
                    if (!current.IsSuccess) return current;
                    if (BackendEpoch != backendEpoch || !_executableArtifacts.TryGetValue(binding.BindingId, out var saved) ||
                        !ReferenceEquals(saved, artifact) || !artifact.Started)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Exact execution receipt changed before local state publication.");
                    return commit();
                }
            }
            return terminalCompletionConsumer is not null ? terminalCompletionConsumer(terminalKind, CommitCurrent) :
                completionConsumer is null ? continuity : completionConsumer(CommitCurrent);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            FaultPinExecutableArtifactDependencies(child, guest, parent, artifact);
            return KernelResult.Fail(KernelError.PlatformFaulted, $"Executable start may have taken effect without exact publication: {exception.Message}");
        }
        finally
        {
            EndStart();
        }
    }

    private void FaultPinExecutableArtifactDependencies(ChildBindingRecord child, GuestBindingRecord guest, MappingRecord parent,
        ExecutableArtifactRecord? artifact = null)
    {
        lock (_secureDomainLifecycleGate)
        {
            var fact = artifact?.StartInvocation as ExecutableDependencyFailureFact ?? artifact?.BindInvocation;
            bool ownFailure = fact is { Invoked: true } && SameExecutableSourceRevision(fact) &&
                ReferenceEquals(fact.Source.Child, child) && ReferenceEquals(fact.Source.Guest, guest) && ReferenceEquals(fact.Source.Parent, parent) &&
                (fact.OwnFailurePinned ? HasOwnExecutableStartFailure(artifact!) :
                 child.State == fact.ChildBefore && guest.Closure == fact.GuestBefore && parent.ClosureState == fact.ParentBefore);
            child.State = PlatformChildDomainState.Faulted;
            guest.Closure = PlatformExternalClosureState.Faulted;
            parent.ClosureState = PlatformExternalClosureState.Faulted;
            if (fact is not null)
            {
                fact.OwnFailurePinned = ownFailure;
                fact.ChildRevision = child.LifecycleRevision;
                fact.GuestRevision = guest.LifecycleRevision;
                fact.ParentRevision = parent.LifecycleRevision;
            }
        }
    }

    // Returned invocation receipt for cleanup only, never a published child or admission.
    private sealed record UnpublishedChildCreateReceipt(PlatformDomainBinding Parent,
        PlatformProviderDomainLease AdmittedParent, PlatformChildDomainIntent Intent,
        PlatformBackendEpoch AdmissionEpoch, PlatformProviderChildDomainLease Receipt)
    {
        // Exact returned-tuple cleanup fact, never original request closure or permission.
        public PlatformChildDomainClosureReceipt? ReturnedClosureReceipt { get; set; }
    }
    internal KernelResult<PlatformChildBinding> CreateChildDomain(
        PlatformDomainBinding parent, PlatformDomainIdentity expectedParent,
        PlatformChildDomainIntent intent,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Action<PlatformChildBinding>? cleanupConsumer = null)
    {
        var parentValidation = ValidateDomain(parent, expectedParent);
        if (!parentValidation.IsSuccess)
            return KernelResult<PlatformChildBinding>.Fail(parentValidation.Error, parentValidation.Message!);
        if (!SupportsChildFeature(PlatformFeatureFamily.ChildDomainLifecycle, PlatformChildDomainContract.ContractVersion) ||
            _provider is not IPlatformChildDomainProvider provider)
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformUnsupported, "Executable child-domain admission is unavailable.");

        DomainRecord parentRecord = _domains[parent.BindingId];
        ChildCreateInvocationFact? invocation = null;
        PlatformChildBindingId reservedBindingId = default;
        var requestValidation = PlatformChildDomainContract.ValidateCreateRequest(parentRecord.ProviderLease, intent);
        if (!requestValidation.IsSuccess)
            return FromProviderFailure<PlatformChildBinding>(requestValidation.Status, requestValidation.Message);
        PlatformBackendEpoch backendEpoch = default;
        KernelResult Begin()
        {
        lock (_secureDomainLifecycleGate)
        {
            var fresh = ValidateDomain(parent, expectedParent);
            if (!fresh.IsSuccess) return fresh;
            if (!TryGetCanonicalRootRecord(parent.BindingId, out var exact) || exact != parentRecord)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact child-create parent record changed.");
            if (parentRecord.ParentRevokeMayHaveEffect)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Parent-domain revoke is already in flight.");
            if (parentRecord.AuthorityState != DomainAuthorityState.Active ||
                parentRecord.ChildCreateMayHaveEffect)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Parent domain changed before child creation admission.");
            KernelResult CommitAdmission()
            {
                if (parentRecord.PendingChildCreates == int.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Child-create interlock capacity is exhausted.");
                parentRecord.PendingChildCreates++;
                backendEpoch = BackendEpoch;
                return KernelResult.Ok();
            }
            return sourceCommitConsumer is null ? CommitAdmission() : sourceCommitConsumer(CommitAdmission);
        }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformChildBinding>.Fail(admitted.Error, admitted.Message!);
        try
        {
        KernelResult ValidateSource(PlatformProviderIncarnation source)
        {
            if (source.Value == 0 || source != parentRecord.ProviderIncarnation ||
                BackendEpoch != backendEpoch || parentRecord.BackendEpoch != backendEpoch || _backendEpochExhausted ||
                !TryGetCanonicalRootRecord(parent.BindingId, out var current) || !ReferenceEquals(current, parentRecord) ||
                current.Binding != parent || current.AuthorityState != DomainAuthorityState.Active ||
                current.ParentRevokeMayHaveEffect || current.ParentRevokeInFlight || current.ChildCreateMayHaveEffect)
            {
                QuarantineDomain(parentRecord);
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original child-create root source changed.");
            }
            return KernelResult.Ok();
        }
        void PinChildCreate(UnpublishedChildCreateReceipt? receipt = null)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (receipt is not null) parentRecord.UnpublishedChildCreates.Add(receipt);
                parentRecord.ChildCreateMayHaveEffect = true;
                if (invocation is not null && !parentRecord.AuthorityRevisionExhausted &&
                    (parentRecord.AuthorityState == DomainAuthorityState.Active && parentRecord.AuthorityRevision == invocation.Revision ||
                     invocation.OwnFault && parentRecord.AuthorityState == DomainAuthorityState.Quarantined && parentRecord.AuthorityRevision == invocation.FaultRevision))
                {
                    if (!invocation.OwnFault) QuarantineDomain(parentRecord);
                    invocation.OwnFault = true; invocation.FaultRevision = parentRecord.AuthorityRevision;
                }
                else QuarantineDomain(parentRecord);
            }
        }
        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted, "Child-create source unavailable before invocation."); }
        KernelResult FinalAdmission()
        {
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateSource(source);
                if (!exact.IsSuccess) return exact;
                if (_nextChildBindingId == 0 || _nextChildBindingId == ulong.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Child binding identity space is exhausted.");
                KernelResult Commit()
                {
                    reservedBindingId = new(_nextChildBindingId++);
                    invocation = new(parentRecord, intent, reservedBindingId);
                    parentRecord.ChildCreateInvocations.Add(invocation);
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        var final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
        if (!final.IsSuccess) return KernelResult<PlatformChildBinding>.Fail(final.Error, final.Message!);
        PlatformAuthorityResult<PlatformProviderChildDomainLease> result;
        bool refusedBeforeEffect = false;
        try
        {
            result = provider is IPlatformOriginalChildCreateProvider { SupportsOriginalChildCreateResolution: true } originalProvider
                ? originalProvider.CreateOriginalChild(invocation!.OriginalRequest, out refusedBeforeEffect)
                : provider is IPlatformChildCreateDispositionProvider exactProvider
                ? exactProvider.CreateChildDomain(parentRecord.ProviderLease, intent, out refusedBeforeEffect)
                : provider.CreateChildDomain(parentRecord.ProviderLease, intent);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            PinChildCreate();
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                $"Child creation may have taken effect without a receipt: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
        {
        invocation!.Observation = result;
        if (BackendEpoch != backendEpoch || _backendEpochExhausted ||
            !TryGetCanonicalRootRecord(parent.BindingId, out var currentParent) || currentParent != parentRecord ||
            parentRecord.AuthorityState != DomainAuthorityState.Active)
        {
            PinChildCreate(result.IsSuccess ? new(parent, parentRecord.ProviderLease, intent, backendEpoch, result.Value!) : null);
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                "Backend generation changed during child creation; the parent remains pinned.");
        }
        }
        if (!result.IsSuccess)
        {
            if (refusedBeforeEffect && result.Status == PlatformAuthorityStatus.NotAccepted)
            {
                try { source = CurrentProviderIncarnation(); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { PinChildCreate(); return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted, "Exact create refusal source lost; original fact retained."); }
                lock (_secureDomainLifecycleGate)
                {
                    var fresh = ValidateSource(source);
                    if (!fresh.IsSuccess) { PinChildCreate(); return KernelResult<PlatformChildBinding>.Fail(fresh.Error, fresh.Message!); }
                    parentRecord.ChildCreateInvocations.Remove(invocation!);
                }
                return FromProviderFailure<PlatformChildBinding>(result.Status, result.Message);
            }
            PinChildCreate();
            return FromProviderFailure<PlatformChildBinding>(result.Status, result.Message);
        }

        PlatformProviderChildDomainLease lease = result.Value!;
        var leaseValidation = PlatformChildDomainContract.ValidateLease(parentRecord.ProviderLease, intent, lease);
        if (!leaseValidation.IsSuccess)
        {
            var fact = new UnpublishedChildCreateReceipt(parent, parentRecord.ProviderLease, intent, backendEpoch, lease);
            // Malformed authority does not identify the original invocation effect.
            // Retain that owner before any reentrant cleanup/reset callback.
            PinChildCreate(fact);
            try
            {
                var cleanupSource = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                    if (cleanupSource.Value == 0 || cleanupSource != parentRecord.ProviderIncarnation ||
                        BackendEpoch != backendEpoch || _backendEpochExhausted ||
                        !TryGetCanonicalRootRecord(parent.BindingId, out var cleanupRoot) ||
                        !ReferenceEquals(cleanupRoot, parentRecord) || parentRecord.ParentRevokeMayHaveEffect)
                        return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                            "Malformed child receipt retained; original cleanup source changed.");
                var cleanup = provider.CloseChildDomain(lease);
                if (cleanup.IsSuccess && PlatformChildDomainContract.ValidateClosureReceipt(lease, cleanup.Value!).IsSuccess)
                {
                    lock (_secureDomainLifecycleGate) fact.ReturnedClosureReceipt = cleanup.Value;
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // Original pin and known malformed lease remain; no closure is invented.
            }
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                leaseValidation.Message ?? "Provider returned malformed child-domain authority.");
        }

        PlatformChildBinding binding;
        ChildBindingRecord childRecord;
        lock (_secureDomainLifecycleGate)
        {
            if (BackendEpoch != backendEpoch || _backendEpochExhausted ||
                !TryGetCanonicalRootRecord(parent.BindingId, out var publicationParent) || publicationParent != parentRecord ||
                parentRecord.AuthorityState != DomainAuthorityState.Active ||
                parentRecord.ParentRevokeMayHaveEffect)
            {
                PinChildCreate(new(parent, parentRecord.ProviderLease, intent, backendEpoch, lease));
                return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                    "Parent domain changed before child binding publication.");
            }
            binding = new PlatformChildBinding(reservedBindingId, new(1), parent, expectedParent.Process);
            childRecord = new(binding, lease) { CreationPublished = false, CreateRoot = parentRecord };
            _childBindings.Add(binding.BindingId, childRecord);
            _originalChildOwners.Add(binding.BindingId, childRecord);
            childRecord.CreateObservation = invocation!.Observation;
            parentRecord.ChildCreateInvocations.Remove(invocation!);
        }
        try { cleanupConsumer?.Invoke(binding); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate) childRecord.State = PlatformChildDomainState.Faulted;
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted, "Known child cleanup delivery failed; original lease retained.");
        }
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted, "Known child source unavailable; cleanup owner retained without publication."); }
        KernelResult Publish()
        {
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateSource(source);
                if (!exact.IsSuccess)
                {
                    childRecord.State = PlatformChildDomainState.Faulted;
                    return exact;
                }
                if (!_childBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, childRecord) ||
                    childRecord.State != PlatformChildDomainState.Created || childRecord.TransitionInFlight || childRecord.CloseInFlight)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Known child changed before binding publication.");
                KernelResult Commit() { childRecord.CreationPublished = true; return KernelResult.Ok(); }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        var published = admissionConsumer is null ? Publish() : admissionConsumer(Publish);
        return published.IsSuccess ? KernelResult<PlatformChildBinding>.Ok(binding) :
            KernelResult<PlatformChildBinding>.Fail(published.Error, published.Message!);
        }
        finally { lock (_secureDomainLifecycleGate) parentRecord.PendingChildCreates--; }
    }

    // Cleanup fact only; neither the receipt nor the request publishes child authority.
    private sealed record UnpublishedNestedCreateReceipt(PlatformChildBinding Parent,
        PlatformNestedDomainRequest Request, PlatformBackendEpoch AdmissionEpoch,
        PlatformProviderNestedDomainLease Receipt)
    {
        // Returned child closure only; it cannot establish original nested invocation closure.
        public PlatformChildDomainClosureReceipt? ReturnedClosureReceipt { get; set; }
        public NestedCreateInvocationFact? Invocation { get; init; }
    }

    internal KernelResult<PlatformChildBinding> CreateNestedChildDomain(
        PlatformChildBinding parentChild, ProcessHandle expectedOwner,
        PlatformChildDomainIntent intent,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Action<PlatformChildBinding>? cleanupConsumer = null)
    {
        var parent = ResolveChild(parentChild);
        if (!parent.IsSuccess)
            return KernelResult<PlatformChildBinding>.Fail(parent.Error, parent.Message!);
        if (parentChild.Owner != expectedOwner)
            return KernelResult<PlatformChildBinding>.Fail(KernelError.WrongPlatformDomain,
                "Nested-domain owner does not match the exact live parent child.");
        var feature = _featureManifest.Resolve(PlatformFeatureFamily.NestedDomains);
        if (feature.ContractVersion < PlatformNestedDomainContract.ContractVersion ||
            feature.Availability is not (PlatformFeatureAvailability.RuntimeAdmission or PlatformFeatureAvailability.Executable) ||
            _provider is not IPlatformNestedDomainProvider provider)
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformUnsupported,
                "Nested-domain provider admission is unavailable.");

        var request = new PlatformNestedDomainRequest(parent.Value!.ProviderLease, intent);
        var validation = PlatformNestedDomainContract.ValidateRequest(request, parent.Value.State);
        if (!validation.IsSuccess)
            return FromProviderFailure<PlatformChildBinding>(validation.Status, validation.Message);
        var parentRecord = parent.Value!;
        PlatformChildBindingId reservedBindingId = default;
        DomainRecord originalRoot = null!;
        NestedCreateInvocationFact? invocation = null;
        var originalAncestors = new List<ChildBindingRecord>();
        var sourceRevisions = new Dictionary<ChildBindingRecord, long>();
        long rootRevision = 0;
        PlatformBackendEpoch backendEpoch = default;
        KernelResult Begin()
        {
        lock (_secureDomainLifecycleGate)
        {
            if (parentRecord.TransitionInFlight || parentRecord.CloseInFlight ||
                parentRecord.State == PlatformChildDomainState.Faulted ||
                !HasOriginalNestedHistory(parentRecord) ||
                !PlatformNestedDomainContract.ValidateRequest(request, parentRecord.State).IsSuccess)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Immediate parent child changed before nested admission.");
            var fresh = ResolveChild(parentChild);
            if (!fresh.IsSuccess || !ReferenceEquals(fresh.Value, parentRecord))
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact nested parent changed.");
            var root = ValidateDomain(parentChild.ParentBinding, parentChild.ParentBinding.Subject);
            if (!root.IsSuccess) return root;
            originalRoot = _domains[parentChild.ParentBinding.BindingId];
            if (parentRecord.NestedCreatesIssued == ulong.MaxValue || originalRoot.AuthorityRevisionExhausted)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Nested invocation/source identity capacity is exhausted.");
            originalAncestors.Clear();
            var cursor = parentRecord;
            var visited = new HashSet<PlatformChildBindingId> { parentChild.BindingId };
            while (cursor.ParentChild is { } ancestorBinding)
            {
                if (!visited.Add(ancestorBinding.BindingId))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Nested source lineage is cyclic.");
                var ancestor = ResolveChild(ancestorBinding);
                if (!ancestor.IsSuccess) return KernelResult.Fail(ancestor.Error, ancestor.Message!);
                originalAncestors.Add(ancestor.Value!);
                cursor = ancestor.Value!;
            }
            sourceRevisions.Clear();
            foreach (var owner in originalAncestors.Prepend(parentRecord))
            {
                if (owner.LifecycleRevisionExhausted || owner.PendingChildEffects == int.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Nested lineage source/interlock capacity is exhausted.");
                sourceRevisions.Add(owner, owner.LifecycleRevision);
            }
            rootRevision = originalRoot.AuthorityRevision;
            KernelResult Commit()
            {
                if (parentRecord.PendingChildEffects == int.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Nested child interlock capacity is exhausted.");
                foreach (var owner in sourceRevisions.Keys) owner.PendingChildEffects++;
                backendEpoch = BackendEpoch;
                return KernelResult.Ok();
            }
            return CommitActiveChildDomainConsumer(parentChild, () => sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit));
        }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<PlatformChildBinding>.Fail(admitted.Error, admitted.Message!);
        try
        {
        KernelResult ValidateSource(PlatformProviderIncarnation source)
        {
            if (source.Value == 0 || source != originalRoot.ProviderIncarnation ||
                BackendEpoch != backendEpoch || originalRoot.BackendEpoch != backendEpoch || _backendEpochExhausted ||
                originalRoot.AuthorityRevision != rootRevision || originalRoot.AuthorityRevisionExhausted ||
                !TryGetCanonicalRootRecord(parentChild.ParentBinding.BindingId, out var root) || !ReferenceEquals(root, originalRoot) ||
                root.Binding != parentChild.ParentBinding || root.ParentRevokeMayHaveEffect || root.ParentRevokeInFlight)
            {
                QuarantineDomain(originalRoot);
                parentRecord.State = PlatformChildDomainState.Faulted;
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original nested root source changed.");
            }
            if (!_childBindings.TryGetValue(parentChild.BindingId, out var current) || !ReferenceEquals(current, parentRecord) ||
                current.Binding != parentChild || current.ProviderLease != request.ParentChildLease ||
                current.ProviderLease.ParentDomainLease != root.ProviderLease ||
                originalAncestors.Any(ancestor => !_childBindings.TryGetValue(ancestor.Binding.BindingId, out var exact) ||
                    !ReferenceEquals(exact, ancestor) || ancestor.Binding.ParentBinding != root.Binding ||
                    ancestor.Binding.Owner != expectedOwner || ancestor.ProviderLease.ParentDomainLease != root.ProviderLease))
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact original nested parent/ancestor tuple changed.");
            if (!current.CreationPublished || current.TransitionInFlight || current.CloseInFlight ||
                !HasOriginalNestedHistory(current) ||
                sourceRevisions.Any(pair => pair.Key.LifecycleRevisionExhausted || pair.Key.LifecycleRevision != pair.Value) ||
                current.NestedCreateMayHaveEffect || !PlatformNestedDomainContract.ValidateRequest(request, current.State).IsSuccess)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Nested parent admission changed after source observation.");
            return ValidateActiveChildLineage(parentRecord);
        }
        void PinNestedCreate(UnpublishedNestedCreateReceipt? receipt = null)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (receipt is not null) parentRecord.UnpublishedNestedCreates.Add(receipt with { Invocation = invocation });
                parentRecord.NestedCreateMayHaveEffect = true;
                bool ownFault = invocation is not null && !parentRecord.LifecycleRevisionExhausted &&
                    parentRecord.State == invocation.AdmittedState && parentRecord.LifecycleRevision == invocation.AdmittedRevision;
                parentRecord.State = PlatformChildDomainState.Faulted;
                if (ownFault && !parentRecord.LifecycleRevisionExhausted) invocation!.OwnFaultRevision = parentRecord.LifecycleRevision;
            }
        }
        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted, "Nested source unavailable before invocation."); }
        KernelResult FinalAdmission()
        {
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateSource(source);
                if (!exact.IsSuccess) return exact;
                if (_nextChildBindingId == 0 || _nextChildBindingId == ulong.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Child binding identity space is exhausted.");
                KernelResult Commit()
                {
                    reservedBindingId = new(_nextChildBindingId++);
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        var final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
        if (!final.IsSuccess) return KernelResult<PlatformChildBinding>.Fail(final.Error, final.Message!);
        PlatformAuthorityResult<PlatformProviderNestedDomainLease> result;
        try
        {
            lock (_secureDomainLifecycleGate)
            {
                var fresh = ValidateSource(source);
                if (!fresh.IsSuccess) return KernelResult<PlatformChildBinding>.Fail(fresh.Error, fresh.Message!);
                if (parentRecord.NestedCreatesIssued == ulong.MaxValue)
                    return KernelResult<PlatformChildBinding>.Fail(KernelError.CapacityExhausted, "Nested invocation capacity changed before effect.");
                invocation = new(parentRecord, request);
                parentRecord.NestedCreateInvocations.Add(invocation);
                parentRecord.NestedCreatesIssued++;
            }
            if (provider is IPlatformOriginalNestedDomainProvider { SupportsOriginalNestedDomains: true } originalProvider)
            {
                try { result = originalProvider.CreateOriginalNestedDomain(invocation!.OriginalRequest); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { result = originalProvider.ResolveOriginalNestedDomain(invocation!.OriginalRequest); }
            }
            else result = provider.CreateNestedChildDomain(request);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            PinNestedCreate();
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                $"Nested child creation may have taken effect without a receipt: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
        {
        invocation!.Observation = result;
        if (BackendEpoch != backendEpoch || parentRecord.State == PlatformChildDomainState.Faulted ||
            !HasCurrentChildCreateSources(parentRecord) || !HasOriginalNestedHistory(parentRecord) ||
            !parentRecord.NestedCreateInvocations.Contains(invocation) || !ReferenceEquals(invocation.OriginalOwner, invocation))
        {
            PinNestedCreate(result.IsSuccess ? new(parentChild, request, backendEpoch, result.Value!) : null);
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                "Backend generation changed during nested child creation.");
        }
        }
        if (!result.IsSuccess)
        {
            if (result.Status == PlatformAuthorityStatus.NotAccepted &&
                provider is IPlatformOriginalNestedDomainProvider { SupportsOriginalNestedDomains: true } originalProvider)
            {
                try
                {
                    var resolved = originalProvider.ResolveOriginalNestedDomain(invocation!.OriginalRequest);
                    var currentSource = CurrentProviderIncarnation();
                    lock (_secureDomainLifecycleGate)
                    {
                        if (resolved.Status == PlatformAuthorityStatus.NotAccepted && currentSource == originalRoot.ProviderIncarnation &&
                            BackendEpoch == backendEpoch && !_backendEpochExhausted && HasCurrentChildCreateSources(parentRecord) &&
                            HasOriginalNestedHistory(parentRecord) && parentRecord.NestedCreateInvocations.Contains(invocation) &&
                            ReferenceEquals(invocation.OriginalOwner, invocation))
                        {
                            parentRecord.NestedCreateInvocations.Remove(invocation); parentRecord.NestedCreatesConsumed++;
                            return FromProviderFailure<PlatformChildBinding>(result.Status, result.Message);
                        }
                    }
                }
                catch (Exception exception) when (exception is not StackOverflowException) { /* Absence/unknown is not no-effect. */ }
            }
            PinNestedCreate();
            return FromProviderFailure<PlatformChildBinding>(result.Status, result.Message);
        }
        var nestedLease = result.Value!;
        var lease = nestedLease.ChildLease;
        var exact = PlatformNestedDomainContract.ValidateLease(request, nestedLease);
        if (!exact.IsSuccess)
        {
            var fact = new UnpublishedNestedCreateReceipt(parentChild, request, backendEpoch, nestedLease);
            // A malformed immediate-parent/request tuple leaves the original effect unknown.
            // Retain that parent before any returned-object compensation callback/reset.
            PinNestedCreate(fact);
            // A returned lease is not continuity of the invocation. Closing it could
            // destroy a foreign equal-tuple lifetime. Only original-request cleanup
            // may discharge this pin; V1 does not provide that proof.
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                exact.Message ?? "Provider returned malformed nested-domain authority.");
        }
        PlatformChildBinding binding;
        ChildBindingRecord childRecord;
        lock (_secureDomainLifecycleGate)
        {
            var publicationLineage = ValidateActiveChildLineage(parentRecord);
            if (BackendEpoch != backendEpoch || parentRecord.State == PlatformChildDomainState.Faulted ||
                !publicationLineage.IsSuccess)
            {
                PinNestedCreate(new(parentChild, request, backendEpoch, nestedLease));
                return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                    "Immediate parent child changed before nested publication.");
            }
            binding = new PlatformChildBinding(reservedBindingId, new(1),
                parentChild.ParentBinding, expectedOwner);
            childRecord = new(binding, lease, parentChild) { CreationPublished = false, CreateRoot = originalRoot, CreateParentChild = parentRecord };
            _childBindings.Add(binding.BindingId, childRecord);
            _originalChildOwners.Add(binding.BindingId, childRecord);
            childRecord.NestedCreateObservation = invocation!.Observation;
            parentRecord.NestedCreateInvocations.Remove(invocation);
            parentRecord.NestedCreatesConsumed++;
        }
        try { cleanupConsumer?.Invoke(binding); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate) childRecord.State = PlatformChildDomainState.Faulted;
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted, "Known nested cleanup delivery failed; original lease retained.");
        }
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted, "Known nested source unavailable; cleanup owner retained without publication."); }
        KernelResult Publish()
        {
            lock (_secureDomainLifecycleGate)
            {
                var exactSource = ValidateSource(source);
                if (!exactSource.IsSuccess)
                {
                    // Lineage/admission refusal alone does not lose this known child effect.
                    // Original-source failure faults the parent and retains the containment pin.
                    if (parentRecord.State == PlatformChildDomainState.Faulted)
                        childRecord.State = PlatformChildDomainState.Faulted;
                    return exactSource;
                }
                if (!_childBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, childRecord) ||
                    childRecord.State != PlatformChildDomainState.Created || childRecord.TransitionInFlight || childRecord.CloseInFlight)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Known nested child changed before binding publication.");
                KernelResult Commit() { childRecord.CreationPublished = true; return KernelResult.Ok(); }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        var published = admissionConsumer is null ? Publish() : admissionConsumer(Publish);
        return published.IsSuccess ? KernelResult<PlatformChildBinding>.Ok(binding) :
            KernelResult<PlatformChildBinding>.Fail(published.Error, published.Message!);
        }
        finally { lock (_secureDomainLifecycleGate) foreach (var owner in sourceRevisions.Keys) owner.PendingChildEffects--; }
    }

    // Trusted pure consumer mutation only; provider/caller callbacks stay outside this gate.
    internal KernelResult CommitActiveChildDomainConsumer(PlatformChildBinding binding, Func<KernelResult> commit)
    {
        lock (_secureDomainLifecycleGate)
        {
            var child = ResolveChild(binding);
            if (!child.IsSuccess) return KernelResult.Fail(child.Error, child.Message!);
            var lineage = ValidateActiveChildLineage(child.Value!);
            if (!lineage.IsSuccess) return lineage;
            return commit();
        }
    }

    // Pure existing-owner validation. Own in-flight flag is checked by each caller;
    // ancestor lifecycle/closure must be active at every effect boundary.
    // Original lineage identity for admission/cleanup; lifecycle permission is checked independently.
    private static bool HasOriginalNestedHistory(ChildBindingRecord owner) =>
        owner.NestedCreatesConsumed <= owner.NestedCreatesIssued &&
        owner.NestedCreatesIssued - owner.NestedCreatesConsumed == (ulong)owner.NestedCreateInvocations.Count &&
        owner.NestedCreateInvocations.All(f => ReferenceEquals(f.OriginalOwner, f) && ReferenceEquals(f.Source, owner) &&
            f.Request.ParentChildLease == owner.ProviderLease);

    private bool HasCurrentChildCreateSources(ChildBindingRecord record)
    {
        if (!_originalChildOwners.TryGetValue(record.Binding.BindingId, out var original) || !ReferenceEquals(original, record))
            return false;
        if (!_domains.TryGetValue(record.Binding.ParentBinding.BindingId, out var root) || !ReferenceEquals(root, record.CreateRoot) ||
            root.Binding != record.Binding.ParentBinding || root.ProviderLease != record.ProviderLease.ParentDomainLease)
            return false;
        // Active/unfinished sources need the canonical original root. Lawfully closed
        // original parent/child facts can finish local cleanup after subject removal.
        if (!TryGetCanonicalRootRecord(record.Binding.ParentBinding.BindingId, out var canonical) &&
            !(record.State == PlatformChildDomainState.Closed && HasValidatedChildClosureFact(record) &&
              root.AuthorityState == DomainAuthorityState.Closed && root.ProviderClosureConfirmed &&
              root.RootRevokeObservation is { IsSuccess: true } && !HasRetainedRootInvocation(root)))
            return false;
        var cursor = record;
        var visited = new HashSet<PlatformChildBindingId> { record.Binding.BindingId };
        while (cursor.ParentChild is { } parent)
        {
            if (cursor.CreateParentChild is not { } admitted || !visited.Add(parent.BindingId) ||
                !_childBindings.TryGetValue(parent.BindingId, out var current) || !ReferenceEquals(current, admitted) ||
                !_originalChildOwners.TryGetValue(parent.BindingId, out var originalAncestor) || !ReferenceEquals(originalAncestor, admitted) ||
                admitted.Binding != parent || admitted.Binding.Owner != record.Binding.Owner ||
                admitted.Binding.ParentBinding != root.Binding || !ReferenceEquals(admitted.CreateRoot, root) ||
                admitted.ProviderLease.ParentDomainLease != root.ProviderLease)
                return false;
            cursor = admitted;
        }
        return cursor.CreateParentChild is null;
    }

    private KernelResult ValidateActiveChildLineage(ChildBindingRecord record)
    {
        if (record.EventCleanupOnly || record.NestedCleanupOnly)
            return KernelResult.Fail(KernelError.PlatformDenied, "Original event cleanup continuation grants no new child effects.");
        if (!HasCurrentChildCreateSources(record) || !_childBindings.TryGetValue(record.Binding.BindingId, out var current) || !ReferenceEquals(current, record))
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact child owner changed.");
        if (record.CloseInFlight)
            return KernelResult.Fail(KernelError.PlatformBindingActive, "Child closure is pending.");
        var root = ValidateDomain(record.Binding.ParentBinding, record.Binding.ParentBinding.Subject);
        if (!root.IsSuccess) return root;
        var ancestor = record;
        var visited = new HashSet<PlatformChildBindingId> { record.Binding.BindingId };
        while (ancestor.ParentChild is { } parentChild)
        {
            if (!visited.Add(parentChild.BindingId))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Child lineage is cyclic.");
            var exact = ResolveChild(parentChild);
            if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
            if (exact.Value!.CloseInFlight || exact.Value.Binding.Owner != record.Binding.Owner ||
                exact.Value.Binding.ParentBinding != record.Binding.ParentBinding)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Exact child lineage is unavailable.");
            ancestor = exact.Value;
        }
        return KernelResult.Ok();
    }

    internal KernelResult TransitionChildDomain(PlatformChildBinding binding, PlatformChildDomainTransition transition,
        bool allowCommittedDrain = false,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
    {
        if (_provider is not IPlatformChildDomainProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Child-domain provider is unavailable.");
        if (transition == PlatformChildDomainTransition.BeginDrain)
        {
            var nestedCleanup = ContainOriginalNestedForDrain(binding);
            if (!nestedCleanup.IsSuccess) return nestedCleanup;
            var eventCleanup = ContainOriginalChildEventsForDrain(binding);
            if (!eventCleanup.IsSuccess) return eventCleanup;
            var guestCleanup = ResolveOriginalGuestMapsForDrain(binding);
            if (!guestCleanup.IsSuccess) return guestCleanup;
        }
        ChildBindingRecord? retainedDrain = null;
        lock (_secureDomainLifecycleGate)
        {
            if (allowCommittedDrain && transition == PlatformChildDomainTransition.BeginDrain &&
                _childBindings.TryGetValue(binding.BindingId, out var candidate) && candidate.Binding == binding &&
                candidate.State == PlatformChildDomainState.Faulted &&
                TryRetainedChildCleanupState(candidate, out _) &&
                _originalChildOwners.TryGetValue(binding.BindingId, out var original) && ReferenceEquals(original, candidate))
                retainedDrain = candidate;
        }
        if (retainedDrain is not null)
        {
            var drainAlreadyObserved = false;
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Known original child drain source unavailable; effect is not repeated."); }
            KernelResult RestoreDrain()
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!_childBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, retainedDrain) ||
                        current.Binding != binding || current.State != PlatformChildDomainState.Faulted ||
                        !TryRetainedChildCleanupState(current, out var retainedState) || !HasCurrentChildCreateSources(current) ||
                        current.CreateRoot is not { } root || root.AuthorityState == DomainAuthorityState.Closed || root.ParentRevokeInFlight ||
                        _backendEpochExhausted || current.TransitionEpoch != BackendEpoch || root.BackendEpoch != BackendEpoch ||
                        source.Value == 0 || source != root.ProviderIncarnation || current.TransitionInFlight || current.CloseInFlight ||
                        current.PendingChildEffects != 0)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Exact original child drain source changed; cleanup remains pinned.");
                    var identity = ValidateDomainIdentity(binding.ParentBinding, binding.ParentBinding.Subject);
                    if (!identity.IsSuccess) return identity;
                    KernelResult Commit()
                    {
                        // With a retained closure this is only the local cleanup path.
                        // Close consumes the exact fact and never invokes the provider again.
                        current.State = retainedState;
                        drainAlreadyObserved = retainedState == PlatformChildDomainState.Draining;
                        return KernelResult.Ok();
                    }
                    return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
                }
            }
            var restored = admissionConsumer is null ? RestoreDrain() : admissionConsumer(RestoreDrain);
            if (!restored.IsSuccess || drainAlreadyObserved) return restored;
            // Known non-drain outcomes restore only their exact cleanup state. The drain below
            // performs fresh admission/source checks and is a new invocation.
        }
        ChildBindingRecord record = null!;
        DomainRecord originalRoot = null!;
        PlatformBackendEpoch backendEpoch = default;
        var committedDrain = false;
        PlatformChildDomainState admittedState = default;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                if (allowCommittedDrain && transition == PlatformChildDomainTransition.BeginDrain &&
                    _childBindings.TryGetValue(binding.BindingId, out var committed) && committed.Binding == binding &&
                    _originalChildOwners.TryGetValue(binding.BindingId, out var original) && ReferenceEquals(original, committed) &&
                    committed.State is PlatformChildDomainState.Draining or PlatformChildDomainState.Closed)
                {
                    committedDrain = true;
                    return KernelResult.Ok();
                }
                var resolved = ResolveChild(binding, allowDraining: transition == PlatformChildDomainTransition.BeginDrain,
                    allowUnpublished: transition == PlatformChildDomainTransition.BeginDrain);
                if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
                record = resolved.Value!;
                if (!HasCurrentChildCreateSources(record) || !TryGetCanonicalRootRecord(binding.ParentBinding.BindingId, out originalRoot!) ||
                    originalRoot.Binding != binding.ParentBinding || record.ProviderLease.ParentDomainLease != originalRoot.ProviderLease)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original child root source is unavailable.");
                if (record.TransitionInFlight || record.CloseInFlight || record.PendingChildEffects != 0)
                    return KernelResult.Fail(KernelError.PlatformBindingActive,
                        "Exact child already has an external effect in flight.");
                if (!LegalTransition(record.State, transition))
                    return KernelResult.Fail(KernelError.PlatformDenied, "Child-domain lifecycle transition is not legal from the current state.");
                // Drain is the real cleanup path after local source revocation/quarantine.
                if (transition != PlatformChildDomainTransition.BeginDrain)
                {
                    var lineage = ValidateActiveChildLineage(record);
                    if (!lineage.IsSuccess) return lineage;
                }
                KernelResult Commit()
                {
                    admittedState = record.State;
                    record.TransitionInFlight = true;
                    backendEpoch = BackendEpoch;
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess || committedDrain) return admitted;
        try
        {
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Child transition source unavailable before invocation."); }
            KernelResult FinalAdmission()
            {
                lock (_secureDomainLifecycleGate)
                {
                    var exact = ValidateSource(source);
                    if (!exact.IsSuccess) return exact;
                    if (record.State != admittedState)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Child transition state changed before invocation.");
                    if (transition != PlatformChildDomainTransition.BeginDrain)
                    {
                        var lineage = ValidateActiveChildLineage(record);
                        if (!lineage.IsSuccess) return lineage;
                    }
                    KernelResult RecordIntent()
                    {
                        record.TransitionIntent = transition;
                        record.TransitionObservation = null;
                        record.TransitionPublicationRefused = false;
                        record.TransitionEpoch = backendEpoch;
                        record.TransitionAdmittedState = admittedState;
                        return KernelResult.Ok();
                    }
                    return sourceCommitConsumer is null ? RecordIntent() : sourceCommitConsumer(RecordIntent);
                }
            }
            var final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
            if (!final.IsSuccess) return final;
            var result = provider.TransitionChildDomain(record.ProviderLease, transition);
            lock (_secureDomainLifecycleGate) record.TransitionObservation = result;
            if (result.Status == PlatformAuthorityStatus.NotAccepted)
            {
                // No new transition fact. Retain the existing state only after a
                // fresh external source query and exact local owner/lineage checks.
                try { source = CurrentProviderIncarnation(); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { return KernelResult.Fail(KernelError.PlatformFaulted, "Refused child transition source unavailable; original owner retained."); }
                lock (_secureDomainLifecycleGate)
                {
                    var exact = ValidateSource(source);
                    if (!exact.IsSuccess) return exact;
                    if (record.State != admittedState)
                    {
                        record.State = PlatformChildDomainState.Faulted;
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Child state changed during refused transition; publication prohibited.");
                    }
                    if (transition != PlatformChildDomainTransition.BeginDrain)
                    {
                        var lineage = ValidateActiveChildLineage(record);
                        if (!lineage.IsSuccess) return lineage;
                    }
                    return FromProviderFailure(result.Status, result.Message);
                }
            }
            lock (_secureDomainLifecycleGate)
            {
                if (!_childBindings.TryGetValue(binding.BindingId, out var current) ||
                    !ReferenceEquals(current, record) || current.Binding != binding ||
                    BackendEpoch != backendEpoch || _backendEpochExhausted || record.State == PlatformChildDomainState.Faulted)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    record.TransitionPublicationRefused = true;
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Backend or exact child changed during transition; late state publication is prohibited.");
                }
                if (!result.IsSuccess)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return FromProviderFailure(result.Status, result.Message);
                }
                // This owner records the known returned provider transition even when
                // local source permission has since been revoked. It is cleanup evidence only.
                record.State = transition switch
                {
                    PlatformChildDomainTransition.Start or PlatformChildDomainTransition.Resume => PlatformChildDomainState.Running,
                    PlatformChildDomainTransition.Park => PlatformChildDomainState.Parked,
                    PlatformChildDomainTransition.BeginDrain => PlatformChildDomainState.Draining,
                    _ => PlatformChildDomainState.Faulted,
                };
            }
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // Known returned transition state is retained for real drain/close;
                // unavailable source cannot authorize local publication.
                return KernelResult.Fail(KernelError.PlatformFaulted, "Known child transition source unavailable; state publication refused.");
            }
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateSource(source);
                if (!exact.IsSuccess) return exact;
            }
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!_childBindings.TryGetValue(binding.BindingId, out var current) ||
                        !ReferenceEquals(current, record) || current.Binding != binding ||
                        BackendEpoch != backendEpoch || _backendEpochExhausted ||
                        record.State is PlatformChildDomainState.Faulted or PlatformChildDomainState.Closed ||
                        record.CloseInFlight)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Exact child receipt cannot admit local publication.");
                    var lineage = ValidateActiveChildLineage(record);
                    return lineage.IsSuccess ? commit() : lineage;
                }
            }
            return completionConsumer is null
                ? transition == PlatformChildDomainTransition.BeginDrain ? KernelResult.Ok() : CommitCurrent(() => KernelResult.Ok())
                : completionConsumer(CommitCurrent);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate) record.State = PlatformChildDomainState.Faulted;
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Child transition may have taken effect without a receipt: {exception.Message}");
        }
        finally
        {
            lock (_secureDomainLifecycleGate) record.TransitionInFlight = false;
        }
        KernelResult ValidateSource(PlatformProviderIncarnation source)
        {
            if (!HasCurrentChildCreateSources(record) || source.Value == 0 || source != originalRoot.ProviderIncarnation ||
                BackendEpoch != backendEpoch || originalRoot.BackendEpoch != backendEpoch || _backendEpochExhausted ||
                !TryGetCanonicalRootRecord(binding.ParentBinding.BindingId, out var root) || !ReferenceEquals(root, originalRoot) ||
                root.Binding != binding.ParentBinding || root.AuthorityState == DomainAuthorityState.Closed || root.ParentRevokeInFlight ||
                !_childBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, record) ||
                current.Binding != binding || record.ProviderLease.ParentDomainLease != root.ProviderLease ||
                record.State is PlatformChildDomainState.Faulted or PlatformChildDomainState.Closed || record.CloseInFlight)
            {
                record.State = PlatformChildDomainState.Faulted;
                record.TransitionPublicationRefused = record.TransitionObservation is not null;
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original child/root transition source changed; reclaim remains pinned.");
            }
            return ValidateDomainIdentity(binding.ParentBinding, binding.ParentBinding.Subject);
        }
    }

    internal KernelResult CloseChildDomain(PlatformChildBinding binding, bool allowCommittedClosure = false,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? cleanupCompletionConsumer = null)
    {
        if (cleanupCompletionConsumer is not null)
        {
            ChildBindingRecord closed;
            lock (_secureDomainLifecycleGate)
            {
                if (!allowCommittedClosure || !_childBindings.TryGetValue(binding.BindingId, out closed!) ||
                    closed.Binding != binding || closed.State != PlatformChildDomainState.Closed || closed.LastClosureReceipt is null)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Exact committed child closure is unavailable to local cleanup.");
            }
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Child cleanup source unavailable; exact closure remains retained."); }
            KernelResult Commit(Func<KernelResult> localCommit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    var fact = closed.LastClosureReceipt;
                    if (!_childBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, closed) ||
                        current.Binding != binding || current.State != PlatformChildDomainState.Closed ||
                        current.CloseInFlight || current.TransitionInFlight || current.PendingChildEffects != 0 ||
                        HasUnclosedGuestDependency(current) || !HasCurrentChildCreateSources(current) || current.CreateRoot is not { } root ||
                        _backendEpochExhausted || root.BackendEpoch != BackendEpoch || source.Value == 0 || source != root.ProviderIncarnation ||
                        fact is null || fact.AdmittedLease != current.ProviderLease || fact.AdmissionEpoch != BackendEpoch ||
                        !PlatformChildDomainContract.ValidateClosureReceipt(current.ProviderLease, fact.Receipt).IsSuccess)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Exact child closure source changed before local cleanup commit.");
                    return localCommit(); // Trusted local owner mutation; no provider callback.
                }
            }
            return cleanupCompletionConsumer(Commit);
        }
        lock (_secureDomainLifecycleGate)
        {
            // Only the existing owner state committed by exact closure receipt
            // makes retry idempotent; absence/fault/terminal response cannot.
            if (allowCommittedClosure && _childBindings.TryGetValue(binding.BindingId, out var committed) &&
                _originalChildOwners.TryGetValue(binding.BindingId, out var original) && ReferenceEquals(original, committed) &&
                committed.Binding == binding && committed.State == PlatformChildDomainState.Closed &&
                !committed.CloseInFlight && !committed.TransitionInFlight && committed.PendingChildEffects == 0 &&
                !HasUnclosedGuestDependency(committed) && HasValidatedChildClosureFact(committed))
                return KernelResult.Ok();
        }
        var resolved = ResolveChild(binding, allowDraining: true, allowUnpublished: true, allowOwnCloseFailure: true);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        ChildBindingRecord record = resolved.Value!;
        if (_provider is not IPlatformChildDomainProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Child-domain provider is unavailable.");
        var eventCleanup = ContainOriginalChildEvents(record);
        if (!eventCleanup.IsSuccess) return eventCleanup;
        bool supportsOriginalCloseResolution;
        try { supportsOriginalCloseResolution = _provider is IPlatformOriginalChildCloseProvider { SupportsOriginalChildCloseResolution: true }; }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult.Fail(KernelError.PlatformFaulted, "Original child Close resolution source unavailable."); }
        DomainRecord root;
        var ancestors = new List<ChildBindingRecord>();
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            if (record.TransitionInFlight || record.CloseInFlight || record.PendingChildEffects != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Child lifecycle or external effect is already in flight.");
            if (!HasChildCloseStateContinuity(record))
                return KernelResult.Fail(KernelError.PlatformDenied, "Child closure requires an explicit draining state.");
            if (record.NestedCreateMayHaveEffect || record.NestedCreateInvocations.Count != 0 || !HasOriginalNestedHistory(record))
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Nested child creation may have taken effect without exact closure.");
            if (_childBindings.Values.Concat(_originalChildOwners.Values).Any(x => x.ParentChild == binding &&
                (x.State != PlatformChildDomainState.Closed || !HasValidatedChildClosureFact(x))))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Nested child domains must reach exact terminal closure before their parent child closes.");
            if (HasUnclosedGuestDependency(record) ||
                _virtualIoOwners.Any(x => x.Binding.Child == binding && !HasConsumedVirtualIo(x)) ||
                DeviceDependencyRecordsLocked().Any(device => HasOriginalIoInvocationPins(device, record)))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Child mappings and virtual-I/O bindings must close before child closure.");
            if (!HasCurrentChildCreateSources(record) || !TryGetCanonicalRootRecord(binding.ParentBinding.BindingId, out root!) ||
                root.Binding != binding.ParentBinding || record.ProviderLease.ParentDomainLease != root.ProviderLease)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original root cleanup owner is unavailable.");
            var cursor = record;
            var visited = new HashSet<PlatformChildBindingId> { binding.BindingId };
            while (cursor.ParentChild is { } parent)
            {
                if (!visited.Add(parent.BindingId) ||
                    !_childBindings.TryGetValue(parent.BindingId, out var ancestor) || ancestor.Binding != parent ||
                    ancestor.Binding.ParentBinding != root.Binding || ancestor.Binding.Owner != binding.Owner)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Exact original child cleanup lineage is unavailable.");
                ancestors.Add(ancestor);
                cursor = ancestor;
            }
            record.CloseInFlight = true;
            backendEpoch = root.BackendEpoch;
        }
        try
        {
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Child cleanup source unavailable; original state and receipt retained."); }
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateChildCloseSourceLocked(record, root, ancestors, backendEpoch, source);
                if (!exact.IsSuccess) return exact;
            }
            if (record.LastClosureReceipt is null)
            {
                PlatformAuthorityResult<PlatformChildDomainClosureReceipt> result;
                // A previous drain receipt cannot establish containment of this close.
                // Retain the attempt on the captured owner even if routing changes.
                OriginalChildCloseInvocation invocation;
                bool resolveClose;
                lock (_secureDomainLifecycleGate)
                {
                    if (record.CloseInvocationAttempted && record.OriginalCloseInvocation is null)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original Close request history unavailable; repeat prohibited.");
                    if (record.OriginalCloseInvocation is null || record.OriginalCloseInvocation.NoEffectConsumed)
                        record.OriginalCloseInvocation = new(record, root, ancestors, backendEpoch, supportsOriginalCloseResolution);
                    invocation = record.OriginalCloseInvocation;
                    resolveClose = invocation.Invoked;
                    if (resolveClose && _provider is not IPlatformOriginalChildCloseProvider)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original child Close resolver unavailable; no effect repeat.");
                    invocation.Invoked = true;
                    record.CloseInvocationAttempted = true;
                }
                try
                {
                    result = resolveClose ? ((IPlatformOriginalChildCloseProvider)_provider).ResolveOriginalChildClose(record.ProviderLease)
                        : provider.CloseChildDomain(record.ProviderLease);
                    lock (_secureDomainLifecycleGate)
                    { if (resolveClose) invocation.ResolutionObservation = result; else invocation.Observation = result; }
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    lock (_secureDomainLifecycleGate) PinOwnChildCloseFailure(record);
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Child closure outcome unknown; original owner remains pinned.");
                }
                if (result.Status == PlatformAuthorityStatus.NotAccepted)
                {
                    if (_provider is IPlatformOriginalChildCloseProvider exactResolver && !resolveClose)
                    {
                        result = exactResolver.ResolveOriginalChildClose(record.ProviderLease);
                        lock (_secureDomainLifecycleGate) invocation.ResolutionObservation = result;
                    }
                    if (result.Status == PlatformAuthorityStatus.NotAccepted)
                    {
                    // This invocation produced no closure fact. A fresh source check
                    // permits retaining Draining only for the exact original owner.
                    // Query outside the lifecycle lock: source callbacks may reenter/reset.
                    try { source = CurrentProviderIncarnation(); }
                    catch (Exception exception) when (exception is not StackOverflowException)
                    { return KernelResult.Fail(KernelError.PlatformFaulted, "Refused child closure source unavailable; original owner remains pinned."); }
                    lock (_secureDomainLifecycleGate)
                    {
                        var exact = ValidateChildCloseSourceLocked(record, root, ancestors, backendEpoch, source);
                        if (exact.IsSuccess && _provider is IPlatformOriginalChildCloseProvider && result.Status == PlatformAuthorityStatus.NotAccepted)
                        {
                            if (invocation.OwnFaultPinned) record.State = PlatformChildDomainState.Draining;
                            invocation.NoEffectConsumed = true; invocation.OwnFaultPinned = false;
                        }
                        return exact.IsSuccess ? FromProviderFailure(result.Status, result.Message) : exact;
                    }
                    }
                }
                lock (_secureDomainLifecycleGate)
                {
                    // Original cleanup fact precedes late query/validation/publication.
                    if (result.IsSuccess)
                        record.LastClosureReceipt = new(record.ProviderLease, backendEpoch, result.Value!);
                    else
                    {
                        PinOwnChildCloseFailure(record);
                        return FromProviderFailure(result.Status, result.Message);
                    }
                }
            }
            var fact = record.LastClosureReceipt!;
            var receipt = PlatformChildDomainContract.ValidateClosureReceipt(record.ProviderLease, fact.Receipt);
            if (fact.AdmittedLease != record.ProviderLease || fact.AdmissionEpoch != backendEpoch || !receipt.IsSuccess)
            {
                lock (_secureDomainLifecycleGate) record.State = PlatformChildDomainState.Faulted;
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original child closure receipt is malformed or belongs to another source.");
            }
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Known child closure source unavailable; exact receipt retained for retry."); }
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateChildCloseSourceLocked(record, root, ancestors, backendEpoch, source);
                if (!exact.IsSuccess) return exact;
                record.State = PlatformChildDomainState.Closed;
                return KernelResult.Ok();
            }
        }
        finally { lock (_secureDomainLifecycleGate) record.CloseInFlight = false; }
    }

    private bool TryRetainedChildCleanupState(ChildBindingRecord record, out PlatformChildDomainState state)
    {
        state = PlatformChildDomainState.Faulted;
        if (record.CloseInvocationAttempted)
        {
            if (!HasExactRetainedChildClosure(record)) return false;
            state = PlatformChildDomainState.Draining;
            return true;
        }
        if (!record.TransitionPublicationRefused || record.TransitionIntent is not { } intent ||
            !LegalTransition(record.TransitionAdmittedState, intent) || record.TransitionObservation is not { } observed)
            return false;
        if (observed.Status == PlatformAuthorityStatus.NotAccepted)
        {
            state = record.TransitionAdmittedState;
            return true;
        }
        if (!observed.IsSuccess) return false;
        state = intent switch
        {
            PlatformChildDomainTransition.Start or PlatformChildDomainTransition.Resume => PlatformChildDomainState.Running,
            PlatformChildDomainTransition.Park => PlatformChildDomainState.Parked,
            PlatformChildDomainTransition.BeginDrain => PlatformChildDomainState.Draining,
            _ => PlatformChildDomainState.Faulted,
        };
        return state != PlatformChildDomainState.Faulted;
    }

    internal bool HasOriginalChildCloseContinuation(PlatformChildBinding binding)
    {
        lock (_secureDomainLifecycleGate)
            return _originalChildOwners.TryGetValue(binding.BindingId, out var owner) && owner.Binding == binding &&
                _childBindings.TryGetValue(binding.BindingId, out var route) && ReferenceEquals(route, owner) &&
                owner.LastClosureReceipt is null &&
                owner.OriginalCloseInvocation is { Invoked: true, NoEffectConsumed: false, SupportsResolution: true } fact &&
                ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Child, owner);
    }

    // Original completed lifetime fact, never admission on the current backend.
    private static bool HasValidatedChildClosureFact(ChildBindingRecord record) =>
        record.LastClosureReceipt is { } fact && fact.AdmittedLease == record.ProviderLease &&
        record.CreateRoot is { } root && fact.AdmissionEpoch == root.BackendEpoch &&
        root.Binding == record.Binding.ParentBinding && root.ProviderLease == record.ProviderLease.ParentDomainLease &&
        PlatformChildDomainContract.ValidateClosureReceipt(record.ProviderLease, fact.Receipt).IsSuccess;

    private bool HasExactRetainedChildClosure(ChildBindingRecord record) =>
        HasValidatedChildClosureFact(record) && !_backendEpochExhausted &&
        record.LastClosureReceipt!.AdmissionEpoch == BackendEpoch;

    private KernelResult ValidateChildCloseSourceLocked(ChildBindingRecord record, DomainRecord root,
        IReadOnlyList<ChildBindingRecord> ancestors, PlatformBackendEpoch epoch, PlatformProviderIncarnation source)
    {
        var binding = record.Binding;
        if (HasUnclosedGuestDependency(record))
            return KernelResult.Fail(KernelError.PlatformBindingActive,
                "Original guest dependencies must close before child closure publication or cleanup.");
        if (!HasCurrentChildCreateSources(record) || _backendEpochExhausted || BackendEpoch != epoch || root.BackendEpoch != epoch ||
            source.Value == 0 || source != root.ProviderIncarnation ||
            !_childBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, record) ||
            !TryGetCanonicalRootRecord(binding.ParentBinding.BindingId, out var currentRoot) || !ReferenceEquals(currentRoot, root) ||
            root.Binding != binding.ParentBinding || root.AuthorityState == DomainAuthorityState.Closed || root.ParentRevokeInFlight ||
            record.ProviderLease.ParentDomainLease != root.ProviderLease ||
            !HasChildCloseStateContinuity(record) ||
            record.OriginalCloseInvocation is { Invoked: true, NoEffectConsumed: false } saved &&
                (saved.Ancestors.Length != ancestors.Count || saved.Ancestors.Where((ancestor, index) => !ReferenceEquals(ancestor, ancestors[index])).Any()) ||
            ancestors.Any(ancestor => !_childBindings.TryGetValue(ancestor.Binding.BindingId, out var exact) ||
                !ReferenceEquals(exact, ancestor) || ancestor.State is PlatformChildDomainState.Faulted or PlatformChildDomainState.Closed ||
                ancestor.CloseInFlight || ancestor.TransitionInFlight || ancestor.PendingChildEffects != 0 ||
                ancestor.Binding.ParentBinding != root.Binding ||
                ancestor.ProviderLease.ParentDomainLease != root.ProviderLease))
        {
            PinOwnChildCloseFailure(record);
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original child/root source or lineage changed; closure/reclaim remains pinned.");
        }
        return ValidateDomainIdentity(binding.ParentBinding, binding.ParentBinding.Subject);
    }

    private KernelResult<ChildBindingRecord> ResolveChild(PlatformChildBinding binding, bool allowDraining = false, bool allowUnpublished = false, bool allowOwnCloseFailure = false)
    {
        lock (_secureDomainLifecycleGate)
        {
        if (!_childBindings.TryGetValue(binding.BindingId, out ChildBindingRecord? record))
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformBindingNotFound, "Child binding was not found.");
        if (record.Binding.Generation != binding.Generation)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.StaleGeneration, "Child binding generation is stale.");
        if (record.Binding != binding)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.WrongPlatformDomain, "Child binding has a different parent or owner.");
        if (!_originalChildOwners.TryGetValue(binding.BindingId, out var original) || !ReferenceEquals(original, record))
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformFaulted, "Original child owner is unavailable; routing cannot supply permission.");
        if (!allowUnpublished && !record.CreationPublished)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformDenied, "Known child cleanup receipt has not published admission.");
        if (!allowDraining && (record.EventCleanupOnly || record.NestedCleanupOnly))
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformDenied, "Child is retained only for original event cleanup.");
        lock (_secureDomainLifecycleGate)
            if (record.TransitionInFlight)
                return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformBindingActive,
                    "Child transition is in flight.");
        if (record.State == PlatformChildDomainState.Closed)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformBindingRevoked, "Child binding is closed.");
        if (record.State == PlatformChildDomainState.Faulted && !(allowOwnCloseFailure && HasOwnChildCloseFailure(record)))
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformFaulted, "Child binding is quarantined.");
        if (!allowDraining && record.State == PlatformChildDomainState.Draining)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformDenied, "Child binding is draining.");
        return KernelResult<ChildBindingRecord>.Ok(record);
        }
    }

    private bool SupportsChildFeature(PlatformFeatureFamily family, uint version)
    {
        PlatformFeatureDescriptor feature = _featureManifest.Resolve(family);
        return feature.ContractVersion >= version && feature.Availability is
            PlatformFeatureAvailability.RuntimeAdmission or PlatformFeatureAvailability.Executable;
    }

    private static bool LegalTransition(PlatformChildDomainState state, PlatformChildDomainTransition transition) =>
        transition switch
        {
            PlatformChildDomainTransition.Start => state == PlatformChildDomainState.Created,
            PlatformChildDomainTransition.Park => state == PlatformChildDomainState.Running,
            PlatformChildDomainTransition.Resume => state == PlatformChildDomainState.Parked,
            PlatformChildDomainTransition.BeginDrain => state is PlatformChildDomainState.Created or
                PlatformChildDomainState.Running or PlatformChildDomainState.Parked,
            _ => false,
        };

}


