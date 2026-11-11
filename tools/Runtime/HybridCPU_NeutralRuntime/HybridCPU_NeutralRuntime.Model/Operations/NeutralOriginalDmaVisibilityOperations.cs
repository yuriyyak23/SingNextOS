namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalDmaVisibilityProvider
{
    public NeutralOriginalDmaVisibilityObservation PrepareDmaVisibility(NeutralOriginalDmaVisibilityRequest request) => RunOriginalDmaVisibility(request, false);
    public NeutralOriginalDmaVisibilityObservation AcquireDmaVisibility(NeutralOriginalDmaVisibilityRequest request) => RunOriginalDmaVisibility(request, true);
    public NeutralOriginalDmaVisibilityObservation? ResolveDmaVisibility(NeutralOriginalDmaVisibilityRequest request)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || request.InvocationId == Guid.Empty ||
                !_dependencies.Dma.TryGetValue(request.Grant.Handle,out NeutralDmaGrantState? source) || source.Lease != request.Grant || !source.IsActive || !OriginalVisibilityHistoryIntact(source) ||
                source.OriginalAdmission is not { } admission || request.DmaBindInvocationId != admission.Request.InvocationId ||
                !OriginalDmaSources(source,admission,request.RuntimeIncarnation)) return null;
            NeutralOriginalDmaVisibilityFact[] history = source.OriginalVisibilityHistory.Where(old => old.Request.InvocationId == request.InvocationId).ToArray();
            if (history.Length != 1 || history[0] is not { } fact || fact.Request != request || !ReferenceEquals(fact.OwnerSelf,fact) ||
                !ReferenceEquals(fact.Source,source) || !ReferenceEquals(fact.Admission,admission) || fact.Observation is not { } raw || raw.Request != request) return null;
            if (raw.NoEffectGuaranteed) return raw;
            if (fact.ProducedState is not { } cycle || !ReferenceEquals(source.Visibility,cycle) || !ReferenceEquals(cycle.OwnerSelf,cycle) ||
                source.VisibilitySourceExhausted || cycle.SourceExhausted || source.VisibilityRevision != fact.GrantVisibilityRevision || cycle.Revision != fact.ProducedRevision ||
                cycle.Cycle != (request.Acquire ? raw.Acquire?.Evidence.Cycle : raw.Prepare?.Evidence.Cycle)) return null;
            return raw; // Surviving exact owner/cycle, never permission from missing state.
        }
    }
    private static bool OriginalVisibilityHistoryIntact(NeutralDmaGrantState source) =>
        ReferenceEquals(source.OwnerSelf,source) && (ulong)source.OriginalVisibilityHistory.Count == source.OriginalVisibilityIssued &&
        source.OriginalVisibilityHistory.All(fact => fact is not null && ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Source,source) &&
            ReferenceEquals(fact.Admission,source.OriginalAdmission) && (fact.Observation is null || fact.Observation.Value.Request == fact.Request)) &&
        source.OriginalVisibilityHistory.Distinct(ReferenceEqualityComparer.Instance).Count() == source.OriginalVisibilityHistory.Count &&
        source.OriginalVisibilityHistory.Select(fact => fact.Request.InvocationId).Distinct().Count() == source.OriginalVisibilityHistory.Count;

    private NeutralOriginalDmaVisibilityObservation RunOriginalDmaVisibility(NeutralOriginalDmaVisibilityRequest request, bool acquire)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalDmaVisibilityObservation Refuse(bool matched, bool noEffect) => new(request,matched,noEffect,
                acquire ? null : new() { Decision = NeutralDmaPrepareDecision.Faulted },
                acquire ? new() { Decision = NeutralDmaAcquireDecision.Faulted } : null);
            if (request.RuntimeIncarnation != MappingClosureIncarnation) return Refuse(false,true);
            if (request.Acquire != acquire) return Refuse(true,false); // Wrong method is not a new no-effect proof for an already known request.
            if (_dependencies.Dma.TryGetValue(request.Grant.Handle,out NeutralDmaGrantState? retained))
            {
                if (!OriginalVisibilityHistoryIntact(retained)) return Refuse(true,false);
                NeutralOriginalDmaVisibilityFact[] history = retained.OriginalVisibilityHistory.Where(old => old.Request.InvocationId == request.InvocationId).ToArray();
                if (history.Length != 0)
                    return history.Length == 1 && history[0].Request == request && ReferenceEquals(history[0].OwnerSelf,history[0]) &&
                        ReferenceEquals(history[0].Source,retained) && OriginalDmaSources(retained,history[0].Admission,request.RuntimeIncarnation,false) &&
                        history[0].Observation is { } raw ? raw : Refuse(true,false);
            }
            NeutralLeaseValidation<NeutralDmaGrantState> valid = _leases.Validate(request.Grant);
            if (request.InvocationId == Guid.Empty || request.Acquire != acquire || !valid.IsValid || valid.State!.OriginalAdmission is not { } admission ||
                request.DmaBindInvocationId != admission.Request.InvocationId || !OriginalDmaSources(valid.State,admission,request.RuntimeIncarnation) ||
                acquire && (valid.State.Visibility is not { } cycle || !ReferenceEquals(cycle.OwnerSelf,cycle) || cycle.Cycle != request.ExpectedCycle) ||
                !acquire && request.ExpectedCycle != default) return Refuse(true,true);
            if (valid.State.OriginalVisibilityIssued == ulong.MaxValue) return Refuse(true,true);
            NeutralOriginalDmaVisibilityFact fact = new NeutralOriginalDmaVisibilityFact(request,valid.State,admission);
            valid.State.OriginalVisibilityHistory.Add(fact); valid.State.OriginalVisibilityIssued++; // Intent before effect, under producing owner lock.
            NeutralDmaPrepareResult? prepare = acquire ? null : PrepareDmaVisibility(request.Grant);
            NeutralDmaAcquireResult? acquired = acquire ? AcquireDmaVisibility(request.Grant) : null;
            bool success = acquire ? acquired!.IsAcquired : prepare!.IsPrepared;
            NeutralOriginalDmaVisibilityObservation rawOutcome = new NeutralOriginalDmaVisibilityObservation(request,true,!success,prepare,acquired);
            fact.ProducedState = valid.State.Visibility; fact.ProducedRevision = valid.State.Visibility?.Revision ?? 0;
            fact.GrantVisibilityRevision = valid.State.VisibilityRevision; fact.Observation = rawOutcome;
            return rawOutcome;
        }
    }
}
