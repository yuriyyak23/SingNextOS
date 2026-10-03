using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed record RuntimeKernelRecoveryOptions(string ResourceBudgetJournalPath, byte[] AuthenticationKey);

public sealed partial class RuntimeKernel
{
    private readonly TimeProvider _operabilityTimeProvider;
    private readonly ResourceBudgetRecoveryJournal? _resourceBudgetRecoveryJournal;

    public RuntimeKernel()
        : this(null, null)
    {
    }

    public RuntimeKernel(IPlatformAuthorityProvider? platformProvider)
        : this(platformProvider, null)
    {
    }

    public RuntimeKernel(IPlatformAuthorityProvider? platformProvider, TimeProvider? timeProvider)
        : this(platformProvider, timeProvider, null)
    {
    }

    public RuntimeKernel(
        IPlatformAuthorityProvider? platformProvider,
        TimeProvider? timeProvider,
        RuntimeKernelRecoveryOptions? recoveryOptions)
        : this(platformProvider, timeProvider, recoveryOptions, null)
    {
    }

    internal RuntimeKernel(
        IPlatformAuthorityProvider? platformProvider,
        TimeProvider? timeProvider,
        RuntimeKernelRecoveryOptions? recoveryOptions,
        IResourceBudgetJournalStore? resourceBudgetJournalStore)
    {
        var selectedTimeProvider = timeProvider ?? TimeProvider.System;
        _operabilityTimeProvider = selectedTimeProvider;
        Processes = new ProcessRegistry();
        Domains = new DomainRegistry();
        CapabilityAuthority = new CapabilityAuthority(new AuthorityRealmId(Guid.NewGuid()),
            CapabilityAuthorityLimits.Default, timeProvider: selectedTimeProvider);
        SealedObjects = new SealedObjectAuthority(CapabilityAuthority.RealmId);
        Regions = new RegionAuthority();
        ExternalOperations = new ExternalOperationAuthority(Regions);
        ComputePlanner = new ComputePlanner(Regions);
        Channels = new ChannelRegistry(CapabilityAuthority, Regions, TransferRegionForIpc);
        PlatformAuthority = new PlatformAuthorityBridge(platformProvider);
        Services = new ServiceRegistry();
        EndpointSessions = new EndpointSessionRegistry(selectedTimeProvider);
        CancellationScopes = new CancellationScopeAuthority(selectedTimeProvider);
        Budgets = new ResourceBudgetAuthority();
        Channels.ChannelClosed += ReleaseClosedIpcBudgetsForChannel;
        if (recoveryOptions is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(recoveryOptions.ResourceBudgetJournalPath);
            ArgumentNullException.ThrowIfNull(recoveryOptions.AuthenticationKey);
            _resourceBudgetRecoveryJournal = new ResourceBudgetRecoveryJournal(
                resourceBudgetJournalStore ?? new FileResourceBudgetJournalStore(recoveryOptions.ResourceBudgetJournalPath),
                recoveryOptions.AuthenticationKey);
            var recovery = _resourceBudgetRecoveryJournal.Replay();
            var staged = Budgets.StageConservativeRecoveryCharge(recovery.ConservativeRecoveryCharge);
            if (!staged.IsSuccess)
                throw new InvalidDataException(staged.Message ?? "Resource budget recovery charge could not be staged.");
        }
        Traces = new DeterministicTraceAuthority(selectedTimeProvider);
    }

    public ProcessRegistry Processes { get; }
    public DomainRegistry Domains { get; }
    public CapabilityAuthority CapabilityAuthority { get; }
    internal SealedObjectAuthority SealedObjects { get; }
    public RegionAuthority Regions { get; }
    public ExternalOperationAuthority ExternalOperations { get; }
    public ComputePlanner ComputePlanner { get; }
    public ChannelRegistry Channels { get; }
    public PlatformAuthorityBridge PlatformAuthority { get; }
    internal ServiceRegistry Services { get; }
    internal EndpointSessionRegistry EndpointSessions { get; }
    public CancellationScopeAuthority CancellationScopes { get; }
    public ResourceBudgetAuthority Budgets { get; }
    internal DeterministicTraceAuthority Traces { get; }

    internal ResourceBudgetRecoverySnapshot? ResourceBudgetRecovery =>
        _resourceBudgetRecoveryJournal?.Replay();

