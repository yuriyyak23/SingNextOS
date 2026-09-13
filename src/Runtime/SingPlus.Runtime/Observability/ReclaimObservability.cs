using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

/// <summary>
/// Privileged diagnostic-only view of external lifetime evidence. These values
/// never confer authority and intentionally contain no provider lease, operation,
/// hardware, physical-address, page-table, or IOMMU identifiers.
/// </summary>
internal enum ReclaimExternalState
{
    NotApplicable = 0,
    Active,
    Draining,
    Closed,
    ExternalStateUnknown,
    Quarantined,
}

internal enum ReclaimDependencyKind
{
    None = 0,
    EndpointSession,
    PlatformDma,
    PlatformIrq,
    PlatformMmio,
    PlatformDevice,
    PlatformRegionMapping,
    PlatformDomain,
    KernelEventPublication,
}

internal sealed record ReclaimBlockerSnapshot(
    ReclaimDependencyKind Dependency,
    string ResourceId,
    ReclaimExternalState ExternalState,
    KernelError? Error,
    string Reason);

internal sealed record EndpointSessionDiagnosticSnapshot(
    EndpointSessionHandle Session,
    ProcessHandle Caller,
    ProcessHandle Service,
    EndpointSessionState State,
    DateTimeOffset? ExpiresAt,
    int PendingInvocations,
    bool ReclaimBlocking);

internal sealed record PlatformResourceDiagnosticSnapshot(
    ReclaimDependencyKind Kind,
    string ResourceId,
    ReclaimExternalState ExternalState,
    bool ReclaimBlocking);

internal sealed record PlatformAuthorityDiagnosticSnapshot(
    ProcessHandle Process,
    ReclaimExternalState DomainState,
    IReadOnlyList<PlatformResourceDiagnosticSnapshot> Resources)
{
    public bool HasLiveExternalAuthority =>
        DomainState is ReclaimExternalState.Active or ReclaimExternalState.Draining or ReclaimExternalState.ExternalStateUnknown or ReclaimExternalState.Quarantined ||
        Resources.Any(static resource => resource.ReclaimBlocking);
}

internal sealed record ProcessReclaimDiagnosticSnapshot(
    ProcessHandle Process,
    ProcessState? ProcessState,
    bool ReclaimRequested,
    ProcessTeardownPhase? TeardownPhase,
    bool Reclaimable,
    IReadOnlyList<EndpointSessionDiagnosticSnapshot> Sessions,
    PlatformAuthorityDiagnosticSnapshot Platform,
    ReclaimBlockerSnapshot? BlockingDependency);

internal sealed record ComponentReclaimDiagnosticSnapshot(
    ComponentLifecycleSnapshot Component,
    ProcessReclaimDiagnosticSnapshot Process,
    ReclaimBlockerSnapshot? BlockingDependency);

