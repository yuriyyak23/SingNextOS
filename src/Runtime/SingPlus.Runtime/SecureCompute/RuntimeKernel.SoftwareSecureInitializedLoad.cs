using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

/// <summary>Historical OS result of loading explicitly initialized separate storage, not existing guest bytes.</summary>
public sealed record SoftwareSecureInitializedLoadResult(ExternalOperationHandle Operation, bool ResourcesClosed,
    bool WasPublished, ulong? Value, ulong MachineAttempt, ulong Commit);

public sealed partial class RuntimeKernel
{
    public static string SoftwareSecureInitializedLoadResource(VirtualDomainHandle domain) =>
        $"securecompute:software:initialized-load:{domain.DomainId.Value}:{domain.Generation.Value}";

    private sealed class InitializedLoadRecord(ProcessHandle owner, VirtualDomainAuthority.Record domain,
        VirtualDomainAuthority.EffectSource source, GuestRegionMapping mapping,
        (CapabilityId Memory, CapabilityId Region) mappingCapabilities, CapabilityId configure, CapabilityId execute,
        ExternalOperationHandle operation, SoftwareSecureInitializedLoadIntent intent, object operationOwner,
        RuntimeBufferLease<byte>? bufferLease)
    {
        internal InitializedLoadRecord? OriginalSelf;
        internal readonly ProcessHandle Owner = owner;
        internal readonly VirtualDomainAuthority.Record Domain = domain;
        internal readonly VirtualDomainAuthority.EffectSource Source = source;
        internal readonly GuestRegionMapping Mapping = mapping;
        internal readonly (CapabilityId Memory, CapabilityId Region) MappingCapabilities = mappingCapabilities;
        internal readonly CapabilityId Configure = configure, Execute = execute;
        internal readonly ExternalOperationHandle Operation = operation;
        internal readonly SoftwareSecureInitializedLoadIntent Intent = intent;
        internal readonly object OperationOwner = operationOwner;
        internal BudgetReservationHandle? Charge;
        internal OperationBinding? Binding;
        internal bool Active = true, Invoked, Closed, Cancelled, PublicationAttempted;
        internal SoftwareSecureInitializedLoadObservation? Observation;
        internal InitializedLoadPublication? Candidate, Published;
        private readonly RuntimeBufferLease<byte>? _retainedBufferLease = bufferLease;
        internal RuntimeBufferLease<byte>? OriginalBufferLease = bufferLease;
        internal bool HasOriginalBufferLease => ReferenceEquals(OriginalBufferLease, _retainedBufferLease);
        internal Func<KernelResult>? BufferSource;
        internal bool BufferLeaseReleased;
    }
    private sealed record InitializedLoadPublication(SoftwareSecureInitializedLoadObservation Proof,
        ExternalPublicationDecisionV1 Decision, ulong Value);
    private readonly Dictionary<ExternalOperationHandle, InitializedLoadRecord> _kernelInitializedLoads = [];
    private readonly List<InitializedLoadRecord> _originalKernelInitializedLoads = [];
    private ulong _kernelInitializedLoadsIssued;
    internal Action? BeforeSoftwareSecureInitializedLoadPublication { private get; set; }
    internal Action? AfterSoftwareSecureInitializedLoadStaging { private get; set; }

    private bool IntactKernelInitializedLoads() => _kernelInitializedLoadsIssued == (ulong)_originalKernelInitializedLoads.Count &&
        _kernelInitializedLoads.Count == _originalKernelInitializedLoads.Count &&
        _originalKernelInitializedLoads.Distinct(ReferenceEqualityComparer.Instance).Count() == _originalKernelInitializedLoads.Count &&
        _originalKernelInitializedLoads.All(x => ReferenceEquals(x.OriginalSelf, x) &&
            _kernelInitializedLoads.TryGetValue(x.Operation, out var route) && ReferenceEquals(route, x));

