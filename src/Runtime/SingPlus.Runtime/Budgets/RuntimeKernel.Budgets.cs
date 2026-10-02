using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> _regionBudgetReservations = [];
    private readonly object _regionBudgetReservationsGate = new();
    private readonly Dictionary<ExternalOperationHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> _externalOperationBudgetReservations = [];
    private readonly object _externalOperationBudgetReservationsGate = new();
    private readonly Dictionary<(ChannelId Channel, ulong Sequence), (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation)> _ipcBudgetReservations = [];
    private readonly Dictionary<PlatformRegionMappingId, (ProcessHandle Owner, BudgetReservationHandle Reservation)> _mappingBudgetReservations = [];

    private KernelResult<BudgetReservationHandle?> ReserveAttachedBudget(
        ProcessHandle owner,
        IReadOnlyList<BudgetAmount> amounts,
        BudgetReservationLifetime lifetime,
        AdmissionQosHint qosHint = AdmissionQosHint.None)
    {
        var reserved = Budgets.ReserveIfAttached(owner, amounts, lifetime, qosHint);
        if (reserved.IsSuccess && reserved.Value is { } snapshot)
            RecordTrace(owner, TraceEventKind.BudgetReserved, null, "budget-reservation",
                snapshot.Reservation.ReservationId.Value.ToString(), "active", "reserved");
        return reserved.IsSuccess
            ? KernelResult<BudgetReservationHandle?>.Ok(reserved.Value?.Reservation)
            : KernelResult<BudgetReservationHandle?>.Fail(reserved.Error, reserved.Message!);
    }

    private KernelResult ReleaseAttachedBudget(ProcessHandle owner, BudgetReservationHandle? reservation)
    {
        if (reservation is not { } exact) return KernelResult.Ok();
        var released = Budgets.Release(owner, exact);
        if (released.IsSuccess && released.Value!.State == BudgetReservationState.Released)
            RecordTrace(owner, TraceEventKind.BudgetReleased, null, "budget-reservation",
                exact.ReservationId.Value.ToString(), "released", "closed");
        return released.IsSuccess && released.Value!.State == BudgetReservationState.Released
            ? KernelResult.Ok()
            : KernelResult.Fail(released.Error == KernelError.None ? KernelError.StaleGeneration : released.Error,
                released.Message ?? "Budget reservation release was stale.");
    }

    public KernelResult<BudgetAccountSnapshot> ConfigureSystemBudget(
        ProcessHandle principal,
        CapabilityId administrationCapability,
        IReadOnlyList<BudgetAmount> limits)
    {
        var capability = ValidateCapability(principal, administrationCapability, CapabilityRights.Configure);
        if (!capability.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.KernelService ||
            capability.Value.ResourceId != CapabilityResourceIds.BudgetAdministration)
            return KernelResult<BudgetAccountSnapshot>.Fail(KernelError.SupervisorDenied, "Budget administration requires the exact kernel budget capability.");
        return Budgets.ConfigureSystem(limits);
    }

    public KernelResult<BudgetReservationSnapshot> ReserveBudget(
        ProcessHandle owner,
        IReadOnlyList<BudgetAmount> amounts,
        BudgetReservationLifetime lifetime,
        AdmissionQosHint qosHint = AdmissionQosHint.None)
    {
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(process.Error, process.Message!);
        var effect = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!effect.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(effect.Error, effect.Message!);
        return Budgets.Reserve(owner, amounts, lifetime, qosHint);
    }

    public KernelResult<ProcessBudgetAdmission> AdmitProcessBudget(
        ProcessHandle principal,
        CapabilityId administrationCapability,
        ProcessHandle process,
        string serviceCorrelation,
        IReadOnlyList<BudgetAmount> limits)
    {
        var capability = ValidateCapability(principal, administrationCapability, CapabilityRights.Configure);
        if (!capability.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.KernelService ||
            capability.Value.ResourceId != CapabilityResourceIds.BudgetAdministration)
            return KernelResult<ProcessBudgetAdmission>.Fail(KernelError.SupervisorDenied, "Budget admission requires the exact kernel budget capability.");
        var target = Processes.Resolve(process);
        if (!target.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(target.Error, target.Message!);
        var service = Budgets.CreateChild(Budgets.SystemBudget, BudgetAccountLevel.Service, serviceCorrelation, limits);
        if (!service.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(service.Error, service.Message!);
        var child = Budgets.CreateChild(service.Value!.Account, BudgetAccountLevel.ProcessDomain,
            $"process:{process.ProcessId.Value}:{process.Generation}", limits);
        if (!child.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(child.Error, child.Message!);
        var attached = Budgets.AttachProcess(process, child.Value!.Account);
        return attached.IsSuccess
            ? KernelResult<ProcessBudgetAdmission>.Ok(new(process, service.Value.Account, child.Value.Account))
            : KernelResult<ProcessBudgetAdmission>.Fail(attached.Error, attached.Message!);
    }

    public KernelResult<BudgetReservationSnapshot> ReleaseBudget(
        ProcessHandle owner,
        BudgetReservationHandle reservation) => Budgets.ReleaseExplicit(owner, reservation);

    public KernelResult<BudgetAccountSnapshot> QueryBudget(BudgetAccountHandle account) => Budgets.Query(account);

    public KernelResult<BudgetReservationSnapshot> QueryBudget(BudgetReservationHandle reservation) => Budgets.Query(reservation);

    private KernelResult<BudgetReservationHandle?> PrepareRegionBudgetTransfer(
        ProcessHandle source,
        ProcessHandle target,
        RegionHandle region)
    {
        bool sourceCharged;
        lock (_regionBudgetReservationsGate)
            sourceCharged = _regionBudgetReservations.ContainsKey(region);
        if (sourceCharged && !Budgets.HasProcessAccount(target))
            return KernelResult<BudgetReservationHandle?>.Fail(KernelError.BudgetNotConfigured, "Budgeted ownership cannot transfer into an unbudgeted process generation.");
        if (!Budgets.HasProcessAccount(target)) return KernelResult<BudgetReservationHandle?>.Ok(null);
        var descriptor = Regions.Snapshot().Single(candidate => candidate.Handle == region);
        return ReserveAttachedBudget(target,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, checked((ulong)descriptor.ByteLength))],
            BudgetReservationLifetime.LocalResource);
    }

    // TCB-only composition used by real queued/inline request and response owners.
    // The pair preparation callback only acquires its separate local Region loan.
    private KernelResult<RegionHandle> TransferRegionForIpc(
        SingProcess source, SingProcess target, RegionHandle region, Func<KernelResult>? preparePairBorrow)
    {
        lock (_platformMemoryUseGate)
        {
            var sourceHandle = new ProcessHandle(source.ProcessId, source.Generation);
            var targetHandle = new ProcessHandle(target.ProcessId, target.Generation);
            var sourceResolved = Processes.Resolve(sourceHandle);
            var targetResolved = Processes.Resolve(targetHandle);
            if (!sourceResolved.IsSuccess) return KernelResult<RegionHandle>.Fail(sourceResolved.Error, sourceResolved.Message!);
            if (!targetResolved.IsSuccess) return KernelResult<RegionHandle>.Fail(targetResolved.Error, targetResolved.Message!);
            if (!ReferenceEquals(sourceResolved.Value, source) || !ReferenceEquals(targetResolved.Value, target))
                return KernelResult<RegionHandle>.Fail(KernelError.StaleHandle, "IPC transfer requires the exact live process records.");
            var sourceEffect = EnsureProcessAcceptsNewEffects(source);
            if (!sourceEffect.IsSuccess) return KernelResult<RegionHandle>.Fail(sourceEffect.Error, sourceEffect.Message!);
            var targetEffect = EnsureProcessAcceptsNewEffects(target);
            if (!targetEffect.IsSuccess) return KernelResult<RegionHandle>.Fail(targetEffect.Error, targetEffect.Message!);
            var owner = new RegionOwner(source.DomainId, source.Generation);
            var valid = Regions.Validate(region, owner);
            if (!valid.IsSuccess) return KernelResult<RegionHandle>.Fail(valid.Error, valid.Message!);
            var budget = PrepareRegionBudgetTransfer(sourceHandle, targetHandle, region);
            if (!budget.IsSuccess) return KernelResult<RegionHandle>.Fail(budget.Error, budget.Message!);
            KernelResult<RegionHandle> moved;
            try
            {
                if (preparePairBorrow is not null)
                {
                  var prepared = preparePairBorrow();
                  if (!prepared.IsSuccess)
                  {
                      _ = ReleaseAttachedBudget(targetHandle, budget.Value);
                      return KernelResult<RegionHandle>.Fail(prepared.Error, prepared.Message!);
                  }
                }
                // Pair preparation can reenter kernel lifecycle code. Its result
                // does not preserve permission from the earlier process snapshot.
                var freshSource = Processes.Resolve(sourceHandle);
                var freshTarget = Processes.Resolve(targetHandle);
                if (!freshSource.IsSuccess || !freshTarget.IsSuccess ||
                    !ReferenceEquals(freshSource.Value, source) || !ReferenceEquals(freshTarget.Value, target))
                {
                    _ = ReleaseAttachedBudget(targetHandle, budget.Value);
                    return KernelResult<RegionHandle>.Fail(KernelError.StaleHandle, "IPC transfer process incarnation changed during preparation.");
                }
                var freshSourceEffect = EnsureProcessAcceptsNewEffects(source);
                var freshTargetEffect = EnsureProcessAcceptsNewEffects(target);
                if (!freshSourceEffect.IsSuccess || !freshTargetEffect.IsSuccess)
                {
                    _ = ReleaseAttachedBudget(targetHandle, budget.Value);
                    var refused = !freshSourceEffect.IsSuccess ? freshSourceEffect : freshTargetEffect;
                    return KernelResult<RegionHandle>.Fail(refused.Error, refused.Message!);
                }
                moved = Regions.Transfer(region, owner, new RegionOwner(target.DomainId, target.Generation));
            }
            catch (Exception exception)
            {
                if (budget.Value is { } exact) _ = Budgets.QuarantineLease(targetHandle, exact);
                return KernelResult<RegionHandle>.Fail(KernelError.PlatformFaulted,
                  $"IPC ownership composition failed with uncertain local mutation: {exception.Message}");
            }
            if (!moved.IsSuccess)
            {
                _ = ReleaseAttachedBudget(targetHandle, budget.Value);
                return moved;
            }
            CompleteRegionBudgetTransfer(sourceHandle, targetHandle, region, moved.Value, budget.Value);
            return moved;
        }
    }

    private void CompleteRegionBudgetTransfer(
        ProcessHandle source,
        ProcessHandle target,
        RegionHandle oldRegion,
        RegionHandle newRegion,
        BudgetReservationHandle? targetReservation)
    {
        var payer = source;
        lock (_regionBudgetReservationsGate)
            if (_regionBudgetReservations.TryGetValue(oldRegion, out var prior)) payer = prior.Owner;
        // V1 Region authorization is domain/generation scoped. A valid cohort
        // peer may transfer a Region whose quantitative payer is another process.
        ReleaseRegionBudget(payer, oldRegion);
        if (targetReservation is { } exact)
            lock (_regionBudgetReservationsGate)
                _regionBudgetReservations.Add(newRegion, (target, exact));
    }

    private void ReleaseRegionBudget(ProcessHandle owner, RegionHandle region)
    {
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge;
        lock (_regionBudgetReservationsGate)
            if (!_regionBudgetReservations.TryGetValue(region, out charge) || charge.Owner != owner)
                return;
        // Region release/transfer is independent of quantitative settlement.
        // A transferred generation must not overwrite an unresolved prior charge.
        if (!ReleaseAttachedBudget(charge.Owner, charge.Reservation).IsSuccess) return;
        lock (_regionBudgetReservationsGate)
            if (_regionBudgetReservations.TryGetValue(region, out var current) && current == charge)
                _regionBudgetReservations.Remove(region);
    }

    private KernelResult ReleaseMappingBudget(PlatformRegionMappingId mapping)
    {
        if (!_mappingBudgetReservations.TryGetValue(mapping, out var charge)) return KernelResult.Ok();
        var released = ReleaseAttachedBudget(charge.Owner, charge.Reservation);
        if (!released.IsSuccess) return released;
        _mappingBudgetReservations.Remove(mapping);
        return KernelResult.Ok();
    }

    private void ReleaseClosedIpcBudgetsForProcess(ProcessHandle process)
    {
        (ChannelId Channel, ulong Sequence)[] pending;
        lock (_requestResponseCorrelationGate)
            pending = _ipcBudgetReservations.Where(item => item.Value.Sender == process ||
                item.Value.Receiver == process).Select(item => item.Key).ToArray();
        foreach (var key in pending) ReleaseIpcBudget(key);
    }

    private void ReleaseClosedIpcBudgetsForChannel(ChannelId channel)
    {
        (ChannelId Channel, ulong Sequence)[] pending;
        lock (_requestResponseCorrelationGate)
            pending = _ipcBudgetReservations.Keys.Where(key => key.Channel == channel).ToArray();
        foreach (var key in pending) ReleaseIpcBudget(key);
    }

    private void ReleaseIpcBudget((ChannelId Channel, ulong Sequence) key)
    {
        (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) charge;
        lock (_requestResponseCorrelationGate)
            if (!_ipcBudgetReservations.TryGetValue(key, out charge)) return;
        // Delivered/closed queue lifetime is not quantitative settlement.
        // Retain the exact routing association when the budget owner refuses.
        if (!ReleaseAttachedBudget(charge.Sender, charge.Reservation).IsSuccess) return;
        lock (_requestResponseCorrelationGate)
            if (_ipcBudgetReservations.TryGetValue(key, out var current) && current == charge)
                _ipcBudgetReservations.Remove(key);
    }

    private void ReleaseReclaimedRegionBudgetsForProcess(ProcessHandle process, IReadOnlyList<RegionHandle> reclaimed)
    {
        var exactReclaimed = reclaimed.ToHashSet();
        KeyValuePair<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)>[] pending;
        lock (_regionBudgetReservationsGate)
            // Preserve the existing final-process retry and also route actual domain
            // reclaim results to exact charges belonging to earlier cohort members.
            pending = _regionBudgetReservations.Where(item => item.Value.Owner == process ||
                exactReclaimed.Contains(item.Key)).ToArray();
        foreach (var item in pending)
            ReleaseRegionBudget(item.Value.Owner, item.Key);
    }

    private void ReconcileReleasedExternalOperationBudgets(ProcessHandle process)
    {
        KeyValuePair<ExternalOperationHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)>[] pending;
        lock (_externalOperationBudgetReservationsGate)
            pending = _externalOperationBudgetReservations.Where(item => item.Value.Owner == process).ToArray();
        foreach (var item in pending)
        {
            var operation = ExternalOperations.Query(item.Key);
            if (!operation.IsSuccess || operation.Value!.State != ExternalOperationState.Released) continue;
            ReleaseExternalOperationBudget(process, item.Key);
        }
    }

    private void ReleaseExternalOperationBudget(ProcessHandle owner, ExternalOperationHandle operation)
    {
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge;
        lock (_externalOperationBudgetReservationsGate)
            if (!_externalOperationBudgetReservations.TryGetValue(operation, out charge) || charge.Owner != owner)
                return;
        // Local operation release is not quantitative settlement. Keep the exact
        // association for the existing release/teardown retry if the budget owner refuses.
        if (!ReleaseAttachedBudget(charge.Owner, charge.Reservation).IsSuccess) return;
        lock (_externalOperationBudgetReservationsGate)
            if (_externalOperationBudgetReservations.TryGetValue(operation, out var current) && current == charge)
                _externalOperationBudgetReservations.Remove(operation);
    }
}
