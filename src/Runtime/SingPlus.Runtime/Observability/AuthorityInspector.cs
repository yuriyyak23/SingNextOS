using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

internal readonly record struct SecureDomainInspectionRecord(
    SecureDomainHandle Handle,
    ProcessHandle Owner,
    SecureDomainState State,
    ulong PolicyGeneration,
    ulong ProtectionGeneration,
    int RegionCount);

public sealed class AuthorityInspector
{
    private readonly RuntimeKernel _kernel;
    private readonly ProcessHandle _principal;
    private readonly CapabilityId? _systemCapability;
    private readonly TimeProvider _timeProvider;
    private readonly object _captureGate = new();
    private ulong _captureSequence;

    internal AuthorityInspector(
        RuntimeKernel kernel,
        ProcessHandle principal,
        CapabilityId? systemCapability,
        TimeProvider timeProvider)
    {
        _kernel = kernel;
        _principal = principal;
        _systemCapability = systemCapability;
        _timeProvider = timeProvider;
    }

    public KernelResult<AuthorityInspectionSnapshot> Capture(
        AuthorityInspectionConsistency consistency = AuthorityInspectionConsistency.PointInTimeBestEffort)
    {
        if (!Enum.IsDefined(consistency))
            return KernelResult<AuthorityInspectionSnapshot>.Fail(KernelError.InvalidTransition, "Inspection consistency class is undefined.");
        var access = ValidateAccess();
        if (!access.IsSuccess)
            return KernelResult<AuthorityInspectionSnapshot>.Fail(access.Error, access.Message!);

        if (consistency == AuthorityInspectionConsistency.HistoricalReference)
            return KernelResult<AuthorityInspectionSnapshot>.Ok(Materialize(BuildHistoricalProjection(), consistency));

        if (consistency == AuthorityInspectionConsistency.PointInTimeBestEffort)
            return KernelResult<AuthorityInspectionSnapshot>.Ok(Materialize(BuildLiveProjection(), consistency));

        // A bounded stable cut: every owning registry supplies detached snapshots; the complete
        // projection must converge twice before it is labelled AuthorityLockedSnapshot.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var first = BuildLiveProjection();
            var second = BuildLiveProjection();
            if (string.Equals(first.Fingerprint, second.Fingerprint, StringComparison.Ordinal))
                return KernelResult<AuthorityInspectionSnapshot>.Ok(Materialize(second, consistency));
        }

