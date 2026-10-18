using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly record struct BudgetTraceObservation(ProcessHandle Owner,
        TraceEventKind Kind, BudgetReservationHandle Reservation);

    private void EmitBudgetObservations(List<BudgetTraceObservation> observations)
    {
        foreach (var observation in observations)
            RecordTrace(observation.Owner, observation.Kind, null, "budget-reservation",
                observation.Reservation.ReservationId.Value.ToString(),
                observation.Kind == TraceEventKind.BudgetReserved ? "active" : "released",
                observation.Kind == TraceEventKind.BudgetReserved ? "reserved" : "closed");
    }
    private readonly Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> _regionBudgetReservations = [];
    private readonly object _regionBudgetReservationsGate = new();
    private readonly Dictionary<ExternalOperationHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> _externalOperationBudgetReservations = [];
    private readonly object _externalOperationBudgetReservationsGate = new();
    private readonly Dictionary<(ChannelId Channel, ulong Sequence), (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation)> _ipcBudgetReservations = [];
    // Observation buffer only; never lifecycle, permission or accounting truth.
    private readonly List<BudgetTraceObservation> _pendingIpcBudgetObservations = [];
    private readonly Dictionary<PlatformRegionMappingId, (ProcessHandle Owner, BudgetReservationHandle Reservation)> _mappingBudgetReservations = [];

    private KernelResult<BudgetReservationHandle?> ReserveAttachedBudget(
        ProcessHandle owner,
        IReadOnlyList<BudgetAmount> amounts,
        BudgetReservationLifetime lifetime,
        AdmissionQosHint qosHint = AdmissionQosHint.None,
        List<BudgetTraceObservation>? observations = null)
    {
        var reserved = Budgets.ReserveIfAttached(owner, amounts, lifetime, qosHint);
        if (reserved.IsSuccess && reserved.Value is { } snapshot)
        {
            if (observations is not null)
                observations.Add(new(owner, TraceEventKind.BudgetReserved, snapshot.Reservation));
            else
            RecordTrace(owner, TraceEventKind.BudgetReserved, null, "budget-reservation",
                snapshot.Reservation.ReservationId.Value.ToString(), "active", "reserved");
        }
        return reserved.IsSuccess
            ? KernelResult<BudgetReservationHandle?>.Ok(reserved.Value?.Reservation)
            : KernelResult<BudgetReservationHandle?>.Fail(reserved.Error, reserved.Message!);
    }

    private KernelResult ReleaseAttachedBudget(ProcessHandle owner, BudgetReservationHandle? reservation,
        List<BudgetTraceObservation>? observations = null)
    {
        if (reservation is not { } exact) return KernelResult.Ok();
        var released = Budgets.Release(owner, exact);
        if (released.IsSuccess && released.Value!.State == BudgetReservationState.Released)
        {
            if (observations is not null)
                observations.Add(new(owner, TraceEventKind.BudgetReleased, exact));
            else
            RecordTrace(owner, TraceEventKind.BudgetReleased, null, "budget-reservation",
                exact.ReservationId.Value.ToString(), "released", "closed");
        }
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
        var staged = StageBudgetAmounts(limits);
        if (!staged.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(staged.Error, staged.Message!);
        lock (_platformMemoryUseGate)
        {
            var exact = Processes.Resolve(principal);
            if (!exact.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(exact.Error, exact.Message!);
            var effect = EnsureProcessAcceptsNewEffects(exact.Value!);
            if (!effect.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(effect.Error, effect.Message!);
            return CapabilityAuthority.CommitBudgetAdministration(administrationCapability, exact.Value!.DomainId,
                principal.Generation, () => Budgets.ConfigureSystem(staged.Value!));
        }
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
        var staged = StageBudgetAmounts(amounts);
        if (!staged.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(staged.Error, staged.Message!);
        lock (_platformMemoryUseGate)
        {
            process = Processes.Resolve(owner);
            if (!process.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(process.Error, process.Message!);
            effect = EnsureProcessAcceptsNewEffects(process.Value!);
            if (!effect.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(effect.Error, effect.Message!);
            return Budgets.Reserve(owner, staged.Value!, lifetime, qosHint);
        }
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
        var staged = StageBudgetAmounts(limits);
        if (!staged.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(staged.Error, staged.Message!);
        lock (_platformMemoryUseGate)
        {
            var exact = Processes.Resolve(principal);
            if (!exact.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(exact.Error, exact.Message!);
            var effect = EnsureProcessAcceptsNewEffects(exact.Value!);
            if (!effect.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(effect.Error, effect.Message!);
            target = Processes.Resolve(process);
            if (!target.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(target.Error, target.Message!);
            effect = EnsureProcessAcceptsNewEffects(target.Value!);
            if (!effect.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(effect.Error, effect.Message!);
            return CapabilityAuthority.CommitBudgetAdministration(administrationCapability, exact.Value!.DomainId,
                principal.Generation, () => Budgets.CreateProcessHierarchy(process, serviceCorrelation,
                    $"process:{process.ProcessId.Value}:{process.Generation}", staged.Value!));
        }
    }

    private static KernelResult<BudgetAmount[]> StageBudgetAmounts(IReadOnlyList<BudgetAmount> amounts)
    {
        try { return KernelResult<BudgetAmount[]>.Ok(amounts.ToArray()); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<BudgetAmount[]>.Fail(KernelError.PlatformFaulted, "Budget input observation failed before owner commit.");
        }
    }
    public KernelResult<BudgetReservationSnapshot> ReleaseBudget(
        ProcessHandle owner,
        BudgetReservationHandle reservation)
    {
        var released = Budgets.ReleaseExplicit(owner, reservation);
        if (!released.IsSuccess || released.Value!.State != BudgetReservationState.Released)
            return released;
        // Accounting release is owned by Budgets. Retire only routing facts whose
        // exact local Region lifetime has independently ended; no reclaim permission.
        RegionHandle[] candidates;
        lock (_regionBudgetReservationsGate)
            candidates = _regionBudgetReservations.Where(item => item.Value.Owner == owner &&
                item.Value.Reservation == reservation).Select(item => item.Key).ToArray();
        var ended = candidates.Where(Regions.HasEndedAccountingLifetime).ToHashSet();
        lock (_regionBudgetReservationsGate)
        {
            var completed = _regionBudgetReservations.Where(item => ended.Contains(item.Key) &&
                item.Value.Owner == owner && item.Value.Reservation == reservation).Select(item => item.Key).ToArray();
            foreach (var region in completed) _regionBudgetReservations.Remove(region);
        }
        return released;
    }

    public KernelResult<BudgetAccountSnapshot> QueryBudget(BudgetAccountHandle account) => Budgets.Query(account);

    public KernelResult<BudgetReservationSnapshot> QueryBudget(BudgetReservationHandle reservation) => Budgets.Query(reservation);

    private KernelResult<BudgetReservationHandle?> PrepareRegionBudgetTransfer(
        ProcessHandle source,
        ProcessHandle target,
        RegionHandle region,
        List<BudgetTraceObservation>? observations = null)
    {
        var continuity = ValidateOriginalRegionBudgetRouting(region, out var sourceCharged);
        if (!continuity.IsSuccess)
            return KernelResult<BudgetReservationHandle?>.Fail(continuity.Error, continuity.Message!);
        if (sourceCharged && !Budgets.HasProcessAccount(target))
            return KernelResult<BudgetReservationHandle?>.Fail(KernelError.BudgetNotConfigured, "Budgeted ownership cannot transfer into an unbudgeted process generation.");
        if (!Budgets.HasProcessAccount(target)) return KernelResult<BudgetReservationHandle?>.Ok(null);
        var descriptor = Regions.Snapshot().Single(candidate => candidate.Handle == region);
        return ReserveAttachedBudget(target,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, checked((ulong)descriptor.ByteLength))],
            BudgetReservationLifetime.LocalResource, observations: observations);
    }

    // TCB-only composition used by real queued/inline request and response owners.
    // The pair preparation callback only acquires its separate local Region loan.
    private KernelResult<RegionHandle> TransferRegionForIpc(
        SingProcess source, SingProcess target, RegionHandle region, Func<KernelResult>? preparePairBorrow)
        => TransferRegionForIpcCore(source,target,region,preparePairBorrow,null);
    private KernelResult<RegionHandle> TransferPreparedRegionForIpc(
        SingProcess source, SingProcess target, RegionHandle region, Func<KernelResult>? preparePairBorrow, Action noEffect, PreparedOwnedTransfer prepared) => TransferRegionForIpcCore(source, target, region, preparePairBorrow, null,noEffect,prepared);

    private KernelResult<RegionHandle> TransferResponseRegionForIpc(SingProcess source,SingProcess target,RegionHandle region,
        Func<KernelResult>? admission,Action noEffect,PreparedOwnedTransfer prepared)
        => TransferRegionForIpcCore(source,target,region,admission,null,noEffect,prepared);

    private KernelResult<RegionHandle> TransferRegionForIpcCore(
        SingProcess source, SingProcess target, RegionHandle region, Func<KernelResult>? preparePairBorrow,
        List<BudgetTraceObservation>? observations,Action? originalRegionNoEffect = null,PreparedOwnedTransfer? preparedOwnership = null,
        EndpointSessionInvocationRegistry.OriginalSend? originalSend = null,
        ResponseRegistry.OriginalPublication? originalResponse = null)
    {
        var regionEffectPossible=false;
        try
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
            var originalPayload=Regions.CaptureTransferPayload(region,owner);
            if(!originalPayload.IsSuccess)return KernelResult<RegionHandle>.Fail(originalPayload.Error,originalPayload.Message!);
            if(originalResponse is not null)
            {
                if(preparedOwnership is null)throw new InvalidOperationException("Original response requires its prepared payload.");
                var regionOwner=Regions.CaptureOriginalAccountingOwner(region);
                if(regionOwner is null)throw new InvalidOperationException("Original response Region owner absent before budget admission.");
                Responses.RetainTransferProducer(new(originalResponse,preparedOwnership,region,source.DomainId,
                    new RegionOwner(target.DomainId,targetHandle.Generation),regionOwner,targetHandle,null,null,
                    Budgets.OriginalRegionCharges(region)));
            }
            var budget = PrepareRegionBudgetTransfer(sourceHandle, targetHandle, region, observations);
            if (!budget.IsSuccess)
            {
                if(originalResponse is not null)Responses.RetainTransferBudget(originalResponse,preparedOwnership!,budget,null);
                return KernelResult<RegionHandle>.Fail(budget.Error, budget.Message!);
            }
            KernelResult<RegionHandle> moved;
            try
            {
                if(originalSend is not null)
                {
                    if(preparedOwnership is null) throw new InvalidOperationException("Original send producer requires its prepared payload.");
                    var originalBudget=Budgets.CaptureOriginalTransferReservationOwner(targetHandle,budget.Value,preparedOwnership);
                    SessionInvocations.RetainOriginalTransferProducerAdmission(originalSend,preparedOwnership,budget.Value,originalBudget,
                        region,source.DomainId,new RegionOwner(target.DomainId,targetHandle.Generation),Budgets.OriginalRegionCharges(region),
                        Regions.CaptureOriginalAccountingOwner(region)!);
                }
                if(originalResponse is not null)
                {
                    var originalBudget=Budgets.CaptureOriginalTransferReservationOwner(targetHandle,budget.Value,preparedOwnership!);
                    Responses.RetainTransferBudget(originalResponse,preparedOwnership!,budget,originalBudget);
                }
                KernelResult<RegionHandle> RefuseBeforeRegion(KernelError error,string message)
                {
                    // Own checked producer refusal; never inferred from absent raw
                    // or an exception. Retain before quantitative consumption.
                    if(originalResponse is not null)Responses.RetainPreRegionRefusal(originalResponse,preparedOwnership!,error);
                    _=ReleaseAttachedBudget(targetHandle,budget.Value,observations);
                    return KernelResult<RegionHandle>.Fail(error,message);
                }
                if (preparePairBorrow is not null)
                {
                  var prepared = preparePairBorrow();
                  if (!prepared.IsSuccess)
                  {
                      return RefuseBeforeRegion(prepared.Error,prepared.Message!);
                  }
                }
                // Pair preparation can reenter kernel lifecycle code. Its result
                // does not preserve permission from the earlier process snapshot.
                var freshSource = Processes.Resolve(sourceHandle);
                var freshTarget = Processes.Resolve(targetHandle);
                if (!freshSource.IsSuccess || !freshTarget.IsSuccess ||
                    !ReferenceEquals(freshSource.Value, source) || !ReferenceEquals(freshTarget.Value, target))
                {
                    return RefuseBeforeRegion(KernelError.StaleHandle,"IPC transfer process incarnation changed during preparation.");
                }
                var freshSourceEffect = EnsureProcessAcceptsNewEffects(source);
                var freshTargetEffect = EnsureProcessAcceptsNewEffects(target);
                if (!freshSourceEffect.IsSuccess || !freshTargetEffect.IsSuccess)
                {
                    var refused = !freshSourceEffect.IsSuccess ? freshSourceEffect : freshTargetEffect;
                    return RefuseBeforeRegion(refused.Error,refused.Message!);
                }
                var freshCharge = ValidateOriginalRegionBudgetRouting(region, out _);
                if (!freshCharge.IsSuccess)
                {
                    return RefuseBeforeRegion(freshCharge.Error,freshCharge.Message!);
                }
                regionEffectPossible=true; // From here failure/exception is not a pre-effect proof.
                moved = Regions.Transfer(region, owner, new RegionOwner(target.DomainId, target.Generation),originalPayload.Value,preparedOwnership);
                if(originalSend is not null) SessionInvocations.RetainOriginalTransferRegionRaw(originalSend,preparedOwnership!,moved);
                if(originalResponse is not null) Responses.RetainTransferRaw(originalResponse,preparedOwnership!,moved);
            }
            catch (Exception exception)
            {
                if (budget.Value is { } exact) _ = Budgets.QuarantineLease(targetHandle, exact);
                return KernelResult<RegionHandle>.Fail(KernelError.PlatformFaulted,
                  $"IPC ownership composition failed with uncertain local mutation: {exception.Message}");
            }
            if (!moved.IsSuccess)
            {
                regionEffectPossible=false; // Exact RegionAuthority refusal occurs before mutation.
                _ = ReleaseAttachedBudget(targetHandle, budget.Value, observations);
                return moved;
            }
            var completed=CompleteRegionBudgetTransfer(sourceHandle, targetHandle, region, moved.Value, budget.Value, observations);
            if(!completed.IsSuccess) return KernelResult<RegionHandle>.Fail(completed.Error,completed.Message!);
            return moved;
        }
        }
        finally { if(!regionEffectPossible)originalRegionNoEffect?.Invoke(); }
    }

    private KernelResult CompleteRegionBudgetTransfer(
        ProcessHandle source,
        ProcessHandle target,
        RegionHandle oldRegion,
        RegionHandle newRegion,
        BudgetReservationHandle? targetReservation,
        List<BudgetTraceObservation>? observations = null)
    {
        // V1 Region authorization is domain/generation scoped. A valid cohort
        // peer may transfer a Region whose quantitative payer is another process.
        if (targetReservation is { } exact)
        {
            var process = Processes.Resolve(target);
            if(!process.IsSuccess)
            {
                _=Budgets.QuarantineLease(target,exact);
                return KernelResult.Fail(process.Error,process.Message!);
            }
            var bound=BindOriginalRegionBudget(target,exact,newRegion,process.Value!.DomainId);
            if(!bound.IsSuccess)
            {
                _=Budgets.QuarantineLease(target,exact);
                return bound;
            }
            lock (_regionBudgetReservationsGate)
            {
                if(_regionBudgetReservations.TryGetValue(newRegion,out var existing) && existing!=(target,exact))
                {
                    _=Budgets.QuarantineLease(target,exact);
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Target Region route conflicts with its original quantitative owner.");
                }
                _regionBudgetReservations.TryAdd(newRegion,(target,exact));
            }
        }
        ReleaseOriginalRegionBudget(oldRegion, observations);
        return KernelResult.Ok();
    }

    private KernelResult BindOriginalRegionBudget(ProcessHandle owner, BudgetReservationHandle reservation,
        RegionHandle region, DomainId domain)
    {
        var original = Regions.CaptureOriginalAccountingOwner(region);
        return original is null
            ? KernelResult.Fail(KernelError.PlatformFaulted, "Original Region charge owner could not be captured.")
            : Budgets.BindOriginalRegionCharge(owner, reservation, region, domain, original);
    }

    private void ReleaseEndedRegionBudget(RegionHandle region, List<BudgetTraceObservation> observations)
        => ReleaseOriginalRegionBudget(region, observations);

    private KernelResult ValidateOriginalRegionBudgetRouting(RegionHandle region, out bool charged)
    {
        var facts = Budgets.OriginalRegionCharges(region);
        charged = facts.Length != 0;
        if (facts.Length > 1)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Multiple original Region charge owners are inconsistent.");
        lock (_regionBudgetReservationsGate)
            if (_regionBudgetReservations.TryGetValue(region, out var route) &&
                (facts.Length != 1 || route != (facts[0].Owner, facts[0].Reservation)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Region routing replaced its original charge.");
        if (!charged) return KernelResult.Ok();
        var original = facts[0];
        if (original.RegionOwner is null)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original Region accounting source is missing.");
        var source = Regions.HasEndedOriginalAccountingLifetime(original.RegionOwner, region);
        if (!source.IsSuccess) return KernelResult.Fail(source.Error, source.Message!);
        return Budgets.ValidateOriginalRegionCharge(original.Fact, original.Owner, original.Reservation);
    }

    private void ReleaseOriginalRegionBudget(RegionHandle region,
        List<BudgetTraceObservation>? observations = null)
    {
        var continuity = ValidateOriginalRegionBudgetRouting(region, out var charged);
        if (!continuity.IsSuccess || !charged) return;
        var facts = Budgets.OriginalRegionCharges(region);
        if (facts.Length != 1) return;
        var charge = facts[0];
        var ended = Regions.HasEndedOriginalAccountingLifetime(charge.RegionOwner!, region);
        if (!ended.IsSuccess || !ended.Value) return;
        // Region release/transfer is independent of quantitative settlement.
        // A transferred generation must not overwrite an unresolved prior charge.
        var released = Budgets.ReleaseOriginalRegionCharge(charge.Fact, charge.Owner, charge.Reservation);
        if (!released.IsSuccess || released.Value!.State != BudgetReservationState.Released) return;
        if (observations is not null) observations.Add(new(charge.Owner, TraceEventKind.BudgetReleased, charge.Reservation));
        else RecordTrace(charge.Owner, TraceEventKind.BudgetReleased, null, "budget-reservation",
            charge.Reservation.ReservationId.Value.ToString(), "released", "closed");
        lock (_regionBudgetReservationsGate)
            if (_regionBudgetReservations.TryGetValue(region, out var current) && current == (charge.Owner, charge.Reservation))
                _regionBudgetReservations.Remove(region);
    }

    private KernelResult ReleaseMappingBudget(PlatformRegionMapping mapping, List<BudgetTraceObservation> observations)
    {
        lock (Budgets.MappingReservationPublicationGate)
        {
        var original = PlatformAuthority.QueryOriginalMappingBudgetOwner(mapping);
        if (!original.IsSuccess) return KernelResult.Fail(original.Error, original.Message!);
        var routed = _mappingBudgetReservations.TryGetValue(mapping.MappingId, out var charge);
        if (!original.Value.Expected)
            return !routed ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.PlatformFaulted, "Unbudgeted mapping cannot consume another charge route.");
        if (!original.Value.Known)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original mapping charge binding is unknown; finalization remains pinned.");
        var consumed = Budgets.IsOriginalMappingChargeConsumedLocked(original.Value.Owner, original.Value.MappingOwner);
        if (!routed)
            return consumed ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.PlatformFaulted, "Original mapping charge route is absent; finalization remains pinned.");
        var continuity = Budgets.ValidateOriginalMappingChargeLocked(original.Value.Owner, original.Value.MappingOwner, charge.Owner, charge.Reservation);
        if (!continuity.IsSuccess) return continuity;
        if (consumed)
        {
            _mappingBudgetReservations.Remove(mapping.MappingId);
            return KernelResult.Ok();
        }
        var released = ReleaseAttachedBudget(charge.Owner, charge.Reservation, observations);
        if (!released.IsSuccess) return released;
        _mappingBudgetReservations.Remove(mapping.MappingId);
        return KernelResult.Ok();
        }
    }

    private void ReleaseClosedIpcBudgetsForProcess(ProcessHandle process)
    {
        (ChannelId Channel, ulong Sequence)[] pending;
        lock (_requestResponseCorrelationGate)
            pending = Budgets.OriginalQueuedCharges().Where(item => item.Sender == process ||
                item.Receiver == process).Select(item => (item.Channel, item.Sequence)).ToArray();
        foreach (var key in pending) ReleaseIpcBudget(key);
    }

    private void ReleaseClosedIpcBudgetsForChannel(ChannelId channel)
    {
        (ChannelId Channel, ulong Sequence)[] pending;
        lock (_requestResponseCorrelationGate)
            pending = Budgets.OriginalQueuedCharges().Where(item => item.Channel == channel)
                .Select(item => (item.Channel, item.Sequence)).ToArray();
        foreach (var key in pending) ReleaseIpcBudget(key);
    }

    private void ReleaseIpcBudget((ChannelId Channel, ulong Sequence) key)
    {
        KernelResult<BudgetReservationSnapshot>? released = null;
        ProcessHandle sender = default;
        lock (_requestResponseCorrelationGate)
        {
            var originals = Budgets.OriginalQueuedCharges().Where(item => item.Channel == key.Channel && item.Sequence == key.Sequence).ToArray();
            if (originals.Length != 1) return;
            var original = originals[0];
            if (!Channels.HasEndedOriginalQueuedLifetime(original.Lifetime, key.Channel, key.Sequence, original.Fact)) return;
            var charge = (original.Sender, original.Receiver, original.Reservation);
            if (_ipcBudgetReservations.TryGetValue(key, out var route) && route != charge) return;
            released = Budgets.ReleaseOriginalRegionCharge(original.Fact, original.Sender, original.Reservation);
            if (!released.Value.IsSuccess || released.Value.Value!.State != BudgetReservationState.Released) return;
            sender = original.Sender;
            if (_ipcBudgetReservations.TryGetValue(key, out var current) && current == charge)
                _ipcBudgetReservations.Remove(key);
            Channels.ConsumeOriginalQueuedLifetime(original.Lifetime, key.Channel, key.Sequence, original.Fact);
            _pendingIpcBudgetObservations.Add(new(sender, TraceEventKind.BudgetReleased, original.Reservation));
        }
        FlushIpcBudgetObservations();
    }

    private void FlushIpcBudgetObservations()
    {
        if (Monitor.IsEntered(_platformMemoryUseGate) || Monitor.IsEntered(_requestResponseCorrelationGate)) return;
        List<BudgetTraceObservation> observations;
        lock (_requestResponseCorrelationGate)
        {
            observations = [.. _pendingIpcBudgetObservations];
            _pendingIpcBudgetObservations.Clear();
        }
        EmitBudgetObservations(observations);
    }

    private KernelResult ReleaseReclaimedRegionBudgetsForProcess(ProcessHandle process, DomainId domain,
        List<BudgetTraceObservation> observations)
    {
        // Enumerate the original quantitative owner's records, never infer absence
        // of a charge from missing kernel routing. Region closure is independent.
        foreach (var original in Budgets.OriginalRegionChargesForCleanup(process, domain))
        {
            if (original.Region is not { } region || original.RegionOwner is null)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original Region charge association is unknown; cleanup remains pinned.");
            var ended = Regions.HasEndedOriginalAccountingLifetime(original.RegionOwner, region);
            if (!ended.IsSuccess) return KernelResult.Fail(ended.Error, ended.Message!);
            if (!ended.Value) continue;
            lock (_regionBudgetReservationsGate)
                if (_regionBudgetReservations.TryGetValue(region, out var route) &&
                    route != (original.Owner, original.Reservation))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Region charge routing replaced the original quantitative fact.");
            var released = ReleaseAttachedBudget(original.Owner, original.Reservation, observations);
            if (!released.IsSuccess)
            {
                // Local Region reclaim does not settle a consuming/quarantined
                // quantitative lifetime. Its exact owner and charge stay retained
                // for the existing independent reconciliation/settlement consumer.
                var retained = Budgets.Query(original.Reservation);
                if (released.Error == KernelError.InvalidTransition && retained.IsSuccess &&
                    retained.Value!.State is BudgetReservationState.Consuming or BudgetReservationState.Quarantined or
                        BudgetReservationState.Reconciled or BudgetReservationState.Settling)
                    continue;
                return released;
            }
            lock (_regionBudgetReservationsGate)
                if (_regionBudgetReservations.TryGetValue(region, out var route) &&
                    route == (original.Owner, original.Reservation))
                    _regionBudgetReservations.Remove(region);
        }
        return KernelResult.Ok();
    }

    private void ReconcileReleasedExternalOperationBudgets(ProcessHandle process)
    {
        foreach (var original in Budgets.OriginalExternalOperationCharges(process))
            if (ExternalOperations.HasEndedOriginalAccountingLifetime(original.OperationOwner, original.Operation))
                ReleaseExternalOperationBudget(process, original.Operation);
    }

    private void ReleaseExternalOperationBudget(ProcessHandle owner, ExternalOperationHandle operation)
    {
        foreach (var original in Budgets.OriginalExternalOperationCharges(owner).Where(charge => charge.Operation == operation))
        {
        var charge = (Owner: original.Owner, Reservation: original.Reservation);
        if (!ExternalOperations.HasEndedOriginalAccountingLifetime(original.OperationOwner, operation)) return;
        lock (_externalOperationBudgetReservationsGate)
            if (_externalOperationBudgetReservations.TryGetValue(operation, out var routed) && routed != charge) return;
        // Local operation release is not quantitative settlement. Keep the exact
        // association for the existing release/teardown retry if the budget owner refuses.
        if (!ReleaseAttachedBudget(charge.Owner, charge.Reservation).IsSuccess) return;
        lock (_externalOperationBudgetReservationsGate)
            if (_externalOperationBudgetReservations.TryGetValue(operation, out var current) && current == charge)
                _externalOperationBudgetReservations.Remove(operation);
        }
    }
}