public sealed partial class PlatformAuthorityBridge
{
    internal PlatformAuthorityDiagnosticSnapshot DiagnosticSnapshot(PlatformDomainIdentity subject)
    {
        var domainRecord = _domains.Values
            .Where(record => record.Binding.Subject == subject)
            .OrderByDescending(record => record.Binding.BindingId.Value)
            .FirstOrDefault();
        if (domainRecord is null)
            return new PlatformAuthorityDiagnosticSnapshot(subject.Process, ReclaimExternalState.NotApplicable, []);

        var domainState = domainRecord.AuthorityState switch
        {
            DomainAuthorityState.Active => ReclaimExternalState.Active,
            DomainAuthorityState.Quarantined => ReclaimExternalState.Quarantined,
            DomainAuthorityState.Closed => ReclaimExternalState.Closed,
            _ => ReclaimExternalState.ExternalStateUnknown,
        };
        var bindingId = domainRecord.Binding.BindingId;
        var resources = new List<PlatformResourceDiagnosticSnapshot>();

        foreach (var record in _dmaGrants.Values
                     .Where(record => record.Grant.DeviceLease.DomainBinding.BindingId == bindingId)
                     .OrderBy(record => record.Grant.GrantId.Value))
        {
            var state = record.PlatformClosed
                ? ReclaimExternalState.Closed
                : domainState == ReclaimExternalState.Quarantined
                    ? ReclaimExternalState.Quarantined
                    : HasFaultPinnedDmaSubmission(record.Grant.GrantId)
                        ? ReclaimExternalState.ExternalStateUnknown
                        : HasActiveDmaSubmission(record.Grant.GrantId)
                            ? ReclaimExternalState.Draining
                            : ReclaimExternalState.Active;
            resources.Add(new(
                ReclaimDependencyKind.PlatformDma,
                DmaResourceId(record.Grant),
                state,
                !record.PlatformClosed));
        }

        foreach (var record in _irqBindings.Values
                     .Where(record => record.Binding.DeviceLease.DomainBinding.BindingId == bindingId)
                     .OrderBy(record => record.Binding.BindingId.Value))
        {
            resources.Add(new(
                ReclaimDependencyKind.PlatformIrq,
                record.Binding.Source.ResourceId,
                ChildExternalState(record.PlatformClosed, domainState),
                !record.PlatformClosed));
        }

        foreach (var record in _mmioLeases.Values
                     .Where(record => record.Lease.DeviceLease.DomainBinding.BindingId == bindingId)
                     .OrderBy(record => record.Lease.LeaseId.Value))
        {
            resources.Add(new(
                ReclaimDependencyKind.PlatformMmio,
                record.Lease.Region.ResourceId,
                ChildExternalState(record.PlatformClosed, domainState),
                !record.PlatformClosed));
        }

        foreach (var record in _deviceLeases.Values
                     .Where(record => record.Lease.DomainBinding.BindingId == bindingId)
                     .OrderBy(record => record.Lease.LeaseId.Value))
        {
            resources.Add(new(
                ReclaimDependencyKind.PlatformDevice,
                record.Lease.Device.ResourceId,
                ChildExternalState(record.PlatformClosed, domainState),
                !record.PlatformClosed));
        }

        foreach (var record in _mappings.Values
                     .Where(record => record.Mapping.DomainBinding.BindingId == bindingId)
                     .OrderBy(record => record.Mapping.MappingId.Value))
        {
            var state = domainState == ReclaimExternalState.Quarantined && record.ClosureState != PlatformExternalClosureState.Closed
                ? ReclaimExternalState.Quarantined
                : record.ClosureState switch
                {
                    PlatformExternalClosureState.Active => ReclaimExternalState.Active,
                    PlatformExternalClosureState.Draining => ReclaimExternalState.Draining,
                    PlatformExternalClosureState.Closed => ReclaimExternalState.Closed,
                    PlatformExternalClosureState.Faulted => ReclaimExternalState.ExternalStateUnknown,
                    _ => ReclaimExternalState.ExternalStateUnknown,
                };
            resources.Add(new(
                ReclaimDependencyKind.PlatformRegionMapping,
                CapabilityResourceIds.MemoryRegion(record.Mapping.Region.RegionId),
                state,
                !record.LocalReservationReleased));
        }

        return new PlatformAuthorityDiagnosticSnapshot(subject.Process, domainState, resources.ToArray());
    }

    private static ReclaimExternalState ChildExternalState(bool closed, ReclaimExternalState domainState) =>
        closed
            ? ReclaimExternalState.Closed
            : domainState == ReclaimExternalState.Quarantined
                ? ReclaimExternalState.Quarantined
                : ReclaimExternalState.Active;

    private static string DmaResourceId(PlatformDmaGrant grant) =>
        $"dma:{grant.DeviceLease.Device.ResourceId}:{CapabilityResourceIds.MemoryRegion(grant.Mapping.Mapping.Region.RegionId)}:{grant.Range.Offset}:{grant.Range.Length}:{grant.Direction}";
}

