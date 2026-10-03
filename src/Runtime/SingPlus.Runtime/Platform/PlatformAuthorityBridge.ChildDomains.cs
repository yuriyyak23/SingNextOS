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
    private sealed class ChildBindingRecord(
        PlatformChildBinding binding,
        PlatformProviderChildDomainLease providerLease,
        PlatformChildBinding? parentChild = null)
    {
        public PlatformChildBinding Binding { get; } = binding;
        public PlatformProviderChildDomainLease ProviderLease { get; } = providerLease;
        public PlatformChildBinding? ParentChild { get; } = parentChild;
        public PlatformChildDomainState State { get; set; } = PlatformChildDomainState.Created;
        public ulong LastEventSequence { get; set; }
        public ulong LastTrapSequence { get; set; }
        public bool NestedCreateMayHaveEffect { get; set; }
        public bool TransitionInFlight { get; set; }
        public bool CloseInFlight { get; set; }
        public int PendingChildEffects { get; set; }
    }

    private sealed class GuestBindingRecord(
        PlatformGuestMapping mapping,
        PlatformProviderGuestRegionMappingLease providerLease)
    {
        public PlatformGuestMapping Mapping { get; } = mapping;
        public PlatformProviderGuestRegionMappingLease ProviderLease { get; } = providerLease;
        public PlatformExternalClosureState Closure { get; set; } = PlatformExternalClosureState.Active;
        public int PendingExecutableEffects { get; set; }
    }

    private sealed class VirtualIoBindingRecord(
        PlatformVirtualIoBinding binding,
        PlatformProviderVirtualIoLease providerLease)
    {
        public PlatformVirtualIoBinding Binding { get; } = binding;
        public PlatformProviderVirtualIoLease ProviderLease { get; } = providerLease;
        public PlatformExternalClosureState Closure { get; set; } = PlatformExternalClosureState.Active;
    }
    private sealed class ExecutableArtifactRecord(
        PlatformExecutableArtifactBinding binding,
        PlatformExecutableArtifactReceipt providerReceipt)
    {
        public PlatformExecutableArtifactBinding Binding { get; } = binding;
        public PlatformExecutableArtifactReceipt ProviderReceipt { get; } = providerReceipt;
        public bool Started { get; set; }
        public PlatformChildExecutionReceipt? ExecutionReceipt { get; set; }
        public bool StartInFlight { get; set; }
    }

    private readonly Dictionary<PlatformChildBindingId, ChildBindingRecord> _childBindings = [];
    private readonly Dictionary<PlatformGuestMappingId, GuestBindingRecord> _guestBindings = [];
    private readonly Dictionary<PlatformVirtualIoBindingId, VirtualIoBindingRecord> _virtualIoBindings = [];
    private readonly Dictionary<PlatformExecutableArtifactBindingId, ExecutableArtifactRecord> _executableArtifacts = [];
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
            guest.Mapping.Child != child.Binding || guest.Mapping.ParentMapping != parent.Mapping)
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact executable dependency owner changed.");
        if (child.TransitionInFlight || child.CloseInFlight)
            return KernelResult.Fail(KernelError.PlatformBindingActive, "Child lifecycle is pending.");
        if (child.State is PlatformChildDomainState.Faulted or PlatformChildDomainState.Closed or PlatformChildDomainState.Draining ||
            guest.Closure != PlatformExternalClosureState.Active || parent.BackendEpoch != BackendEpoch || _backendEpochExhausted)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Executable dependencies are not active.");
        var root = ValidateDomain(child.Binding.ParentBinding, child.Binding.ParentBinding.Subject);
        if (!root.IsSuccess) return root;
        return ValidateMapping(parent.Mapping, child.Binding.ParentBinding.Subject);
    }

    internal KernelResult<PlatformExecutableArtifactBinding> BindChildExecutableArtifact(
        PlatformChildBinding childBinding, PlatformGuestMapping guestMapping,
        ReadOnlyMemory<byte> immutablePackage, int maximumExecutionSteps,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<PlatformExecutableArtifactBinding, Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
    {
        if (_provider is not IPlatformChildExecutionProvider provider)
            return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.PlatformUnsupported, "Executable artifact provider is unavailable.");
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
        var request = new PlatformExecutableArtifactRequest(child.ProviderLease, guest.ProviderLease, immutablePackage, maximumExecutionSteps);
        var valid = PlatformChildExecutionContract.ValidateRequest(request);
        if (!valid.IsSuccess) return FromProviderFailure<PlatformExecutableArtifactBinding>(valid.Status, valid.Message);
        // Capture caller memory outside owners; provider receives the exact private package.
        request = request with { ImmutablePackage = immutablePackage.ToArray() };
        var digest = Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(request.ImmutablePackage.Span));
        var generation = ValidateMappingClosureGeneration(parent); // provider observation outside owner locks
        if (!generation.IsSuccess) return KernelResult<PlatformExecutableArtifactBinding>.Fail(generation.Error, generation.Message!);
        PlatformBackendEpoch backendEpoch = default;
        PlatformExecutableArtifactBinding binding = default;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                var current = ValidateExecutableInputs(child, guest, parent);
                if (!current.IsSuccess) return current;
                if (child.PendingChildEffects != 0 || guest.PendingExecutableEffects != 0 ||
                    _executableArtifacts.Values.Any(x => x.Binding.GuestMapping == guestMapping))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Executable guest already has pending or retained artifact admission.");
                if (_nextExecutableArtifactBindingId == ulong.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Executable artifact identity space is exhausted.");
                KernelResult Commit()
                {
                    binding = new(new(_nextExecutableArtifactBindingId++), new(1), childBinding, guestMapping);
                    guest.PendingExecutableEffects++;
                    child.PendingChildEffects++;
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
            var result = provider.BindExecutableArtifact(request);
            if (!result.IsSuccess)
            {
                if (result.Status != PlatformAuthorityStatus.NotAccepted) FaultPinExecutableArtifactDependencies(child, guest);
                return FromProviderFailure<PlatformExecutableArtifactBinding>(result.Status, result.Message);
            }
            var exact = PlatformChildExecutionContract.ValidateAdmissionReceipt(request, result.Value!);
            if (!exact.IsSuccess || !string.Equals(result.Value!.ContentDigest, digest, StringComparison.Ordinal))
            {
                FaultPinExecutableArtifactDependencies(child, guest);
                return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.PlatformFaulted, "Executable receipt does not identify the exact captured package/dependency tuple.");
            }
            ExecutableArtifactRecord artifact;
            lock (_secureDomainLifecycleGate)
            {
                // Known source-tuple receipt is retained before mutable permission checks.
                artifact = new(binding, result.Value);
                _executableArtifacts.Add(binding.BindingId, artifact);
            }
            generation = ValidateMappingClosureGeneration(parent);
            KernelResult continuity;
            lock (_secureDomainLifecycleGate)
            {
                continuity = ValidateExecutableInputs(child, guest, parent);
                if (!generation.IsSuccess || !continuity.IsSuccess || BackendEpoch != backendEpoch)
                {
                    FaultPinExecutableArtifactDependencies(child, guest);
                    continuity = KernelResult.Fail(KernelError.PlatformFaulted, "Executable admission lost exact dependency continuity; receipt retained.");
                }
            }
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    var current = ValidateExecutableInputs(child, guest, parent);
                    if (!current.IsSuccess) return current;
                    if (BackendEpoch != backendEpoch || !_executableArtifacts.TryGetValue(binding.BindingId, out var saved) ||
                        !ReferenceEquals(saved, artifact))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Exact executable receipt changed before local publication.");
                    return commit();
                }
            }
            var completed = completionConsumer is null ? continuity : completionConsumer(binding, CommitCurrent);
            return completed.IsSuccess ? KernelResult<PlatformExecutableArtifactBinding>.Ok(binding) :
                KernelResult<PlatformExecutableArtifactBinding>.Fail(completed.Error, completed.Message!);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            FaultPinExecutableArtifactDependencies(child, guest);
            return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.PlatformFaulted, $"Executable admission may have taken effect without exact publication: {exception.Message}");
        }
        finally
        {
            lock (_secureDomainLifecycleGate) { guest.PendingExecutableEffects--; child.PendingChildEffects--; }
        }
    }

    internal KernelResult StartChildExecutableArtifact(PlatformExecutableArtifactBinding binding,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null)
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
        }
        var generation = ValidateMappingClosureGeneration(parent); // provider observation outside owner locks
        if (!generation.IsSuccess) return generation;
        PlatformChildExecutionStartRequest request = default;
        PlatformBackendEpoch backendEpoch = default;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                var current = ValidateExecutableInputs(child, guest, parent);
                if (!current.IsSuccess) return current;
                if (!_executableArtifacts.TryGetValue(binding.BindingId, out var saved) || !ReferenceEquals(saved, artifact))
                    return KernelResult.Fail(KernelError.StaleGeneration, "Executable artifact owner changed.");
                if (artifact.Started) return KernelResult.Fail(KernelError.PlatformDenied, "Executable artifact was already started.");
                if (artifact.StartInFlight || child.PendingChildEffects != 0 || guest.PendingExecutableEffects != 0)
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Executable artifact was started or an effect is pending.");
                if (_nextChildStartOperationId == ulong.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Executable start identity space is exhausted.");
                request = new(child.ProviderLease, artifact.ProviderReceipt, new(_nextChildStartOperationId), new(1));
                var valid = PlatformChildExecutionContract.ValidateStart(request);
                if (!valid.IsSuccess) return FromProviderFailure(valid.Status, valid.Message);
                KernelResult Commit()
                {
                    _nextChildStartOperationId++;
                    guest.PendingExecutableEffects++;
                    child.PendingChildEffects++;
                    artifact.StartInFlight = true;
                    backendEpoch = BackendEpoch;
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        var admitted = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!admitted.IsSuccess) return admitted;
        try
        {
            var result = provider.StartExecutableArtifact(request);
            if (!result.IsSuccess)
            {
                if (result.Status != PlatformAuthorityStatus.NotAccepted) FaultPinExecutableArtifactDependencies(child, guest);
                return FromProviderFailure(result.Status, result.Message);
            }
            var exact = PlatformChildExecutionContract.ValidateExecution(request, result.Value!);
            if (!exact.IsSuccess) { FaultPinExecutableArtifactDependencies(child, guest); return KernelResult.Fail(KernelError.PlatformFaulted, exact.Message!); }
            lock (_secureDomainLifecycleGate)
            {
                artifact.ExecutionReceipt = result.Value;
                artifact.Started = true; // exact returned provider fact, never local permission or release
            }
            generation = ValidateMappingClosureGeneration(parent);
            KernelResult continuity;
            lock (_secureDomainLifecycleGate)
            {
                continuity = ValidateExecutableInputs(child, guest, parent);
                if (!generation.IsSuccess || !continuity.IsSuccess || BackendEpoch != backendEpoch ||
                    !_executableArtifacts.TryGetValue(binding.BindingId, out var saved) || !ReferenceEquals(saved, artifact))
                {
                    FaultPinExecutableArtifactDependencies(child, guest);
                    continuity = KernelResult.Fail(KernelError.PlatformFaulted, "Executable receipt retained without current dependency continuity.");
                }
                else child.State = PlatformChildDomainState.Running;
            }
            KernelResult CommitCurrent(Func<KernelResult> commit)
            {
                lock (_secureDomainLifecycleGate)
                {
                    var current = ValidateExecutableInputs(child, guest, parent);
                    if (!current.IsSuccess) return current;
                    if (BackendEpoch != backendEpoch || !_executableArtifacts.TryGetValue(binding.BindingId, out var saved) ||
                        !ReferenceEquals(saved, artifact) || !artifact.Started)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Exact execution receipt changed before local state publication.");
                    return commit();
                }
            }
            return completionConsumer is null ? continuity : completionConsumer(CommitCurrent);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            FaultPinExecutableArtifactDependencies(child, guest);
            return KernelResult.Fail(KernelError.PlatformFaulted, $"Executable start may have taken effect without exact publication: {exception.Message}");
        }
        finally
        {
            lock (_secureDomainLifecycleGate) { guest.PendingExecutableEffects--; child.PendingChildEffects--; artifact.StartInFlight = false; }
        }
    }

    private void FaultPinExecutableArtifactDependencies(ChildBindingRecord child, GuestBindingRecord guest)
    {
        lock (_secureDomainLifecycleGate)
        {
            child.State = PlatformChildDomainState.Faulted;
            guest.Closure = PlatformExternalClosureState.Faulted;
            if (_mappings.TryGetValue(guest.Mapping.ParentMapping.MappingId, out var parent))
                parent.ClosureState = PlatformExternalClosureState.Faulted;
        }
    }

    internal KernelResult<PlatformChildBinding> CreateChildDomain(
        PlatformDomainBinding parent, PlatformDomainIdentity expectedParent,
        PlatformChildDomainIntent intent,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null)
    {
        var parentValidation = ValidateDomain(parent, expectedParent);
        if (!parentValidation.IsSuccess)
            return KernelResult<PlatformChildBinding>.Fail(parentValidation.Error, parentValidation.Message!);
        if (!SupportsChildFeature(PlatformFeatureFamily.ChildDomainLifecycle, PlatformChildDomainContract.ContractVersion) ||
            _provider is not IPlatformChildDomainProvider provider)
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformUnsupported, "Executable child-domain admission is unavailable.");

        DomainRecord parentRecord = _domains[parent.BindingId];
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
            if (!_domains.TryGetValue(parent.BindingId, out var exact) || exact != parentRecord)
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
        PlatformAuthorityResult<PlatformProviderChildDomainLease> result;
        try { result = provider.CreateChildDomain(parentRecord.ProviderLease, intent); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            parentRecord.ChildCreateMayHaveEffect = true;
            QuarantineDomain(parentRecord);
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                $"Child creation may have taken effect without a receipt: {exception.Message}");
        }
        if (BackendEpoch != backendEpoch || _backendEpochExhausted ||
            !_domains.TryGetValue(parent.BindingId, out var currentParent) || currentParent != parentRecord ||
            parentRecord.AuthorityState != DomainAuthorityState.Active)
        {
            parentRecord.ChildCreateMayHaveEffect = true;
            QuarantineDomain(parentRecord);
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                "Backend generation changed during child creation; the parent remains pinned.");
        }
        if (!result.IsSuccess)
        {
            if (result.Status != PlatformAuthorityStatus.NotAccepted)
            {
                parentRecord.ChildCreateMayHaveEffect = true;
                QuarantineDomain(parentRecord);
            }
            return FromProviderFailure<PlatformChildBinding>(result.Status, result.Message);
        }

        PlatformProviderChildDomainLease lease = result.Value!;
        var leaseValidation = PlatformChildDomainContract.ValidateLease(parentRecord.ProviderLease, intent, lease);
        if (!leaseValidation.IsSuccess)
        {
            var cleanupProven = false;
            try
            {
                var cleanup = provider.CloseChildDomain(lease);
                cleanupProven = cleanup.IsSuccess &&
                    PlatformChildDomainContract.ValidateClosureReceipt(lease, cleanup.Value!).IsSuccess &&
                    BackendEpoch == backendEpoch;
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                // No exact cleanup receipt keeps the parent pinned.
            }
            if (!cleanupProven)
            {
                parentRecord.ChildCreateMayHaveEffect = true;
                QuarantineDomain(parentRecord);
            }
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                leaseValidation.Message ?? "Provider returned malformed child-domain authority.");
        }

        lock (_secureDomainLifecycleGate)
        {
            if (BackendEpoch != backendEpoch || _backendEpochExhausted ||
                !_domains.TryGetValue(parent.BindingId, out var publicationParent) || publicationParent != parentRecord ||
                parentRecord.AuthorityState != DomainAuthorityState.Active ||
                parentRecord.ParentRevokeMayHaveEffect)
            {
                parentRecord.ChildCreateMayHaveEffect = true;
                QuarantineDomain(parentRecord);
                return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                    "Parent domain changed before child binding publication.");
            }
            var binding = new PlatformChildBinding(new(_nextChildBindingId++), new(1), parent, expectedParent.Process);
            _childBindings.Add(binding.BindingId, new(binding, lease));
            return KernelResult<PlatformChildBinding>.Ok(binding);
        }
        }
        finally { lock (_secureDomainLifecycleGate) parentRecord.PendingChildCreates--; }
    }

    internal KernelResult<PlatformChildBinding> CreateNestedChildDomain(
        PlatformChildBinding parentChild, ProcessHandle expectedOwner,
        PlatformChildDomainIntent intent,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null)
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
        PlatformBackendEpoch backendEpoch = default;
        KernelResult Begin()
        {
        lock (_secureDomainLifecycleGate)
        {
            if (parentRecord.TransitionInFlight || parentRecord.CloseInFlight ||
                parentRecord.State == PlatformChildDomainState.Faulted ||
                !PlatformNestedDomainContract.ValidateRequest(request, parentRecord.State).IsSuccess)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Immediate parent child changed before nested admission.");
            var fresh = ResolveChild(parentChild);
            if (!fresh.IsSuccess || !ReferenceEquals(fresh.Value, parentRecord))
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact nested parent changed.");
            var root = ValidateDomain(parentChild.ParentBinding, parentChild.ParentBinding.Subject);
            if (!root.IsSuccess) return root;
            KernelResult Commit()
            {
                parentRecord.PendingChildEffects++;
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
        PlatformAuthorityResult<PlatformProviderNestedDomainLease> result;
        try { result = provider.CreateNestedChildDomain(request); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            parentRecord.NestedCreateMayHaveEffect = true;
            parentRecord.State = PlatformChildDomainState.Faulted;
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                $"Nested child creation may have taken effect without a receipt: {exception.Message}");
        }
        if (BackendEpoch != backendEpoch || parentRecord.State == PlatformChildDomainState.Faulted)
        {
            parentRecord.NestedCreateMayHaveEffect = true;
            parentRecord.State = PlatformChildDomainState.Faulted;
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                "Backend generation changed during nested child creation.");
        }
        if (!result.IsSuccess)
        {
            if (result.Status != PlatformAuthorityStatus.NotAccepted)
            {
                parentRecord.NestedCreateMayHaveEffect = true;
                parentRecord.State = PlatformChildDomainState.Faulted;
            }
            return FromProviderFailure<PlatformChildBinding>(result.Status, result.Message);
        }
        var nestedLease = result.Value!;
        var lease = nestedLease.ChildLease;
        var exact = PlatformNestedDomainContract.ValidateLease(request, nestedLease);
        if (!exact.IsSuccess)
        {
            var cleanupProven = false;
            if (_provider is IPlatformChildDomainProvider lifecycle)
            {
                try
                {
                    var cleanup = lifecycle.CloseChildDomain(lease);
                    cleanupProven = cleanup.IsSuccess &&
                        PlatformChildDomainContract.ValidateClosureReceipt(lease, cleanup.Value!).IsSuccess &&
                        BackendEpoch == backendEpoch;
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    // Missing exact cleanup keeps the parent child pinned.
                }
            }
            if (!cleanupProven)
            {
                parentRecord.NestedCreateMayHaveEffect = true;
                parentRecord.State = PlatformChildDomainState.Faulted;
            }
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                exact.Message ?? "Provider returned malformed nested-domain authority.");
        }
        lock (_secureDomainLifecycleGate)
        {
            if (BackendEpoch != backendEpoch || parentRecord.State == PlatformChildDomainState.Faulted)
            {
                parentRecord.NestedCreateMayHaveEffect = true;
                parentRecord.State = PlatformChildDomainState.Faulted;
                return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                    "Immediate parent child changed before nested publication.");
            }
            var binding = new PlatformChildBinding(new(_nextChildBindingId++), new(1),
                parentChild.ParentBinding, expectedOwner);
            _childBindings.Add(binding.BindingId, new(binding, lease, parentChild));
            return KernelResult<PlatformChildBinding>.Ok(binding);
        }
        }
        finally { lock (_secureDomainLifecycleGate) parentRecord.PendingChildEffects--; }
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
    private KernelResult ValidateActiveChildLineage(ChildBindingRecord record)
    {
        if (!_childBindings.TryGetValue(record.Binding.BindingId, out var current) || !ReferenceEquals(current, record))
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
        ChildBindingRecord record = null!;
        PlatformBackendEpoch backendEpoch = default;
        var committedDrain = false;
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                if (allowCommittedDrain && transition == PlatformChildDomainTransition.BeginDrain &&
                    _childBindings.TryGetValue(binding.BindingId, out var committed) && committed.Binding == binding &&
                    committed.State is PlatformChildDomainState.Draining or PlatformChildDomainState.Closed)
                {
                    committedDrain = true;
                    return KernelResult.Ok();
                }
                var resolved = ResolveChild(binding);
                if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
                record = resolved.Value!;
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
            var result = provider.TransitionChildDomain(record.ProviderLease, transition);
            lock (_secureDomainLifecycleGate)
            {
                if (!_childBindings.TryGetValue(binding.BindingId, out var current) ||
                    !ReferenceEquals(current, record) || current.Binding != binding ||
                    BackendEpoch != backendEpoch || _backendEpochExhausted || record.State == PlatformChildDomainState.Faulted)
                {
                    record.State = PlatformChildDomainState.Faulted;
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
    }

    internal KernelResult CloseChildDomain(PlatformChildBinding binding, bool allowCommittedClosure = false)
    {
        lock (_secureDomainLifecycleGate)
        {
            // Only the existing owner state committed by exact closure receipt
            // makes retry idempotent; absence/fault/terminal response cannot.
            if (allowCommittedClosure && _childBindings.TryGetValue(binding.BindingId, out var committed) &&
                committed.Binding == binding && committed.State == PlatformChildDomainState.Closed)
                return KernelResult.Ok();
        }
        var resolved = ResolveChild(binding, allowDraining: true);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        ChildBindingRecord record = resolved.Value!;
        if (_provider is not IPlatformChildDomainProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Child-domain provider is unavailable.");
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            if (record.TransitionInFlight || record.CloseInFlight || record.PendingChildEffects != 0)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Child lifecycle or external effect is already in flight.");
            if (record.State != PlatformChildDomainState.Draining)
                return KernelResult.Fail(KernelError.PlatformDenied, "Child closure requires an explicit draining state.");
            if (record.NestedCreateMayHaveEffect)
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Nested child creation may have taken effect without exact closure.");
            if (_childBindings.Values.Any(x => x.ParentChild == binding && x.State != PlatformChildDomainState.Closed))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Nested child domains must reach exact terminal closure before their parent child closes.");
            if (_guestBindings.Values.Any(x => x.Mapping.Child == binding && x.Closure != PlatformExternalClosureState.Closed) ||
                _virtualIoBindings.Values.Any(x => x.Binding.Child == binding && x.Closure != PlatformExternalClosureState.Closed))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Child mappings and virtual-I/O bindings must close before child closure.");
            record.CloseInFlight = true;
            backendEpoch = BackendEpoch;
        }
        PlatformAuthorityResult<PlatformChildDomainClosureReceipt> result;
        try { result = provider.CloseChildDomain(record.ProviderLease); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate)
            {
                record.State = PlatformChildDomainState.Faulted;
                record.CloseInFlight = false;
            }
            return KernelResult.Fail(KernelError.PlatformFaulted,
                $"Child closure may have taken effect without a receipt: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
        {
            record.CloseInFlight = false;
            if (BackendEpoch != backendEpoch || record.State == PlatformChildDomainState.Faulted)
            {
                record.State = PlatformChildDomainState.Faulted;
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Backend reset during child closure leaves the child quarantined.");
            }
            if (!result.IsSuccess)
            {
                record.State = PlatformChildDomainState.Faulted;
                return FromProviderFailure(result.Status, result.Message);
            }
            var receipt = PlatformChildDomainContract.ValidateClosureReceipt(record.ProviderLease, result.Value!);
            if (!receipt.IsSuccess)
            {
                record.State = PlatformChildDomainState.Faulted;
                return KernelResult.Fail(KernelError.PlatformFaulted, receipt.Message ?? "Child closure evidence is ambiguous.");
            }
            record.State = PlatformChildDomainState.Closed;
            return KernelResult.Ok();
        }
    }

    private KernelResult<ChildBindingRecord> ResolveChild(PlatformChildBinding binding, bool allowDraining = false)
    {
        lock (_secureDomainLifecycleGate)
        {
        if (!_childBindings.TryGetValue(binding.BindingId, out ChildBindingRecord? record))
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformBindingNotFound, "Child binding was not found.");
        if (record.Binding.Generation != binding.Generation)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.StaleGeneration, "Child binding generation is stale.");
        if (record.Binding != binding)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.WrongPlatformDomain, "Child binding has a different parent or owner.");
        lock (_secureDomainLifecycleGate)
            if (record.TransitionInFlight)
                return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformBindingActive,
                    "Child transition is in flight.");
        if (record.State == PlatformChildDomainState.Closed)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformBindingRevoked, "Child binding is closed.");
        if (record.State == PlatformChildDomainState.Faulted)
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
