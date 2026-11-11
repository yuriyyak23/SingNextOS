using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly record struct BudgetTraceObservation(ProcessHandle Owner,
        TraceEventKind Kind, BudgetReservationHandle Reservation);

    private void EmitBudgetObservations(List<BudgetTraceObservation> observations)
    {
        foreach (BudgetTraceObservation observation in observations)
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
        KernelResult<BudgetReservationSnapshot?> reserved = Budgets.ReserveIfAttached(owner, amounts, lifetime, qosHint);
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
        KernelResult<BudgetReservationSnapshot> released = Budgets.Release(owner, exact);
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
        KernelResult<CapabilityDescriptorV1> capability = ValidateCapability(principal, administrationCapability, CapabilityRights.Configure);
        if (!capability.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.KernelService ||
            capability.Value.ResourceId != CapabilityResourceIds.BudgetAdministration)
            return KernelResult<BudgetAccountSnapshot>.Fail(KernelError.SupervisorDenied, "Budget administration requires the exact kernel budget capability.");
        KernelResult<BudgetAmount[]> staged = StageBudgetAmounts(limits);
        if (!staged.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(staged.Error, staged.Message!);
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> exact = Processes.Resolve(principal);
            if (!exact.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(exact.Error, exact.Message!);
            KernelResult effect = EnsureProcessAcceptsNewEffects(exact.Value!);
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
        KernelResult<SingProcess> process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(process.Error, process.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!effect.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(effect.Error, effect.Message!);
        KernelResult<BudgetAmount[]> staged = StageBudgetAmounts(amounts);
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
        KernelResult<CapabilityDescriptorV1> capability = ValidateCapability(principal, administrationCapability, CapabilityRights.Configure);
        if (!capability.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.KernelService ||
            capability.Value.ResourceId != CapabilityResourceIds.BudgetAdministration)
            return KernelResult<ProcessBudgetAdmission>.Fail(KernelError.SupervisorDenied, "Budget admission requires the exact kernel budget capability.");
        KernelResult<SingProcess> target = Processes.Resolve(process);
        if (!target.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(target.Error, target.Message!);
        KernelResult<BudgetAmount[]> staged = StageBudgetAmounts(limits);
        if (!staged.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(staged.Error, staged.Message!);
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> exact = Processes.Resolve(principal);
            if (!exact.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(exact.Error, exact.Message!);
            KernelResult effect = EnsureProcessAcceptsNewEffects(exact.Value!);
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
        KernelResult<BudgetReservationSnapshot> released = Budgets.ReleaseExplicit(owner, reservation);
        if (!released.IsSuccess || released.Value!.State != BudgetReservationState.Released)
            return released;
        // Accounting release is owned by Budgets. Retire only routing facts whose
        // exact local Region lifetime has independently ended; no reclaim permission.
        RegionHandle[] candidates;
        lock (_regionBudgetReservationsGate)
            candidates = _regionBudgetReservations.Where(item => item.Value.Owner == owner &&
                item.Value.Reservation == reservation).Select(item => item.Key).ToArray();
        HashSet<RegionHandle> ended = candidates.Where(Regions.HasEndedAccountingLifetime).ToHashSet();
        lock (_regionBudgetReservationsGate)
        {
            RegionHandle[] completed = _regionBudgetReservations.Where(item => ended.Contains(item.Key) &&
                item.Value.Owner == owner && item.Value.Reservation == reservation).Select(item => item.Key).ToArray();
            foreach (RegionHandle region in completed) _regionBudgetReservations.Remove(region);
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
        KernelResult continuity = ValidateOriginalRegionBudgetRouting(region, out bool sourceCharged);
        if (!continuity.IsSuccess)
            return KernelResult<BudgetReservationHandle?>.Fail(continuity.Error, continuity.Message!);
        if (sourceCharged && !Budgets.HasProcessAccount(target))
            return KernelResult<BudgetReservationHandle?>.Fail(KernelError.BudgetNotConfigured, "Budgeted ownership cannot transfer into an unbudgeted process generation.");
        if (!Budgets.HasProcessAccount(target)) return KernelResult<BudgetReservationHandle?>.Ok(null);
        RegionDescriptor descriptor = Regions.Snapshot().Single(candidate => candidate.Handle == region);
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
        bool regionEffectPossible =false;
        try
        {
        lock (_platformMemoryUseGate)
        {
                ProcessHandle sourceHandle = new ProcessHandle(source.ProcessId, source.Generation);
                ProcessHandle targetHandle = new ProcessHandle(target.ProcessId, target.Generation);
                KernelResult<SingProcess> sourceResolved = Processes.Resolve(sourceHandle);
                KernelResult<SingProcess> targetResolved = Processes.Resolve(targetHandle);
            if (!sourceResolved.IsSuccess) return KernelResult<RegionHandle>.Fail(sourceResolved.Error, sourceResolved.Message!);
            if (!targetResolved.IsSuccess) return KernelResult<RegionHandle>.Fail(targetResolved.Error, targetResolved.Message!);
            if (!ReferenceEquals(sourceResolved.Value, source) || !ReferenceEquals(targetResolved.Value, target))
                return KernelResult<RegionHandle>.Fail(KernelError.StaleHandle, "IPC transfer requires the exact live process records.");
                KernelResult sourceEffect = EnsureProcessAcceptsNewEffects(source);
            if (!sourceEffect.IsSuccess) return KernelResult<RegionHandle>.Fail(sourceEffect.Error, sourceEffect.Message!);
                KernelResult targetEffect = EnsureProcessAcceptsNewEffects(target);
            if (!targetEffect.IsSuccess) return KernelResult<RegionHandle>.Fail(targetEffect.Error, targetEffect.Message!);
                RegionOwner owner = new RegionOwner(source.DomainId, source.Generation);
                KernelResult<RegionDescriptor> valid = Regions.Validate(region, owner);
            if (!valid.IsSuccess) return KernelResult<RegionHandle>.Fail(valid.Error, valid.Message!);
                KernelResult<ITransferableOwnedPayload> originalPayload =Regions.CaptureTransferPayload(region,owner);
            if(!originalPayload.IsSuccess)return KernelResult<RegionHandle>.Fail(originalPayload.Error,originalPayload.Message!);
            if(originalResponse is not null)
            {
                if(preparedOwnership is null)throw new InvalidOperationException("Original response requires its prepared payload.");
                    object? regionOwner =Regions.CaptureOriginalAccountingOwner(region);
                if(regionOwner is null)throw new InvalidOperationException("Original response Region owner absent before budget admission.");
                Responses.RetainTransferProducer(new(originalResponse,preparedOwnership,region,source.DomainId,
                    new RegionOwner(target.DomainId,targetHandle.Generation),regionOwner,targetHandle,null,null,
                    Budgets.OriginalRegionCharges(region)));
            }
                KernelResult<BudgetReservationHandle?> budget = PrepareRegionBudgetTransfer(sourceHandle, targetHandle, region, observations);
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
                        object? originalBudget =Budgets.CaptureOriginalTransferReservationOwner(targetHandle,budget.Value,preparedOwnership);
                    SessionInvocations.RetainOriginalTransferProducerAdmission(originalSend,preparedOwnership,budget.Value,originalBudget,
                        region,source.DomainId,new RegionOwner(target.DomainId,targetHandle.Generation),Budgets.OriginalRegionCharges(region),
                        Regions.CaptureOriginalAccountingOwner(region)!);
                }
                if(originalResponse is not null)
                {
                        object? originalBudget =Budgets.CaptureOriginalTransferReservationOwner(targetHandle,budget.Value,preparedOwnership!);
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
                        KernelResult prepared = preparePairBorrow();
                  if (!prepared.IsSuccess)
                  {
                      return RefuseBeforeRegion(prepared.Error,prepared.Message!);
                  }
                }
                    // Pair preparation can reenter kernel lifecycle code. Its result
                    // does not preserve permission from the earlier process snapshot.
                    KernelResult<SingProcess> freshSource = Processes.Resolve(sourceHandle);
                    KernelResult<SingProcess> freshTarget = Processes.Resolve(targetHandle);
                if (!freshSource.IsSuccess || !freshTarget.IsSuccess ||
                    !ReferenceEquals(freshSource.Value, source) || !ReferenceEquals(freshTarget.Value, target))
                {
                    return RefuseBeforeRegion(KernelError.StaleHandle,"IPC transfer process incarnation changed during preparation.");
                }
                    KernelResult freshSourceEffect = EnsureProcessAcceptsNewEffects(source);
                    KernelResult freshTargetEffect = EnsureProcessAcceptsNewEffects(target);
                if (!freshSourceEffect.IsSuccess || !freshTargetEffect.IsSuccess)
                {
                        KernelResult refused = !freshSourceEffect.IsSuccess ? freshSourceEffect : freshTargetEffect;
                    return RefuseBeforeRegion(refused.Error,refused.Message!);
                }
                    KernelResult freshCharge = ValidateOriginalRegionBudgetRouting(region, out _);
                if (!freshCharge.IsSuccess)
                {
                    return RefuseBeforeRegion(freshCharge.Error,freshCharge.Message!);
                }
                if (preparedOwnership is not null)
                    _ = Budgets.CaptureOriginalTransferReservationOwner(targetHandle, budget.Value, preparedOwnership);
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
                KernelResult completed =CompleteRegionBudgetTransfer(sourceHandle, targetHandle, region, moved.Value, budget.Value, observations, preparedOwnership);
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
        List<BudgetTraceObservation>? observations = null, PreparedOwnedTransfer? prepared = null)
    {
        // V1 Region authorization is domain/generation scoped. A valid cohort
        // peer may transfer a Region whose quantitative payer is another process.
        if (targetReservation is { } exact)
        {
            KernelResult<SingProcess> process = Processes.Resolve(target);
            if(!process.IsSuccess)
            {
                _=Budgets.QuarantineLease(target,exact);
                return KernelResult.Fail(process.Error,process.Message!);
            }
            KernelResult bound =BindOriginalRegionBudget(target,exact,newRegion,process.Value!.DomainId);
            if(!bound.IsSuccess)
            {
                _=Budgets.QuarantineLease(target,exact);
                return bound;
            }
            lock (_regionBudgetReservationsGate)
            {
                if(_regionBudgetReservations.TryGetValue(newRegion,out (ProcessHandle Owner, BudgetReservationHandle Reservation) existing) && existing!=(target,exact))
                {
                    KernelResult retained = Budgets.QuarantineFailedRegionTransfer(target, exact, prepared, newRegion);
                    if (!retained.IsSuccess) _ = Budgets.QuarantineLease(target, exact);
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
        object? original = Regions.CaptureOriginalAccountingOwner(region);
        return original is null
            ? KernelResult.Fail(KernelError.PlatformFaulted, "Original Region charge owner could not be captured.")
            : Budgets.BindOriginalRegionCharge(owner, reservation, region, domain, original);
    }

    private void ReleaseEndedRegionBudget(RegionHandle region, List<BudgetTraceObservation> observations)
        => ReleaseOriginalRegionBudget(region, observations);

    private KernelResult ValidateOriginalRegionBudgetRouting(RegionHandle region, out bool charged)
    {
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation)[] facts = Budgets.OriginalRegionCharges(region);
        charged = facts.Length != 0;
        if (facts.Length > 1)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Multiple original Region charge owners are inconsistent.");
        lock (_regionBudgetReservationsGate)
            if (_regionBudgetReservations.TryGetValue(region, out (ProcessHandle Owner, BudgetReservationHandle Reservation) route) &&
                (facts.Length != 1 || route != (facts[0].Owner, facts[0].Reservation)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Region routing replaced its original charge.");
        if (!charged) return KernelResult.Ok();
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) original = facts[0];
        if (original.RegionOwner is null)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original Region accounting source is missing.");
        KernelResult<bool> source = Regions.HasEndedOriginalAccountingLifetime(original.RegionOwner, region);
        if (!source.IsSuccess) return KernelResult.Fail(source.Error, source.Message!);
        return Budgets.ValidateOriginalRegionCharge(original.Fact, original.Owner, original.Reservation);
    }

    private void ReleaseOriginalRegionBudget(RegionHandle region,
        List<BudgetTraceObservation>? observations = null)
    {
        KernelResult continuity = ValidateOriginalRegionBudgetRouting(region, out bool charged);
        if (!continuity.IsSuccess || !charged) return;
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation)[] facts = Budgets.OriginalRegionCharges(region);
        if (facts.Length != 1) return;
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) charge = facts[0];
        KernelResult<bool> ended = Regions.HasEndedOriginalAccountingLifetime(charge.RegionOwner!, region);
        if (!ended.IsSuccess || !ended.Value) return;
        // Region release/transfer is independent of quantitative settlement.
        // A transferred generation must not overwrite an unresolved prior charge.
        KernelResult<BudgetReservationSnapshot> released = Budgets.ReleaseOriginalRegionCharge(charge.Fact, charge.Owner, charge.Reservation);
        if (!released.IsSuccess || released.Value!.State != BudgetReservationState.Released) return;
        if (observations is not null) observations.Add(new(charge.Owner, TraceEventKind.BudgetReleased, charge.Reservation));
        else RecordTrace(charge.Owner, TraceEventKind.BudgetReleased, null, "budget-reservation",
            charge.Reservation.ReservationId.Value.ToString(), "released", "closed");
        lock (_regionBudgetReservationsGate)
            if (_regionBudgetReservations.TryGetValue(region, out (ProcessHandle Owner, BudgetReservationHandle Reservation) current) && current == (charge.Owner, charge.Reservation))
                _regionBudgetReservations.Remove(region);
    }

    internal bool HasConsumedOriginalMappingClosure(ProcessHandle owner, PlatformRegionMapping mapping)
    {
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> process = Processes.Resolve(owner);
            if (!process.IsSuccess) return false;
            KernelResult<PlatformRegionMappingLifecycle> lifecycle = PlatformAuthority.QueryRegionMappingLifecycle(mapping, PlatformIdentity(process.Value!));
            if (!lifecycle.IsSuccess || !lifecycle.Value.LocalReclaimAllowed) return false;
            lock (Budgets.MappingReservationPublicationGate)
            {
                KernelResult<(bool Known, bool Expected, object? Owner, object MappingOwner, ulong PrivateDataQuota)> original = PlatformAuthority.QueryOriginalMappingBudgetOwner(mapping);
                if (!original.IsSuccess) return false;
                bool routed = _mappingBudgetReservations.TryGetValue(mapping.MappingId, out (ProcessHandle Owner, BudgetReservationHandle Reservation) charge);
                if (!original.Value.Expected) return !routed;
                if (!original.Value.Known ||
                    !Budgets.IsOriginalMappingChargeConsumedLocked(original.Value.Owner, original.Value.MappingOwner)) return false;
                return !routed || Budgets.ValidateOriginalMappingChargeLocked(original.Value.Owner,
                    original.Value.MappingOwner, charge.Owner, charge.Reservation).IsSuccess;
            }
        }
    }

    private KernelResult ReleaseMappingBudget(PlatformRegionMapping mapping, List<BudgetTraceObservation> observations)
    {
        lock (Budgets.MappingReservationPublicationGate)
        {
            KernelResult<(bool Known, bool Expected, object? Owner, object MappingOwner, ulong PrivateDataQuota)> original = PlatformAuthority.QueryOriginalMappingBudgetOwner(mapping);
        if (!original.IsSuccess) return KernelResult.Fail(original.Error, original.Message!);
            bool routed = _mappingBudgetReservations.TryGetValue(mapping.MappingId, out (ProcessHandle Owner, BudgetReservationHandle Reservation) charge);
        if (!original.Value.Expected)
            return !routed ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.PlatformFaulted, "Unbudgeted mapping cannot consume another charge route.");
        if (!original.Value.Known)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original mapping charge binding is unknown; finalization remains pinned.");
            bool consumed = Budgets.IsOriginalMappingChargeConsumedLocked(original.Value.Owner, original.Value.MappingOwner);
        if (!routed)
            return consumed ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.PlatformFaulted, "Original mapping charge route is absent; finalization remains pinned.");
            KernelResult continuity = Budgets.ValidateOriginalMappingChargeLocked(original.Value.Owner, original.Value.MappingOwner, charge.Owner, charge.Reservation);
        if (!continuity.IsSuccess) return continuity;
        if (consumed)
        {
            _mappingBudgetReservations.Remove(mapping.MappingId);
            return KernelResult.Ok();
        }
            KernelResult released = ReleaseAttachedBudget(charge.Owner, charge.Reservation, observations);
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
        foreach ((ChannelId Channel, ulong Sequence) key in pending) ReleaseIpcBudget(key);
    }

    private void ReleaseClosedIpcBudgetsForChannel(ChannelId channel)
    {
        (ChannelId Channel, ulong Sequence)[] pending;
        lock (_requestResponseCorrelationGate)
            pending = Budgets.OriginalQueuedCharges().Where(item => item.Channel == channel)
                .Select(item => (item.Channel, item.Sequence)).ToArray();
        foreach ((ChannelId Channel, ulong Sequence) key in pending) ReleaseIpcBudget(key);
    }

    private void ReleaseIpcBudget((ChannelId Channel, ulong Sequence) key)
    {
        KernelResult<BudgetReservationSnapshot>? released = null;
        ProcessHandle sender = default;
        lock (_requestResponseCorrelationGate)
        {
            (object Fact, object? Lifetime, ChannelId Channel, ulong Sequence, ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation)[] originals = Budgets.OriginalQueuedCharges().Where(item => item.Channel == key.Channel && item.Sequence == key.Sequence).ToArray();
            if (originals.Length != 1) return;
            (object Fact, object? Lifetime, ChannelId Channel, ulong Sequence, ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) original = originals[0];
            if (!Channels.HasEndedOriginalQueuedLifetime(original.Lifetime, key.Channel, key.Sequence, original.Fact)) return;
            (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) charge = (original.Sender, original.Receiver, original.Reservation);
            if (_ipcBudgetReservations.TryGetValue(key, out (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) route) && route != charge) return;
            released = Budgets.ReleaseOriginalRegionCharge(original.Fact, original.Sender, original.Reservation);
            if (!released.Value.IsSuccess || released.Value.Value!.State != BudgetReservationState.Released) return;
            sender = original.Sender;
            if (_ipcBudgetReservations.TryGetValue(key, out (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) current) && current == charge)
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
            KernelResult<bool> ended = Regions.HasEndedOriginalAccountingLifetime(original.RegionOwner, region);
            if (!ended.IsSuccess) return KernelResult.Fail(ended.Error, ended.Message!);
            if (!ended.Value) continue;
            lock (_regionBudgetReservationsGate)
                if (_regionBudgetReservations.TryGetValue(region, out (ProcessHandle Owner, BudgetReservationHandle Reservation) route) &&
                    route != (original.Owner, original.Reservation))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Region charge routing replaced the original quantitative fact.");
            KernelResult released;
            if (original.FailedTransfer is { } failure)
            {
                released = Budgets.ConsumeFailedRegionTransfer(original.Fact, failure, original.Owner, original.Reservation,
                    region, original.RegionOwner, domain);
                if (!released.IsSuccess) return released;
                observations.Add(new(original.Owner, TraceEventKind.BudgetReleased, original.Reservation));
            }
            else released = ReleaseAttachedBudget(original.Owner, original.Reservation, observations);
            if (!released.IsSuccess)
            {
                // Local Region reclaim does not settle a consuming/quarantined
                // quantitative lifetime. Its exact owner and charge stay retained
                // for the existing independent reconciliation/settlement consumer.
                KernelResult<BudgetReservationSnapshot> retained = Budgets.Query(original.Reservation);
                if (released.Error == KernelError.InvalidTransition && retained.IsSuccess &&
                    retained.Value!.State is BudgetReservationState.Consuming or BudgetReservationState.Quarantined or
                        BudgetReservationState.Reconciled or BudgetReservationState.Settling)
                    continue;
                return released;
            }
            lock (_regionBudgetReservationsGate)
                if (_regionBudgetReservations.TryGetValue(region, out (ProcessHandle Owner, BudgetReservationHandle Reservation) route) &&
                    route == (original.Owner, original.Reservation))
                    _regionBudgetReservations.Remove(region);
        }
        return KernelResult.Ok();
    }

    private void ReconcileReleasedExternalOperationBudgets(ProcessHandle process)
    {
        foreach ((ExternalOperationHandle Operation, object OperationOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) original in Budgets.OriginalExternalOperationCharges(process))
            if (ExternalOperations.HasEndedOriginalAccountingLifetime(original.OperationOwner, original.Operation))
                ReleaseExternalOperationBudget(process, original.Operation);
    }

    private void ReleaseExternalOperationBudget(ProcessHandle owner, ExternalOperationHandle operation)
    {
        foreach ((ExternalOperationHandle Operation, object OperationOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) original in Budgets.OriginalExternalOperationCharges(owner).Where(charge => charge.Operation == operation))
        {
            (ProcessHandle Owner, BudgetReservationHandle Reservation) charge = (Owner: original.Owner, Reservation: original.Reservation);
        if (!ExternalOperations.HasEndedOriginalAccountingLifetime(original.OperationOwner, operation)) return;
        lock (_externalOperationBudgetReservationsGate)
            if (_externalOperationBudgetReservations.TryGetValue(operation, out (ProcessHandle Owner, BudgetReservationHandle Reservation) routed) && routed != charge) return;
        // Local operation release is not quantitative settlement. Keep the exact
        // association for the existing release/teardown retry if the budget owner refuses.
        if (!ReleaseAttachedBudget(charge.Owner, charge.Reservation).IsSuccess) return;
        lock (_externalOperationBudgetReservationsGate)
            if (_externalOperationBudgetReservations.TryGetValue(operation, out (ProcessHandle Owner, BudgetReservationHandle Reservation) current) && current == charge)
                _externalOperationBudgetReservations.Remove(operation);
        }
    }
}



