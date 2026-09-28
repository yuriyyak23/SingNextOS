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
    }

    private sealed class GuestBindingRecord(
        PlatformGuestMapping mapping,
        PlatformProviderGuestRegionMappingLease providerLease)
    {
        public PlatformGuestMapping Mapping { get; } = mapping;
        public PlatformProviderGuestRegionMappingLease ProviderLease { get; } = providerLease;
        public PlatformExternalClosureState Closure { get; set; } = PlatformExternalClosureState.Active;
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

    internal KernelResult<PlatformExecutableArtifactBinding> BindChildExecutableArtifact(
        PlatformChildBinding childBinding, PlatformGuestMapping guestMapping,
        ReadOnlyMemory<byte> immutablePackage, int maximumExecutionSteps)
    {
        var child = ResolveChild(childBinding);
        if (!child.IsSuccess) return KernelResult<PlatformExecutableArtifactBinding>.Fail(child.Error, child.Message!);
        if (!_guestBindings.TryGetValue(guestMapping.MappingId, out var guest) || guest.Mapping != guestMapping ||
            guest.Closure != PlatformExternalClosureState.Active || guestMapping.Child != childBinding)
            return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.StaleGeneration, "Exact guest mapping is absent, stale, or belongs to another child.");
        if (_provider is not IPlatformChildExecutionProvider provider)
            return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.PlatformUnsupported, "Executable artifact provider is unavailable.");
        var request = new PlatformExecutableArtifactRequest(child.Value!.ProviderLease, guest.ProviderLease,
            immutablePackage, maximumExecutionSteps);
        var result = provider.BindExecutableArtifact(request);
        if (!result.IsSuccess) return FromProviderFailure<PlatformExecutableArtifactBinding>(result.Status, result.Message);
        var exact = PlatformChildExecutionContract.ValidateAdmissionReceipt(request, result.Value!);
        if (!exact.IsSuccess)
        {
            child.Value!.State = PlatformChildDomainState.Faulted;
            return KernelResult<PlatformExecutableArtifactBinding>.Fail(KernelError.PlatformFaulted,
                exact.Message ?? "Executable artifact admission evidence is malformed.");
        }
        var binding = new PlatformExecutableArtifactBinding(new(_nextExecutableArtifactBindingId++), new(1), childBinding, guestMapping);
        _executableArtifacts.Add(binding.BindingId, new(binding, result.Value!));
        return KernelResult<PlatformExecutableArtifactBinding>.Ok(binding);
    }

    internal KernelResult StartChildExecutableArtifact(PlatformExecutableArtifactBinding binding)
    {
        if (!_executableArtifacts.TryGetValue(binding.BindingId, out var artifact))
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Executable artifact binding was not found.");
        if (artifact.Binding.Generation != binding.Generation)
            return KernelResult.Fail(KernelError.StaleGeneration, "Executable artifact binding generation is stale.");
        if (artifact.Binding != binding || artifact.Started)
            return KernelResult.Fail(KernelError.PlatformDenied, "Executable artifact binding differs or was already started.");
        var child = ResolveChild(binding.Child);
        if (!child.IsSuccess) return KernelResult.Fail(child.Error, child.Message!);
        var request = new PlatformChildExecutionStartRequest(child.Value!.ProviderLease, artifact.ProviderReceipt,
            new(_nextChildStartOperationId++), new(1));
        var admission = PlatformChildExecutionContract.ValidateStart(request);
        if (!admission.IsSuccess)
            return KernelResult.Fail(admission.Status == PlatformAuthorityStatus.Stale
                ? KernelError.StaleGeneration : KernelError.PlatformDenied, admission.Message!);
        if (_provider is not IPlatformChildExecutionProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Executable artifact provider is unavailable.");
        var result = provider.StartExecutableArtifact(request);
        if (!result.IsSuccess) { QuarantineChild(child.Value!, result.Status); return FromProviderFailure(result.Status, result.Message); }
        var exact = PlatformChildExecutionContract.ValidateExecution(request, result.Value!);
        if (!exact.IsSuccess) { child.Value!.State = PlatformChildDomainState.Faulted; return KernelResult.Fail(KernelError.PlatformFaulted, exact.Message!); }
        artifact.Started = true;
        child.Value!.State = PlatformChildDomainState.Running;
        return KernelResult.Ok();
    }

    internal KernelResult<PlatformChildBinding> CreateChildDomain(
        PlatformDomainBinding parent, PlatformDomainIdentity expectedParent,
        PlatformChildDomainIntent intent)
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
        var result = provider.CreateChildDomain(parentRecord.ProviderLease, intent);
        if (!result.IsSuccess)
        {
            if (RequiresDomainQuarantine(result.Status)) QuarantineDomain(parentRecord);
            return FromProviderFailure<PlatformChildBinding>(result.Status, result.Message);
        }

        PlatformProviderChildDomainLease lease = result.Value!;
        var leaseValidation = PlatformChildDomainContract.ValidateLease(parentRecord.ProviderLease, intent, lease);
        if (!leaseValidation.IsSuccess)
        {
            _ = provider.CloseChildDomain(lease);
            QuarantineDomain(parentRecord);
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                leaseValidation.Message ?? "Provider returned malformed child-domain authority.");
        }

        var binding = new PlatformChildBinding(new(_nextChildBindingId++), new(1), parent, expectedParent.Process);
        _childBindings.Add(binding.BindingId, new(binding, lease));
        return KernelResult<PlatformChildBinding>.Ok(binding);
    }

    internal KernelResult<PlatformChildBinding> CreateNestedChildDomain(
        PlatformChildBinding parentChild, ProcessHandle expectedOwner,
        PlatformChildDomainIntent intent)
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
        var result = provider.CreateNestedChildDomain(request);
        if (!result.IsSuccess)
        {
            QuarantineChild(parent.Value, result.Status);
            return FromProviderFailure<PlatformChildBinding>(result.Status, result.Message);
        }
        var nestedLease = result.Value!;
        var lease = nestedLease.ChildLease;
        var exact = PlatformNestedDomainContract.ValidateLease(request, nestedLease);
        if (!exact.IsSuccess)
        {
            if (_provider is IPlatformChildDomainProvider lifecycle)
                _ = lifecycle.CloseChildDomain(lease);
            parent.Value.State = PlatformChildDomainState.Faulted;
            return KernelResult<PlatformChildBinding>.Fail(KernelError.PlatformFaulted,
                exact.Message ?? "Provider returned malformed nested-domain authority.");
        }
        var binding = new PlatformChildBinding(new(_nextChildBindingId++), new(1),
            parentChild.ParentBinding, expectedOwner);
        _childBindings.Add(binding.BindingId, new(binding, lease, parentChild));
        return KernelResult<PlatformChildBinding>.Ok(binding);
    }

    internal KernelResult TransitionChildDomain(PlatformChildBinding binding, PlatformChildDomainTransition transition)
    {
        var resolved = ResolveChild(binding);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        ChildBindingRecord record = resolved.Value!;
        if (_provider is not IPlatformChildDomainProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Child-domain provider is unavailable.");
        if (!LegalTransition(record.State, transition))
            return KernelResult.Fail(KernelError.PlatformDenied, "Child-domain lifecycle transition is not legal from the current state.");
        var result = provider.TransitionChildDomain(record.ProviderLease, transition);
        if (!result.IsSuccess)
        {
            QuarantineChild(record, result.Status);
            return FromProviderFailure(result.Status, result.Message);
        }
        record.State = transition switch
        {
            PlatformChildDomainTransition.Start or PlatformChildDomainTransition.Resume => PlatformChildDomainState.Running,
            PlatformChildDomainTransition.Park => PlatformChildDomainState.Parked,
            PlatformChildDomainTransition.BeginDrain => PlatformChildDomainState.Draining,
            _ => PlatformChildDomainState.Faulted,
        };
        return KernelResult.Ok();
    }

    internal KernelResult CloseChildDomain(PlatformChildBinding binding)
    {
        var resolved = ResolveChild(binding, allowDraining: true);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        ChildBindingRecord record = resolved.Value!;
        if (record.State != PlatformChildDomainState.Draining)
            return KernelResult.Fail(KernelError.PlatformDenied, "Child closure requires an explicit draining state.");
        if (_childBindings.Values.Any(x => x.ParentChild == binding && x.State != PlatformChildDomainState.Closed))
            return KernelResult.Fail(KernelError.PlatformBindingActive,
                "Nested child domains must reach exact terminal closure before their parent child closes.");
        if (_guestBindings.Values.Any(x => x.Mapping.Child == binding && x.Closure != PlatformExternalClosureState.Closed) ||
            _virtualIoBindings.Values.Any(x => x.Binding.Child == binding && x.Closure != PlatformExternalClosureState.Closed))
            return KernelResult.Fail(KernelError.PlatformBindingActive, "Child mappings and virtual-I/O bindings must close before child closure.");
        if (_provider is not IPlatformChildDomainProvider provider)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "Child-domain provider is unavailable.");
        var result = provider.CloseChildDomain(record.ProviderLease);
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

    private KernelResult<ChildBindingRecord> ResolveChild(PlatformChildBinding binding, bool allowDraining = false)
    {
        if (!_childBindings.TryGetValue(binding.BindingId, out ChildBindingRecord? record))
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformBindingNotFound, "Child binding was not found.");
        if (record.Binding.Generation != binding.Generation)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.StaleGeneration, "Child binding generation is stale.");
        if (record.Binding != binding)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.WrongPlatformDomain, "Child binding has a different parent or owner.");
        if (record.State == PlatformChildDomainState.Closed)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformBindingRevoked, "Child binding is closed.");
        if (record.State == PlatformChildDomainState.Faulted)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformFaulted, "Child binding is quarantined.");
        if (!allowDraining && record.State == PlatformChildDomainState.Draining)
            return KernelResult<ChildBindingRecord>.Fail(KernelError.PlatformDenied, "Child binding is draining.");
        return KernelResult<ChildBindingRecord>.Ok(record);
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

    private static void QuarantineChild(ChildBindingRecord record, PlatformAuthorityStatus status)
    {
        if (status is PlatformAuthorityStatus.Stale or PlatformAuthorityStatus.Revoked or
            PlatformAuthorityStatus.WrongDomain or PlatformAuthorityStatus.Faulted || !Enum.IsDefined(status))
            record.State = PlatformChildDomainState.Faulted;
    }
}
