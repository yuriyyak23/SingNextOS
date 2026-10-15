using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public enum ProcessTeardownPhase
{
    LocalExitStarted = 0,
    PlatformDraining,
    PlatformClosed,
    PlatformFaulted
}

public readonly record struct ProcessTeardownSnapshot(
    ProcessHandle Process,
    ProcessState TargetTerminalState,
    ProcessTeardownPhase Phase,
    bool ChannelsClosed,
    bool LocalAuthorizationRevoked,
    int PendingPlatformMappings,
    bool PlatformDomainClosed,
    bool LocalReclaimCompleted,
    KernelError? BlockingError)
{
    public bool IsComplete => LocalReclaimCompleted;
}

public sealed partial class RuntimeKernel
{
    private sealed class ProcessTeardownRecord(
        ProcessHandle handle,
        ProcessState targetTerminalState,
        PlatformRegionMapping[] mappings,
        PlatformDomainBinding? domainBinding)
    {
        public ProcessHandle Handle { get; } = handle;
        public ProcessState TargetTerminalState { get; } = targetTerminalState;
        public PlatformRegionMapping[] Mappings { get; set; } = mappings;
        public PlatformDomainBinding? DomainBinding { get; set; } = domainBinding;
        public ProcessTeardownPhase Phase { get; set; } = ProcessTeardownPhase.LocalExitStarted;
        public bool ChannelsClosed { get; set; }
        public bool LocalAuthorizationRevoked { get; set; }
        public int PendingPlatformMappings { get; set; } = mappings.Length;
        public bool PlatformDomainClosed { get; set; }
        public bool LocalReclaimCompleted { get; set; }
        public KernelError? BlockingError { get; set; }
        public bool ComposedAuthorityPrepared { get; set; }
        public bool ComposedPreparationInFlight { get; set; }
        public bool ProgressionInFlight { get; set; } // Progress admission only; not closure evidence.
        // The last-member decision has committed, but local reclaim may still be pinned.
        public DomainId? DomainReclaimPending { get; set; }

        public ProcessTeardownSnapshot Snapshot => new(
            Handle,
            TargetTerminalState,
            Phase,
            ChannelsClosed,
            LocalAuthorizationRevoked,
            PendingPlatformMappings,
            PlatformDomainClosed,
            LocalReclaimCompleted,
            BlockingError);
    }

    private readonly Dictionary<ProcessHandle, ProcessTeardownRecord> _processTeardowns = [];
    private readonly Dictionary<ProcessHandle, PlatformDomainBinding> _processPlatformBindings = [];
    private readonly HashSet<ProcessHandle> _pendingPlatformDomainBinds = [];
    private readonly HashSet<ProcessHandle> _platformExecutionAttachments = [];
    private readonly Dictionary<ProcessHandle, List<PlatformRegionMapping>> _processPlatformMappings = [];
    private readonly Dictionary<ProcessHandle, int> _pendingPlatformMappingAdmissions = [];
    // Admission interlock only: entries carry no capability state or permission.
    private readonly List<CapabilityId> _pendingPlatformMappingCapabilities = [];

    public KernelResult<ProcessTeardownSnapshot> ObserveProcessTeardown(ProcessHandle handle)
    {
        try
        {
            return ObserveProcessTeardownCore(handle);
        }
        finally { FlushIpcBudgetObservations(); EmitProcessTeardownExternalOperationTraces(handle); }
    }

    private KernelResult<ProcessTeardownSnapshot> ObserveProcessTeardownCore(ProcessHandle handle)
    {
        ProcessTeardownRecord record;
        SingProcess process;
        lock (_platformMemoryUseGate)
        {
            if (!_processTeardowns.TryGetValue(handle, out record!))
            {
                var resolved = Processes.Resolve(handle);
                return KernelResult<ProcessTeardownSnapshot>.Fail(resolved.IsSuccess ? KernelError.InvalidTransition : resolved.Error,
                    resolved.IsSuccess ? "Process teardown has not started for this process generation." : resolved.Message!);
            }
            var current = Processes.Resolve(handle);
            if (!current.IsSuccess) return KernelResult<ProcessTeardownSnapshot>.Fail(current.Error, current.Message!);
            process = current.Value!;
        }
        return AdvanceProcessTeardown(process, record);
    }
    public KernelResult<ProcessTeardownSnapshot> QueryProcessTeardown(ProcessHandle handle)
    {
        lock (_platformMemoryUseGate)
            return QueryProcessTeardownLocked(handle);
    }