        return KernelResult<AuthorityInspectionSnapshot>.Fail(
            KernelError.SnapshotUnstable,
            "Authoritative registries did not reach a stable bounded inspection cut.");
    }

    public KernelResult<AuthorityInspectionSnapshot> InspectOwner(RegionHandle region) =>
        SelectRegionSubgraph(region, incomingOnly: true);

    public KernelResult<AuthorityInspectionSnapshot> InspectDependents(RegionHandle region) =>
        SelectRegionSubgraph(region, incomingOnly: false);

    public KernelResult<AuthorityInspectionSnapshot> InspectCapabilityProvenance(CapabilityId capability)
    {
        var access = ValidateAccess();
        if (!access.IsSuccess) return KernelResult<AuthorityInspectionSnapshot>.Fail(access.Error, access.Message!);
        var records = _kernel.CapabilityAuthority.InspectionSnapshot();
        var target = records.SingleOrDefault(record => record.Descriptor.CapabilityId == capability);
        if (target is null)
            return KernelResult<AuthorityInspectionSnapshot>.Fail(KernelError.CapabilityNotFound, "Capability provenance was not found.");
        if (!CanSee(new RegionOwner(target.Descriptor.SubjectDomainId, target.Descriptor.Generation)))
            return KernelResult<AuthorityInspectionSnapshot>.Fail(KernelError.ProjectionDenied, "Cross-service capability inspection requires system inspection authority.");

        var full = Materialize(BuildLiveProjection(), AuthorityInspectionConsistency.PointInTimeBestEffort);
        var ids = new HashSet<AuthorityNodeId> { CapabilityNodeId(target.Descriptor) };
        var byId = records.ToDictionary(record => record.Descriptor.CapabilityId);
        var cursor = target;
        while (cursor.DelegatedFrom is { } parentId && byId.TryGetValue(parentId, out var parent))
        {
            if (CanSee(new RegionOwner(parent.Descriptor.SubjectDomainId, parent.Descriptor.Generation)))
                ids.Add(CapabilityNodeId(parent.Descriptor));
            else
                ids.Add(RedactedNodeId());
            cursor = parent;
        }
        return KernelResult<AuthorityInspectionSnapshot>.Ok(SelectSubgraph(full, ids));
    }

    public KernelResult<AuthorityInspectionExplanation> WhyMoveBlocked(RegionHandle region)
    {
        var resolved = ResolveVisibleRegion(region);
        if (resolved.Error == KernelError.StaleGeneration)
            return KernelResult<AuthorityInspectionExplanation>.Ok(Explanation([
                new(AuthorityBlockReasonKind.StaleGeneration, RegionNodeId(region), "The requested region generation is stale and was not merged with the current node.", false)
            ]));
        if (!resolved.IsSuccess)
            return KernelResult<AuthorityInspectionExplanation>.Fail(resolved.Error, resolved.Message!);
        return KernelResult<AuthorityInspectionExplanation>.Ok(ExplainRegion(resolved.Value!, includeOwnershipState: true));
    }

    public KernelResult<AuthorityInspectionExplanation> WhyReclaimBlocked(RegionHandle region)
    {
        var resolved = ResolveVisibleRegion(region);
        if (resolved.Error == KernelError.StaleGeneration)
            return KernelResult<AuthorityInspectionExplanation>.Ok(Explanation([
                new(AuthorityBlockReasonKind.StaleGeneration, RegionNodeId(region), "The requested region generation is stale and was not merged with the current node.", false)
            ]));
        if (!resolved.IsSuccess)
            return KernelResult<AuthorityInspectionExplanation>.Fail(resolved.Error, resolved.Message!);
        return KernelResult<AuthorityInspectionExplanation>.Ok(ExplainRegion(resolved.Value!, includeOwnershipState: false));
    }

    public KernelResult<AuthorityInspectionExplanation> WhyReclaimBlocked(ProcessHandle process)
    {
        var visibility = EnsureVisible(process);
        if (!visibility.IsSuccess)
            return KernelResult<AuthorityInspectionExplanation>.Fail(visibility.Error, visibility.Message!);
        var diagnostic = _kernel.QueryProcessReclaimDiagnostics(process);
        if (!diagnostic.IsSuccess)
        {
            var stale = _kernel.Processes.Resolve(process);
            if (stale.Error is KernelError.StaleHandle or KernelError.StaleGeneration)
                return KernelResult<AuthorityInspectionExplanation>.Ok(Explanation([
                    new(AuthorityBlockReasonKind.StaleGeneration, ProcessNodeId(process), "The requested process generation is stale.", false)
                ]));
            return KernelResult<AuthorityInspectionExplanation>.Fail(diagnostic.Error, diagnostic.Message!);
        }

        var reasons = new List<AuthorityBlockReason>();
        if (diagnostic.Value!.BlockingDependency is { } blocker)
            reasons.Add(ConvertReclaimBlocker(blocker));
        var processRecord = _kernel.Processes.Resolve(process);
        if (processRecord.IsSuccess)
        {
            var principal = new RegionOwner(processRecord.Value!.DomainId, process.Generation);
            foreach (var operation in _kernel.ExternalOperations.InspectionSnapshot()
                         .Where(operation => operation.Principal == principal && operation.State != ExternalOperationState.Released))
            {
                reasons.Add(new(
                    operation.Disposition is ExternalOperationDisposition.ProviderLost or ExternalOperationDisposition.Faulted
                        ? AuthorityBlockReasonKind.ProviderEffectUncontained
                        : AuthorityBlockReasonKind.ProviderClosurePending,
                    OperationNodeId(operation.Operation),
                    "An exact ExternalOperation remains live and blocks process reclaim until closure or containment.",
                    false));
            }
        }
        if (!diagnostic.Value.Reclaimable && diagnostic.Value.ReclaimRequested && reasons.Count == 0)
            reasons.Add(new(AuthorityBlockReasonKind.ProviderClosurePending, ProcessNodeId(process), "Process teardown has not reached exact reclaim closure.", false));
        return KernelResult<AuthorityInspectionExplanation>.Ok(Explanation(reasons));
    }

    public KernelResult<AuthorityInspectionExplanation> WhyServiceDrainBlocked(ServiceInstanceHandle service)
    {
        var access = ValidateAccess();
        if (!access.IsSuccess) return KernelResult<AuthorityInspectionExplanation>.Fail(access.Error, access.Message!);
        var record = _kernel.Services.Snapshot().SingleOrDefault(candidate =>
            candidate.Descriptor.Service.Id == service.ServiceId);
        if (record is null)
            return KernelResult<AuthorityInspectionExplanation>.Fail(KernelError.ServiceNotFound, "Service was not found.");
        if (!CanSee(record.Provider))
            return KernelResult<AuthorityInspectionExplanation>.Fail(KernelError.ProjectionDenied, "Cross-service drain inspection requires system inspection authority.");
        var process = _kernel.Processes.Resolve(record.Provider);
        var domain = process.IsSuccess ? process.Value!.DomainId : default;
        if (record.Descriptor.Generation != service.Generation || record.Provider != service.Process || domain != service.Domain)
            return KernelResult<AuthorityInspectionExplanation>.Ok(Explanation([
                new(AuthorityBlockReasonKind.StaleGeneration, ServiceNodeId(service.ServiceId, service.Generation, service.Process), "The requested service instance is stale and was not merged with the current generation.", false)
            ]));
        return WhyReclaimBlocked(record.Provider);
    }

    public KernelResult<AuthorityInspectionExplanation> WhyExternalOperationPinned(ExternalOperationHandle operation)
    {
        var access = ValidateAccess();
        if (!access.IsSuccess) return KernelResult<AuthorityInspectionExplanation>.Fail(access.Error, access.Message!);
        var current = _kernel.ExternalOperations.InspectionSnapshot()
            .SingleOrDefault(candidate => candidate.Operation.OperationId == operation.OperationId);
        if (current is null)
            return KernelResult<AuthorityInspectionExplanation>.Fail(KernelError.ExternalOperationNotFound, "External operation was not found.");
        if (!CanSee(current.Principal))
            return KernelResult<AuthorityInspectionExplanation>.Fail(KernelError.ProjectionDenied, "Cross-service operation inspection requires system inspection authority.");
        if (current.Operation.Generation != operation.Generation)
            return KernelResult<AuthorityInspectionExplanation>.Ok(Explanation([
                new(AuthorityBlockReasonKind.StaleGeneration, OperationNodeId(operation), "The requested external-operation generation is stale.", false)
            ]));

        var reasons = new List<AuthorityBlockReason>();
        if (current.State != ExternalOperationState.Released)
        {
            var kind = current.Disposition is ExternalOperationDisposition.ProviderLost or ExternalOperationDisposition.Faulted
                ? AuthorityBlockReasonKind.ProviderEffectUncontained
                : current.State == ExternalOperationState.Published
                    ? AuthorityBlockReasonKind.ProviderClosurePending
                    : AuthorityBlockReasonKind.ExternalOperationActive;
            reasons.Add(new(kind, OperationNodeId(operation), "The operation retains local pins until exact provider closure or independently proven containment.", false));
        }
        foreach (var use in current.Admission?.RegionUses.Where(static use => use.State == RegionUseState.Active) ?? [])
            reasons.Add(new(AuthorityBlockReasonKind.RegionUseActive, RegionUseNodeId(use.Handle), "An exact RegionUse remains active for this operation.", false));
        return KernelResult<AuthorityInspectionExplanation>.Ok(Explanation(reasons));
    }

    private KernelResult<AuthorityInspectionSnapshot> SelectRegionSubgraph(RegionHandle region, bool incomingOnly)
    {
        var resolved = ResolveVisibleRegion(region);
        if (resolved.Error == KernelError.StaleGeneration)
        {
            var staleBase = Materialize(BuildLiveProjection(), AuthorityInspectionConsistency.PointInTimeBestEffort);
            var stale = new AuthorityInspectionNode(RegionNodeId(region), AuthorityNodeKind.OwnedRegion, "Stale", region.Generation.Value, true, false);
            return KernelResult<AuthorityInspectionSnapshot>.Ok(staleBase with { Nodes = [stale], Edges = [] });
        }
        if (!resolved.IsSuccess) return KernelResult<AuthorityInspectionSnapshot>.Fail(resolved.Error, resolved.Message!);
        var full = Materialize(BuildLiveProjection(), AuthorityInspectionConsistency.PointInTimeBestEffort);
        var target = RegionNodeId(resolved.Value!.Region.Handle);
        var ids = new HashSet<AuthorityNodeId> { target };
        foreach (var edge in full.Edges.Where(edge => edge.Target == target || (!incomingOnly && edge.Source == target)))
        {
            ids.Add(edge.Source);
            ids.Add(edge.Target);
        }
        return KernelResult<AuthorityInspectionSnapshot>.Ok(SelectSubgraph(full, ids));
    }

    private AuthorityInspectionExplanation ExplainRegion(RegionAuthorityInspectionRecord region, bool includeOwnershipState)
    {
        var reasons = new List<AuthorityBlockReason>();
        if (region.Borrow is { } borrow)
            reasons.Add(new(AuthorityBlockReasonKind.BorrowActive, BorrowNodeId(borrow), "An exact borrow lease is active.", false));
        foreach (var use in region.Uses.Where(static use => use.State == RegionUseState.Active))
            reasons.Add(new(AuthorityBlockReasonKind.RegionUseActive, RegionUseNodeId(use.Handle), $"Active {use.Mode} RegionUse pins the exact range.", false));
        if (region.PlatformMappingReserved)
            reasons.Add(new(AuthorityBlockReasonKind.PlatformMappingActive, PlatformMappingNodeId(region.Region.Handle), "An exact platform mapping reservation remains active or draining.", false));
        if (region.BackingLease is { } backing)
            reasons.Add(new(AuthorityBlockReasonKind.BackingLeaseActive, BackingNodeId(backing.Handle), "A backing lease remains active.", false));
        if (region.ExternalBorrowReadGrantReserved)
            reasons.Add(new(AuthorityBlockReasonKind.ExternalBorrowActive, BorrowNodeId(region.Borrow ?? default), "An external read grant keeps the borrow lifetime pinned.", false));
        if (includeOwnershipState && region.Region.State != RegionState.Owned)
            reasons.Add(new(AuthorityBlockReasonKind.WrongOwner, RegionNodeId(region.Region.Handle), $"MOVE requires Owned state; current state is {region.Region.State}.", false));
        return Explanation(reasons);
    }

    private AuthorityInspectionExplanation Explanation(IReadOnlyList<AuthorityBlockReason> reasons) =>
        new(Materialize(BuildLiveProjection(), AuthorityInspectionConsistency.PointInTimeBestEffort), reasons);

    private KernelResult<RegionAuthorityInspectionRecord> ResolveVisibleRegion(RegionHandle region)
    {
        var access = ValidateAccess();
        if (!access.IsSuccess) return KernelResult<RegionAuthorityInspectionRecord>.Fail(access.Error, access.Message!);
        var current = _kernel.Regions.InspectionSnapshot().SingleOrDefault(candidate => candidate.Region.Handle.RegionId == region.RegionId);
        if (current is null)
            return KernelResult<RegionAuthorityInspectionRecord>.Fail(KernelError.RegionNotFound, "Region was not found.");
        if (!CanSee(current.Region.Owner))
            return KernelResult<RegionAuthorityInspectionRecord>.Fail(KernelError.ProjectionDenied, "Cross-service region inspection requires system inspection authority.");
        if (current.Region.Handle.Generation != region.Generation)
            return KernelResult<RegionAuthorityInspectionRecord>.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        return KernelResult<RegionAuthorityInspectionRecord>.Ok(current);
    }

    private KernelResult ValidateAccess()
    {
        var principal = _kernel.Processes.Resolve(_principal);
        if (!principal.IsSuccess) return KernelResult.Fail(principal.Error, principal.Message!);
        if (_systemCapability is not { } capability) return KernelResult.Ok();
        var validated = _kernel.ValidateCapability(_principal, capability, CapabilityRights.Read);
        if (!validated.IsSuccess) return KernelResult.Fail(KernelError.ProjectionDenied, validated.Message ?? "Inspection capability is invalid.");
        return validated.Value!.ResourceKind == ResourceKind.KernelService &&
               validated.Value.ResourceId == CapabilityResourceIds.AuthorityInspector
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.ProjectionDenied, "Capability does not authorize system authority inspection.");
    }

    private KernelResult EnsureVisible(ProcessHandle process)
    {
        var access = ValidateAccess();
        if (!access.IsSuccess) return access;
        return CanSee(process)
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.ProjectionDenied, "Cross-service inspection requires system inspection authority.");
    }

    private bool CanSee(ProcessHandle process) => _systemCapability is not null || process == _principal;
    private bool CanSee(RegionOwner owner) => _systemCapability is not null ||
        (_kernel.Processes.Resolve(_principal).IsSuccess &&
         _kernel.Processes.Resolve(_principal).Value!.DomainId == owner.DomainId && _principal.Generation == owner.ProcessGeneration);

    private Projection BuildLiveProjection()
    {
        var nodes = new Dictionary<AuthorityNodeId, AuthorityInspectionNode>();
        var edges = new List<AuthorityInspectionEdge>();
        var redacted = 0;
        var processes = _kernel.Processes.Snapshot().Where(process => CanSee(new ProcessHandle(process.ProcessId, process.Generation))).ToArray();
        var visibleHandles = processes.Select(static process => new ProcessHandle(process.ProcessId, process.Generation)).ToHashSet();

        foreach (var process in processes)
        {
            var handle = new ProcessHandle(process.ProcessId, process.Generation);
            var processId = ProcessNodeId(handle);
            var domainId = DomainNodeId(process.DomainId, process.Generation);
            AddNode(nodes, processId, AuthorityNodeKind.Process, process.State.ToString(), process.Generation);
            AddNode(nodes, domainId, AuthorityNodeKind.Domain, "Active", process.Generation);
            edges.Add(new(domainId, processId, AuthorityEdgeKind.Owns, false));
        }

        var capabilities = _kernel.CapabilityAuthority.InspectionSnapshot();
        var visibleCapabilities = capabilities.Where(record => processes.Any(process => process.DomainId == record.Descriptor.SubjectDomainId && process.Generation == record.Descriptor.Generation)).ToArray();
        var visibleCapabilityIds = visibleCapabilities.Select(static record => record.Descriptor.CapabilityId).ToHashSet();
        foreach (var capability in visibleCapabilities)
        {
            var id = CapabilityNodeId(capability.Descriptor);
            AddNode(nodes, id, AuthorityNodeKind.Capability, capability.Revoked ? "Revoked" : "Active", capability.Descriptor.Generation, capability.Revoked);
            var owner = DomainNodeId(capability.Descriptor.SubjectDomainId, capability.Descriptor.Generation);
            AddNode(nodes, owner, AuthorityNodeKind.Domain, "Active", capability.Descriptor.Generation);
            edges.Add(new(owner, id, AuthorityEdgeKind.Owns, false));
            if (capability.DelegatedFrom is { } parent && capabilities.SingleOrDefault(record => record.Descriptor.CapabilityId == parent) is { } parentRecord)
            {
                if (visibleCapabilityIds.Contains(parent))
                    edges.Add(new(CapabilityNodeId(parentRecord.Descriptor), id, AuthorityEdgeKind.DelegatedTo, false));
                else
                {
                    AddNode(nodes, RedactedNodeId(), AuthorityNodeKind.Redacted, "Redacted", 0, redacted: true);
                    edges.Add(new(RedactedNodeId(), id, AuthorityEdgeKind.DelegatedTo, true));
                    redacted++;
                }
            }
            else
            {
                var issuer = DomainNodeId(capability.Descriptor.IssuerDomainId, capability.Descriptor.Generation);
                if (nodes.ContainsKey(issuer)) edges.Add(new(issuer, id, AuthorityEdgeKind.MintedBy, false));
            }
        }

        var regions = _kernel.Regions.InspectionSnapshot().Where(region => CanSee(region.Region.Owner)).ToArray();
        foreach (var region in regions)
        {
            var regionId = RegionNodeId(region.Region.Handle);
            AddNode(nodes, regionId, AuthorityNodeKind.OwnedRegion, region.Region.State.ToString(), region.Region.Handle.Generation.Value);
            var ownerProcess = processes.SingleOrDefault(process => process.DomainId == region.Region.Owner.DomainId && process.Generation == region.Region.Owner.ProcessGeneration);
            if (ownerProcess is not null)
                edges.Add(new(ProcessNodeId(new(ownerProcess.ProcessId, ownerProcess.Generation)), regionId, AuthorityEdgeKind.Owns, false));
            if (region.Borrow is { } borrow)
            {
                var borrowId = BorrowNodeId(borrow);
                AddNode(nodes, borrowId, AuthorityNodeKind.BorrowLease, "Active", borrow.Generation.Value);
                edges.Add(new(regionId, borrowId, AuthorityEdgeKind.BorrowedBy, false));
            }
            if (region.BackingLease is { } backing)
            {
                var backingId = BackingNodeId(backing.Handle);
                AddNode(nodes, backingId, AuthorityNodeKind.BackingLease, "Active", backing.Handle.Generation);
                edges.Add(new(regionId, backingId, AuthorityEdgeKind.BackedBy, false));
            }
            if (region.PlatformMappingReserved)
            {
                var mappingId = PlatformMappingNodeId(region.Region.Handle);
                AddNode(nodes, mappingId, AuthorityNodeKind.PlatformMapping, "ActiveOrDraining", region.Region.Handle.Generation.Value);
                edges.Add(new(mappingId, regionId, AuthorityEdgeKind.Pins, false));
            }
            foreach (var use in region.Uses)
            {
                var useId = RegionUseNodeId(use.Handle);
                AddNode(nodes, useId, AuthorityNodeKind.RegionUse, use.State.ToString(), use.Handle.Generation, use.State != RegionUseState.Active);
                edges.Add(new(useId, regionId, AuthorityEdgeKind.Pins, false));
            }
        }

        foreach (var operation in _kernel.ExternalOperations.InspectionSnapshot().Where(operation => CanSee(operation.Principal)))
        {
            var operationId = OperationNodeId(operation.Operation);
            AddNode(nodes, operationId, AuthorityNodeKind.ExternalOperation, $"{operation.State}/{operation.Disposition}", operation.Operation.Generation.Value,
                operation.State == ExternalOperationState.Released);
            var ownerProcess = processes.SingleOrDefault(process => process.DomainId == operation.Principal.DomainId && process.Generation == operation.Principal.ProcessGeneration);
            if (ownerProcess is not null)
                edges.Add(new(ProcessNodeId(new(ownerProcess.ProcessId, ownerProcess.Generation)), operationId, AuthorityEdgeKind.Owns, false));
            foreach (var use in operation.Admission?.RegionUses ?? [])
                edges.Add(new(operationId, RegionUseNodeId(use.Handle), AuthorityEdgeKind.Requires, false));
            if (operation.State != ExternalOperationState.Released)
                foreach (var request in operation.PreparationRegionHandles())
                    edges.Add(new(operationId, RegionNodeId(request), AuthorityEdgeKind.Pins, false));
        }

        foreach (var reservation in _kernel.Budgets.InspectionSnapshot().Where(reservation => CanSee(reservation.Owner)))
        {
            var reservationId = BudgetReservationNodeId(reservation.Reservation);
            AddNode(nodes, reservationId, AuthorityNodeKind.BudgetReservation,
                $"{reservation.State}/{reservation.Lifetime}", reservation.Reservation.Generation.Value);
            if (visibleHandles.Contains(reservation.Owner))
                edges.Add(new(ProcessNodeId(reservation.Owner), reservationId, AuthorityEdgeKind.Owns, false));
        }

        foreach (var checkpoint in _kernel.CheckpointInspectionSnapshot().Where(checkpoint => CanSee(checkpoint.SourceProcess)))
        {
            var checkpointId = CheckpointPinNodeId(checkpoint.Handle);
            AddNode(nodes, checkpointId, AuthorityNodeKind.CheckpointPin, checkpoint.State.ToString(), checkpoint.Handle.Generation.Value);
            if (visibleHandles.Contains(checkpoint.SourceProcess))
                edges.Add(new(ProcessNodeId(checkpoint.SourceProcess), checkpointId, AuthorityEdgeKind.Owns, false));
            edges.Add(new(checkpointId, BudgetReservationNodeId(checkpoint.StorageReservation), AuthorityEdgeKind.Requires, false));
        }

        foreach (var process in processes)
        {
            var handle = new ProcessHandle(process.ProcessId, process.Generation);
            var platform = _kernel.PlatformAuthority.DiagnosticSnapshot(new PlatformDomainIdentity(process.DomainId, handle));
            foreach (var resource in platform.Resources)
            {
                var kind = resource.Kind switch
                {
                    ReclaimDependencyKind.PlatformDma => AuthorityNodeKind.DmaGrant,
                    ReclaimDependencyKind.PlatformDevice => AuthorityNodeKind.DeviceLease,
                    ReclaimDependencyKind.PlatformRegionMapping => AuthorityNodeKind.PlatformMapping,
                    _ => AuthorityNodeKind.PlatformMapping
                };
                var id = Opaque($"platform:{handle.ProcessId.Value}:{handle.Generation}:{resource.Kind}:{resource.ResourceId}");
                AddNode(nodes, id, kind, resource.ExternalState.ToString(), handle.Generation);
                edges.Add(new(ProcessNodeId(handle), id, AuthorityEdgeKind.Owns, false));
            }
        }

        foreach (var domain in _kernel.VirtualDomainInspectionSnapshot().Where(domain => visibleHandles.Contains(domain.Owner)))
        {
            var id = VirtualNodeId(domain.Handle);
            AddNode(nodes, id, AuthorityNodeKind.VirtualDomain, domain.State.ToString(), domain.Handle.Generation.Value);
            edges.Add(new(ProcessNodeId(domain.Owner), id, AuthorityEdgeKind.Owns, false));
            if (domain.Parent is { } parent) edges.Add(new(VirtualNodeId(parent), id, AuthorityEdgeKind.Requires, false));
        }
        foreach (var domain in _kernel.SecureDomainInspectionSnapshot().Where(domain => visibleHandles.Contains(domain.Owner)))
        {
            var id = SecureNodeId(domain.Handle);
            AddNode(nodes, id, AuthorityNodeKind.SecureDomain, domain.State.ToString(), domain.Handle.Generation.Value);
            edges.Add(new(ProcessNodeId(domain.Owner), id, AuthorityEdgeKind.Owns, false));
        }

        foreach (var service in _kernel.Services.Snapshot().Where(service => visibleHandles.Contains(service.Provider)))
        {
            var id = ServiceNodeId(service.Descriptor.Service.Id, service.Descriptor.Generation, service.Provider);
            AddNode(nodes, id, AuthorityNodeKind.ServiceInstance, service.Descriptor.Availability.ToString(), service.Descriptor.Generation.Value);
            edges.Add(new(ProcessNodeId(service.Provider), id, AuthorityEdgeKind.Owns, false));
        }
        foreach (var process in processes)
        {
            var handle = new ProcessHandle(process.ProcessId, process.Generation);
            foreach (var session in _kernel.QueryEndpointSessionDiagnostics(handle).Value ?? [])
            {
                if (!visibleHandles.Contains(session.Caller) && !visibleHandles.Contains(session.Service)) continue;
                var id = Opaque($"service-binding:{session.Session.SessionId.Value}:{session.Session.Generation.Value}");
                AddNode(nodes, id, AuthorityNodeKind.ServiceBinding, session.State.ToString(), session.Session.Generation.Value,
                    session.State == EndpointSessionState.Closed);
                if (visibleHandles.Contains(session.Caller)) edges.Add(new(ProcessNodeId(session.Caller), id, AuthorityEdgeKind.Requires, false));
                if (visibleHandles.Contains(session.Service)) edges.Add(new(id, ProcessNodeId(session.Service), AuthorityEdgeKind.AuthorizedBy, false));
            }
        }

        return Projection.Create(nodes.Values, edges, redacted);
    }

    private Projection BuildHistoricalProjection()
    {
        var nodes = new Dictionary<AuthorityNodeId, AuthorityInspectionNode>();
        var edges = new List<AuthorityInspectionEdge>();
        foreach (var lineage in _kernel.Services.ReplacementLineageSnapshot().Where(lineage =>
                     _systemCapability is not null || lineage.Previous.Provider == _principal || lineage.Replacement.Provider == _principal))
        {
            var previous = ServiceNodeId(lineage.Previous.ServiceId, lineage.Previous.Generation, lineage.Previous.Provider);
            var replacement = ServiceNodeId(lineage.Replacement.ServiceId, lineage.Replacement.Generation, lineage.Replacement.Provider);
            AddNode(nodes, previous, AuthorityNodeKind.ServiceInstance, "Historical", lineage.Previous.Generation.Value, stale: true);
            AddNode(nodes, replacement, AuthorityNodeKind.ServiceInstance, "Historical", lineage.Replacement.Generation.Value, stale: true);
            edges.Add(new(previous, replacement, AuthorityEdgeKind.ReplacedBy, false));
        }
        return Projection.Create(nodes.Values, edges, 0);
    }

    private AuthorityInspectionSnapshot Materialize(Projection projection, AuthorityInspectionConsistency consistency)
    {
        ulong sequence;
        lock (_captureGate) sequence = ++_captureSequence;
        return new(
            AuthorityInspectionContract.Version,
            sequence,
            _timeProvider.GetTimestamp(),
            consistency,
            _systemCapability is null ? AuthorityInspectionScope.Self : AuthorityInspectionScope.System,
            projection.Nodes,
            projection.Edges,
            projection.RedactedFactCount);
    }

    private static AuthorityInspectionSnapshot SelectSubgraph(AuthorityInspectionSnapshot full, HashSet<AuthorityNodeId> ids)
    {
        var edges = full.Edges.Where(edge => ids.Contains(edge.Source) && ids.Contains(edge.Target)).ToArray();
        return full with { Nodes = full.Nodes.Where(node => ids.Contains(node.Id)).ToArray(), Edges = edges };
    }

    private static AuthorityBlockReason ConvertReclaimBlocker(ReclaimBlockerSnapshot blocker)
    {
        var kind = blocker.ExternalState switch
        {
            ReclaimExternalState.Quarantined or ReclaimExternalState.ExternalStateUnknown => AuthorityBlockReasonKind.ProviderEffectUncontained,
            _ => blocker.Dependency switch
            {
                ReclaimDependencyKind.EndpointSession => AuthorityBlockReasonKind.EndpointSessionActive,
                ReclaimDependencyKind.PlatformDma or ReclaimDependencyKind.PlatformDevice => AuthorityBlockReasonKind.DeviceOrDmaActive,
                ReclaimDependencyKind.PlatformRegionMapping => AuthorityBlockReasonKind.PlatformMappingActive,
                ReclaimDependencyKind.KernelEventPublication => AuthorityBlockReasonKind.PublicationPending,
                _ => AuthorityBlockReasonKind.ProviderClosurePending
            }
        };
        return new(kind, Opaque($"blocker:{blocker.Dependency}:{blocker.ResourceId}"), blocker.Reason, false);
    }

    private static void AddNode(
        IDictionary<AuthorityNodeId, AuthorityInspectionNode> nodes,
        AuthorityNodeId id,
        AuthorityNodeKind kind,
        string state,
        ulong generation,
        bool stale = false,
        bool redacted = false) =>
        nodes.TryAdd(id, new(id, kind, state, generation, stale, redacted));

    private static AuthorityNodeId ProcessNodeId(ProcessHandle handle) => Opaque($"process:{handle.ProcessId.Value}:{handle.Generation}");
    private static AuthorityNodeId DomainNodeId(DomainId domain, ulong generation) => Opaque($"domain:{domain.Value}:{generation}");
    private static AuthorityNodeId CapabilityNodeId(CapabilityDescriptorV1 descriptor) => Opaque($"capability:{descriptor.CapabilityId.Value}:{descriptor.Generation}:{descriptor.RevocationEpoch}");
    private static AuthorityNodeId RegionNodeId(RegionHandle region) => Opaque($"region:{region.RegionId.Value}:{region.Generation.Value}");
    private static AuthorityNodeId BorrowNodeId(BorrowLeaseHandle borrow) => Opaque($"borrow:{borrow.Region.RegionId.Value}:{borrow.Region.Generation.Value}:{borrow.Generation.Value}");
    private static AuthorityNodeId RegionUseNodeId(RegionUseHandle use) => Opaque($"region-use:{use.UseId.Value}:{use.Generation}");
    private static AuthorityNodeId BackingNodeId(RegionBackingLeaseHandle backing) => Opaque($"backing:{backing.LeaseId.Value}:{backing.Generation}");
    private static AuthorityNodeId PlatformMappingNodeId(RegionHandle region) => Opaque($"platform-mapping:{region.RegionId.Value}:{region.Generation.Value}");
    private static AuthorityNodeId OperationNodeId(ExternalOperationHandle operation) => Opaque($"operation:{operation.OperationId.Value}:{operation.Generation.Value}");
    private static AuthorityNodeId VirtualNodeId(VirtualDomainHandle domain) => Opaque($"virtual:{domain.DomainId.Value}:{domain.Generation.Value}");
    private static AuthorityNodeId SecureNodeId(SecureDomainHandle domain) => Opaque($"secure:{domain.DomainId.Value}:{domain.Generation.Value}");
    private static AuthorityNodeId ServiceNodeId(ServiceId service, ServiceGeneration generation, ProcessHandle process) => Opaque($"service:{service.Value}:{generation.Value}:{process.ProcessId.Value}:{process.Generation}");
    private static AuthorityNodeId BudgetReservationNodeId(BudgetReservationHandle reservation) => Opaque($"budget-reservation:{reservation.ReservationId.Value}:{reservation.Generation.Value}");
    private static AuthorityNodeId CheckpointPinNodeId(CheckpointHandle checkpoint) => Opaque($"checkpoint-pin:{checkpoint.CheckpointId.Value}:{checkpoint.Generation.Value}");
    private static AuthorityNodeId RedactedNodeId() => Opaque("redacted");

    private static AuthorityNodeId Opaque(string material)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new(Convert.ToHexString(bytes));
    }

    private sealed record Projection(
        AuthorityInspectionNode[] Nodes,
        AuthorityInspectionEdge[] Edges,
        int RedactedFactCount,
        string Fingerprint)
    {
        internal static Projection Create(
            IEnumerable<AuthorityInspectionNode> nodes,
            IEnumerable<AuthorityInspectionEdge> edges,
            int redacted)
        {
            var orderedNodes = nodes.OrderBy(static node => node.Id.Value, StringComparer.Ordinal).ToArray();
            var orderedEdges = edges.Distinct().OrderBy(static edge => edge.Source.Value, StringComparer.Ordinal)
                .ThenBy(static edge => edge.Target.Value, StringComparer.Ordinal).ThenBy(static edge => edge.Kind).ToArray();
            var material = string.Join('|', orderedNodes.Select(static node => $"N:{node.Id.Value}:{node.Kind}:{node.State}:{node.Generation}:{node.Stale}:{node.Redacted}")) +
                           string.Join('|', orderedEdges.Select(static edge => $"E:{edge.Source.Value}:{edge.Target.Value}:{edge.Kind}:{edge.Redacted}"));
            return new(orderedNodes, orderedEdges, redacted, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))));
        }
    }
}

