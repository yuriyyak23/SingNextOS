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
        public PlatformRegionMapping[] Mappings { get; } = mappings;
        public PlatformDomainBinding? DomainBinding { get; } = domainBinding;
        public ProcessTeardownPhase Phase { get; set; } = ProcessTeardownPhase.LocalExitStarted;
        public bool ChannelsClosed { get; set; }
        public bool LocalAuthorizationRevoked { get; set; }
        public int PendingPlatformMappings { get; set; } = mappings.Length;
        public bool PlatformDomainClosed { get; set; } = domainBinding is null;
        public bool LocalReclaimCompleted { get; set; }
        public KernelError? BlockingError { get; set; }
        public bool ComposedAuthorityPrepared { get; set; }

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
    private readonly HashSet<ProcessHandle> _platformExecutionAttachments = [];
    private readonly Dictionary<ProcessHandle, List<PlatformRegionMapping>> _processPlatformMappings = [];

    public KernelResult<ProcessTeardownSnapshot> ObserveProcessTeardown(ProcessHandle handle)
    {
        lock (_platformMemoryUseGate)
            return ObserveProcessTeardownLocked(handle);
    }

    private KernelResult<ProcessTeardownSnapshot> ObserveProcessTeardownLocked(
        ProcessHandle handle)
    {
        if (!_processTeardowns.TryGetValue(handle, out var record))
        {
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

        var processResult = Processes.Resolve(handle);
        if (!processResult.IsSuccess)
        {
            return KernelResult<ProcessTeardownSnapshot>.Fail(
                processResult.Error,
                processResult.Message!);
        }

        return AdvanceProcessTeardown(processResult.Value!, record);
    }

    public KernelResult<ProcessTeardownSnapshot> QueryProcessTeardown(ProcessHandle handle)
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

        if (!record.ComposedAuthorityPrepared)
        {
            var prepared = PrepareComposedAuthorityTeardown(handle);
            lock (_platformMemoryUseGate)
            {
                if (!prepared.IsSuccess)
                {
                    record.Phase = ProcessTeardownPhase.PlatformFaulted;
                    record.BlockingError = prepared.Error;
                    return prepared;
                }
                record.ComposedAuthorityPrepared = true;
                record.Phase = ProcessTeardownPhase.LocalExitStarted;
                record.BlockingError = null;
            }
        }
        lock (_platformMemoryUseGate)
            return BeginOrAdvanceProcessTeardownLocked(handle, targetTerminalState);
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

        var nativeServices = DrainNativeServicesForProcess(handle);
        if (!nativeServices.IsSuccess)
            return KernelResult<ProcessTeardownRecord>.Fail(nativeServices.Error, nativeServices.Message!);
        process.SetState(ProcessState.Exiting);
        Channels.CloseAllForProcess(handle);
        ReleaseClosedIpcBudgetsForProcess(handle);
        CloseTraceSessionsForProcess(handle);
        CloseTelemetrySubscriptionsForProcess(handle);
        var sessionsClosed = CloseSessionsForProcess(handle);
        if (!sessionsClosed.IsSuccess)
            return KernelResult<ProcessTeardownRecord>.Fail(sessionsClosed.Error, sessionsClosed.Message!);
        _kernelEvents.BeginCloseForProcess(handle);

        var mappings = new Dictionary<PlatformRegionMappingId, PlatformRegionMapping>();
        if (_processPlatformMappings.TryGetValue(handle, out var trackedMappings))
            foreach (var mapping in trackedMappings) mappings[mapping.MappingId] = mapping;
        foreach (var capabilityId in process.Capabilities.Items)
        {
            foreach (var mapping in PlatformAuthority.BeginCapabilityRevocation(capabilityId))
                mappings[mapping.MappingId] = mapping;
            _ = PlatformAuthority.BeginIrqCapabilityRevocation(capabilityId);
            _ = PlatformAuthority.BeginMmioCapabilityRevocation(capabilityId);
            _ = PlatformAuthority.BeginDeviceCapabilityRevocation(capabilityId);
            _ = CapabilityAuthority.Revoke(capabilityId);
        }
        process.ClearCapabilities();
        _processPlatformBindings.TryGetValue(handle, out var domainBinding);
        var record = new ProcessTeardownRecord(handle, targetTerminalState,
            mappings.Values.OrderBy(static mapping => mapping.MappingId.Value).ToArray(),
            _processPlatformBindings.ContainsKey(handle) ? domainBinding : null)
        {
            ChannelsClosed = true,
            LocalAuthorizationRevoked = true
        };
        _processTeardowns.Add(handle, record);
        return KernelResult<ProcessTeardownRecord>.Ok(record);
    }

    private KernelResult PrepareComposedAuthorityTeardown(ProcessHandle handle)
    {
        var process = Processes.Resolve(handle);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
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
        return CloseVirtualDomainsForProcess(handle);
    }

    private KernelResult BeginOrAdvanceProcessTeardownLocked(
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
        ProcessHandle handle,
        ProcessState targetTerminalState)
    {
        if (targetTerminalState is not ProcessState.Exited and not ProcessState.Faulted)
        {
            return KernelResult<ProcessTeardownSnapshot>.Fail(
                KernelError.InvalidTransition,
                "Process teardown target must be Exited or Faulted.");
        }

        var resolved = Processes.Resolve(handle);
        if (!resolved.IsSuccess)
        {
            return KernelResult<ProcessTeardownSnapshot>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        if (process.State is ProcessState.Exited or ProcessState.Faulted)
        {
            return KernelResult<ProcessTeardownSnapshot>.Fail(
                KernelError.InvalidTransition,
                $"Cannot begin teardown from terminal state {process.State}.");
        }

        if (_processTeardowns.TryGetValue(handle, out var existing))
        {
            if (existing.TargetTerminalState != targetTerminalState)
            {
                return KernelResult<ProcessTeardownSnapshot>.Fail(
                    KernelError.InvalidTransition,
                    $"Process teardown is already targeting {existing.TargetTerminalState}.");
            }

            return AdvanceProcessTeardown(process, existing);
        }

        return KernelResult<ProcessTeardownSnapshot>.Fail(KernelError.PlatformFaulted,
            "Process teardown lifecycle was invoked without its admission-stop record.");
    }

    private KernelResult<ProcessTeardownSnapshot> AdvanceProcessTeardown(
        SingProcess process,
        ProcessTeardownRecord record)
    {
        var identity = PlatformIdentity(process);
        KernelError? firstBlockingError = null;
        var pendingMappings = 0;

        var cxlClosure = CloseCxlResourcesForProcess(record.Handle, new RegionOwner(process.DomainId, process.Generation));
        if (!cxlClosure.IsSuccess)
        {
            record.Phase = cxlClosure.Error == KernelError.PlatformBindingDraining
                ? ProcessTeardownPhase.PlatformDraining
                : ProcessTeardownPhase.PlatformFaulted;
            record.BlockingError = cxlClosure.Error == KernelError.PlatformBindingDraining ? null : cxlClosure.Error;
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

        var computeProgress = AdvancePlatformDsc1ForProcess(record.Handle);
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

        var deviceProgress = AdvancePlatformDeviceLeasesForProcess(process, record.Handle);
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

        var borrowGrantProgress = AdvancePlatformBorrowReadGrantsForProcess(record.Handle);
        if (!borrowGrantProgress.IsSuccess)
        {
            firstBlockingError ??= borrowGrantProgress.Error;
        }
        else
        {
            pendingMappings += borrowGrantProgress.Value;
        }

        foreach (var mapping in record.Mappings)
        {
            var queried = PlatformAuthority.QueryRegionMappingLifecycle(mapping, identity);
            if (!queried.IsSuccess)
            {
                if (queried.Error is KernelError.PlatformBindingNotFound or KernelError.PlatformBindingRevoked &&
                    (!_processPlatformMappings.TryGetValue(record.Handle, out var currentMappings) ||
                     currentMappings.All(current => current != mapping)))
                    continue;
                firstBlockingError ??= queried.Error;
                continue;
            }

            var lifecycle = queried.Value!;
            if (lifecycle.LocalReservationReleased)
                continue;

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
                    firstBlockingError ??= KernelError.PlatformFaulted;
                    continue;
                default:
                    firstBlockingError ??= KernelError.PlatformFaulted;
                    continue;
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
            if (current.PlatformClosure == PlatformExternalClosureState.Faulted)
            {
                firstBlockingError ??= KernelError.PlatformFaulted;
                continue;
            }

            if (current.PlatformClosure != PlatformExternalClosureState.Closed)
            {
                pendingMappings++;
                continue;
            }

            var finalize = FinalizePlatformRegionMappingClosure(mapping, identity, current);
            if (!finalize.IsSuccess)
            {
                if (finalize.Error == KernelError.PlatformBindingDraining)
                    pendingMappings++;
                else
                    firstBlockingError ??= finalize.Error;
            }
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

        if (!record.PlatformDomainClosed && record.DomainBinding is { } binding)
        {
            var revokeDomain = PlatformAuthority.RevokeDomain(binding, identity);
            if (!revokeDomain.IsSuccess)
            {
                record.Phase = ProcessTeardownPhase.PlatformFaulted;
                record.BlockingError = revokeDomain.Error;
                return KernelResult<ProcessTeardownSnapshot>.Ok(record.Snapshot);
            }

            record.PlatformDomainClosed = true;
            UntrackPlatformBinding(record.Handle, binding);
        }
        else
        {
            record.PlatformDomainClosed = true;
        }

        record.Phase = ProcessTeardownPhase.PlatformClosed;
        var cleanup = FinalizeProcessCleanup(process);
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

    private KernelResult FinalizeProcessCleanup(SingProcess process)
    {
        var handle = new ProcessHandle(process.ProcessId, process.Generation);
        if (HasPinnedSecureExecution(handle))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Live or quarantined secure execution forbids local process reclaim.");
        Channels.CloseAllForProcess(handle);
        var eventsClosed = _kernelEvents.CloseAllForProcess(handle);
        if (!eventsClosed.IsSuccess) return eventsClosed;

        var domainEnded = Domains.Remove(process);
        if (domainEnded)
        {
            CapabilityAuthority.RevokeAllForDomain(process.DomainId);
            Regions.ReturnAllLoansForBorrowerDomain(process.DomainId);
            Regions.ReclaimAllForDomain(process.DomainId);
            ReleaseReclaimedRegionBudgetsForProcess(handle);
            Channels.CloseAllForDomain(process.DomainId);
        }

        process.ClearRuntimeResources();
        return KernelResult.Ok();
    }

    private void TrackPlatformBinding(ProcessHandle process, PlatformDomainBinding binding, bool executionAttached = true)
    {
        _processPlatformBindings[process] = binding;
        if (executionAttached) _platformExecutionAttachments.Add(process);
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
        if (!_processPlatformMappings.TryGetValue(process, out var mappings))
        {
            mappings = [];
            _processPlatformMappings.Add(process, mappings);
        }

        if (!mappings.Any(existing => existing.MappingId == mapping.MappingId))
            mappings.Add(mapping);
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
