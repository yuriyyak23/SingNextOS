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
    {
        var selectedTimeProvider = timeProvider ?? TimeProvider.System;
        _operabilityTimeProvider = selectedTimeProvider;
        Processes = new ProcessRegistry();
        Domains = new DomainRegistry();
        CapabilityAuthority = new CapabilityAuthority();
        SealedObjects = new SealedObjectAuthority(CapabilityAuthority.RealmId);
        Regions = new RegionAuthority();
        ExternalOperations = new ExternalOperationAuthority(Regions);
        ComputePlanner = new ComputePlanner(Regions);
        Channels = new ChannelRegistry(CapabilityAuthority, Regions);
        PlatformAuthority = new PlatformAuthorityBridge(platformProvider);
        Services = new ServiceRegistry();
        EndpointSessions = new EndpointSessionRegistry(selectedTimeProvider);
        CancellationScopes = new CancellationScopeAuthority(selectedTimeProvider);
        Budgets = new ResourceBudgetAuthority();
        if (recoveryOptions is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(recoveryOptions.ResourceBudgetJournalPath);
            ArgumentNullException.ThrowIfNull(recoveryOptions.AuthenticationKey);
            _resourceBudgetRecoveryJournal = new ResourceBudgetRecoveryJournal(
                new FileResourceBudgetJournalStore(recoveryOptions.ResourceBudgetJournalPath),
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
        var result = Processes.Create(manifest);
        if (!result.IsSuccess) return result;
        Domains.Add(result.Value!);
        return result;
    }

    public KernelResult AdmitProcess(ProcessHandle handle)
    {
        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        if (process.State != ProcessState.Created) return InvalidTransition(process.State, ProcessState.Admitted);
        foreach (var requirement in process.Manifest.RequiredCapabilities)
        {
            var satisfied = CapabilityAuthority.SnapshotForDomain(process.DomainId).Any(c => c.Generation == process.Generation && c.ResourceKind == requirement.ResourceKind && string.Equals(c.ResourceId, requirement.ResourceId, StringComparison.Ordinal) && (c.Rights & requirement.Rights) == requirement.Rights);
            if (!satisfied) return KernelResult.Fail(KernelError.MissingCapability, $"Missing capability {requirement.ResourceKind}:{requirement.ResourceId} ({requirement.Rights}).");
        }
        process.SetState(ProcessState.Admitted);
        return KernelResult.Ok();
    }

    public KernelResult StartProcess(ProcessHandle handle)
    {
        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        if (process.State != ProcessState.Admitted) return InvalidTransition(process.State, ProcessState.Runnable);

        var platform = TransitionPlatformExecutionIfBound(
            handle,
            process,
            PlatformDomainExecutionTransition.Start);
        if (!platform.IsSuccess) return platform;

        process.SetState(ProcessState.Runnable);
        process.SetState(ProcessState.Running);
        RecordTrace(handle, TraceEventKind.ProcessLifecycle, null, "process",
            $"{handle.ProcessId.Value}:{handle.Generation}", "running", "started");
        return KernelResult.Ok();
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
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(effect.Error, effect.Message!);
        if (!Domains.Contains(issuerDomain)) return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DomainNotFound, $"Issuer domain {issuerDomain} is not active.");
        var minted = CapabilityAuthority.Mint(issuerDomain, resolved.Value!.DomainId, resourceKind, resourceId, rights, subject.Generation, resourceGeneration);
        if (!minted.IsSuccess) return minted;
        var descriptor = minted.Value!;
        resolved.Value.AddCapability(descriptor.CapabilityId);
        RecordTrace(subject, TraceEventKind.CapabilityMinted, traceContext, "capability",
            descriptor.CapabilityId.Value.ToString(), "active", "minted");
        return KernelResult<CapabilityDescriptorV1>.Ok(descriptor);
    }

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

        var delegated = CapabilityAuthority.Delegate(sourceCapability, sourceProcess.Value!.DomainId, targetProcess.Value!.DomainId, rights, target.Generation);
        if (delegated.IsSuccess)
        {
            targetProcess.Value!.AddCapability(delegated.Value!.CapabilityId);
            RecordTrace(target, TraceEventKind.CapabilityDelegated, traceContext, "capability",
                delegated.Value.CapabilityId.Value.ToString(), "active", "delegated");
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

    private KernelResult TransitionProcessExecution(
        ProcessHandle handle,
        ProcessState from,
        ProcessState to,
        PlatformDomainExecutionTransition platformTransition)
    {
        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        if (process.State != from) return InvalidTransition(process.State, to);

        var platform = TransitionPlatformExecutionIfBound(
            handle,
            process,
            platformTransition);
        if (!platform.IsSuccess) return platform;

        process.SetState(to);
        return KernelResult.Ok();
    }

    private KernelResult TransitionPlatformExecutionIfBound(
        ProcessHandle handle,
        SingProcess process,
        PlatformDomainExecutionTransition transition)
    {
        if (!_platformExecutionAttachments.Contains(handle) || !_processPlatformBindings.TryGetValue(handle, out var binding))
            return KernelResult.Ok();

        var identity = PlatformIdentity(process);
        return PlatformAuthority.TransitionDomainExecution(binding, identity, transition);
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
