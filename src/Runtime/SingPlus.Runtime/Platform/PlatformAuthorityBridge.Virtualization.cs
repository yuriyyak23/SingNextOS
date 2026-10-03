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

    private sealed record VirtualDomainRecord(
        VirtualDomainBinding Binding,
        PlatformProviderVirtualDomainLease ProviderLease)
    {
        public PlatformBackendEpoch CreatedInEpoch { get; init; }
        public bool Quarantined { get; set; }
        public bool ReceiptValid { get; init; }
        public bool TransitionInFlight { get; set; }
        public bool TransitionMayHaveEffect { get; set; }
        public bool CloseInFlight { get; set; }
        public bool CloseMayHaveEffect { get; set; }
    }

    private readonly Dictionary<VirtualDomainBindingId, VirtualDomainRecord> _virtualDomainBindings = [];
    // Admission and unresolved effects belong to the same existing model-domain owner.
    private readonly HashSet<PlatformDomainIdentity> _pendingVirtualDomainCreates = [];
    private readonly Dictionary<PlatformDomainIdentity, PlatformBackendEpoch> _unresolvedVirtualDomainCreates = [];
    private ulong _nextVirtualDomainBindingId = 1;

    internal bool HasUnresolvedVirtualDomainCreate(PlatformDomainIdentity owner)
    {
        lock (_secureDomainLifecycleGate)
            return _pendingVirtualDomainCreates.Contains(owner) || _unresolvedVirtualDomainCreates.ContainsKey(owner);
    }

    internal KernelResult<VirtualDomainBinding> CreateVirtualDomain(
        PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<VirtualDomainBinding, Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
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
        VirtualDomainBinding binding = default;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                if (_backendEpochExhausted || _nextVirtualDomainBindingId == ulong.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Model-domain generation space is exhausted.");
                if (_pendingVirtualDomainCreates.Contains(owner))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Model-domain creation is pending for this exact subject.");
                if (_unresolvedVirtualDomainCreates.ContainsKey(owner))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Model-domain creation has an unresolved effect for this exact subject.");
                KernelResult Commit()
                {
                    epoch = BackendEpoch;
                    binding = new(new(_nextVirtualDomainBindingId++), new(1), owner.Process);
                    _pendingVirtualDomainCreates.Add(owner);
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return KernelResult<VirtualDomainBinding>.Fail(admitted.Error, admitted.Message!);
        try
        {
            PlatformAuthorityResult<PlatformProviderVirtualDomainLease> result;
            try { result = provider.CreateVirtualDomain(owner, profile); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate) _unresolvedVirtualDomainCreates.TryAdd(owner, epoch);
                return KernelResult<VirtualDomainBinding>.Fail(KernelError.PlatformFaulted,
                    $"Model creation may have taken effect without a lease: {exception.Message}");
            }
            if (!result.IsSuccess)
            {
                lock (_secureDomainLifecycleGate)
                    if (result.Status != PlatformAuthorityStatus.NotAccepted || BackendEpoch != epoch)
                        _unresolvedVirtualDomainCreates.TryAdd(owner, epoch);
                return FromProviderFailure<VirtualDomainBinding>(result.Status, result.Message);
            }
            var lease = result.Value!;
            var valid = lease.LeaseId.Value != 0 && lease.Generation.Value != 0 && lease.Owner == owner;
            VirtualDomainRecord record;
            lock (_secureDomainLifecycleGate)
            {
                record = new(binding, lease)
                {
                    CreatedInEpoch = epoch, ReceiptValid = valid,
                    Quarantined = !valid || BackendEpoch != epoch || _backendEpochExhausted
                };
                _virtualDomainBindings.Add(binding.BindingId, record);
                if (!valid || BackendEpoch != epoch || _backendEpochExhausted)
                    _unresolvedVirtualDomainCreates.TryAdd(owner, epoch);
            }
            KernelResult CommitLocal(Func<KernelResult> commit) =>
                CommitModelDomainConsumer(binding, commit);
            KernelResult completed;
            try { completed = completionConsumer is null ? CommitLocal(KernelResult.Ok) : completionConsumer(binding, CommitLocal); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate) _unresolvedVirtualDomainCreates.TryAdd(owner, epoch);
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
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                var current = ResolveVirtualDomain(binding);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                record = current.Value!;
                if (record.CloseInFlight || record.TransitionInFlight)
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Model close/transition is pending.");
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
        if (!admitted.IsSuccess) return admitted;
        void PinUnknown()
        {
            lock (_secureDomainLifecycleGate)
            {
                record.Quarantined = true;
                record.TransitionMayHaveEffect = true;
                if (_virtualDomainBindings.TryGetValue(binding.BindingId, out var current) &&
                    current.ProviderLease == record.ProviderLease && current.Binding.Owner == record.Binding.Owner)
                {
                    current.Quarantined = true;
                    current.TransitionMayHaveEffect = true;
                }
            }
        }
        try
        {
            var result = provider.TransitionVirtualDomain(record.ProviderLease, transition);
            lock (_secureDomainLifecycleGate)
            {
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
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    var current = ResolveVirtualDomain(binding);
                    if (!current.IsSuccess || !ReferenceEquals(current.Value, record) || record.Quarantined)
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
                if (_virtualDomainBindings.TryGetValue(binding.BindingId, out var current) &&
                    current.ProviderLease == record.ProviderLease && current.Binding.Owner == record.Binding.Owner &&
                    current.CreatedInEpoch == record.CreatedInEpoch)
                    current.TransitionInFlight = false;
            }
        }
    }

    internal KernelResult RevokeVirtualDomain(VirtualDomainBinding binding)
    {
        if (_provider is not IPlatformVirtualizationProvider provider) return KernelResult.Fail(KernelError.PlatformUnsupported, "Platform virtualization provider is unavailable.");
        VirtualDomainRecord record;
        lock (_secureDomainLifecycleGate)
        {
            var current = ResolveVirtualDomain(binding);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            record = current.Value!;
            if (!record.ReceiptValid || record.CloseMayHaveEffect || record.TransitionMayHaveEffect)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Model-domain lease lacks unambiguous closure evidence.");
            if (record.CloseInFlight || record.TransitionInFlight || _pendingVirtualDomainCreates.Any(x => x.Process == binding.Owner))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Model-domain creation or close is pending.");
            record.CloseInFlight = true;
            record.Quarantined = true;
        }
        try
        {
            var result = provider.RevokeVirtualDomain(record.ProviderLease);
            lock (_secureDomainLifecycleGate)
            {
                if (!_virtualDomainBindings.TryGetValue(binding.BindingId, out var current) || !ReferenceEquals(current, record) ||
                    current.Binding != binding || BackendEpoch != record.CreatedInEpoch || _backendEpochExhausted)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Model revoke lost exact record/backend continuity.");
                if (!result.IsSuccess) return FromProviderFailure(result.Status, result.Message);
                _virtualDomainBindings.Remove(binding.BindingId);
                return KernelResult.Ok();
            }
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
                if (_virtualDomainBindings.TryGetValue(binding.BindingId, out var current) &&
                    current.ProviderLease == record.ProviderLease && current.Binding.Owner == record.Binding.Owner &&
                    current.CreatedInEpoch == record.CreatedInEpoch)
                    current.CloseInFlight = false;
            }
        }
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
            return KernelResult<VirtualDomainRecord>.Ok(record);
        }
    }
}