    private KernelResult<ProcessTeardownSnapshot> QueryProcessTeardownLocked(ProcessHandle handle)
    {
        if (_processTeardowns.TryGetValue(handle, out var record))
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);

        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess)
        {
            return KernelResult<ProcessTeardownSnapshot>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        return KernelResult<ProcessTeardownSnapshot>.Fail(
            KernelError.InvalidTransition,
            "Process teardown has not started for this process generation.");
    }

    private KernelResult BeginOrAdvanceProcessTeardown(
        ProcessHandle handle,
        ProcessState targetTerminalState)
    {
        try { return BeginOrAdvanceProcessTeardownCore(handle, targetTerminalState); }
        finally { FlushIpcBudgetObservations(); EmitProcessTeardownExternalOperationTraces(handle); }
    }

    private KernelResult BeginOrAdvanceProcessTeardownCore(
        ProcessHandle handle,
        ProcessState targetTerminalState)
    {
        ProcessTeardownRecord record;
        lock (_platformMemoryUseGate)
        {
            if (!_processTeardowns.TryGetValue(handle, out record!))
            {
                var initialized = InitializeProcessTeardownLocked(handle, targetTerminalState);
                if (!initialized.IsSuccess) return KernelResult.Fail(initialized.Error, initialized.Message!);
                record = initialized.Value!;
            }
            else if (record.TargetTerminalState != targetTerminalState)
            {
                return KernelResult.Fail(KernelError.InvalidTransition,
                    $"Process teardown is already targeting {record.TargetTerminalState}.");
            }
        }

        bool prepare;
        lock (_platformMemoryUseGate)
        {
            // Local revoke already committed above. Root/virtual creation may still
            // publish a known cleanup record, so composed closure cannot freeze yet.
            if (_pendingPlatformDomainBinds.Contains(handle))
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Root/virtual-domain creation must settle before composed teardown can advance.");
            if (_pendingPlatformMappingAdmissions.ContainsKey(handle) || record.ComposedPreparationInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingDraining,
                    "Mapping admission or composed preparation must settle before teardown can advance.");
            if (_virtualDomains.ForOwner(handle).Any(domain => _pendingVirtualTransitions.Contains(domain.Handle)))
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Virtual admission/publication must settle before composed cleanup can consume local mappings.");
            prepare = !record.ComposedAuthorityPrepared;
            if (prepare) record.ComposedPreparationInFlight = true;
        }

        if (prepare)
        {
            var prepared = KernelResult.Fail(KernelError.PlatformFaulted,
                "Composed teardown preparation did not return closure evidence.");
            try
            {
                prepared = PrepareComposedAuthorityTeardown(handle);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Possible effects remain owned by the existing participants.
            }
            finally
            {
                lock (_platformMemoryUseGate)
                {
                    record.ComposedAuthorityPrepared = prepared.IsSuccess;
                    record.Phase = prepared.IsSuccess ? ProcessTeardownPhase.LocalExitStarted : ProcessTeardownPhase.PlatformFaulted;
                    record.BlockingError = prepared.IsSuccess ? null : prepared.Error;
                    record.ComposedPreparationInFlight = false;
                }
            }
            if (!prepared.IsSuccess) return prepared;
        }
        return BeginOrAdvanceProcessTeardownResult(handle, targetTerminalState);
    }

    private KernelResult<ProcessTeardownRecord> InitializeProcessTeardownLocked(
        ProcessHandle handle, ProcessState targetTerminalState)
    {
        if (targetTerminalState is not ProcessState.Exited and not ProcessState.Faulted)
            return KernelResult<ProcessTeardownRecord>.Fail(KernelError.InvalidTransition,
                "Process teardown target must be Exited or Faulted.");
        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess)
            return KernelResult<ProcessTeardownRecord>.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        if (process.State is ProcessState.Exited or ProcessState.Faulted or ProcessState.Exiting)
            return KernelResult<ProcessTeardownRecord>.Fail(KernelError.InvalidTransition,
                $"Cannot begin teardown from state {process.State} without a tracked record.");

        process.SetState(ProcessState.Exiting);
        // Revoke local access immediately. Region backing and possible external
        // effects remain pinned until their existing owners prove closure.
        Regions.InvalidateLocalTokensForProcessExit(
            new RegionOwner(process.DomainId, process.Generation));
        lock (_requestResponseCorrelationGate)
            Channels.CloseAllForProcess(handle);
        ReleaseClosedIpcBudgetsForProcess(handle);
        CloseTraceSessionsForProcess(handle);
        CloseTelemetrySubscriptionsForProcess(handle);
        _kernelEvents.BeginCloseForProcess(handle);

        var mappings = new Dictionary<PlatformRegionMappingId, PlatformRegionMapping>();
        if (_processPlatformMappings.TryGetValue(handle, out var trackedMappings))
            foreach (var mapping in trackedMappings) mappings[mapping.MappingId] = mapping;
        foreach (var capabilityId in process.Capabilities.Items)
        {
            foreach (var mapping in PlatformAuthority.BeginCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId), processCleanupSubject: PlatformIdentity(process)))
                mappings[mapping.MappingId] = mapping;
            _ = PlatformAuthority.BeginIrqCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId));
            _ = PlatformAuthority.BeginMmioCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId));
            _ = PlatformAuthority.BeginDeviceCapabilityRevocation(capabilityId,
                id => CapabilityAuthority.DependsOnCapability(id, capabilityId));
            _ = CapabilityAuthority.Revoke(capabilityId);
        }
        process.ClearCapabilities();
        // Kernel routing is correlation; the original bridge owner supplies the cleanup dependency.
        var hasDomainBinding = PlatformAuthority.TryGetOriginalDomainCleanupBinding(PlatformIdentity(process), out var domainBinding);
        if (!hasDomainBinding)
            hasDomainBinding = _processPlatformBindings.TryGetValue(handle, out domainBinding);
        var record = new ProcessTeardownRecord(handle, targetTerminalState,
            mappings.Values.OrderBy(static mapping => mapping.MappingId.Value).ToArray(),
            hasDomainBinding ? domainBinding : null)
        {
            ChannelsClosed = true,
            LocalAuthorizationRevoked = true,
            // A missing consumer route is not original closure or no-effect.
            PlatformDomainClosed = !hasDomainBinding &&
                !_pendingPlatformDomainBinds.Contains(handle) &&
                !PlatformAuthority.HasUnresolvedDomainBindEffect(PlatformIdentity(process)) &&
                !PlatformAuthority.HasActiveAuthority(PlatformIdentity(process))
        };
        _processTeardowns.Add(handle, record);
        return KernelResult<ProcessTeardownRecord>.Ok(record);
    }

    private KernelResult PrepareComposedAuthorityTeardown(ProcessHandle handle)
    {
        var native = DrainNativeServicesForProcess(handle);
        if (!native.IsSuccess) return native;
        var sessions = CloseSessionsForProcess(handle);
        if (!sessions.IsSuccess) return sessions;
        var process = Processes.Resolve(handle);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        if (PlatformAuthority.HasUnresolvedVirtualDomainCreate(PlatformIdentity(process.Value!)))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Model creation lacks exact receipt/reconciliation; process remains pinned.");
        var owner = new RegionOwner(process.Value!.DomainId, process.Value.Generation);

        var work = CloseComposedWorkForProcess(handle, owner);
        if (!work.IsSuccess) return work;
        var overlays = CloseSecureGuestRegionsForProcess(handle);
        if (!overlays.IsSuccess) return overlays;
        var virtualIo = CloseVirtualIoForProcess(handle);
        if (!virtualIo.IsSuccess) return virtualIo;
        var guestMappings = CloseGuestMappingsForProcess(handle);
        if (!guestMappings.IsSuccess) return guestMappings;
        var executions = DrainSecureExecutions(handle);
        if (!executions.IsSuccess) return executions;
        var secureDomains = CloseSecureDomainsForProcess(handle);
        if (!secureDomains.IsSuccess) return secureDomains;
        // Virtual root closure follows device/CXL/compute progression below.
        // Preparation alone cannot close a parent with live provider children.
        return KernelResult.Ok();
    }

    private KernelResult BeginOrAdvanceProcessTeardownResult(
        ProcessHandle handle,
        ProcessState targetTerminalState)
    {
        var lifecycle = BeginOrAdvanceProcessTeardownLifecycle(handle, targetTerminalState);
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);

        var snapshot = lifecycle.Value!;
        if (snapshot.LocalReclaimCompleted)
            return KernelResult.Ok();

        if (snapshot.Phase == ProcessTeardownPhase.PlatformFaulted)
        {
            return KernelResult.Fail(
                snapshot.BlockingError ?? KernelError.PlatformFaulted,
                "Process teardown is fault-contained and local reclaim remains forbidden.");
        }

        return KernelResult.Fail(
            KernelError.PlatformBindingDraining,
            "Process teardown is still draining authority or staged publication before local reclaim.");
    }

    private KernelResult<ProcessTeardownSnapshot> BeginOrAdvanceProcessTeardownLifecycle(
        ProcessHandle handle, ProcessState targetTerminalState)
    {
        SingProcess process;
        ProcessTeardownRecord record;
        lock (_platformMemoryUseGate)
        {
            if (targetTerminalState is not ProcessState.Exited and not ProcessState.Faulted)
                return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.InvalidTransition,
                    "Process teardown target must be Exited or Faulted.");
            var current = Processes.Resolve(handle);
            if (!current.IsSuccess) return KernelResult<ProcessTeardownSnapshot>.Fail(current.Error, current.Message!);
            process = current.Value!;
            if (process.State is ProcessState.Exited or ProcessState.Faulted)
                return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.InvalidTransition,
                    $"Cannot begin teardown from terminal state {process.State}.");
            if (!_processTeardowns.TryGetValue(handle, out record!))
                return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.PlatformFaulted,
                    "Process teardown lifecycle was invoked without its admission-stop record.");
            if (record.TargetTerminalState != targetTerminalState)
                return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.InvalidTransition,
                    $"Process teardown is already targeting {record.TargetTerminalState}.");
        }
        return AdvanceProcessTeardown(process, record);
    }
    private KernelResult<ProcessTeardownSnapshot> AdvanceProcessTeardown(
        SingProcess process,
        ProcessTeardownRecord record)
    {
        var regionBudgetObservations = new List<BudgetTraceObservation>();
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(record.Handle);
            if (!current.IsSuccess || !ReferenceEquals(current.Value, process) ||
                !_processTeardowns.TryGetValue(record.Handle, out var exact) || !ReferenceEquals(exact, record))
                return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.StaleGeneration, "Exact teardown owner is no longer current.");
            if (record.ProgressionInFlight)
                return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
            record.ProgressionInFlight = true;
        }
        try
        {
        ICxlTeardownParticipant[] participants;
        PlatformRegionMapping[] mappingSnapshot;
        PlatformDomainBinding? domainBindingSnapshot;
        PlatformDomainBinding? trackedDomainSnapshot;
        PlatformDomainBinding? domainToClose;
        var identity = PlatformIdentity(process);
        KernelError? firstBlockingError = null;
        var pendingMappings = 0;
        lock (_platformMemoryUseGate)
        {
        if (!record.ComposedAuthorityPrepared || record.ComposedPreparationInFlight)
        {
            if (record.Phase != ProcessTeardownPhase.PlatformFaulted)
                record.Phase = ProcessTeardownPhase.PlatformDraining;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }

        if (_pendingPlatformMappingAdmissions.TryGetValue(record.Handle, out var pendingAdmissions))
        {
            record.Phase = ProcessTeardownPhase.PlatformDraining;
            record.PendingPlatformMappings = pendingAdmissions;
            record.BlockingError = null;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }
        participants = _cxlTeardownParticipants.AsEnumerable().Reverse().ToArray();
        }
        var admissionResolution = PlatformAuthority.ResolveOriginalAdmissionsForCleanup(identity,out var originalRootNoEffect);
        lock (_platformMemoryUseGate)
        {
            var afterResolution = Processes.Resolve(record.Handle);
            if (!afterResolution.IsSuccess || !ReferenceEquals(afterResolution.Value, process) ||
                !_processTeardowns.TryGetValue(record.Handle, out var originalTeardown) || !ReferenceEquals(originalTeardown, record))
                return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.StaleGeneration, "Original process/teardown owner changed during admission resolution.");
            if (!admissionResolution.IsSuccess)
            {
                record.Phase = admissionResolution.Error == KernelError.PlatformBindingDraining ? ProcessTeardownPhase.PlatformDraining : ProcessTeardownPhase.PlatformFaulted;
                record.BlockingError = admissionResolution.Error;
                return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
            }
            if(originalRootNoEffect && record.DomainBinding is null && !_processPlatformBindings.ContainsKey(record.Handle) &&
                !PlatformAuthority.HasActiveAuthority(identity) && !PlatformAuthority.HasUnresolvedDomainBindEffect(identity))
                record.PlatformDomainClosed=true;
            if (record.DomainBinding is null && PlatformAuthority.TryGetOriginalDomainCleanupBinding(identity, out var recovered))
            {
                if (_processPlatformBindings.TryGetValue(record.Handle, out var routed) && routed != recovered)
                    return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.PlatformFaulted, "Cleanup resolution cannot replace a foreign kernel root route.");
                record.DomainBinding = recovered;
                record.PlatformDomainClosed = false;
                TrackPlatformBinding(record.Handle, recovered, executionAttached: false);
            }
        }
        KernelResult cxlClosure;
        try { cxlClosure = CloseCxlResourcesForProcess(record.Handle,
            new RegionOwner(process.DomainId, process.Generation), participants); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            cxlClosure = KernelResult.Fail(KernelError.PlatformFaulted,
                $"CXL progression did not return closure evidence: {exception.Message}");
        }
        lock (_platformMemoryUseGate)
        {
        var afterCxl = Processes.Resolve(record.Handle);
        if (!afterCxl.IsSuccess || !ReferenceEquals(afterCxl.Value, process) ||
            !_processTeardowns.TryGetValue(record.Handle, out var currentCxlRecord) || !ReferenceEquals(currentCxlRecord, record))
            return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.StaleGeneration,
                "Exact process teardown owner changed during CXL progression.");
        if (!cxlClosure.IsSuccess)
        {
            record.Phase = cxlClosure.Error == KernelError.PlatformBindingDraining
                ? ProcessTeardownPhase.PlatformDraining
                : ProcessTeardownPhase.PlatformFaulted;
            record.BlockingError = cxlClosure.Error == KernelError.PlatformBindingDraining ? null : cxlClosure.Error;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }

        if (!HasCurrentCxlTeardownParticipants(participants))
        {
            record.Phase = ProcessTeardownPhase.PlatformDraining;
            record.BlockingError = null;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }
        var externalOperationProgress = ExternalOperations.AdvanceForTeardown(
            new RegionOwner(process.DomainId, process.Generation));
        ReconcileReleasedExternalOperationBudgets(record.Handle);
        if (!externalOperationProgress.IsSuccess)
        {
            if (externalOperationProgress.Error == KernelError.PlatformBindingDraining)
            {
                record.Phase = ProcessTeardownPhase.PlatformDraining;
                record.BlockingError = null;
                return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
            }

            record.Phase = ProcessTeardownPhase.PlatformFaulted;
            record.BlockingError = externalOperationProgress.Error;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }

        }
        var computeProgress = AdvancePlatformDsc1ForProcess(record.Handle);
        lock (_platformMemoryUseGate)
        {
        var afterCompute = Processes.Resolve(record.Handle);
        if (!afterCompute.IsSuccess || !ReferenceEquals(afterCompute.Value, process) ||
            !_processTeardowns.TryGetValue(record.Handle, out var computeRecord) || !ReferenceEquals(computeRecord, record))
            return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.StaleGeneration,
                "Exact process teardown owner changed during DSC1 progression.");
        if (!computeProgress.IsSuccess)
        {
            if (computeProgress.Error == KernelError.PlatformBindingDraining)
            {
                record.Phase = ProcessTeardownPhase.PlatformDraining;
                record.BlockingError = null;
                return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
            }

            record.Phase = ProcessTeardownPhase.PlatformFaulted;
            record.BlockingError = computeProgress.Error;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }

        if (!HasCurrentCxlTeardownParticipants(participants))
        {
            record.Phase = ProcessTeardownPhase.PlatformDraining;
            record.BlockingError = null;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }
        }
        var deviceProgress = AdvancePlatformDeviceLeasesForProcess(process, record.Handle);
        lock (_platformMemoryUseGate)
        {
        var fresh = Processes.Resolve(record.Handle);
        if (!fresh.IsSuccess || !ReferenceEquals(fresh.Value, process) ||
            !_processTeardowns.TryGetValue(record.Handle, out var exact) || !ReferenceEquals(exact, record))
            return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.StaleGeneration, "Teardown owner changed during provider progression.");
        if (!deviceProgress.IsSuccess)
        {
            if (deviceProgress.Error == KernelError.PlatformBindingDraining)
            {
                // A submitted DMA operation is an expected external lifetime, not a fault.
                // Do not advance mapping/domain closure until a later completion slice can
                // prove the exact operation drained and release its grant pin.
                record.Phase = ProcessTeardownPhase.PlatformDraining;
                record.BlockingError = null;
                return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
            }

            // A device/DMA fault may mean an external effect still exists. Fail closed before
            // touching any lower mapping/domain authority or local reclaim path.
            record.Phase = ProcessTeardownPhase.PlatformFaulted;
            record.BlockingError = deviceProgress.Error;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }

        if (!HasCurrentCxlTeardownParticipants(participants))
        {
            record.Phase = ProcessTeardownPhase.PlatformDraining;
            record.BlockingError = null;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }
        }
        var virtualDomainProgress = CloseVirtualDomainsForProcess(record.Handle);
        lock (_platformMemoryUseGate)
        {
            var afterVirtual = Processes.Resolve(record.Handle);
            if (!afterVirtual.IsSuccess || !ReferenceEquals(afterVirtual.Value, process) ||
                !_processTeardowns.TryGetValue(record.Handle, out var virtualRecord) || !ReferenceEquals(virtualRecord, record))
                return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.StaleGeneration,
                    "Exact teardown owner changed during virtual domain closure.");
            if (!virtualDomainProgress.IsSuccess)
            {
                record.Phase = virtualDomainProgress.Error == KernelError.PlatformBindingDraining
                    ? ProcessTeardownPhase.PlatformDraining : ProcessTeardownPhase.PlatformFaulted;
                record.BlockingError = virtualDomainProgress.Error == KernelError.PlatformBindingDraining
                    ? null : virtualDomainProgress.Error;
                return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
            }
            if (!HasCurrentCxlTeardownParticipants(participants))
            {
                record.Phase = ProcessTeardownPhase.PlatformDraining;
                record.BlockingError = null;
                return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
            }
        }
        var borrowGrantProgress = AdvancePlatformBorrowReadGrantsForProcess(record.Handle);
        lock (_platformMemoryUseGate)
        {
        var afterBorrow = Processes.Resolve(record.Handle);
        if (!afterBorrow.IsSuccess || !ReferenceEquals(afterBorrow.Value, process) ||
            !_processTeardowns.TryGetValue(record.Handle, out var borrowRecord) || !ReferenceEquals(borrowRecord, record))
            return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.StaleGeneration,
                "Exact process teardown owner changed during borrow progression.");
        if (!HasCurrentCxlTeardownParticipants(participants))
        {
            record.Phase = ProcessTeardownPhase.PlatformDraining;
            record.BlockingError = null;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }
        if (!borrowGrantProgress.IsSuccess)
        {
            firstBlockingError ??= borrowGrantProgress.Error;
        }
        else
        {
            pendingMappings += borrowGrantProgress.Value;
        }

        mappingSnapshot = record.Mappings.ToArray();
        }
        foreach (var mapping in mappingSnapshot)
        {
            var queried = PlatformAuthority.QueryRegionMappingLifecycle(mapping, identity);
            if (!queried.IsSuccess)
            {
                lock (_platformMemoryUseGate)
                {
                if (queried.Error is KernelError.PlatformBindingNotFound or KernelError.PlatformBindingRevoked &&
                    (!_processPlatformMappings.TryGetValue(record.Handle, out var currentMappings) ||
                     currentMappings.All(current => current != mapping)))
                    continue;
                firstBlockingError ??= queried.Error;
                continue;
                }
            }

            var lifecycle = queried.Value!;

            KernelResult<PlatformRegionMappingLifecycle> progress;
            switch (lifecycle.PlatformClosure)
            {
                case PlatformExternalClosureState.Active:
                    progress = PlatformAuthority.BeginRegionMappingRevocation(
                        mapping,
                        identity,
                        PlatformRegionRevocationPolicy.DrainBeforeRevoke);
                    break;
                case PlatformExternalClosureState.Draining:
                    progress = PlatformAuthority.ObserveRegionMappingRevocation(mapping, identity);
                    break;
                case PlatformExternalClosureState.Closed:
                    progress = KernelResult<PlatformRegionMappingLifecycle>.Ok(lifecycle);
                    break;
                case PlatformExternalClosureState.Faulted:
                    if (lifecycle.AdmissionNoEffectProven)
                    {
                        progress = KernelResult<PlatformRegionMappingLifecycle>.Ok(lifecycle);
                        break;
                    }
                    firstBlockingError ??= KernelError.PlatformFaulted;
                    continue;
                default:
                    firstBlockingError ??= KernelError.PlatformFaulted;
                    continue;
            }

            // Source getters can invoke arbitrary provider composition. Observe
            // outside the outer kernel gate, then revalidate the actual process,
            // teardown record and participant inventory before local release.
            var releaseSource = progress.IsSuccess &&
                (progress.Value!.PlatformClosure == PlatformExternalClosureState.Closed || progress.Value.AdmissionNoEffectProven)
                ? PlatformAuthority.ValidateRegionMappingReleaseSource(mapping, identity)
                : KernelResult.Ok();
            var mappingBudgetObservations = new List<BudgetTraceObservation>();
            try
            {
            lock (_platformMemoryUseGate)
            {
            var ownerCheck = ValidateProcessMappingProgressionLocked(process, record, mappingSnapshot, participants);
            if (!ownerCheck.IsSuccess)
            {
                if (ownerCheck.Error != KernelError.PlatformBindingDraining)
                    return KernelResult<ProcessTeardownSnapshot>.Fail(ownerCheck.Error, ownerCheck.Message!);
                record.Phase = ProcessTeardownPhase.PlatformDraining;
                record.BlockingError = null;
                return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
            }
            if (!progress.IsSuccess)
            {
                if (progress.Error == KernelError.PlatformBindingDraining)
                {
                    pendingMappings++;
                    continue;
                }

                firstBlockingError ??= progress.Error;
                continue;
            }

            var current = progress.Value!;
            if (current.PlatformClosure == PlatformExternalClosureState.Faulted && !current.AdmissionNoEffectProven)
            {
                firstBlockingError ??= KernelError.PlatformFaulted;
                continue;
            }

            if (current.PlatformClosure != PlatformExternalClosureState.Closed && !current.AdmissionNoEffectProven)
            {
                pendingMappings++;
                continue;
            }

            if (!releaseSource.IsSuccess)
            {
                firstBlockingError ??= releaseSource.Error;
                continue;
            }
            var finalize = FinalizePlatformRegionMappingClosureLocal(mapping, identity, current, mappingBudgetObservations);
            if (!finalize.IsSuccess)
            {
                if (finalize.Error == KernelError.PlatformBindingDraining)
                    pendingMappings++;
                else
                    firstBlockingError ??= finalize.Error;
            }
            }
            }
            finally { EmitBudgetObservations(mappingBudgetObservations); }
        }

        lock (_platformMemoryUseGate)
        {
        var suffixOwner = ValidateProcessMappingProgressionLocked(process, record, mappingSnapshot, participants);
        if (!suffixOwner.IsSuccess)
        {
            if (suffixOwner.Error != KernelError.PlatformBindingDraining)
                return KernelResult<ProcessTeardownSnapshot>.Fail(suffixOwner.Error, suffixOwner.Message!);
            record.Phase = ProcessTeardownPhase.PlatformDraining;
            record.BlockingError = null;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }
        record.PendingPlatformMappings = pendingMappings;
        if (firstBlockingError is { } blockingError)
        {
            record.Phase = ProcessTeardownPhase.PlatformFaulted;
            record.BlockingError = blockingError;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }

        if (pendingMappings != 0)
        {
            record.Phase = ProcessTeardownPhase.PlatformDraining;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }

        if (_pendingPlatformDomainBinds.Contains(record.Handle) ||
            PlatformAuthority.HasUnresolvedDomainBindEffect(identity) ||
            (record.DomainBinding is null && PlatformAuthority.HasActiveAuthority(identity)))
        {
            record.Phase = ProcessTeardownPhase.PlatformFaulted;
            record.BlockingError = KernelError.PlatformFaulted;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }

        domainBindingSnapshot = record.DomainBinding;
        trackedDomainSnapshot = _processPlatformBindings.TryGetValue(record.Handle, out var trackedDomain) ? trackedDomain : null;
        if (trackedDomainSnapshot is { } routedRoot && routedRoot != domainBindingSnapshot)
        {
            record.Phase = ProcessTeardownPhase.PlatformFaulted;
            record.BlockingError = KernelError.PlatformFaulted;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }
        domainToClose = record.PlatformDomainClosed ? null : record.DomainBinding;
        }
        var revokeDomain = domainToClose is { } bindingToClose
            ? PlatformAuthority.RevokeDomain(bindingToClose, identity) : KernelResult.Ok();
        lock (_platformMemoryUseGate)
        {
        var domainOwner = Processes.Resolve(record.Handle);
        if (!domainOwner.IsSuccess || !ReferenceEquals(domainOwner.Value, process) ||
            !_processTeardowns.TryGetValue(record.Handle, out var domainRecord) || !ReferenceEquals(domainRecord, record))
            return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.StaleGeneration,
                "Exact process teardown owner changed during parent-domain progression.");
        var currentTrackedDomain = _processPlatformBindings.TryGetValue(record.Handle, out var trackedAfterDomain)
            ? (PlatformDomainBinding?)trackedAfterDomain : null;
        // Accepted exact closure survives a late routing refusal, but cannot close a replacement binding.
        if (revokeDomain.IsSuccess && record.DomainBinding == domainBindingSnapshot)
            record.PlatformDomainClosed = true;
        if (record.DomainBinding != domainBindingSnapshot || currentTrackedDomain != trackedDomainSnapshot)
        {
            record.Phase = ProcessTeardownPhase.PlatformDraining;
            record.BlockingError = null;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }
        if (!revokeDomain.IsSuccess)
        {
            record.Phase = ProcessTeardownPhase.PlatformFaulted;
            record.BlockingError = revokeDomain.Error;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }
        // Retain the actual accepted closure before a late inventory refusal.
        // It does not imply Region reclaim or quantitative settlement.
        record.PlatformDomainClosed = true;
        if (record.DomainBinding is { } closedBinding) UntrackPlatformBinding(record.Handle, closedBinding);
        record.Phase = ProcessTeardownPhase.PlatformClosed;
        var afterDomain = ValidateProcessMappingProgressionLocked(process, record, mappingSnapshot, participants);
        if (!afterDomain.IsSuccess)
        {
            if (afterDomain.Error != KernelError.PlatformBindingDraining)
                return KernelResult<ProcessTeardownSnapshot>.Fail(afterDomain.Error, afterDomain.Message!);
            record.BlockingError = null;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }
        var cleanup = FinalizeProcessCleanup(process, record, regionBudgetObservations);
        if (!cleanup.IsSuccess)
        {
            if (cleanup.Error == KernelError.PlatformBindingDraining)
            {
                // External domain authority is already closed. Keep that phase
                // truthful while a staged local event publication delays reclaim.
                record.BlockingError = null;
                return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
            }

            record.Phase = ProcessTeardownPhase.PlatformFaulted;
            record.BlockingError = cleanup.Error;
            return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
        }

        process.SetState(record.TargetTerminalState);
        record.LocalReclaimCompleted = true;
        record.PendingPlatformMappings = 0;
        record.BlockingError = null;
        var completed = record.Snapshot;

        Processes.Retire(process);
        _processTeardowns.Remove(record.Handle);
        _processPlatformBindings.Remove(record.Handle);
        _platformExecutionAttachments.Remove(record.Handle);
        _processPlatformMappings.Remove(record.Handle);
        _processPlatformIrqBindings.Remove(record.Handle);
        _processPlatformMmioLeases.Remove(record.Handle);
        _processPlatformDeviceLeases.Remove(record.Handle);
        return KernelResult<ProcessTeardownSnapshot>.Ok(completed);
        }
        }
        finally
        {
            lock (_platformMemoryUseGate) record.ProgressionInFlight = false;
            EmitBudgetObservations(regionBudgetObservations);
        }
    }

    // Existing owners only; called with kernel gate held after provider work.
    private KernelResult ValidateProcessMappingProgressionLocked(SingProcess process,
        ProcessTeardownRecord record, PlatformRegionMapping[] mappings, ICxlTeardownParticipant[] participants)
    {
        var current = Processes.Resolve(record.Handle);
        if (!current.IsSuccess || !ReferenceEquals(current.Value, process) ||
            !_processTeardowns.TryGetValue(record.Handle, out var exact) || !ReferenceEquals(exact, record))
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact teardown owner changed during mapping progression.");
        if (!record.Mappings.SequenceEqual(mappings) || _pendingPlatformMappingAdmissions.ContainsKey(record.Handle) ||
            !HasCurrentCxlTeardownParticipants(participants))
            return KernelResult.Fail(KernelError.PlatformBindingDraining,
                "Mapping admission/inventory or CXL participants changed; a fresh progression pass is required.");
        return KernelResult.Ok();
    }

    private KernelResult FinalizeProcessCleanup(SingProcess process, ProcessTeardownRecord teardown,
        List<BudgetTraceObservation> observations)
    {
        var handle = new ProcessHandle(process.ProcessId, process.Generation);
        var identity = PlatformIdentity(process);
        if (_processPlatformBindings.ContainsKey(handle))
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "A remaining kernel root correlation has not been discharged by exact closure; local reclaim remains pinned.");
        if (PlatformAuthority.HasUnresolvedDomainBindEffect(identity) || PlatformAuthority.HasActiveAuthority(identity))
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Original platform authority or unresolved admission remains live; missing kernel routing cannot authorize local reclaim.");
        if (PlatformAuthority.HasUnclosedModelDomainOwner(handle))
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "An original model-domain lease remains unclosed; missing local model inventory cannot authorize reclaim.");
        if (_virtualDomains.HasUnconsumedOriginalOwner(handle))
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Original virtual local cleanup owner remains unconsumed; missing or falsely Closed routing cannot authorize reclaim.");
        if (HasPinnedSecureExecution(handle))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Live or quarantined secure execution forbids local process reclaim.");
        if (_endpointSessionInvocationRegistry is { } invocations &&
            EndpointSessions.SnapshotForProcess(handle).Any(session =>
                invocations.UnclosedPossibleEffectCount(session.Session) != 0))
        {
            return KernelResult.Fail(KernelError.PlatformBindingDraining,
                "An accepted session invocation or possible resource effect lacks terminal settlement; local reclaim remains forbidden.");
        }
        lock (_requestResponseCorrelationGate)
            Channels.CloseAllForProcess(handle);
        var eventsClosed = _kernelEvents.CloseAllForProcess(handle);
        if (!eventsClosed.IsSuccess) return eventsClosed;

        if (teardown.DomainReclaimPending is null && Domains.Remove(process))
            teardown.DomainReclaimPending = process.DomainId;
        if (teardown.DomainReclaimPending is not null)
        {
            CapabilityAuthority.RevokeAllForDomain(process.DomainId);
            var returnedLoans = Regions.ReturnAllLoansForBorrowerDomain(process.DomainId);
            if (!returnedLoans.IsSuccess) return KernelResult.Fail(returnedLoans.Error, returnedLoans.Message!);
            Regions.ReleaseLocalBorrowUsesForExitedDomain(process.DomainId);
            var reclaimed = Regions.TryReclaimAllForDomain(process.DomainId);
            if (!reclaimed.IsSuccess) return KernelResult.Fail(reclaimed.Error, reclaimed.Message!);
            var settled = ReleaseReclaimedRegionBudgetsForProcess(handle, process.DomainId, observations);
            if (!settled.IsSuccess) return settled;
            lock (_requestResponseCorrelationGate)
                Channels.CloseAllForDomain(process.DomainId);
        }

        process.ClearRuntimeResources();
        teardown.DomainReclaimPending = null;
        return KernelResult.Ok();
    }

    private void TrackPlatformBinding(ProcessHandle process, PlatformDomainBinding binding, bool executionAttached = true)
    {
        lock (_platformMemoryUseGate)
        {
            _processPlatformBindings[process] = binding;
            if (executionAttached) _platformExecutionAttachments.Add(process);
            if (_processTeardowns.TryGetValue(process, out var teardown))
            {
                teardown.DomainBinding = binding;
                teardown.PlatformDomainClosed = false;
            }
        }
    }

    private void UntrackPlatformBinding(ProcessHandle process, PlatformDomainBinding binding)
    {
        if (_processPlatformBindings.TryGetValue(process, out var tracked) && tracked == binding)
        {
            _processPlatformBindings.Remove(process);
            _platformExecutionAttachments.Remove(process);
        }
    }

    private void TrackPlatformMapping(ProcessHandle process, PlatformRegionMapping mapping)
    {
        lock (_platformMemoryUseGate)
        {
        if (!_processPlatformMappings.TryGetValue(process, out var mappings))
        {
            mappings = [];
            _processPlatformMappings.Add(process, mappings);
        }

        if (!mappings.Any(existing => existing.MappingId == mapping.MappingId))
            mappings.Add(mapping);
        if (_processTeardowns.TryGetValue(process, out var teardown) &&
            !teardown.Mappings.Any(existing => existing.MappingId == mapping.MappingId))
        {
            teardown.Mappings = [.. teardown.Mappings, mapping];
            teardown.PendingPlatformMappings++;
        }
        }
    }

    private KernelResult BeginPlatformMappingAdmission(
        ProcessHandle handle, SingProcess process, CapabilityId capabilityId)
    {
        lock (_platformMemoryUseGate)
        {
            var effect = EnsureProcessAcceptsNewEffects(process);
            if (!effect.IsSuccess) return effect;
            _pendingPlatformMappingAdmissions.TryGetValue(handle, out var pending);
            if (pending == int.MaxValue || _pendingPlatformMappingCapabilities.Count == int.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted,
                    "Platform mapping admission counter is exhausted.");
            _pendingPlatformMappingAdmissions[handle] = pending + 1;
            _pendingPlatformMappingCapabilities.Add(capabilityId);
            return KernelResult.Ok();
        }
    }

    private void EndPlatformMappingAdmission(ProcessHandle handle, CapabilityId capabilityId)
    {
        lock (_platformMemoryUseGate)
        {
            var pending = _pendingPlatformMappingAdmissions[handle];
            if (pending == 1) _pendingPlatformMappingAdmissions.Remove(handle);
            else _pendingPlatformMappingAdmissions[handle] = pending - 1;
            _pendingPlatformMappingCapabilities.Remove(capabilityId);
        }
    }

    private void UntrackPlatformMapping(PlatformRegionMapping mapping)
    {
        foreach (var entry in _processPlatformMappings.ToArray())
        {
            entry.Value.RemoveAll(existing => existing.MappingId == mapping.MappingId);
            if (entry.Value.Count == 0)
                _processPlatformMappings.Remove(entry.Key);
        }
    }
}