    public KernelResult<SingProcess> CreateProcess(SingProcessManifestV1 manifest)
    {
        if (manifest is null) return KernelResult<SingProcess>.Fail(KernelError.InvalidManifest, "Manifest is required.");
        // Registry commits share the existing lifecycle scope with teardown and
        // final issuance. Registry owner locks protect metadata readers only.
        lock (_platformMemoryUseGate)
        {
            var result = Processes.Create(manifest);
            if (!result.IsSuccess) return result;
            Domains.Add(result.Value!);
            return result;
        }
    }

    public KernelResult AdmitProcess(ProcessHandle handle)
    {
        SingProcess process;
        lock (_platformMemoryUseGate)
        {
            var resolved = Processes.Resolve(handle);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            process = resolved.Value!;
            if (process.State != ProcessState.Created) return InvalidTransition(process.State, ProcessState.Admitted);
        }
        // Capture caller-visible manifest requirements outside authority gates.
        // This private sequence is an input, never permission or a live view.
        var requirements = process.Manifest.RequiredCapabilities.ToArray();
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(handle);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (!ReferenceEquals(current.Value, process))
                return KernelResult.Fail(KernelError.StaleHandle, "Admission process changed during requirement capture.");
            if (process.State != ProcessState.Created) return InvalidTransition(process.State, ProcessState.Admitted);
            return CapabilityAuthority.CommitProcessAdmission(process.DomainId, process.Generation, requirements, () =>
            {
                process.SetState(ProcessState.Admitted);
                return KernelResult.Ok();
            });
        }
    }
    public KernelResult StartProcess(ProcessHandle handle)
    {
        var result = TransitionProcessExecution(handle, ProcessState.Admitted,
            ProcessState.Running, PlatformDomainExecutionTransition.Start);
        if (result.IsSuccess)
            RecordTrace(handle, TraceEventKind.ProcessLifecycle, null, "process",
                $"{handle.ProcessId.Value}:{handle.Generation}", "running", "started");
        return result;
    }
    public KernelResult ParkProcess(ProcessHandle handle) =>
        TransitionProcessExecution(
            handle,
            ProcessState.Running,
            ProcessState.Parked,
            PlatformDomainExecutionTransition.Park);

    public KernelResult ResumeProcess(ProcessHandle handle) =>
        TransitionProcessExecution(
            handle,
            ProcessState.Parked,
            ProcessState.Running,
            PlatformDomainExecutionTransition.Resume);

    public KernelResult TerminateProcess(ProcessHandle handle) =>
        BeginOrAdvanceProcessTeardown(handle, ProcessState.Exited);

    public KernelResult FaultProcess(ProcessHandle handle) =>
        BeginOrAdvanceProcessTeardown(handle, ProcessState.Faulted);

    public KernelResult<CapabilityDescriptorV1> MintCapability(DomainId issuerDomain, ProcessHandle subject, ResourceKind resourceKind, string resourceId, CapabilityRights rights,
        TraceCausalContext? traceContext = null, ulong resourceGeneration = 1)
        => MintCapabilityCore(issuerDomain, subject, resourceKind, resourceId, rights, traceContext, resourceGeneration);

    private KernelResult<CapabilityDescriptorV1> MintCapabilityCore(DomainId issuerDomain, ProcessHandle subject, ResourceKind resourceKind, string resourceId, CapabilityRights rights,
        TraceCausalContext? traceContext = null, ulong resourceGeneration = 1,
        Func<Func<KernelResult<CapabilityDescriptorV1>>, KernelResult<CapabilityDescriptorV1>>? sourceCommitConsumer = null)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(effect.Error, effect.Message!);
        var issuer = ValidateCapabilityIssuerPresence(issuerDomain);
        if (!issuer.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(issuer.Error, issuer.Message!);
        var minted = CapabilityAuthority.Mint(issuerDomain, resolved.Value!.DomainId, resourceKind, resourceId, rights,
            subject.Generation, resourceGeneration, commitConsumer: commit =>
            {
                lock (_platformMemoryUseGate)
                {
                    var current = Processes.Resolve(subject);
                    if (!current.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(current.Error, current.Message!);
                    var accepts = EnsureProcessAcceptsNewEffects(current.Value!);
                    if (!accepts.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(accepts.Error, accepts.Message!);
                    if (current.Value!.DomainId != resolved.Value!.DomainId)
                        return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.WrongCapabilitySubject, "Mint subject domain changed during preparation.");
                    var issuerCurrent = ValidateCapabilityIssuerPresence(issuerDomain);
                    if (!issuerCurrent.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(issuerCurrent.Error, issuerCurrent.Message!);
                    var issued = sourceCommitConsumer is null ? commit() : sourceCommitConsumer(commit);
                    if (issued.IsSuccess) current.Value.AddCapability(issued.Value!.CapabilityId);
                    return issued;
                }
            });
        if (!minted.IsSuccess) return minted;
        var descriptor = minted.Value!;
        RecordTrace(subject, TraceEventKind.CapabilityMinted, traceContext, "capability",
            descriptor.CapabilityId.Value.ToString(), "active", "minted");
        return KernelResult<CapabilityDescriptorV1>.Ok(descriptor);
    }

    private KernelResult ValidateCapabilityIssuerPresence(DomainId issuerDomain) => Domains.Contains(issuerDomain)
        ? KernelResult.Ok()
        : KernelResult.Fail(KernelError.DomainNotFound, $"Issuer domain {issuerDomain} is not active.");

    public KernelResult<CapabilityHandleV2> MintCapabilityV2(
        DomainId issuerDomain, ProcessHandle subject, ResourceKind resourceKind,
        string resourceId, CapabilityRights rights, TraceCausalContext? traceContext = null,
        ulong resourceGeneration = 1)
    {
        var minted = MintCapability(issuerDomain, subject, resourceKind, resourceId, rights,
            traceContext, resourceGeneration);
        if (!minted.IsSuccess)
            return KernelResult<CapabilityHandleV2>.Fail(minted.Error, minted.Message!);
        var process = Processes.Resolve(subject);
        if (!process.IsSuccess)
            return KernelResult<CapabilityHandleV2>.Fail(process.Error, process.Message!);
        return CapabilityAuthority.GetHandleV2(minted.Value!.CapabilityId,
            process.Value!.DomainId, subject.Generation);
    }

    public KernelResult<CapabilityHandleV2> UpgradeCapabilityV1(
        ProcessHandle subject, CapabilityId capabilityId)
    {
        var process = Processes.Resolve(subject);
        if (!process.IsSuccess)
            return KernelResult<CapabilityHandleV2>.Fail(process.Error, process.Message!);
        return CapabilityAuthority.GetHandleV2(capabilityId, process.Value!.DomainId, subject.Generation);
    }

    public KernelResult<CapabilityDescriptorV1> DelegateCapability(ProcessHandle delegator, ProcessHandle target, CapabilityId sourceCapability, CapabilityRights rights,
        TraceCausalContext? traceContext = null)
    {
        var sourceProcess = Processes.Resolve(delegator);
        if (!sourceProcess.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(sourceProcess.Error, sourceProcess.Message!);
        var sourceEffect = EnsureProcessAcceptsNewEffects(sourceProcess.Value!);
        if (!sourceEffect.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(sourceEffect.Error, sourceEffect.Message!);

        var targetProcess = Processes.Resolve(target);
        if (!targetProcess.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(targetProcess.Error, targetProcess.Message!);
        var targetEffect = EnsureProcessAcceptsNewEffects(targetProcess.Value!);
        if (!targetEffect.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(targetEffect.Error, targetEffect.Message!);

        var delegated = CapabilityAuthority.Delegate(sourceCapability, sourceProcess.Value!.DomainId,
            targetProcess.Value!.DomainId, rights, target.Generation, derive: null, commitConsumer: commit =>
            {
                lock (_platformMemoryUseGate)
                {
                    var sourceCurrent = Processes.Resolve(delegator);
                    if (!sourceCurrent.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(sourceCurrent.Error, sourceCurrent.Message!);
                    var sourceAccepts = EnsureProcessAcceptsNewEffects(sourceCurrent.Value!);
                    if (!sourceAccepts.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(sourceAccepts.Error, sourceAccepts.Message!);
                    var targetCurrent = Processes.Resolve(target);
                    if (!targetCurrent.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(targetCurrent.Error, targetCurrent.Message!);
                    var targetAccepts = EnsureProcessAcceptsNewEffects(targetCurrent.Value!);
                    if (!targetAccepts.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(targetAccepts.Error, targetAccepts.Message!);
                    if (sourceCurrent.Value!.DomainId != sourceProcess.Value!.DomainId || targetCurrent.Value!.DomainId != targetProcess.Value!.DomainId)
                        return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.WrongCapabilitySubject, "Delegation process domain changed during preparation.");
                    var exactSource = CapabilityAuthority.Validate(sourceCapability, sourceCurrent.Value.DomainId,
                        delegator.Generation, CapabilityRights.Delegate);
                    if (!exactSource.IsSuccess) return exactSource;
                    var issued = commit();
                    if (issued.IsSuccess) targetCurrent.Value.AddCapability(issued.Value!.CapabilityId);
                    return issued;
                }
            });
        if (delegated.IsSuccess)
        {

            RecordTrace(target, TraceEventKind.CapabilityDelegated, traceContext, "capability",
                delegated.Value!.CapabilityId.Value.ToString(), "active", "delegated");
        }
        return delegated;
    }

    public KernelResult<CapabilityHandleV2> DelegateCapabilityV2(
        ProcessHandle delegator, ProcessHandle target, CapabilityHandleV2 sourceCapability,
        CapabilityRights rights, TraceCausalContext? traceContext = null)
    {
        var sourceId = CapabilityAuthority.ResolveCapabilityId(sourceCapability);
        if (!sourceId.IsSuccess)
            return KernelResult<CapabilityHandleV2>.Fail(sourceId.Error, sourceId.Message!);
        var delegated = DelegateCapability(delegator, target, sourceId.Value, rights, traceContext);
        if (!delegated.IsSuccess)
            return KernelResult<CapabilityHandleV2>.Fail(delegated.Error, delegated.Message!);
        var targetProcess = Processes.Resolve(target);
        if (!targetProcess.IsSuccess)
            return KernelResult<CapabilityHandleV2>.Fail(targetProcess.Error, targetProcess.Message!);
        return CapabilityAuthority.GetHandleV2(delegated.Value!.CapabilityId,
            targetProcess.Value!.DomainId, target.Generation);
    }

    public KernelResult<CapabilityDescriptorV1> ValidateCapability(ProcessHandle subject, CapabilityId capabilityId, CapabilityRights rights,
        ulong? expectedResourceGeneration = null)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(resolved.Error, resolved.Message!);
        return CapabilityAuthority.Validate(capabilityId, resolved.Value!.DomainId, subject.Generation, rights, expectedResourceGeneration);
    }

    public KernelResult<CapabilityInspectionDescriptorV2> ValidateCapability(
        ProcessHandle subject, CapabilityHandleV2 capability, CapabilityRights rights,
        ulong? expectedResourceGeneration = null)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult<CapabilityInspectionDescriptorV2>.Fail(resolved.Error, resolved.Message!);
        return CapabilityAuthority.Validate(capability, resolved.Value!.DomainId,
            subject.Generation, rights, expectedResourceGeneration);
    }

    public KernelResult RevokeCapability(CapabilityHandleV2 capability)
    {
        var id = CapabilityAuthority.ResolveCapabilityId(capability);
        return id.IsSuccess ? RevokeCapability(id.Value) : KernelResult.Fail(id.Error, id.Message!);
    }

    public KernelResult RevokeCapability(CapabilityId capabilityId)
    {
        lock (_platformMemoryUseGate)
            return RevokeCapabilityLocked(capabilityId);
    }

    private KernelResult RevokeCapabilityLocked(CapabilityId capabilityId)
    {
        var traceSubjects = CapabilityAuthority.InspectionSnapshot()
            .Where(record => record.Descriptor.CapabilityId == capabilityId)
            .SelectMany(record => Processes.Snapshot()
                .Where(process => process.DomainId == record.Descriptor.SubjectDomainId &&
                                  process.Generation == record.Descriptor.Generation)
                .Select(process => new ProcessHandle(process.ProcessId, process.Generation)))
            .ToArray();
        var result = CapabilityAuthority.Revoke(capabilityId);
        if (!result.IsSuccess) return result;

        // Revoke local mapping permission before any cascade can enter a provider
        // callback. Closure is still performed later and may remain draining/faulted.
        _ = PlatformAuthority.BeginCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId));
        _ = PlatformAuthority.BeginIrqCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId));
        _ = PlatformAuthority.BeginMmioCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId));
        foreach (var process in Processes.Snapshot())
            process.RemoveCapability(capabilityId);
        foreach (var subject in traceSubjects)
            RecordTrace(subject, TraceEventKind.CapabilityRevoked, null, "capability",
                capabilityId.Value.ToString(), "revoked", "revoked");

        var computeCascade = CascadePlatformDsc1CapabilityRevocation(capabilityId);
        var irqCascade = CascadePlatformIrqCapabilityRevocation(capabilityId);
        var mmioCascade = CascadePlatformMmioCapabilityRevocation(capabilityId);
        var deviceCascade = CascadePlatformDeviceCapabilityRevocation(capabilityId);
        var mappingCascade = CascadePlatformCapabilityRevocation(capabilityId);
        if (!computeCascade.IsSuccess) return computeCascade;
        if (!irqCascade.IsSuccess) return irqCascade;
        if (!mmioCascade.IsSuccess) return mmioCascade;
        if (!deviceCascade.IsSuccess) return deviceCascade;
        return mappingCascade.IsSuccess ? result : mappingCascade;
    }

    // Admission interlock only; ProcessRegistry and SingProcess remain identity/state owners.
    private readonly HashSet<ProcessHandle> _executionTransactions = [];

    private KernelResult TransitionProcessExecution(
        ProcessHandle handle,
        ProcessState from,
        ProcessState to,
        PlatformDomainExecutionTransition platformTransition)
    {
        SingProcess process;
        PlatformDomainBinding binding;
        lock (_platformMemoryUseGate)
        {
            var resolved = Processes.Resolve(handle);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            process = resolved.Value!;
            if (_executionTransactions.Contains(handle) || _pendingPlatformDomainBinds.Contains(handle))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "An execution transaction is pending for this process.");
            if (process.State != from) return InvalidTransition(process.State, to);
            if (!_platformExecutionAttachments.Contains(handle) || !_processPlatformBindings.TryGetValue(handle, out binding))
            {
                KernelResult CommitFallback()
                {
                    CommitState();
                    return KernelResult.Ok();
                }
                // An authority-only/cleanup root is still a declared dependency.
                // Its quarantine cannot silently become an unbound execution fallback.
                if (_processPlatformBindings.TryGetValue(handle, out var authorityBinding))
                    return PlatformAuthority.CommitActiveDomainConsumer(authorityBinding, PlatformIdentity(process), CommitFallback);
                return CommitFallback();
            }
            _executionTransactions.Add(handle);
        }

        KernelResult WithCurrentProcess(Func<KernelResult> ownerCommit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(handle);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process) || process.State != from)
                    return InvalidTransition(process.State, to);
                if (!_platformExecutionAttachments.Contains(handle) ||
                    !_processPlatformBindings.TryGetValue(handle, out var currentBinding) || currentBinding != binding)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Execution attachment changed during the transaction.");
                return ownerCommit();
            }
        }

        void CommitState()
        {
            if (platformTransition == PlatformDomainExecutionTransition.Start)
                process.SetState(ProcessState.Runnable);
            process.SetState(to);
        }

        try
        {
            return PlatformAuthority.TransitionDomainExecution(binding, PlatformIdentity(process), platformTransition,
                admissionConsumer: WithCurrentProcess,
                completionConsumer: commit => WithCurrentProcess(() => commit(() =>
                {
                    CommitState();
                    return KernelResult.Ok();
                })));
        }
        finally
        {
            lock (_platformMemoryUseGate) _executionTransactions.Remove(handle);
        }
    }
    private static PlatformDomainIdentity PlatformIdentity(SingProcess process) =>
        new(
            process.DomainId,
            new ProcessHandle(process.ProcessId, process.Generation));

    private static KernelResult EnsureProcessAcceptsNewEffects(SingProcess process)
    {
        if (process.State == ProcessState.Exiting)
        {
            return KernelResult.Fail(
                KernelError.InvalidTransition,
                "Process is Exiting and cannot authorize new effects.");
        }

        if (process.State is ProcessState.Exited or ProcessState.Faulted)
        {
            return KernelResult.Fail(
                KernelError.InvalidTransition,
                $"Terminal process state {process.State} cannot authorize new effects.");
        }

        return KernelResult.Ok();
    }

    private static KernelResult InvalidTransition(ProcessState actual, ProcessState requested) => KernelResult.Fail(KernelError.InvalidTransition, $"Cannot transition from {actual} to {requested}.");
}