public sealed partial class RuntimeKernel
{
    internal KernelResult<EndpointSessionDiagnosticSnapshot[]> QueryEndpointSessionDiagnostics(ProcessHandle process)
    {
        var resolved = Processes.Resolve(process);
        return resolved.IsSuccess
            ? KernelResult<EndpointSessionDiagnosticSnapshot[]>.Ok(BuildSessionDiagnostics(process))
            : KernelResult<EndpointSessionDiagnosticSnapshot[]>.Fail(resolved.Error, resolved.Message!);
    }

    internal KernelResult<PlatformAuthorityDiagnosticSnapshot> QueryPlatformAuthorityDiagnostics(ProcessHandle process)
    {
        var resolved = Processes.Resolve(process);
        return resolved.IsSuccess
            ? KernelResult<PlatformAuthorityDiagnosticSnapshot>.Ok(PlatformAuthority.DiagnosticSnapshot(PlatformIdentity(resolved.Value!)))
            : KernelResult<PlatformAuthorityDiagnosticSnapshot>.Fail(resolved.Error, resolved.Message!);
    }

    internal KernelResult<ProcessReclaimDiagnosticSnapshot> QueryProcessReclaimDiagnostics(ProcessHandle process)
    {
        var resolved = Processes.Resolve(process);
        return resolved.IsSuccess
            ? KernelResult<ProcessReclaimDiagnosticSnapshot>.Ok(BuildProcessReclaimDiagnostics(resolved.Value!, process))
            : KernelResult<ProcessReclaimDiagnosticSnapshot>.Fail(resolved.Error, resolved.Message!);
    }

    internal KernelResult<ComponentReclaimDiagnosticSnapshot> QueryComponentReclaimDiagnostics(ComponentIdentity identity)
    {
        if (!_components.TryGetValue(identity, out var record))
            return KernelResult<ComponentReclaimDiagnosticSnapshot>.Fail(KernelError.ComponentNotFound, $"Component '{identity.Name}' was not found.");

        var resolved = Processes.Resolve(record.Process);
        ProcessReclaimDiagnosticSnapshot process;
        if (resolved.IsSuccess)
        {
            process = BuildProcessReclaimDiagnostics(resolved.Value!, record.Process);
        }
        else if (record.State == ComponentLifecycleState.Reclaimable)
        {
            process = new(
                record.Process,
                null,
                true,
                ProcessTeardownPhase.PlatformClosed,
                true,
                BuildSessionDiagnostics(record.Process),
                new PlatformAuthorityDiagnosticSnapshot(record.Process, ReclaimExternalState.Closed, []),
                null);
        }
        else
        {
            return KernelResult<ComponentReclaimDiagnosticSnapshot>.Fail(resolved.Error, resolved.Message!);
        }

        return KernelResult<ComponentReclaimDiagnosticSnapshot>.Ok(new(record.Snapshot, process, process.BlockingDependency));
    }

    private ProcessReclaimDiagnosticSnapshot BuildProcessReclaimDiagnostics(SingProcess process, ProcessHandle handle)
    {
        var sessions = BuildSessionDiagnostics(handle);
        var platform = PlatformAuthority.DiagnosticSnapshot(PlatformIdentity(process));
        if (!_processTeardowns.TryGetValue(handle, out var teardown))
            return new(handle, process.State, false, null, false, sessions, platform, null);

        sessions = sessions.Select(session => session with
        {
            ReclaimBlocking = session.State != EndpointSessionState.Closed || session.PendingInvocations != 0,
        }).ToArray();
        var blocker = SelectReclaimBlocker(teardown, sessions, platform);
        return new(handle, process.State, true, teardown.Phase, teardown.LocalReclaimCompleted, sessions, platform, blocker);
    }

    private EndpointSessionDiagnosticSnapshot[] BuildSessionDiagnostics(ProcessHandle process) =>
        EndpointSessions.SnapshotForProcess(process)
            .Select(session => session with
            {
                PendingInvocations = _endpointSessionInvocationRegistry?.PendingCount(session.Session) ?? 0,
            })
            .ToArray();

