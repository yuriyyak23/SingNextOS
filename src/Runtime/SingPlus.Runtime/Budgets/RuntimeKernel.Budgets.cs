using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly Dictionary<RegionId, (ProcessHandle Owner, BudgetReservationHandle Reservation)> _regionBudgetReservations = [];
    private readonly Dictionary<ExternalOperationHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> _externalOperationBudgetReservations = [];
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
        BudgetReservationHandle reservation) => Budgets.Release(owner, reservation);

    public KernelResult<BudgetAccountSnapshot> QueryBudget(BudgetAccountHandle account) => Budgets.Query(account);

    public KernelResult<BudgetReservationSnapshot> QueryBudget(BudgetReservationHandle reservation) => Budgets.Query(reservation);

    private KernelResult<BudgetReservationHandle?> PrepareRegionBudgetTransfer(
        ProcessHandle source,
        ProcessHandle target,
        RegionHandle region)
    {
        var sourceCharged = _regionBudgetReservations.ContainsKey(region.RegionId);
        if (sourceCharged && !Budgets.HasProcessAccount(target))
            return KernelResult<BudgetReservationHandle?>.Fail(KernelError.BudgetNotConfigured, "Budgeted ownership cannot transfer into an unbudgeted process generation.");
        if (!Budgets.HasProcessAccount(target)) return KernelResult<BudgetReservationHandle?>.Ok(null);
        var descriptor = Regions.Snapshot().Single(candidate => candidate.Handle == region);
        return ReserveAttachedBudget(target,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, checked((ulong)descriptor.ByteLength))],
            BudgetReservationLifetime.LocalResource);
    }

    private void CompleteRegionBudgetTransfer(
        ProcessHandle source,
        ProcessHandle target,
        RegionId region,
        BudgetReservationHandle? targetReservation)
    {
        if (_regionBudgetReservations.Remove(region, out var prior))
            _ = ReleaseAttachedBudget(source, prior.Reservation);
        if (targetReservation is { } exact)
            _regionBudgetReservations[region] = (target, exact);
    }

    private void ReleaseRegionBudget(ProcessHandle owner, RegionId region)
    {
        if (_regionBudgetReservations.Remove(region, out var charge))
            _ = ReleaseAttachedBudget(owner, charge.Reservation);
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
        foreach (var item in _ipcBudgetReservations.Where(item => item.Value.Sender == process || item.Value.Receiver == process).ToArray())
        {
            _ = ReleaseAttachedBudget(item.Value.Sender, item.Value.Reservation);
            _ipcBudgetReservations.Remove(item.Key);
        }
    }

    private void ReleaseReclaimedRegionBudgetsForProcess(ProcessHandle process)
    {
        foreach (var item in _regionBudgetReservations.Where(item => item.Value.Owner == process).ToArray())
        {
            _ = ReleaseAttachedBudget(process, item.Value.Reservation);
            _regionBudgetReservations.Remove(item.Key);
        }
    }

    private void ReconcileReleasedExternalOperationBudgets(ProcessHandle process)
    {
        foreach (var item in _externalOperationBudgetReservations.Where(item => item.Value.Owner == process).ToArray())
        {
            var operation = ExternalOperations.Query(item.Key);
            if (!operation.IsSuccess || operation.Value!.State != ExternalOperationState.Released) continue;
            _ = ReleaseAttachedBudget(process, item.Value.Reservation);
            _externalOperationBudgetReservations.Remove(item.Key);
        }
    }
}