    private KernelResult CommitKernelInitializedLoadSource(InitializedLoadRecord record, Func<KernelResult> commit,
        bool requireCharge = true, bool requireBridge = true)
    {
        var process = Processes.Resolve(record.Owner);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        var active = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!active.IsSuccess) return active;
        if (!IntactKernelInitializedLoads() || !record.HasOriginalBufferLease || record.Cancelled ||
            !ReferenceEquals(ExternalOperations.CaptureOriginalAccountingLifetime(record.Operation), record.OperationOwner))
            return KernelResult.Fail(KernelError.StaleGeneration, "Original initialized-load semantic source changed.");
        if (record.BufferSource is { } bufferSource && !record.BufferLeaseReleased)
        {
            var backing = bufferSource();
            if (!backing.IsSuccess) return backing;
        }
        KernelResult Charge() => !requireCharge ? commit() : record.Charge is { } charge
            ? Budgets.CommitInitializedStorageCharge(record.Owner, charge, record.Operation, record.OperationOwner, commit)
            : KernelResult.Fail(KernelError.PlatformFaulted, "Original initialized storage charge unavailable.");
        KernelResult Bridge() => requireBridge ? PlatformAuthority.CommitInitializedLoadSource(record.Intent, Charge) : Charge();
        return CapabilityAuthority.CommitVirtualDomainEffect(record.Configure, process.Value.DomainId, record.Owner.Generation,
            CapabilityRights.Configure, VirtualizationResourceIds.Domain(record.Domain.Handle.DomainId), () =>
            CapabilityAuthority.CommitSecureDomainEffect(record.Execute, process.Value.DomainId, record.Owner.Generation,
                CapabilityRights.Execute, SoftwareSecureInitializedLoadResource(record.Domain.Handle), () =>
            CapabilityAuthority.CommitGuestRegionSource(record.MappingCapabilities.Region, process.Value.DomainId, record.Owner.Generation,
                CapabilityRights.Map | CapabilityRights.Read, CapabilityResourceIds.MemoryRegion(record.Mapping.Region.RegionId), () =>
            CapabilityAuthority.CommitVirtualDomainEffect(record.MappingCapabilities.Memory, process.Value.DomainId, record.Owner.Generation,
                CapabilityRights.Map, VirtualizationResourceIds.Memory(record.Domain.Handle.DomainId), () =>
            _virtualDomains.CommitEffectAdmission(record.Domain, record.Source.Lineage[0].State, () =>
            _virtualDomains.CommitInitializedLoadMapping(record.Domain, record.Mapping, record.Intent.Mapping, record.MappingCapabilities, () =>
            PlatformAuthority.CommitGuestLocalMappingSource(record.Intent.Mapping, record.Domain, record.Mapping.Mapping, Bridge)), record.Source)))));
    }

    /// <summary>Initialize bounded private storage and load its first word. Never reads existing Region contents.</summary>
    public KernelResult<SoftwareSecureInitializedLoadResult> ExecuteSoftwareSecureInitializedLoad(ProcessHandle owner,
        VirtualDomainHandle domain, CapabilityId configureCapability, CapabilityId executeCapability,
        GuestRegionMappingHandle mapping, ReadOnlyMemory<byte> initialBytes)
        => ExecuteSoftwareSecureInitializedLoadCore(owner, domain, configureCapability, executeCapability, mapping, initialBytes);

    private KernelResult<SoftwareSecureInitializedLoadResult> ExecuteSoftwareSecureInitializedLoadCore(ProcessHandle owner,
        VirtualDomainHandle domain, CapabilityId configureCapability, CapabilityId executeCapability,
        GuestRegionMappingHandle mapping, ReadOnlyMemory<byte> initialBytes,
        RuntimeBufferLease<byte>? bufferLease = null, Func<KernelResult>? bufferSource = null, Action? adoptBufferLease = null,
        int sourceOffset = 0)
    {
        if (initialBytes.Length is < 8 or > 4096 || sourceOffset < 0 || (sourceOffset & 7) != 0)
            return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(KernelError.PlatformUnsupported, "One bounded initialized page required.");
        InitializedLoadRecord record;
        lock (_platformMemoryUseGate)
        {
            var resolved = _virtualDomains.ResolveAdmission(owner, domain);
            if (!resolved.IsSuccess) return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(resolved.Error, resolved.Message!);
            var source = resolved.Value!;
            if (!IntactKernelInitializedLoads() || _originalKernelInitializedLoads.Count >= 1024 ||
                _originalKernelInitializedLoads.Any(x => ReferenceEquals(x.Domain, source) && (x.Active || !x.Closed)) ||
                source.ParentDomain is not null || source.ChildBinding is null || source.State != VirtualDomainState.Configured)
                return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(KernelError.PlatformBindingActive, "Original configured root child and closed prior load required.");
            var local = _virtualDomains.ResolveMapping(source, mapping);
            var caps = _virtualDomains.ResolveMappingSource(source, mapping);
            if (!local.IsSuccess || !caps.IsSuccess || local.Value.Access != GuestMemoryAccess.Read ||
                local.Value.GuestRange.ByteLength < initialBytes.Length || sourceOffset > local.Value.GuestRange.ByteLength - initialBytes.Length ||
                local.Value.GuestRange.GuestAddress > ulong.MaxValue - (ulong)sourceOffset ||
                local.Value.GuestRange.GuestAddress + (ulong)sourceOffset > ulong.MaxValue - (ulong)initialBytes.Length ||
                !source.PlatformMappings.TryGetValue(mapping.MappingId, out var platform))
                return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(KernelError.PlatformDenied, "Original read-only Region mapping required.");
            var captured = _virtualDomains.CaptureEffectSource(source, source.State);
            if (!captured.IsSuccess) return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(captured.Error, captured.Message!);
            SoftwareSecureInitializedLoadIntent intent;
            try { intent = new(platform.Guest, Guid.NewGuid(), 1, local.Value.GuestRange.GuestAddress + (ulong)sourceOffset, initialBytes); }
            catch (ArgumentException error) { return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(KernelError.PlatformDenied, error.Message); }
            var principal = ResolveExternalOperationPrincipal(owner, requireNewEffect: true);
            if (!principal.IsSuccess) return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(principal.Error, principal.Message!);
            var prepared = ExternalOperations.PrepareSoftwareInitializedLoad(principal.Value!,
                new(local.Value.Region, RegionUseMode.ReadOnly, new(sourceOffset, initialBytes.Length)), intent);
            if (!prepared.IsSuccess) return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(prepared.Error, prepared.Message!);
            record = new(owner, source, captured.Value!, local.Value, caps.Value, configureCapability, executeCapability,
                prepared.Value!.Operation, intent, ExternalOperations.CaptureOriginalAccountingLifetime(prepared.Value.Operation)!, bufferLease);
            record.OriginalSelf = record;
            record.BufferSource = bufferSource;
            _kernelInitializedLoads.Add(record.Operation, record); _originalKernelInitializedLoads.Add(record); _kernelInitializedLoadsIssued++;
            adoptBufferLease?.Invoke(); // Trusted local ownership transfer, never a caller callback.
        }
        try
        {
            KernelResult permission;
            lock (_platformMemoryUseGate) permission = CommitKernelInitializedLoadSource(record, KernelResult.Ok, false, false);
            if (permission.IsSuccess)
            {
                var dependencies = new OperationDependencySnapshot(record.Intent.Mapping.ParentMapping.Generation.Value,
                    record.Intent.Mapping.Child.Generation.Value);
                var admitted = AdmitExternalOperationForVirtualContext(owner, record.Operation, dependencies, initializedStorageBytes: 4096);
                if (admitted.IsSuccess)
                {
                    lock (_platformMemoryUseGate)
                    {
                        var charges = Budgets.OriginalExternalOperationCharges(owner).Where(x => x.Operation == record.Operation &&
                            ReferenceEquals(x.OperationOwner, record.OperationOwner)).ToArray();
                        if (charges.Length == 1) record.Charge = charges[0].Reservation;
                        _ = CommitKernelInitializedLoadSource(record, () =>
                        {
                            var submitted = ExternalOperations.RecordSubmission(record.Operation, dependencies);
                            if (!submitted.IsSuccess) return KernelResult.Fail(submitted.Error, submitted.Message!);
                            record.Binding = submitted.Value; record.Invoked = true;
                            return KernelResult.Ok(); // Linearization before the bridge callback.
                        }, requireBridge: false);
                    }
                }
            }
            if (record.Invoked)
                try { record.Observation = PlatformAuthority.ExecuteSoftwareSecureInitializedLoad(record.Intent); }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
            return FinishKernelInitializedLoad(record, allowPublication: true);
        }
        finally { lock (_platformMemoryUseGate) record.Active = false; }
    }

    public KernelResult<SoftwareSecureInitializedLoadResult> ResolveSoftwareSecureInitializedLoad(ProcessHandle owner,
        ExternalOperationHandle operation)
    {
        InitializedLoadRecord record;
        lock (_platformMemoryUseGate)
        {
            if (!IntactKernelInitializedLoads() || !_kernelInitializedLoads.TryGetValue(operation, out record!) || record.Owner != owner)
                return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(KernelError.StaleGeneration, "Original initialized-load owner unavailable.");
            if (record.Active) return KernelResult<SoftwareSecureInitializedLoadResult>.Fail(KernelError.PlatformBindingActive, "Original initialized load in flight.");
            record.Active = true;
        }
        try
        {
            if (record.Invoked && !record.Closed)
                record.Observation = PlatformAuthority.ResolveSoftwareSecureInitializedLoad(record.Intent);
            return FinishKernelInitializedLoad(record, allowPublication: false);
        }
        finally { lock (_platformMemoryUseGate) record.Active = false; }
    }

    public KernelResult CancelSoftwareSecureInitializedLoad(ProcessHandle owner, ExternalOperationHandle operation)
    {
        InitializedLoadRecord record;
        lock (_platformMemoryUseGate)
        {
            if (!IntactKernelInitializedLoads() || !_kernelInitializedLoads.TryGetValue(operation, out record!) ||
                record.Owner != owner || record.Closed || record.Published is not null)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original pending initialized load required.");
            record.Cancelled = true;
        }
        if (record.Invoked) _ = PlatformAuthority.CancelSoftwareSecureInitializedLoad(record.Intent);
        return KernelResult.Ok(); // Withdraws publication, never certifies provider quiescence.
    }

    private KernelResult<SoftwareSecureInitializedLoadResult> FinishKernelInitializedLoad(InitializedLoadRecord record, bool allowPublication)
    {
        bool originalClosure = !record.Invoked;
        if (record.Invoked && record.Observation is { ResourcesClosed: true } proof && ReferenceEquals(proof.Intent, record.Intent))
        {
            var queried = PlatformAuthority.ResolveSoftwareSecureInitializedLoad(record.Intent);
            originalClosure = ReferenceEquals(queried, proof) && ExternalOperations.ConfirmSoftwareInitializedLoadClosure(record.Operation, proof).IsSuccess;
            if (originalClosure)
            {
                if (allowPublication) PublishKernelInitializedLoad(record, proof);
                ReconcileKernelInitializedLoadCandidate(record);
            }
        }
        if (originalClosure && !record.Closed)
        {
            if (record.Published is null)
            {
                if (record.Binding is { } binding) _ = RecordExternalOperationCompletion(record.Owner, new(binding, ExternalOperationCompletionDisposition.Cancelled));
                _ = CancelExternalOperation(record.Owner, record.Operation, false);
            }
            var released = ReleaseExternalOperation(record.Owner, record.Operation, new(true, false));
            lock (_platformMemoryUseGate)
            {
                bool resourcesReleased = released.IsSuccess && (record.Charge is not { } charge ||
                    Budgets.Query(charge) is { IsSuccess: true, Value.State: BudgetReservationState.Released });
                if (resourcesReleased && record.OriginalBufferLease is { } lease && !record.BufferLeaseReleased)
                {
                    try { lease.Dispose(); record.BufferLeaseReleased = true; }
                    catch (InvalidOperationException) { resourcesReleased = false; }
                }
                record.Closed = resourcesReleased;
            }
        }
        lock (_platformMemoryUseGate)
        {
            var published = record.Published;
            bool visible = record.Closed && published is not null && CommitKernelInitializedLoadSource(record, KernelResult.Ok, false).IsSuccess;
            return KernelResult<SoftwareSecureInitializedLoadResult>.Ok(new(record.Operation, record.Closed, published is not null,
                visible ? published!.Value : null, visible ? published!.Proof.MachineAttempt : 0, visible ? published!.Proof.Commit : 0));
        }
    }

    private void PublishKernelInitializedLoad(InitializedLoadRecord record, SoftwareSecureInitializedLoadObservation proof)
    {
        if (record.PublicationAttempted || record.Published is not null || record.Binding is not { } binding ||
            proof.Value is not { } value || proof.MachineAttempt == 0 || proof.Commit == 0) return;
        record.PublicationAttempted = true;
        var completed = RecordExternalOperationCompletion(record.Owner, new(binding, ExternalOperationCompletionDisposition.Completed));
        var visible = completed.IsSuccess ? RecordExternalOperationVisibility(record.Owner, new(binding, ExternalVisibilityRequirement.None, true)) : completed;
        if (!visible.IsSuccess) return;
        try
        {
            BeforeSoftwareSecureInitializedLoadPublication?.Invoke();
            ExternalOperations.PublishSoftwareInitializedLoad(record.Operation, proof, visible.Value!.Admission!.Dependencies,
                decision =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        if (!IntactKernelInitializedLoads() || record.Candidate is not null || record.Published is not null || !ReferenceEquals(record.Observation, proof))
                            throw new InvalidOperationException("Original initialized-load staging owner changed.");
                        record.Candidate = new(proof, decision, value);
                    }
                    AfterSoftwareSecureInitializedLoadStaging?.Invoke();
                }, commit => CommitKernelInitializedLoadPublication(record, proof, commit, true),
                admit => CommitKernelInitializedLoadPublication(record, proof, admit, false));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
    }

    private void ReconcileKernelInitializedLoadCandidate(InitializedLoadRecord record)
    {
        if (record.Published is null && record.Candidate is { } candidate)
            _ = ReconcileFailedExternalPublication(record.Owner, record.Operation, candidate.Decision, () =>
            {
                lock (_platformMemoryUseGate)
                    return IntactKernelInitializedLoads() && ReferenceEquals(record.Candidate, candidate) && record.Published is null
                        ? KernelResult.Ok() : KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original OS result sink unknown.");
            });
    }

    private KernelResult<ExternalOperationSnapshot> CommitKernelInitializedLoadPublication(InitializedLoadRecord record,
        SoftwareSecureInitializedLoadObservation proof, Func<KernelResult<ExternalOperationSnapshot>> commit, bool sink)
    {
        lock (_platformMemoryUseGate)
        {
            KernelResult<ExternalOperationSnapshot> result = default;
            var source = CommitKernelInitializedLoadSource(record, () =>
            {
                if (!ReferenceEquals(record.Observation, proof) || !proof.ResourcesClosed ||
                    sink && (record.Candidate is not { } candidate || !ReferenceEquals(candidate.Proof, proof) || record.Published is not null))
                    return KernelResult.Fail(KernelError.StaleGeneration, "Original initialized-load publication candidate changed.");
                result = commit();
                if (result.IsSuccess && sink) record.Published = record.Candidate;
                return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
            });
            return source.IsSuccess ? result : KernelResult<ExternalOperationSnapshot>.Fail(source.Error, source.Message!);
        }
    }
}