internal static class ExternalOperationInspectionExtensions
{
    internal static IEnumerable<RegionHandle> PreparationRegionHandles(this ExternalOperationSnapshot operation) =>
        operation.Admission?.RegionUses.Select(static use => use.Region) ?? [];
}

public sealed partial class RuntimeKernel
{
    public KernelResult<AuthorityInspector> CreateAuthorityInspector(
        ProcessHandle principal,
        CapabilityId? systemInspectionCapability = null,
        TimeProvider? timeProvider = null)
    {
        var process = Processes.Resolve(principal);
        if (!process.IsSuccess) return KernelResult<AuthorityInspector>.Fail(process.Error, process.Message!);
        if (systemInspectionCapability is { } capability)
        {
            var validated = ValidateCapability(principal, capability, CapabilityRights.Read);
            if (!validated.IsSuccess)
                return KernelResult<AuthorityInspector>.Fail(KernelError.ProjectionDenied, validated.Message ?? "Inspection capability is invalid.");
            if (validated.Value!.ResourceKind != ResourceKind.KernelService ||
                validated.Value.ResourceId != CapabilityResourceIds.AuthorityInspector)
                return KernelResult<AuthorityInspector>.Fail(KernelError.ProjectionDenied, "Capability does not authorize system authority inspection.");
        }
        return KernelResult<AuthorityInspector>.Ok(new(this, principal, systemInspectionCapability, timeProvider ?? TimeProvider.System));
    }

    internal VirtualDomainInspectionRecord[] VirtualDomainInspectionSnapshot() => _virtualDomains.InspectionSnapshot();

    internal SecureDomainInspectionRecord[] SecureDomainInspectionSnapshot() =>
        _secureDomainRecords.Values
            .Select(static record => new SecureDomainInspectionRecord(
                record.Handle,
                record.Owner,
                record.State,
                record.PolicyGeneration,
                record.ProtectionGeneration,
                record.Regions.Count))
            .OrderBy(static record => record.Handle.DomainId.Value)
            .ToArray();
}
