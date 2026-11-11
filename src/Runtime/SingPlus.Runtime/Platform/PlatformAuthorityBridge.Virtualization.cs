using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal readonly record struct VirtualDomainBindingId(ulong Value);
    internal readonly record struct VirtualDomainBindingGeneration(ulong Value);
    internal readonly record struct VirtualDomainBinding(
        VirtualDomainBindingId BindingId,
        VirtualDomainBindingGeneration Generation,
        ProcessHandle Owner);

    private sealed class ModelCreateInvocationFact
    {
        public ModelCreateInvocationFact(PlatformDomainIdentity owner,PlatformVirtualDomainProfile profile,PlatformBackendEpoch epoch)
        {OriginalSelf=this;Owner=owner;Profile=profile;AdmissionEpoch=epoch;OriginalRequest=new(owner,profile);CapturedRequest=OriginalRequest;}
        public readonly ModelCreateInvocationFact OriginalSelf;
        public PlatformDomainIdentity Owner { get; }
        public PlatformVirtualDomainProfile Profile { get; }
        public PlatformBackendEpoch AdmissionEpoch { get; }
        public PlatformProviderIncarnation ProviderIncarnation { get; set; }
        public PlatformOriginalModelCreateRequest OriginalRequest { get; }
        public readonly PlatformOriginalModelCreateRequest CapturedRequest;
        public PlatformOriginalModelCreateObservation? OriginalObservation;
        public bool OriginalObservationValidated;
        private PlatformOriginalModelCreateObservation? canonicalObservation;
        public bool CaptureValidatedObservation(PlatformOriginalModelCreateObservation raw)
        {
            if (!ReferenceEquals(OriginalObservation, raw) || !ReferenceEquals(raw.Request, CapturedRequest) ||
                canonicalObservation is not null && !ReferenceEquals(canonicalObservation, raw)) return false;
            canonicalObservation ??= raw;
            OriginalObservationValidated = true;
            return true;
        }
        public bool HasOriginalObservationFor(PlatformProviderVirtualDomainLease lease) => canonicalObservation is null
            ? OriginalObservation is null && !OriginalObservationValidated
            : OriginalObservationValidated && ReferenceEquals(OriginalObservation, canonicalObservation) &&
              ReferenceEquals(canonicalObservation.Request, CapturedRequest) && canonicalObservation.Outcome.IsSuccess && canonicalObservation.Outcome.Value == lease;
        public bool HasOriginalOutcome(PlatformAuthorityResult<PlatformProviderVirtualDomainLease> outcome) => canonicalObservation is null
            ? OriginalObservation is null && !OriginalObservationValidated
            : OriginalObservationValidated && ReferenceEquals(OriginalObservation, canonicalObservation) &&
              ReferenceEquals(canonicalObservation.Request, CapturedRequest) && canonicalObservation.Outcome == outcome;
        public bool CleanupInFlight;
        public bool LocalCleanupOwnerDelivered;
        public VirtualDomainRecord? CleanupOwner;
        public VirtualDomainBinding ReservedBinding;
        public bool EffectMayHaveOccurred { get; set; }
        public PlatformAuthorityResult<PlatformProviderVirtualDomainLease>? Observation { get; set; }
        public Func<KernelResult>? LocalCreationClosureConsumer { get; set; }
        public Func<KernelResult>? LocalCreationNoEffectConsumer { get; set; }
        private Func<KernelResult>? originalLocalClosure, originalLocalNoEffect;
        private bool localConsumersCaptured;
        public void CaptureLocalCreationConsumers(Func<KernelResult>? closure, Func<KernelResult>? noEffect)
        {
            if (localConsumersCaptured) throw new InvalidOperationException("Original model creation consumers already captured.");
            originalLocalClosure = LocalCreationClosureConsumer = closure;
            originalLocalNoEffect = LocalCreationNoEffectConsumer = noEffect;
            localConsumersCaptured = true;
        }
        public bool HasOriginalLocalCreationConsumers => localConsumersCaptured &&
            ReferenceEquals(LocalCreationClosureConsumer, originalLocalClosure) &&
            ReferenceEquals(LocalCreationNoEffectConsumer, originalLocalNoEffect);
    }
    private sealed record VirtualDomainRecord(
        VirtualDomainBinding Binding,
        PlatformProviderVirtualDomainLease ProviderLease)
    {
        public PlatformBackendEpoch CreatedInEpoch { get; init; }
        public ModelCreateInvocationFact? CreateInvocation { get; init; }
        public VirtualDomainRecord? ResetOriginalOwner { get; init; }
        public PlatformProviderIncarnation ProviderIncarnation { get; init; }
        public bool Quarantined { get; set; }
        public bool ReceiptValid { get; init; }
        public bool TransitionInFlight { get; set; }
        public bool TransitionMayHaveEffect { get; set; }
        public PlatformVirtualDomainTransition? TransitionIntent { get; set; }
        public PlatformAuthorityResult? TransitionObservation { get; set; }
        public OriginalModelTransitionFact? OriginalTransition {get;set;}
        public bool CloseInFlight { get; set; }
        public bool CloseMayHaveEffect { get; set; }
        public bool ProviderClosureConfirmed { get; set; }
        public PlatformAuthorityResult? CloseObservation { get; set; }
        public OriginalModelRevokeFact? OriginalClose {get;set;}
    }

    private readonly Dictionary<VirtualDomainBindingId, VirtualDomainRecord> _virtualDomainBindings = [];
    // Stable index to the same authoritative owner; routing/reset copies never become permission.
    private readonly Dictionary<VirtualDomainBindingId, VirtualDomainRecord> _originalVirtualDomainOwners = [];
    // Admission and unresolved effects belong to the same existing model-domain owner.
    private readonly Dictionary<PlatformDomainIdentity, ModelCreateInvocationFact> _pendingVirtualDomainCreates = [];
    private readonly Dictionary<PlatformDomainIdentity, ModelCreateInvocationFact> _unresolvedVirtualDomainCreates = [];
    // Same original facts retained by the creation owner until positive local
    // delivery/no-effect/closure; unresolved dictionary is only a cleanup route.
    private readonly Dictionary<PlatformDomainIdentity, ModelCreateInvocationFact> _originalModelCreateOwners = [];
    private ulong _nextVirtualDomainBindingId = 1;

    internal bool HasUnclosedModelDomainOwner(ProcessHandle owner)
    {
        lock (_secureDomainLifecycleGate)
            return _originalVirtualDomainOwners.Values.Any(record => record.Binding.Owner == owner) ||
                _virtualDomainBindings.Values.Any(record => record.Binding.Owner == owner);
    }

    internal bool HasUnresolvedVirtualDomainCreate(PlatformDomainIdentity owner)
    {
        lock (_secureDomainLifecycleGate)
            return _pendingVirtualDomainCreates.ContainsKey(owner) || _unresolvedVirtualDomainCreates.ContainsKey(owner) || _originalModelCreateOwners.ContainsKey(owner);
    }

    internal KernelResult<VirtualDomainBinding> CreateVirtualDomain(
        PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<VirtualDomainBinding, Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null,
        Action<VirtualDomainBinding>? cleanupConsumer = null,
        Func<object,KernelResult>? originalCreationConsumer=null,
        Func<object,KernelResult>? originalCreationClosureConsumer=null,
        Func<object,KernelResult>? originalCreationNoEffectConsumer=null,
        Func<object,KernelResult>? originalCreationNeverInvokedConsumer=null)
    {
        if (_provider is not IPlatformVirtualizationProvider provider ||
            !_featureManifest.Supports(PlatformFeatureFamily.VirtualizationDomains,
                PlatformVirtualizationContract.ContractVersion, PlatformFeatureAvailability.ModelOnly))
            return KernelResult<VirtualDomainBinding>.Fail(KernelError.PlatformUnsupported, "Platform virtualization ModelOnly contract is unavailable.");
        PlatformAuthorityResult subject = PlatformDomainContract.ValidateSubject(owner);
        if (!subject.IsSuccess) return FromProviderFailure<VirtualDomainBinding>(subject.Status, subject.Message);
        if (profile.VirtualProcessorCount <= 0 || profile.VirtualProcessorCount > 256 || profile.MaximumGuestMemoryBytes <= 0)
            return KernelResult<VirtualDomainBinding>.Fail(KernelError.PlatformDenied, "Model-domain profile is outside local bounds.");
        PlatformBackendEpoch epoch = default;
        ModelCreateInvocationFact invocation = null!;
        VirtualDomainBinding binding = default;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                if (_backendEpochExhausted || _nextVirtualDomainBindingId == ulong.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Model-domain generation space is exhausted.");
                if (_pendingVirtualDomainCreates.ContainsKey(owner))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Model-domain creation is pending for this exact subject.");
                if (_unresolvedVirtualDomainCreates.ContainsKey(owner) || _originalModelCreateOwners.ContainsKey(owner))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Model-domain creation has an unresolved effect for this exact subject.");
                KernelResult Commit()
                {
                    epoch = BackendEpoch;
                    binding = new(new(_nextVirtualDomainBindingId++), new(1), owner.Process);
                    invocation = new(owner, profile, epoch);
                    invocation.ReservedBinding=binding;
                    if(originalCreationConsumer is not null)
                    {
                        KernelResult linked =originalCreationConsumer(invocation);
                        if(!linked.IsSuccess)return linked;
                    }
                    invocation.CaptureLocalCreationConsumers(
                        originalCreationConsumer is null ? null : () => originalCreationClosureConsumer?.Invoke(invocation) ?? KernelResult.Ok(),
                        () => originalCreationNoEffectConsumer?.Invoke(invocation) ?? KernelResult.Ok());
                    _pendingVirtualDomainCreates.Add(owner, invocation);
                    _originalModelCreateOwners.Add(owner,invocation);
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        KernelResult admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<VirtualDomainBinding>.Fail(admitted.Error, admitted.Message!);
        try
        {
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult<VirtualDomainBinding>.Fail(KernelError.PlatformFaulted, "Model create source unavailable before effect."); }
            KernelResult AdmitEffect()
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (!ReferenceEquals(invocation.OriginalRequest,invocation.CapturedRequest) ||
                        source.Value == 0 || BackendEpoch != epoch || _backendEpochExhausted ||
                        !_pendingVirtualDomainCreates.TryGetValue(owner, out ModelCreateInvocationFact? pending) || !ReferenceEquals(pending, invocation))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Model create original source changed before effect.");
                    KernelResult Commit()
                    {
                        if (!invocation.HasOriginalLocalCreationConsumers)
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Original model creation consumers changed before effect.");
                        invocation.ProviderIncarnation = source;
                        invocation.EffectMayHaveOccurred = true;
                        return KernelResult.Ok();
                    }
                    return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
                }
            }
            KernelResult effectAdmission = admissionConsumer is null ? AdmitEffect() : admissionConsumer(AdmitEffect);
            if (!effectAdmission.IsSuccess)
                return KernelResult<VirtualDomainBinding>.Fail(effectAdmission.Error, effectAdmission.Message!);
            PlatformAuthorityResult<PlatformProviderVirtualDomainLease> result;
            try
            {
                if(provider is IPlatformOriginalModelCreateProvider originalProvider)
                {
                    PlatformOriginalModelCreateObservation? raw =originalProvider.CreateOriginalModelDomain(invocation.OriginalRequest);
                    lock(_secureDomainLifecycleGate)invocation.OriginalObservation=raw;
                    PlatformOriginalModelCreateObservation? canonical =originalProvider.QueryOriginalModelCreate(invocation.OriginalRequest);
                    if(raw is null || !ReferenceEquals(raw,canonical) || !ReferenceEquals(raw.Request,invocation.OriginalRequest))
                        throw new InvalidOperationException("Original model create outcome unavailable/foreign.");
                    lock(_secureDomainLifecycleGate)
                        if (!invocation.CaptureValidatedObservation(raw)) throw new InvalidOperationException("Original model create observation changed before validation.");
                    result=raw.Outcome;
                }
                else result = provider.CreateVirtualDomain(owner, profile);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate) _unresolvedVirtualDomainCreates.TryAdd(owner, invocation);
                return KernelResult<VirtualDomainBinding>.Fail(KernelError.PlatformFaulted,
                    $"Model creation may have taken effect without a lease: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate) invocation.Observation = result;
            if (!result.IsSuccess)
            {
                if(result.Status!=PlatformAuthorityStatus.NotAccepted)
                {
                    lock(_secureDomainLifecycleGate)_unresolvedVirtualDomainCreates.TryAdd(owner,invocation);
                    return FromProviderFailure<VirtualDomainBinding>(result.Status,result.Message);
                }
                PlatformProviderIncarnation refusalSource=default;
                if(result.Status==PlatformAuthorityStatus.NotAccepted)
                    try { refusalSource=CurrentProviderIncarnation(); }
                    catch(Exception exception) when(exception is not StackOverflowException) { }
                lock (_secureDomainLifecycleGate)
                {
                    bool exactNoEffect =invocation.HasOriginalLocalCreationConsumers && invocation.HasOriginalOutcome(result) && result.Status==PlatformAuthorityStatus.NotAccepted && result.Value==default && refusalSource==source && source.Value!=0 &&
                        BackendEpoch==epoch && !_backendEpochExhausted &&
                        _pendingVirtualDomainCreates.TryGetValue(owner,out ModelCreateInvocationFact? pending) && ReferenceEquals(pending,invocation) &&
                        invocation.Observation is { Status:PlatformAuthorityStatus.NotAccepted } retained && retained==result;
                    KernelResult consumed =exactNoEffect ? originalCreationNoEffectConsumer?.Invoke(invocation)??KernelResult.Ok() :
                        KernelResult.Fail(KernelError.PlatformFaulted,"Original model refusal source is unavailable or changed.");
                    if(!consumed.IsSuccess)
                    {
                        _unresolvedVirtualDomainCreates.TryAdd(owner, invocation);
                        return KernelResult<VirtualDomainBinding>.Fail(consumed.Error,consumed.Message!);
                    }
                    _originalModelCreateOwners.Remove(owner);
                }
                return FromProviderFailure<VirtualDomainBinding>(result.Status, result.Message);
            }
            PlatformProviderVirtualDomainLease lease = result.Value!;
            bool valid = lease.LeaseId.Value != 0 && lease.Generation.Value != 0 && lease.Owner == owner;
            VirtualDomainRecord record;
            lock (_secureDomainLifecycleGate)
            {
                record = new(binding, lease)
                {
                    CreateInvocation = invocation, CreatedInEpoch = epoch, ProviderIncarnation = source, ReceiptValid = valid,
                    Quarantined = !valid || BackendEpoch != epoch || _backendEpochExhausted
                };
                invocation.CleanupOwner=record;
                _virtualDomainBindings.Add(binding.BindingId, record);
                _originalVirtualDomainOwners.Add(binding.BindingId, record);
                if (!valid || BackendEpoch != epoch || _backendEpochExhausted)
                    _unresolvedVirtualDomainCreates.TryAdd(owner, invocation);
            }
            try
            {
                cleanupConsumer?.Invoke(binding);
                lock(_secureDomainLifecycleGate)invocation.LocalCleanupOwnerDelivered=cleanupConsumer is not null;
                if(cleanupConsumer is not null)lock(_secureDomainLifecycleGate)
                    if(_originalModelCreateOwners.TryGetValue(owner,out ModelCreateInvocationFact? retained) && ReferenceEquals(retained,invocation))_originalModelCreateOwners.Remove(owner);
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate)
                {
                    record.Quarantined = true;
                    _unresolvedVirtualDomainCreates.TryAdd(owner, invocation);
                }
                return KernelResult<VirtualDomainBinding>.Fail(KernelError.PlatformFaulted, "Original model cleanup binding could not reach its consumer.");
            }
            PlatformProviderIncarnation currentSource;
            try { currentSource = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate) record.Quarantined = true;
                return KernelResult<VirtualDomainBinding>.Fail(KernelError.PlatformFaulted, "Known model lease retained while source observation is unavailable.");
            }
            lock (_secureDomainLifecycleGate)
            {
                if (!ReferenceEquals(invocation.OriginalRequest,invocation.CapturedRequest) ||
                    currentSource.Value == 0 || currentSource != source || BackendEpoch != epoch || _backendEpochExhausted ||
                    !_virtualDomainBindings.TryGetValue(binding.BindingId, out VirtualDomainRecord? exact) || !ReferenceEquals(exact, record) ||
                    !_originalVirtualDomainOwners.TryGetValue(binding.BindingId, out VirtualDomainRecord? original) || !ReferenceEquals(original, record))
                {
                    record.Quarantined = true;
                    _unresolvedVirtualDomainCreates.TryAdd(owner, invocation);
                    return KernelResult<VirtualDomainBinding>.Fail(KernelError.PlatformFaulted, "Known model lease lost original source continuity before publication.");
                }
            }
            KernelResult CommitLocal(Func<KernelResult> commit) =>
                CommitModelDomainConsumer(binding, commit);
            KernelResult completed;
            try { completed = completionConsumer is null ? CommitLocal(KernelResult.Ok) : completionConsumer(binding, CommitLocal); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate) _unresolvedVirtualDomainCreates.TryAdd(owner, invocation);
                completed = KernelResult.Fail(KernelError.PlatformFaulted, $"Model receipt could not reach its local consumer: {exception.Message}");
            }
            if (!completed.IsSuccess)
            {
                lock (_secureDomainLifecycleGate) record.Quarantined = true;
                return KernelResult<VirtualDomainBinding>.Fail(completed.Error, completed.Message!);
            }
            return KernelResult<VirtualDomainBinding>.Ok(binding);
        }
        finally
        {
            lock (_secureDomainLifecycleGate)
            {
                if(_pendingVirtualDomainCreates.TryGetValue(owner,out ModelCreateInvocationFact? pending) && ReferenceEquals(pending,invocation))
                {
                    // Own control-flow fact, before every provider effect call. Source failure/reset
                    // cannot turn this into an unknown invoked effect or require fresh permission.
                    if(!invocation.EffectMayHaveOccurred)
                    {
                        KernelResult consumed =originalCreationNeverInvokedConsumer?.Invoke(invocation)??KernelResult.Ok();
                        if(!consumed.IsSuccess)_unresolvedVirtualDomainCreates.TryAdd(owner,invocation);
                        else if(_originalModelCreateOwners.TryGetValue(owner,out ModelCreateInvocationFact? retained) && ReferenceEquals(retained,invocation))_originalModelCreateOwners.Remove(owner);
                    }
                    _pendingVirtualDomainCreates.Remove(owner);
                }
                else if(!invocation.EffectMayHaveOccurred && ReferenceEquals(invocation.OriginalSelf,invocation) &&
                    _originalModelCreateOwners.TryGetValue(owner,out ModelCreateInvocationFact? originalOwner) && ReferenceEquals(originalOwner,invocation))
                {
                    // Own producer control flow proves this exact invocation never
                    // reached the provider. Missing routing does not erase that fact.
                    KernelResult consumed =originalCreationNeverInvokedConsumer?.Invoke(invocation)??KernelResult.Ok();
                    if(consumed.IsSuccess)_originalModelCreateOwners.Remove(owner);
                    else _unresolvedVirtualDomainCreates.TryAdd(owner,invocation);
                }
                else _unresolvedVirtualDomainCreates.TryAdd(owner,invocation);
            }
        }
    }

    internal KernelResult CommitModelDomainConsumer(VirtualDomainBinding binding, Func<KernelResult> commit)
    {
        lock (_secureDomainLifecycleGate)
        {
            KernelResult<VirtualDomainRecord> current = ResolveVirtualDomain(binding);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (current.Value!.Quarantined || !current.Value.ReceiptValid || current.Value.CloseInFlight)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Model-domain receipt is quarantined or closing.");
            return commit();
        }
    }

    internal KernelResult TransitionVirtualDomain(VirtualDomainBinding binding, PlatformVirtualDomainTransition transition,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null,
        Func<KernelResult>? noEffectConsumer = null)
    {
        if (_provider is not IPlatformVirtualizationProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Platform virtualization provider is unavailable.");
        IPlatformOriginalVirtualDomainTransitionProvider? originalProvider =_provider as IPlatformOriginalVirtualDomainTransitionProvider;
        bool originalSupported =originalProvider is {SupportsOriginalVirtualDomainTransitions:true};
        if(originalSupported && _provider is not IPlatformProviderIncarnationSource)
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original model transition requires explicit provider incarnation.");
        if(transition==PlatformVirtualDomainTransition.BeginDrain)
        {
            KernelResult closed =ResolveOriginalModelRevokeForCleanup(binding);
            if(!closed.IsSuccess)return closed;
            KernelResult resolved =ResolveOriginalModelTransitionForCleanup(binding);
            if(!resolved.IsSuccess)return resolved;
        }
        if (!Enum.IsDefined(transition))
            return KernelResult.Fail(KernelError.PlatformDenied, "Unknown mandatory model transition.");
        VirtualDomainRecord record = null!;
        bool retainedCleanup = false;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                KernelResult<VirtualDomainRecord> current = ResolveVirtualDomain(binding);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                record = current.Value!;
                if (record.CloseInFlight || record.TransitionInFlight)
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Model close/transition is pending.");
                if (transition == PlatformVirtualDomainTransition.BeginDrain && record.ProviderClosureConfirmed)
                {
                    // Known original revoke success needs no second model drain.
                    // RevokeVirtualDomain still validates source before local release.
                    retainedCleanup = true;
                    return KernelResult.Ok();
                }
                if ((record.Quarantined && transition != PlatformVirtualDomainTransition.BeginDrain) ||
                    !record.ReceiptValid || record.CloseMayHaveEffect || record.TransitionMayHaveEffect)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Model transition lacks unambiguous admission.");
                KernelResult Commit()
                {
                    record.TransitionInFlight = true;
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        KernelResult admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess || retainedCleanup) return admitted;
        OriginalModelTransitionFact? originalTransition = null;
        PlatformOriginalVirtualDomainTransitionObservation? originalOutcome = null;
        bool HasOriginalOutcome(PlatformProviderIncarnation currentSource, PlatformAuthorityResult outcome) =>
            !originalSupported || originalTransition is not null && originalOutcome is not null &&
            ExactOriginalModelTransition(originalTransition, originalOutcome, currentSource) && originalOutcome.Result == outcome;
        void PinUnknown()
        {
            lock (_secureDomainLifecycleGate)
            {
                record.Quarantined = true;
                record.TransitionMayHaveEffect = true;
                if(record.OriginalTransition is {Invoked:true} fact && ReferenceEquals(fact.Owner,record))fact.OwnUnknown=true;
                if (CurrentOriginalModelOwnerLocked(record) is { } current)
                {
                    current.Quarantined = true;
                    current.TransitionMayHaveEffect = true;
                }
            }
        }
        try
        {
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Model transition source unavailable before invocation."); }
            KernelResult exact = ValidateSource(source);
            if (!exact.IsSuccess) return exact;
            KernelResult FinalAdmission()
            {
                lock (_secureDomainLifecycleGate)
                {
                    KernelResult fresh = ValidateSource(source);
                    if (!fresh.IsSuccess || (record.Quarantined && transition != PlatformVirtualDomainTransition.BeginDrain) ||
                        !record.ReceiptValid || record.CloseMayHaveEffect || record.TransitionMayHaveEffect)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Model transition source admission changed.");
                    KernelResult RecordIntent()
                    {
                        record.TransitionIntent = transition;
                        record.TransitionObservation = null;
                        record.OriginalTransition=originalSupported ? new(record,new(record.ProviderLease,transition,source),record.CreatedInEpoch,noEffectConsumer) : null;
                        return KernelResult.Ok();
                    }
                    return sourceCommitConsumer is null ? RecordIntent() : sourceCommitConsumer(RecordIntent);
                }
            }
            KernelResult final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
            if (!final.IsSuccess) return final;
            PlatformAuthorityResult result;
            if(originalSupported)
            {
                OriginalModelTransitionFact fact =record.OriginalTransition!;
                originalTransition=fact;
                lock(_secureDomainLifecycleGate)fact.Invoked=true;
                KernelResult<PlatformAuthorityResult> original =InvokeOriginalModelTransition(fact,originalProvider!);
                if(!original.IsSuccess){PinUnknown();return KernelResult.Fail(original.Error,original.Message!);}
                lock(_secureDomainLifecycleGate)originalOutcome=fact.Raw;
                result=original.Value;
            }
            else result=provider.TransitionVirtualDomain(record.ProviderLease,transition);
            lock (_secureDomainLifecycleGate)
            {
                // Preserve the original returned outcome before routing/publication checks.
                record.TransitionObservation = result;
                if (CurrentOriginalModelOwnerLocked(record) is { } correlated)
                {
                    correlated.TransitionIntent = transition;
                    correlated.TransitionObservation = result;
                }
                if (!_virtualDomainBindings.TryGetValue(binding.BindingId, out VirtualDomainRecord? current) ||
                    !ReferenceEquals(current, record) || current.Binding != binding ||
                    record.CreatedInEpoch != BackendEpoch || _backendEpochExhausted)
                {
                    PinUnknown();
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Model transition lost exact record/backend continuity.");
                }
                if (!result.IsSuccess)
                {
                    record.Quarantined = true;
                    if (result.Status != PlatformAuthorityStatus.NotAccepted) PinUnknown();
                    if(result.Status!=PlatformAuthorityStatus.NotAccepted || noEffectConsumer is null)
                        return FromProviderFailure(result.Status, result.Message);
                }
            }
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate) record.Quarantined = true;
                return KernelResult.Fail(KernelError.PlatformFaulted, "Known model transition source unavailable; local publication refused.");
            }
            exact = ValidateSource(source);
            if (!exact.IsSuccess) return exact;
            lock(_secureDomainLifecycleGate)
                if(!HasOriginalOutcome(source,result))
                {
                    PinUnknown();
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original model transition raw changed before local consumption.");
                }
            if(!result.IsSuccess)
            {
                lock(_secureDomainLifecycleGate)
                    if(record.TransitionIntent!=transition || record.TransitionObservation!=result)
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original refused model transition fact unavailable.");
                KernelResult consumed =noEffectConsumer!();
                if(!consumed.IsSuccess)return consumed;
                source=CurrentProviderIncarnation();
                lock(_secureDomainLifecycleGate)
                {
                    exact=ValidateSource(source);
                    if(!exact.IsSuccess || !HasOriginalOutcome(source,result) || record.TransitionIntent!=transition || record.TransitionObservation!=result)
                    {
                        PinUnknown();
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original refused model transition changed after no-effect consumption.");
                    }
                }
                return FromProviderFailure(result.Status,result.Message);
            }
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    KernelResult<VirtualDomainRecord> current = ResolveVirtualDomain(binding);
                    if (!HasOriginalOutcome(source,result))
                    {
                        PinUnknown();
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original model transition raw changed before local publication.");
                    }
                    if (!current.IsSuccess || !ReferenceEquals(current.Value, record) || record.Quarantined ||
                        source.Value == 0 || source != record.ProviderIncarnation)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Model receipt is no longer active for local publication.");
                    return commit();
                }
            }
            KernelResult completed = completionConsumer is null ? KernelResult.Ok() : completionConsumer(CommitCurrent);
            if (!completed.IsSuccess)
            {
                // Known returned model effect, but no local permission to publish state.
                lock (_secureDomainLifecycleGate) record.Quarantined = true;
            }
            return completed;
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            PinUnknown();
            return KernelResult.Fail(KernelError.PlatformFaulted, $"Model transition receipt was lost: {exception.Message}");
        }
        finally
        {
            lock (_secureDomainLifecycleGate)
            {
                record.TransitionInFlight = false;
                if (CurrentOriginalModelOwnerLocked(record) is { } current)
                    current.TransitionInFlight = false;
            }
        }
        KernelResult ValidateSource(PlatformProviderIncarnation source)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (source.Value == 0 || source != record.ProviderIncarnation ||
                    record.CreatedInEpoch != BackendEpoch || _backendEpochExhausted ||
                    !_virtualDomainBindings.TryGetValue(binding.BindingId, out VirtualDomainRecord? current) ||
                    !ReferenceEquals(current, record) || current.Binding != binding ||
                    !_originalVirtualDomainOwners.TryGetValue(binding.BindingId, out VirtualDomainRecord? original) || !ReferenceEquals(original, record))
                {
                    record.Quarantined = true;
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Model transition original source is no longer current.");
                }
                return KernelResult.Ok();
            }
        }
    }

    internal KernelResult RevokeVirtualDomain(VirtualDomainBinding binding, bool retainCleanupReceipt = false,
        Func<Func<Func<Func<KernelResult>, KernelResult>, KernelResult>, KernelResult>? cleanupConsumer = null)
    {
        if (_provider is not IPlatformVirtualizationProvider provider) return KernelResult.Fail(KernelError.PlatformUnsupported, "Platform virtualization provider is unavailable.");
        KernelResult reconciled =ResolveOriginalModelTransitionForCleanup(binding);
        if(!reconciled.IsSuccess)return reconciled;
        KernelResult originalClosed =ResolveOriginalModelRevokeForCleanup(binding);
        if(!originalClosed.IsSuccess)return originalClosed;
        VirtualDomainRecord record;
        lock (_secureDomainLifecycleGate)
        {
            KernelResult<VirtualDomainRecord> current = ResolveVirtualDomain(binding);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            record = current.Value!;
            if (!record.ReceiptValid || record.CloseMayHaveEffect && !record.ProviderClosureConfirmed || record.TransitionMayHaveEffect)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Model-domain lease lacks unambiguous closure evidence.");
            if (record.CloseInFlight || record.TransitionInFlight || _pendingVirtualDomainCreates.Keys.Any(x => x.Process == binding.Owner))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Model-domain creation or close is pending.");
            record.CloseInFlight = true;
            record.Quarantined = true;
        }
        try
        {
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Model cleanup source unavailable; original owner retained."); }
            bool invokeRevoke;
            lock (_secureDomainLifecycleGate)
            {
                KernelResult exact = ValidateModelCleanupSourceLocked(record, binding, source);
                if (!exact.IsSuccess) return exact;
                invokeRevoke = !record.ProviderClosureConfirmed;
                if (invokeRevoke) record.CloseMayHaveEffect = true;
            }
            if (invokeRevoke)
            {
                PlatformAuthorityResult result;
                try
                {
                    if(_provider is IPlatformOriginalVirtualDomainRevokeProvider {SupportsOriginalVirtualDomainRevocation:true} originalProvider)
                    {
                        if(_provider is not IPlatformProviderIncarnationSource)return KernelResult.Fail(KernelError.PlatformFaulted,"Original model close requires explicit incarnation.");
                        lock(_secureDomainLifecycleGate)
                        {
                            record.OriginalClose ??=new(record,new(record.ProviderLease,source),record.CreatedInEpoch);
                            record.OriginalClose.Invoked=true;
                        }
                        KernelResult original =InvokeOriginalModelRevoke(record.OriginalClose,originalProvider);
                        if(!original.IsSuccess)return original;
                        result=PlatformAuthorityResult.Ok();
                    }
                    else result = provider.RevokeVirtualDomain(record.ProviderLease);
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    lock (_secureDomainLifecycleGate) record.CloseMayHaveEffect = true;
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Model revoke outcome unknown; original owner pinned.");
                }
                lock (_secureDomainLifecycleGate)
                {
                    record.CloseObservation = result;
                    VirtualDomainRecord? owner = CurrentOriginalModelOwnerLocked(record);
                    if (owner is not null) owner.CloseObservation = result;
                    if (!result.IsSuccess) return FromProviderFailure(result.Status, result.Message);
                    // Cleanup evidence only, including the existing reset replacement.
                    record.ProviderClosureConfirmed = true;
                    if (owner is not null) owner.ProviderClosureConfirmed = true;
                }
            }
            // Preserve the exact closure through query/source callbacks and local consumption.
            OriginalModelRevokeFact? closeFact;
            PlatformOriginalVirtualDomainRevokeObservation? closeRaw;
            lock (_secureDomainLifecycleGate)
            {
                closeFact = record.OriginalClose;
                closeRaw = closeFact?.Raw;
            }
            bool requiresOriginalClose = closeFact is not null ||
                _provider is IPlatformOriginalVirtualDomainRevokeProvider { SupportsOriginalVirtualDomainRevocation: true };
            PlatformOriginalVirtualDomainRevokeObservation? canonicalClose = null;
            if (requiresOriginalClose)
            {
                if (closeFact is null || _provider is not IPlatformOriginalVirtualDomainRevokeProvider originalProvider)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original model closure absent; local consumers retained.");
                canonicalClose = originalProvider.ResolveOriginalVirtualDomainRevoke(closeFact.Request);
            }
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Known model closure source unavailable; success retained for retry."); }
            KernelResult ValidateClosure()
                => !requiresOriginalClose || closeFact is not null && ReferenceEquals(closeRaw, closeFact.Raw) &&
                    ExactOriginalModelRevoke(closeFact, canonicalClose, source)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.PlatformFaulted, "Original model closure changed before local consumption; retained.");
            KernelResult CommitCleanup(Func<Func<KernelResult>, KernelResult> localConsumer)
            {
                lock (_secureDomainLifecycleGate)
                {
                    KernelResult exact = ValidateModelCleanupSourceLocked(record, binding, source);
                    if (!exact.IsSuccess) return exact;
                    KernelResult closure = ValidateClosure();
                    if (!closure.IsSuccess) return closure;
                    if (retainCleanupReceipt) return KernelResult.Ok();
                    return localConsumer(() =>
                    {
                        KernelResult stillClosed = ValidateClosure();
                        if (!stillClosed.IsSuccess) return stillClosed;
                        KernelResult creationClosed =record.CreateInvocation?.LocalCreationClosureConsumer?.Invoke()??KernelResult.Ok();
                        if(!creationClosed.IsSuccess)return creationClosed;
                        _virtualDomainBindings.Remove(binding.BindingId);
                        if (_originalVirtualDomainOwners.TryGetValue(binding.BindingId, out VirtualDomainRecord? original) && ReferenceEquals(original, record))
                            _originalVirtualDomainOwners.Remove(binding.BindingId);
                        if (record.CreateInvocation is { } created &&
                            _unresolvedVirtualDomainCreates.TryGetValue(created.Owner, out ModelCreateInvocationFact? retained) && ReferenceEquals(retained, created))
                            _unresolvedVirtualDomainCreates.Remove(created.Owner);
                        if(record.CreateInvocation is { } creation && _originalModelCreateOwners.TryGetValue(creation.Owner,out ModelCreateInvocationFact? originalCreation) && ReferenceEquals(originalCreation,creation))
                            _originalModelCreateOwners.Remove(creation.Owner);
                        return KernelResult.Ok();
                    }); // Trusted local owner commit, preserving bridge -> virtual-owner lock order.
                }
            }
            return cleanupConsumer is null ? CommitCleanup(commit => commit()) : cleanupConsumer(CommitCleanup);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate) record.CloseMayHaveEffect = true;
            return KernelResult.Fail(KernelError.PlatformFaulted, $"Model revoke receipt was lost: {exception.Message}");
        }
        finally
        {
            lock (_secureDomainLifecycleGate)
            {
                record.CloseInFlight = false;
                // Reset replaces the owner record; balance only its copied admission flag.
                VirtualDomainRecord? current = CurrentOriginalModelOwnerLocked(record);
                if (current is not null) current.CloseInFlight = false;
            }
        }
    }

    private VirtualDomainRecord? CurrentOriginalModelOwnerLocked(VirtualDomainRecord original)
    {
        if (_virtualDomainBindings.TryGetValue(original.Binding.BindingId, out VirtualDomainRecord? current) &&
            HasOriginalModelOwner(original) &&
            (ReferenceEquals(current,original) || ReferenceEquals(current.ResetOriginalOwner,original)) &&
            _originalVirtualDomainOwners.TryGetValue(original.Binding.BindingId,out VirtualDomainRecord? owner) && ReferenceEquals(owner,original) &&
            current.Binding.BindingId == original.Binding.BindingId &&
            current.ProviderLease == original.ProviderLease && current.ProviderIncarnation == original.ProviderIncarnation &&
            current.CreatedInEpoch == original.CreatedInEpoch && current.Binding.Owner == original.Binding.Owner)
            return current;
        return null;
    }

    private static bool HasOriginalModelOwner(VirtualDomainRecord record) => record.CreateInvocation is { } fact &&
        ReferenceEquals(fact.OriginalSelf,fact) && fact.HasOriginalLocalCreationConsumers && fact.HasOriginalObservationFor(record.ProviderLease) && ReferenceEquals(fact.OriginalRequest,fact.CapturedRequest) &&
        ReferenceEquals(fact.CleanupOwner,record) && fact.ReservedBinding==record.Binding;

    private KernelResult ValidateModelCleanupSourceLocked(VirtualDomainRecord record,
        VirtualDomainBinding binding, PlatformProviderIncarnation source)
    {
        if (!HasOriginalModelOwner(record) || source.Value == 0 || source != record.ProviderIncarnation || BackendEpoch != record.CreatedInEpoch ||
            _backendEpochExhausted || !_virtualDomainBindings.TryGetValue(binding.BindingId, out VirtualDomainRecord? current) ||
            !ReferenceEquals(current, record) || current.Binding != binding ||
            !_originalVirtualDomainOwners.TryGetValue(binding.BindingId, out VirtualDomainRecord? original) || !ReferenceEquals(original, record))
        {
            record.Quarantined = true;
            record.CloseMayHaveEffect = true;
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original model source/owner changed; local closure remains pinned.");
        }
        return KernelResult.Ok();
    }

    private KernelResult<VirtualDomainRecord> ResolveVirtualDomain(VirtualDomainBinding binding)
    {
        lock (_secureDomainLifecycleGate)
        {
            if (_backendEpochExhausted)
                return KernelResult<VirtualDomainRecord>.Fail(KernelError.CapacityExhausted, "Platform backend generation space is exhausted.");
            if (!_virtualDomainBindings.TryGetValue(binding.BindingId, out VirtualDomainRecord? record))
                return KernelResult<VirtualDomainRecord>.Fail(KernelError.PlatformBindingNotFound, "Virtual-domain platform binding was not found.");
            if (record.Binding.Generation != binding.Generation)
                return KernelResult<VirtualDomainRecord>.Fail(KernelError.StaleGeneration, "Virtual-domain platform binding generation is stale.");
            if (record.Binding != binding)
                return KernelResult<VirtualDomainRecord>.Fail(KernelError.WrongPlatformDomain, "Virtual-domain platform binding belongs to another local owner.");
            if (record.CreatedInEpoch != BackendEpoch)
                return KernelResult<VirtualDomainRecord>.Fail(KernelError.StaleGeneration, "Model lease belongs to an earlier backend tuple.");
            if (!HasOriginalModelOwner(record) || !_originalVirtualDomainOwners.TryGetValue(binding.BindingId, out VirtualDomainRecord? original) || !ReferenceEquals(original, record))
                return KernelResult<VirtualDomainRecord>.Fail(KernelError.PlatformFaulted,
                    "Model admission/cleanup requires the canonical original owner; routing cannot replace it.");
            return KernelResult<VirtualDomainRecord>.Ok(record);
        }
    }
}
