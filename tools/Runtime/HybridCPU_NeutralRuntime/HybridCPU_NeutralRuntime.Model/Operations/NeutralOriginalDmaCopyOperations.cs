namespace YAKSys_Hybrid_CPU.Core;

public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalDmaCopyProvider
{
    public NeutralOriginalDmaCopyObservation CopyDma(NeutralOriginalDmaCopyRequest request)
    {
        NeutralOriginalDmaCopyFact fact;
        lock (_lifecycleGate)
        {
            var unknown = new NeutralOriginalDmaCopyObservation(request,true,false,false,false);
            if (request.RuntimeIncarnation != MappingClosureIncarnation)
                return new(request,false,true,false,true);
            if (!_dependencies.Dma.TryGetValue(request.Source.Grant.Handle,out var source) ||
                !_dependencies.Dma.TryGetValue(request.Destination.Grant.Handle,out var destination) ||
                !CopyHistoryIntact(source) || !CopyHistoryIntact(destination)) return unknown;
            var prior = source.OriginalCopies.Where(copy => copy.Request.InvocationId == request.InvocationId).ToArray();
            if (prior.Length != 0)
                return prior.Length == 1 && ReferenceEquals(prior[0].Request,request) && CopySources(prior[0],false) &&
                    prior[0].Observation is { } raw ? raw : unknown;
            // Retain refusal only at canonical original grant factories. Broken copy
            // inventory or foreign admissions remain unknown and cannot gain a latch.
            if (request.InvocationId == Guid.Empty || ReferenceEquals(source,destination) ||
                source.Lease != request.Source.Grant || destination.Lease != request.Destination.Grant ||
                source.OriginalAdmission is not { } sourceAdmission || destination.OriginalAdmission is not { } destinationAdmission ||
                sourceAdmission.Request != request.Source.Admission || destinationAdmission.Request != request.Destination.Admission ||
                !OriginalDmaSources(source,sourceAdmission,request.RuntimeIncarnation) || !OriginalDmaSources(destination,destinationAdmission,request.RuntimeIncarnation) ||
                source.Visibility is not { } sourceCycle || destination.Visibility is not { } destinationCycle ||
                !ReferenceEquals(sourceCycle.OwnerSelf,sourceCycle) || !ReferenceEquals(destinationCycle.OwnerSelf,destinationCycle) ||
                destination.OriginalCopies.Any(old => old.Request.InvocationId == request.InvocationId) ||
                source.OriginalCopiesIssued == ulong.MaxValue || destination.OriginalCopiesIssued == ulong.MaxValue) return unknown;
            var refused = !source.IsActive || !destination.IsActive || source.PendingCopy is not null || destination.PendingCopy is not null ||
                !CopyLegPrepared(source,request.Source) || !CopyLegPrepared(destination,request.Destination) ||
                source.Lease.Direction != NeutralDmaDirection.DeviceReadsMemory || destination.Lease.Direction != NeutralDmaDirection.DeviceWritesMemory ||
                request.SourceBytes.Length != source.Lease.Range.Length || request.DestinationBytes.Length != destination.Lease.Range.Length ||
                request.SourceBytes.IsEmpty || request.SourceBytes.Length != request.DestinationBytes.Length ||
                source.OriginalCopies.Any(old => old.Request.Source.Cycle == request.Source.Cycle && old.Observation is not { NoEffectGuaranteed: true }) ||
                destination.OriginalCopies.Any(old => old.Request.Destination.Cycle == request.Destination.Cycle && old.Observation is not { NoEffectGuaranteed: true });
            source.OriginalCopies.EnsureCapacity(source.OriginalCopies.Count + 1);
            destination.OriginalCopies.EnsureCapacity(destination.OriginalCopies.Count + 1);
            fact = new(request,source,destination,source.OriginalAdmission!,destination.OriginalAdmission!,source.Visibility!,destination.Visibility!);
            fact.EstablishSelf();
            source.OriginalCopies.Add(fact); destination.OriginalCopies.Add(fact);
            source.OriginalCopiesIssued++; destination.OriginalCopiesIssued++;
            if (refused)
            {
                fact.Observation = new(request,true,true,false,true); // Same-request latch before reply; no byte callback/effect.
                request.ReleaseBytes();
                return fact.Observation;
            }
            source.PendingCopy = destination.PendingCopy = fact; // Intent before memory-manager callbacks.
        }
        try
        {
            // GetSpan may call an external MemoryManager. No owner lock is held.
            var sourceBytes = request.SourceBytes.Span;
            var destinationBytes = request.DestinationBytes.Span;
            lock (_lifecycleGate)
            {
                if (!CopySources(fact,true) || sourceBytes.Length != request.ByteLength || destinationBytes.Length != request.ByteLength || sourceBytes.Overlaps(destinationBytes))
                    return fact.Observation = new(request,true,true,false,true);
                // Native source/factory/cycle validation and byte effect share this
                // producing linearization point. Span.CopyTo has no callbacks.
                sourceBytes.CopyTo(destinationBytes);
                return fact.Observation = new(request,true,false,true,true);
            }
        }
        catch
        {
            lock (_lifecycleGate) fact.Observation = new(request,true,false,false,true);
            throw; // Synchronous lifetime ended, but arbitrary backing faults are not no-effect.
        }
        finally
        {
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(fact.Source.PendingCopy,fact)) fact.Source.PendingCopy = null;
                if (ReferenceEquals(fact.Destination.PendingCopy,fact)) fact.Destination.PendingCopy = null;
                request.ReleaseBytes(); // Fact identity survives; completed producer retains no byte backing.
            }
        }
    }

    public NeutralOriginalDmaCopyObservation? ResolveDmaCopy(NeutralOriginalDmaCopyRequest request) => LookupCopy(request,false);
    public NeutralOriginalDmaCopyObservation? ValidateDmaCopy(NeutralOriginalDmaCopyRequest request) => LookupCopy(request,true);
    private NeutralOriginalDmaCopyObservation? LookupCopy(NeutralOriginalDmaCopyRequest request,bool prepared)
    {
        lock (_lifecycleGate)
        {
            if (request.RuntimeIncarnation != MappingClosureIncarnation || request.InvocationId == Guid.Empty ||
                !_dependencies.Dma.TryGetValue(request.Source.Grant.Handle,out var source) || !CopyHistoryIntact(source)) return null;
            var facts = source.OriginalCopies.Where(fact => fact.Request.InvocationId == request.InvocationId).ToArray();
            return facts.Length == 1 && ReferenceEquals(facts[0].Request,request) && CopySources(facts[0],prepared) &&
                facts[0].Observation is { NoFurtherByteUseGuaranteed: true } raw ? raw : null;
        }
    }
    private bool CopySources(NeutralOriginalDmaCopyFact fact,bool prepared) =>
        fact.HasSelf && CopyHistoryIntact(fact.Source) && CopyHistoryIntact(fact.Destination) &&
        fact.Source.OriginalCopies.Count(copy => ReferenceEquals(copy,fact)) == 1 && fact.Destination.OriginalCopies.Count(copy => ReferenceEquals(copy,fact)) == 1 &&
        _dependencies.Dma.TryGetValue(fact.Source.Handle,out var source) && ReferenceEquals(source,fact.Source) &&
        _dependencies.Dma.TryGetValue(fact.Destination.Handle,out var destination) && ReferenceEquals(destination,fact.Destination) &&
        OriginalDmaSources(source,fact.SourceAdmission,fact.Request.RuntimeIncarnation,prepared) &&
        OriginalDmaSources(destination,fact.DestinationAdmission,fact.Request.RuntimeIncarnation,prepared) &&
        (!prepared || source.IsActive && destination.IsActive && CopyLegPrepared(source,fact.Request.Source) && CopyLegPrepared(destination,fact.Request.Destination) &&
            ReferenceEquals(source.Visibility,fact.SourceCycle) && ReferenceEquals(destination.Visibility,fact.DestinationCycle) &&
            source.VisibilityRevision == fact.SourceVisibilityRevision && destination.VisibilityRevision == fact.DestinationVisibilityRevision &&
            fact.SourceCycle.Revision == fact.SourceCycleRevision && fact.DestinationCycle.Revision == fact.DestinationCycleRevision);

    private static bool CopyHistoryIntact(NeutralDmaGrantState source) => ReferenceEquals(source.OwnerSelf,source) &&
        (ulong)source.OriginalCopies.Count == source.OriginalCopiesIssued && source.OriginalCopies.All(fact => fact is not null && fact.HasSelf &&
            (ReferenceEquals(fact.Source,source) || ReferenceEquals(fact.Destination,source)) &&
            (fact.Observation is null || ReferenceEquals(fact.Observation.Request,fact.Request))) &&
        source.OriginalCopies.Distinct(ReferenceEqualityComparer.Instance).Count() == source.OriginalCopies.Count &&
        source.OriginalCopies.Select(fact => fact.Request).Distinct(ReferenceEqualityComparer.Instance).Count() == source.OriginalCopies.Count &&
        source.OriginalCopies.Select(fact => fact.Request.InvocationId).Distinct().Count() == source.OriginalCopies.Count;

    private bool CopyLegPrepared(NeutralDmaGrantState source,NeutralOriginalDmaCopyLeg leg)
    {
        if (!(source.OriginalAdmission is { } admission && admission.Request == leg.Admission && OriginalDmaSources(source,admission,leg.Admission.RuntimeIncarnation) &&
        leg.Admission.RuntimeIncarnation == MappingClosureIncarnation && leg.Prepare.RuntimeIncarnation == MappingClosureIncarnation &&
        leg.Prepare.Grant == source.Lease && !leg.Prepare.Acquire && leg.Prepare.DmaBindInvocationId == admission.Request.InvocationId &&
        OriginalVisibilityHistoryIntact(source))) return false;
        var facts = source.OriginalVisibilityHistory.Where(old => old.Request == leg.Prepare).ToArray();
        return facts.Length == 1 && facts[0] is { Observation: { SourceFenceMatched: true, NoEffectGuaranteed: false, Prepare: { IsPrepared: true } } } prepare &&
        ReferenceEquals(prepare.Admission,admission) && prepare.Observation.Value.Prepare!.Evidence.Cycle == leg.Cycle &&
        source.Visibility is { Acquired: false, SourceExhausted: false } cycle && ReferenceEquals(cycle.OwnerSelf,cycle) &&
        ReferenceEquals(prepare.ProducedState,cycle) && !source.VisibilitySourceExhausted && cycle.Cycle == leg.Cycle &&
        cycle.Revision == prepare.ProducedRevision && source.VisibilityRevision == prepare.GrantVisibilityRevision;
    }
}
