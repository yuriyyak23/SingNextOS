using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed record SoftwareSecureAdmissionResult(ExternalOperationHandle Operation,
    bool ResourcesClosed, bool ExpectedDeny, ulong MachineAttempt, ulong NoEffectCommit)
{
    public bool IsExecutionAllowed => false;
}

public sealed partial class RuntimeKernel
{
    public static string SoftwareSecureAdmissionResource(VirtualDomainHandle domain) =>
        $"securecompute:software:no-effect:{domain.DomainId.Value}:{domain.Generation.Value}";
    public static string SoftwareSecureRegisterLocalResource(VirtualDomainHandle domain) =>
        $"securecompute:software:register-local:{domain.DomainId.Value}:{domain.Generation.Value}";

    private sealed class SoftwareSecureRecord(ProcessHandle owner, VirtualDomainAuthority.Record domain,
        VirtualDomainAuthority.EffectSource source, CapabilityId virtualCapability, CapabilityId softwareCapability,
        ExternalOperationHandle operation, PlatformSoftwareSecureAdmissionRequest request,
        Func<Func<KernelResult>, KernelResult> bridgeSource)
    {
        internal readonly ProcessHandle Owner = owner;
        internal readonly VirtualDomainAuthority.Record Domain = domain;
        internal readonly VirtualDomainAuthority.EffectSource Source = source;
        internal readonly CapabilityId VirtualCapability = virtualCapability, SoftwareCapability = softwareCapability;
        internal readonly ExternalOperationHandle Operation = operation;
        internal readonly PlatformSoftwareSecureAdmissionRequest Request = request;
        internal readonly Func<Func<KernelResult>, KernelResult> BridgeSource = bridgeSource;
        internal OperationBinding? Binding;
        internal bool Active = true, Invoked, Closed;
        internal PlatformSoftwareSecureAdmissionObservation? Observation;
        internal bool PublicationAttempted;
        internal SoftwareRegisterLocalPublication? Candidate;
        internal SoftwareRegisterLocalPublication? PublishedResult;
    }
    private readonly List<SoftwareSecureRecord> _softwareSecureOwners = [];
    private readonly Dictionary<ExternalOperationHandle, SoftwareSecureRecord> _softwareSecureRoutes = [];
    private ulong _softwareSecureIssued;

    private bool HasSoftwareSecureInventory() => _softwareSecureOwners.Count == (long)_softwareSecureIssued &&
        _softwareSecureRoutes.Count == _softwareSecureOwners.Count &&
        _softwareSecureOwners.Distinct(ReferenceEqualityComparer.Instance).Count() == _softwareSecureOwners.Count &&
        _softwareSecureOwners.All(x => _softwareSecureRoutes.TryGetValue(x.Operation, out var route) && ReferenceEquals(x, route));

    private bool HasOriginalSoftwareSecureRecord(SoftwareSecureRecord record) =>
        HasSoftwareSecureInventory() && _softwareSecureOwners.Count(x => ReferenceEquals(x, record)) == 1 &&
        _softwareSecureRoutes.TryGetValue(record.Operation, out var route) && ReferenceEquals(route, record);