    private static ReclaimBlockerSnapshot? SelectReclaimBlocker(
        ProcessTeardownRecord teardown,
        IReadOnlyList<EndpointSessionDiagnosticSnapshot> sessions,
        PlatformAuthorityDiagnosticSnapshot platform)
    {
        var session = sessions.FirstOrDefault(static item => item.ReclaimBlocking);
        if (session is not null)
        {
            return new(
                ReclaimDependencyKind.EndpointSession,
                $"session:{session.Session.SessionId.Value}:gen:{session.Session.Generation.Value}",
                ReclaimExternalState.NotApplicable,
                teardown.BlockingError,
                "Endpoint session or invocation work remains live during process teardown.");
        }

        if (teardown.Phase == ProcessTeardownPhase.PlatformClosed && !teardown.LocalReclaimCompleted)
        {
            return new(
                ReclaimDependencyKind.KernelEventPublication,
                "kernel-event-publication",
                ReclaimExternalState.Closed,
                teardown.BlockingError,
                "External platform authority is closed, but staged local event publication still blocks reclaim.");
        }

        if (platform.DomainState == ReclaimExternalState.Quarantined)
        {
            return new(
                ReclaimDependencyKind.PlatformDomain,
                $"process:{platform.Process.ProcessId.Value}:gen:{platform.Process.Generation}",
                ReclaimExternalState.Quarantined,
                teardown.BlockingError ?? KernelError.PlatformFaulted,
                "Platform domain closure is ambiguous; the local binding remains quarantined and reclaim is forbidden.");
        }

        var resource = OrderedBlockingResources(platform.Resources).FirstOrDefault(static item => item.ReclaimBlocking);
        if (resource is not null)
        {
            var state = teardown.Phase == ProcessTeardownPhase.PlatformFaulted &&
                        resource.ExternalState is ReclaimExternalState.Active or ReclaimExternalState.Draining
                ? ReclaimExternalState.ExternalStateUnknown
                : resource.ExternalState;
            return new(
                resource.Kind,
                resource.ResourceId,
                state,
                teardown.BlockingError,
                teardown.Phase == ProcessTeardownPhase.PlatformFaulted
                    ? "External closure could not be proven for the exact resource; it remains pinned."
                    : "The exact external resource is still draining and local reclaim remains blocked.");
        }

        if (platform.DomainState is ReclaimExternalState.Active or ReclaimExternalState.Draining)
        {
            return new(
                ReclaimDependencyKind.PlatformDomain,
                $"process:{platform.Process.ProcessId.Value}:gen:{platform.Process.Generation}",
                teardown.Phase == ProcessTeardownPhase.PlatformFaulted ? ReclaimExternalState.ExternalStateUnknown : platform.DomainState,
                teardown.BlockingError,
                "The platform domain has not reached externally confirmed closure.");
        }

        return teardown.BlockingError is { } error
            ? new(
                ReclaimDependencyKind.PlatformDomain,
                $"process:{platform.Process.ProcessId.Value}:gen:{platform.Process.Generation}",
                ReclaimExternalState.ExternalStateUnknown,
                error,
                "Teardown reported a blocking platform error without a safely attributable closed dependency; reclaim remains forbidden.")
            : null;
    }

    private static IEnumerable<PlatformResourceDiagnosticSnapshot> OrderedBlockingResources(IReadOnlyList<PlatformResourceDiagnosticSnapshot> resources) =>
        resources.OrderBy(static resource => resource.Kind switch
        {
            ReclaimDependencyKind.PlatformDma => 0,
            ReclaimDependencyKind.PlatformIrq => 1,
            ReclaimDependencyKind.PlatformMmio => 2,
            ReclaimDependencyKind.PlatformDevice => 3,
            ReclaimDependencyKind.PlatformRegionMapping => 4,
            _ => 5,
        }).ThenBy(static resource => resource.ResourceId, StringComparer.Ordinal);
}
