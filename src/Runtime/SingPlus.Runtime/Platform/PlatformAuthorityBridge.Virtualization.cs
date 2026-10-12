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

    private sealed class ModelCreateInvocationFact(PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile, PlatformBackendEpoch epoch)
    {
        public PlatformDomainIdentity Owner { get; } = owner;
        public PlatformVirtualDomainProfile Profile { get; } = profile;
        public PlatformBackendEpoch AdmissionEpoch { get; } = epoch;
        public PlatformProviderIncarnation ProviderIncarnation { get; set; }
        public bool EffectMayHaveOccurred { get; set; }
        public PlatformAuthorityResult<PlatformProviderVirtualDomainLease>? Observation { get; set; }
    }
    private sealed record VirtualDomainRecord(
        VirtualDomainBinding Binding,
        PlatformProviderVirtualDomainLease ProviderLease)
    {
        public PlatformBackendEpoch CreatedInEpoch { get; init; }
        public ModelCreateInvocationFact? CreateInvocation { get; init; }
        public PlatformProviderIncarnation ProviderIncarnation { get; init; }
        public bool Quarantined { get; set; }
        public bool ReceiptValid { get; init; }
        public bool TransitionInFlight { get; set; }
        public bool TransitionMayHaveEffect { get; set; }
        public PlatformVirtualDomainTransition? TransitionIntent { get; set; }
        public PlatformAuthorityResult? TransitionObservation { get; set; }
        public bool CloseInFlight { get; set; }
        public bool CloseMayHaveEffect { get; set; }
        public bool ProviderClosureConfirmed { get; set; }
        public PlatformAuthorityResult? CloseObservation { get; set; }
    }

    private readonly Dictionary<VirtualDomainBindingId, VirtualDomainRecord> _virtualDomainBindings = [];
    // Stable index to the same authoritative owner; routing/reset copies never become permission.
    private readonly Dictionary<VirtualDomainBindingId, VirtualDomainRecord> _originalVirtualDomainOwners = [];
    // Admission and unresolved effects belong to the same existing model-domain owner.
    private readonly Dictionary<PlatformDomainIdentity, ModelCreateInvocationFact> _pendingVirtualDomainCreates = [];
    private readonly Dictionary<PlatformDomainIdentity, ModelCreateInvocationFact> _unresolvedVirtualDomainCreates = [];
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
            return _pendingVirtualDomainCreates.ContainsKey(owner) || _unresolvedVirtualDomainCreates.ContainsKey(owner);
    }

    internal KernelResult<VirtualDomainBinding> CreateVirtualDomain(
        PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<VirtualDomainBinding, Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null,
        Action<VirtualDomainBinding>? cleanupConsumer = null)
    {
        if (_provider is not IPlatformVirtualizationProvider provider ||
            !_featureManifest.Supports(PlatformFeatureFamily.VirtualizationDomains,
                PlatformVirtualizationContract.ContractVersion, PlatformFeatureAvailability.ModelOnly))
            return KernelResult<VirtualDomainBinding>.Fail(KernelError.PlatformUnsupported, "Platform virtualization ModelOnly contract is unavailable.");
        var subject = PlatformDomainContract.ValidateSubject(owner);
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
                if (_unresolvedVirtualDomainCreates.ContainsKey(owner))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Model-domain creation has an unresolved effect for this exact subject.");
                KernelResult Commit()
                {
                    epoch = BackendEpoch;
                    binding = new(new(_nextVirtualDomainBindingId++), new(1), owner.Process);
                    invocation = new(owner, profile, epoch);
                    _pendingVirtualDomainCreates.Add(owner, invocation);
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
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
                    if (source.Value == 0 || BackendEpoch != epoch || _backendEpochExhausted ||
                        !_pendingVirtualDomainCreates.TryGetValue(owner, out var pending) || !ReferenceEquals(pending, invocation))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Model create original source changed before effect.");
                    KernelResult Commit()
                    {
                        invocation.ProviderIncarnation = source;
                        invocation.EffectMayHaveOccurred = true;
                        return KernelResult.Ok();
                    }
                    return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
                }
            }
            var effectAdmission = admissionConsumer is null ? AdmitEffect() : admissionConsumer(AdmitEffect);
            if (!effectAdmission.IsSuccess)
                return KernelResult<VirtualDomainBinding>.Fail(effectAdmission.Error, effectAdmission.Message!);
            PlatformAuthorityResult<PlatformProviderVirtualDomainLease> result;
            try { result = provider.CreateVirtualDomain(owner, profile); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate) _unresolvedVirtualDomainCreates.TryAdd(owner, invocation);
                return KernelResult<VirtualDomainBinding>.Fail(KernelError.PlatformFaulted,
                    $"Model creation may have taken effect without a lease: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate) invocation.Observation = result;
            if (!result.IsSuccess)
            {
                lock (_secureDomainLifecycleGate)
                    if (result.Status != PlatformAuthorityStatus.NotAccepted || BackendEpoch != epoch)
                        _unresolvedVirtualDomainCreates.TryAdd(owner, invocation);
                return FromProviderFailure<VirtualDomainBinding>(result.Status, result.Message);
            }
            var lease = result.Value!;
            var valid = lease.LeaseId.Value != 0 && lease.Generation.Value != 0 && lease.Owner == owner;
            VirtualDomainRecord record;
            lock (_secureDomainLifecycleGate)
            {
                record = new(binding, lease)
                {
                    CreateInvocation = invocation, CreatedInEpoch = epoch, ProviderIncarnation = source, ReceiptValid = valid,
                    Quarantined = !valid || BackendEpoch != epoch || _backendEpochExhausted
                };
                _virtualDomainBindings.Add(binding.BindingId, record);
                _originalVirtualDomainOwners.Add(binding.BindingId, record);
                if (!valid || BackendEpoch != epoch || _backendEpochExhausted)
                    _unresolvedVirtualDomainCreates.TryAdd(owner, invocation);
            }
            try { cleanupConsumer?.Invoke(binding); }
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
                if (currentSource.Value == 0 || currentSource != source || BackendEpoch != epoch || _backendEpochExhausted ||
                    !_virtualDomainBindings.TryGetValue(binding.BindingId, out var exact) || !ReferenceEquals(exact, record) ||
                    !_originalVirtualDomainOwners.TryGetValue(binding.BindingId, out var original) || !ReferenceEquals(original, record))
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
        finally { lock (_secureDomainLifecycleGate) _pendingVirtualDomainCreates.Remove(owner); }
    }

    internal KernelResult CommitModelDomainConsumer(VirtualDomainBinding binding, Func<KernelResult> commit)
    {
        lock (_secureDomainLifecycleGate)
        {
            var current = ResolveVirtualDomain(binding);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (current.Value!.Quarantined || !current.Value.ReceiptValid || current.Value.CloseInFlight)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Model-domain receipt is quarantined or closing.");
            return commit();
        }
    }

    internal KernelResult TransitionVirtualDomain(VirtualDomainBinding binding, PlatformVirtualDomainTransition transition,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
    {
        if (_provider is not IPlatformVirtualizationProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Platform virtualization provider is unavailable.");
        if (!Enum.IsDefined(transition))
            return KernelResult.Fail(KernelError.PlatformDenied, "Unknown mandatory model transition.");
        VirtualDomainRecord record = null!;
        var retainedCleanup = false;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                var current = ResolveVirtualDomain(binding);
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
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess || retainedCleanup) return admitted;
        void PinUnknown()
        {
            lock (_secureDomainLifecycleGate)
            {
                record.Quarantined = true;
                record.TransitionMayHaveEffect = true;
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
            var exact = ValidateSource(source);
            if (!exact.IsSuccess) return exact;
            KernelResult FinalAdmission()
            {
                lock (_secureDomainLifecycleGate)
                {
                    var fresh = ValidateSource(source);
                    if (!fresh.IsSuccess || (record.Quarantined && transition != PlatformVirtualDomainTransition.BeginDrain) ||
                        !record.ReceiptValid || record.CloseMayHaveEffect || record.TransitionMayHaveEffect)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Model transition source admission changed.");
                    KernelResult RecordIntent()
                    {
                        record.TransitionIntent = transition;
                        record.TransitionObservation = null;
                        return KernelResult.Ok();
                    }
                    return sourceCommitConsumer is null ? RecordIntent() : sourceCommitConsumer(RecordIntent);
                }
            }
            var final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
            if (!final.IsSuccess) return final;
            var result = provider.TransitionVirtualDomain(record.ProviderLease, transition);
            lock (_secureDomainLifecycleGate)
            {
                // Preserve the original returned outcome before routing/publication checks.
                record.TransitionObservation = result;
                if (CurrentOriginalModelOwnerLocked(record) is { } correlated)
                {
                    correlated.TransitionIntent = transition;
                    correlated.TransitionObservation = result;
                }
                if (!_virtualDomainBindings.TryGetValue(binding.BindingId, out var current) ||
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
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    var current = ResolveVirtualDomain(binding);
                    if (!current.IsSuccess || !ReferenceEquals(current.Value, record) || record.Quarantined ||
                        source.Value == 0 || source != record.ProviderIncarnation)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Model receipt is no longer active for local publication.");
                    return commit();
                }
            }
            var completed = completionConsumer is null ? KernelResult.Ok() : completionConsumer(CommitCurrent);
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
                    !_virtualDomainBindings.TryGetValue(binding.BindingId, out var current) ||
                    !ReferenceEquals(current, record) || current.Binding != binding ||
                    !_originalVirtualDomainOwners.TryGetValue(binding.BindingId, out var original) || !ReferenceEquals(original, record))
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
        VirtualDomainRecord record;
        lock (_secureDomainLifecycleGate)
        {
            var current = ResolveVirtualDomain(binding);
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
                var exact = ValidateModelCleanupSourceLocked(record, binding, source);
                if (!exact.IsSuccess) return exact;
                invokeRevoke = !record.ProviderClosureConfirmed;
                if (invokeRevoke) record.CloseMayHaveEffect = true;
            }
            if (invokeRevoke)
            {
                PlatformAuthorityResult result;
                try { result = provider.RevokeVirtualDomain(record.ProviderLease); }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    lock (_secureDomainLifecycleGate) record.CloseMayHaveEffect = true;
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Model revoke outcome unknown; original owner pinned.");
                }
                lock (_secureDomainLifecycleGate)
                {
                    record.CloseObservation = result;
                    var owner = CurrentOriginalModelOwnerLocked(record);
                    if (owner is not null) owner.CloseObservation = result;
                    if (!result.IsSuccess) return FromProviderFailure(result.Status, result.Message);
                    // Cleanup evidence only, including the existing reset replacement.
                    record.ProviderClosureConfirmed = true;
                    if (owner is not null) owner.ProviderClosureConfirmed = true;
                }
            }
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Known model closure source unavailable; success retained for retry."); }
            KernelResult CommitCleanup(Func<Func<KernelResult>, KernelResult> localConsumer)
            {
                lock (_secureDomainLifecycleGate)
                {
                    var exact = ValidateModelCleanupSourceLocked(record, binding, source);
                    if (!exact.IsSuccess) return exact;
                    if (retainCleanupReceipt) return KernelResult.Ok();
                    return localConsumer(() =>
                    {
                        _virtualDomainBindings.Remove(binding.BindingId);
                        if (_originalVirtualDomainOwners.TryGetValue(binding.BindingId, out var original) && ReferenceEquals(original, record))
                            _originalVirtualDomainOwners.Remove(binding.BindingId);
                        if (record.CreateInvocation is { } created &&
                            _unresolvedVirtualDomainCreates.TryGetValue(created.Owner, out var retained) && ReferenceEquals(retained, created))
                            _unresolvedVirtualDomainCreates.Remove(created.Owner);
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
                var current = CurrentOriginalModelOwnerLocked(record);
                if (current is not null) current.CloseInFlight = false;
            }
        }
    }

    private VirtualDomainRecord? CurrentOriginalModelOwnerLocked(VirtualDomainRecord original)
    {
        if (_virtualDomainBindings.TryGetValue(original.Binding.BindingId, out var current) &&
            current.Binding.BindingId == original.Binding.BindingId &&
            current.ProviderLease == original.ProviderLease && current.ProviderIncarnation == original.ProviderIncarnation &&
            current.CreatedInEpoch == original.CreatedInEpoch && current.Binding.Owner == original.Binding.Owner)
            return current;
        return null;
    }

    private KernelResult ValidateModelCleanupSourceLocked(VirtualDomainRecord record,
        VirtualDomainBinding binding, PlatformProviderIncarnation source)
    {
        if (source.Value == 0 || source != record.ProviderIncarnation || BackendEpoch != record.CreatedInEpoch ||
            _backendEpochExhausted || !_virtualDomainBindings.TryGetValue(binding.BindingId, out var current) ||
            !ReferenceEquals(current, record) || current.Binding != binding ||
            !_originalVirtualDomainOwners.TryGetValue(binding.BindingId, out var original) || !ReferenceEquals(original, record))
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
            if (!_virtualDomainBindings.TryGetValue(binding.BindingId, out var record))
                return KernelResult<VirtualDomainRecord>.Fail(KernelError.PlatformBindingNotFound, "Virtual-domain platform binding was not found.");
            if (record.Binding.Generation != binding.Generation)
                return KernelResult<VirtualDomainRecord>.Fail(KernelError.StaleGeneration, "Virtual-domain platform binding generation is stale.");
            if (record.Binding != binding)
                return KernelResult<VirtualDomainRecord>.Fail(KernelError.WrongPlatformDomain, "Virtual-domain platform binding belongs to another local owner.");
            if (record.CreatedInEpoch != BackendEpoch)
                return KernelResult<VirtualDomainRecord>.Fail(KernelError.StaleGeneration, "Model lease belongs to an earlier backend tuple.");
            if (!_originalVirtualDomainOwners.TryGetValue(binding.BindingId, out var original) || !ReferenceEquals(original, record))
                return KernelResult<VirtualDomainRecord>.Fail(KernelError.PlatformFaulted,
                    "Model admission/cleanup requires the canonical original owner; routing cannot replace it.");
            return KernelResult<VirtualDomainRecord>.Ok(record);
        }
    }
}