    private KernelResult CommitSoftwareSecureSource(SoftwareSecureRecord record, Func<KernelResult> commit)
    {
        var process = Processes.Resolve(record.Owner);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        var active = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!active.IsSuccess) return active;
        if (!HasOriginalSoftwareSecureRecord(record))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original software admission owner unavailable.");
        return CapabilityAuthority.CommitVirtualDomainEffect(record.VirtualCapability, process.Value!.DomainId,
            record.Owner.Generation, CapabilityRights.Configure, VirtualizationResourceIds.Domain(record.Domain.Handle.DomainId), () =>
            CapabilityAuthority.CommitSecureDomainEffect(record.SoftwareCapability, process.Value.DomainId,
                record.Owner.Generation, record.Request.ExecuteRegisterLocal ? CapabilityRights.Execute : CapabilityRights.Configure,
                record.Request.ExecuteRegisterLocal ? SoftwareSecureRegisterLocalResource(record.Domain.Handle) : SoftwareSecureAdmissionResource(record.Domain.Handle), () =>
                _virtualDomains.CommitEffectAdmission(record.Domain, record.Source.Lineage[0].State,
                    () => record.BridgeSource(commit), record.Source)));
    }

    /// <summary>Semantic admission to the limited no-effect software contour. Never executes arithmetic.</summary>
    public KernelResult<SoftwareSecureAdmissionResult> ObserveSoftwareSecureAdmission(ProcessHandle owner,
        VirtualDomainHandle domain, CapabilityId virtualCapability, CapabilityId softwareCapability, ulong left, ulong right)
        => ObserveSoftwareSecureAdmissionCore(owner, domain, virtualCapability, softwareCapability, left, right, false);

    private KernelResult<SoftwareSecureAdmissionResult> ObserveSoftwareSecureAdmissionCore(ProcessHandle owner,
        VirtualDomainHandle domain, CapabilityId virtualCapability, CapabilityId softwareCapability, ulong left, ulong right, bool execute, PlatformExecutableArtifactBinding? artifact = null,
        Func<Func<KernelResult>, KernelResult>? artifactSource = null)
    {
        SoftwareSecureRecord record;
        lock (_platformMemoryUseGate)
        {
            var current = _virtualDomains.ResolveAdmission(owner, domain);
            if (!current.IsSuccess) return KernelResult<SoftwareSecureAdmissionResult>.Fail(current.Error, current.Message!);
            if (current.Value!.ParentDomain is not null || current.Value.ChildBinding is not { } child ||
                current.Value.State is not (VirtualDomainState.Configured or VirtualDomainState.Parked))
                return KernelResult<SoftwareSecureAdmissionResult>.Fail(KernelError.PlatformUnsupported,
                    "Limited software admission requires an original configured or parked root child.");
            if (!HasSoftwareSecureInventory() || _softwareSecureOwners.Count >= 4096 || _softwareSecureOwners.Any(x => ReferenceEquals(x.Domain, current.Value) && (!x.Closed || x.Active)))
                return KernelResult<SoftwareSecureAdmissionResult>.Fail(KernelError.PlatformBindingActive, "Original software admission remains pinned.");
            if (artifact is not null && _softwareSecureOwners.Any(x => ReferenceEquals(x.Domain, current.Value) && x.Request.Artifact is not null))
                return KernelResult<SoftwareSecureAdmissionResult>.Fail(KernelError.PlatformDenied, "Original artifact entry is one-shot.");
            var process = Processes.Resolve(owner);
            if (!process.IsSuccess) return KernelResult<SoftwareSecureAdmissionResult>.Fail(process.Error, process.Message!);
            KernelResult permission = CapabilityAuthority.CommitVirtualDomainEffect(virtualCapability, process.Value!.DomainId,
                owner.Generation, CapabilityRights.Configure, VirtualizationResourceIds.Domain(domain.DomainId), () =>
                CapabilityAuthority.CommitSecureDomainEffect(softwareCapability, process.Value.DomainId, owner.Generation,
                    execute ? CapabilityRights.Execute : CapabilityRights.Configure,
                    execute ? SoftwareSecureRegisterLocalResource(domain) : SoftwareSecureAdmissionResource(domain), KernelResult.Ok));
            if (!permission.IsSuccess) return KernelResult<SoftwareSecureAdmissionResult>.Fail(permission.Error, permission.Message!);
            var source = _virtualDomains.CaptureEffectSource(current.Value, current.Value.State);
            if (!source.IsSuccess) return KernelResult<SoftwareSecureAdmissionResult>.Fail(source.Error, source.Message!);
            var request = PlatformAuthority.PrepareSoftwareSecureAdmission(child, Guid.NewGuid(), 1, left, right, execute, artifact);
            if (!request.IsSuccess) return KernelResult<SoftwareSecureAdmissionResult>.Fail(request.Error, request.Message!);
            // Preparation is local only; permission is committed again at submission below.
            var principal = ResolveExternalOperationPrincipal(owner, requireNewEffect: true);
            if (!principal.IsSuccess) return KernelResult<SoftwareSecureAdmissionResult>.Fail(principal.Error, principal.Message!);
            var prepared = ExternalOperations.PrepareSoftwareNoEffect(principal.Value!, request.Value!.Request);
            if (!prepared.IsSuccess) return KernelResult<SoftwareSecureAdmissionResult>.Fail(prepared.Error, prepared.Message!);
            record = new(owner, current.Value, source.Value!, virtualCapability, softwareCapability,
                prepared.Value!.Operation, request.Value.Request, commit => request.Value.CommitSource(() =>
                    artifactSource is null ? commit() : artifactSource(commit)));
            _softwareSecureOwners.Add(record);
            _softwareSecureRoutes.Add(record.Operation, record);
            _softwareSecureIssued++;
        }
        try
        {
            var dependencies = new OperationDependencySnapshot(record.Request.Child.ParentDomainLease.Generation.Value,
                record.Request.Child.Generation.Value);
            var admitted = AdmitExternalOperation(owner, record.Operation, dependencies);
            if (!admitted.IsSuccess) return SoftwareSecureNeverInvoked(record, admitted.Error, admitted.Message!);
            KernelResult submit;
            lock (_platformMemoryUseGate)
                submit = CommitSoftwareSecureSource(record, () =>
                {
                    var submitted = ExternalOperations.RecordSubmission(record.Operation, dependencies);
                    if (!submitted.IsSuccess) return KernelResult.Fail(submitted.Error, submitted.Message!);
                    record.Binding = submitted.Value;
                    // Linearization before provider callback: all later outcomes are cleanup-only.
                    record.Invoked = true;
                    return KernelResult.Ok();
                });
            if (!submit.IsSuccess) return SoftwareSecureNeverInvoked(record, submit.Error, submit.Message!);
            try { record.Observation = PlatformAuthority.ObserveSoftwareSecureAdmission(record.Request, false); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
            return FinishSoftwareSecureAdmission(record, allowPublication: execute);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (!record.Invoked) return SoftwareSecureNeverInvoked(record, KernelError.PlatformFaulted, ex.Message);
            return FinishSoftwareSecureAdmission(record);
        }
        finally { lock (_platformMemoryUseGate) record.Active = false; }
    }

    private KernelResult<SoftwareSecureAdmissionResult> SoftwareSecureNeverInvoked(SoftwareSecureRecord record,
        KernelError error, string message)
    {
        _ = CancelExternalOperation(record.Owner, record.Operation, false);
        var released = ReleaseExternalOperation(record.Owner, record.Operation, new(true, false));
        lock (_platformMemoryUseGate) record.Closed = released.IsSuccess;
        return KernelResult<SoftwareSecureAdmissionResult>.Fail(error, message);
    }

    /// <summary>Original outcome reconciliation and cleanup only. Never repeats submission.</summary>
    public KernelResult<SoftwareSecureAdmissionResult> ResolveSoftwareSecureAdmission(ProcessHandle owner, ExternalOperationHandle operation)
    {
        SoftwareSecureRecord record;
        lock (_platformMemoryUseGate)
        {
            if (!_softwareSecureRoutes.TryGetValue(operation, out record!) || record.Owner != owner ||
                !HasOriginalSoftwareSecureRecord(record))
                return KernelResult<SoftwareSecureAdmissionResult>.Fail(KernelError.StaleGeneration, "Original software operation unavailable.");
            if (record.Active) return KernelResult<SoftwareSecureAdmissionResult>.Fail(KernelError.PlatformBindingActive, "Software operation in flight.");
            record.Active = true;
        }
        try
        {
            if (!record.Invoked)
            {
                _ = SoftwareSecureNeverInvoked(record, KernelError.PlatformFaulted, "Original invocation never submitted.");
                return FinishSoftwareSecureAdmission(record);
            }
            if (record.Invoked && !record.Closed)
                try { record.Observation = PlatformAuthority.ObserveSoftwareSecureAdmission(record.Request, true); }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
            return FinishSoftwareSecureAdmission(record);
        }
        finally { lock (_platformMemoryUseGate) record.Active = false; }
    }

    private KernelResult<SoftwareSecureAdmissionResult> FinishSoftwareSecureAdmission(SoftwareSecureRecord record, bool allowPublication = false)
    {
        var observation = record.Observation;
        if (record.Invoked && observation is { ResourcesClosed: true } && ReferenceEquals(observation.Request, record.Request) &&
            ExternalOperations.ConfirmSoftwareNoEffectClosure(record.Operation, observation).IsSuccess)
        {
            if (record.Request.ExecuteRegisterLocal) FinishSoftwareRegisterLocalOperation(record, observation, allowPublication);
            else if (record.Binding is { } binding)
                _ = RecordExternalOperationCompletion(record.Owner, new(binding, ExternalOperationCompletionDisposition.Cancelled));
            var released = ReleaseExternalOperation(record.Owner, record.Operation, new(true, false));
            lock (_platformMemoryUseGate) record.Closed |= released.IsSuccess;
        }
        lock (_platformMemoryUseGate)
        {
            bool visible = !record.Request.ExecuteRegisterLocal && record.Closed && observation is { MachineAttempt: > 0, NoEffectCommit: > 0 } &&
                CommitSoftwareSecureSource(record, KernelResult.Ok).IsSuccess;
            return KernelResult<SoftwareSecureAdmissionResult>.Ok(new(record.Operation, record.Closed, visible,
                visible ? observation!.MachineAttempt : 0, visible ? observation!.NoEffectCommit : 0));
        }
    }

    private KernelResult ValidateSoftwareSecureCleanup(VirtualDomainAuthority.Record domain)
    {
        lock (_platformMemoryUseGate)
            return !IntactKernelInitializedLoads() || _originalKernelInitializedLoads.Any(x => ReferenceEquals(x.Domain, domain) && (x.Active || !x.Closed)) ||
                !HasSoftwareSecureInventory() || _softwareSecureOwners.Any(x => ReferenceEquals(x.Domain, domain) &&
                (!HasOriginalSoftwareSecureRecord(x) || !x.Closed || x.Active))
                ? KernelResult.Fail(KernelError.PlatformBindingActive, "Original software admission cleanup is pending.")
                : KernelResult.Ok();
    }
}
