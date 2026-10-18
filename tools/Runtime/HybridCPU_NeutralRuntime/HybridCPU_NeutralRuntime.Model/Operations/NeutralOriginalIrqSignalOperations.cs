namespace YAKSys_Hybrid_CPU.Core;
public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalIrqSignalProvider
{
    private static bool SignalHistory(NeutralInterruptState source)=>(ulong)source.Signals.Count==source.SignalsIssued &&
        source.Signals.All(f=>f is not null && ReferenceEquals(f.Self,f) && ReferenceEquals(f.Source,source) &&
            ReferenceEquals(f.Admission,source.OriginalAdmission) && f.Request.Binding==source.Lease &&
            (f.Raw is null || ReferenceEquals(f.Raw.Original,f.Raw) && ReferenceEquals(f.Raw.Request,f.Request))) &&
        source.Signals.Select(f=>f.Request.InvocationId).Distinct().Count()==source.Signals.Count;

    public NeutralOriginalIrqSignalRequest? PrepareOriginalIrqSignal(string deviceResourceId,string sourceResourceId)
    {
        if(string.IsNullOrWhiteSpace(deviceResourceId) || string.IsNullOrWhiteSpace(sourceResourceId))return null;
        lock(_lifecycleGate)
        {
            var found=_dependencies.Interrupts.Values.Where(s=>s.IsActive && s.Lease.DeviceLease.Device.ResourceId==deviceResourceId && s.Lease.Source.ResourceId==sourceResourceId).ToArray();
            if(found.Length!=1)return null;
            var source=found[0];
            if(source.OriginalAdmission is not { } admission || !OriginalIrqSources(source,admission,MappingClosureIncarnation) ||
                !SignalHistory(source) || source.SourceRevisionExhausted || source.SourceRevision>ulong.MaxValue-2 || source.Signals.Count>=256)return null;
            source.Signals.EnsureCapacity(source.Signals.Count+1);
            var fact=new NeutralOriginalIrqSignalFact(source,admission,this);fact.Self=fact;
            var request=new NeutralOriginalIrqSignalRequest(Guid.NewGuid(),admission.Request,source.Lease,source.SourceRevision,fact);fact.Request=request;
            source.Signals.Add(fact);source.SignalsIssued++;
            return request;
        }
    }
    public NeutralOriginalIrqSignalObservation? SignalOriginalIrq(NeutralOriginalIrqSignalRequest request)
    {
        lock(_lifecycleGate)
        {
            var fact=FindSignal(request);if(fact is null)return null;
            if(fact.Raw is { } raw)return ValidRaw(fact,raw)?raw:null;
            if(fact.Invoked)return null; // Unknown effect cannot be issued again.
            var source=fact.Source;
            fact.Invoked=true; // Retained original intent before any counter/delivery effect.
            if(!source.IsActive || !SignalHistory(source) || !OriginalIrqSources(source,fact.Admission,MappingClosureIncarnation) ||
                !_dependencies.Interrupts.TryGetValue(source.Handle,out var current) || !ReferenceEquals(current,source) ||
                source.SourceRevisionExhausted || source.SourceRevision!=request.SourceRevision)
                return fact.Raw=new(request,false,true,NeutralInterruptSignalDecision.Faulted,source.Sequence);
            var result=SignalInterrupt(source.Lease);
            return fact.Raw=new(request,result.IsSignaled,!result.IsSignaled,result.Decision,result.Sequence);
        }
    }
    public NeutralOriginalIrqSignalObservation? ResolveOriginalIrqSignal(NeutralOriginalIrqSignalRequest request)
    {
        lock(_lifecycleGate)
        {
            var fact=FindSignal(request);
            return fact?.Raw is { } raw && ValidRaw(fact,raw)?raw:null;
        }
    }
    private static bool ValidRaw(NeutralOriginalIrqSignalFact fact,NeutralOriginalIrqSignalObservation raw)=>
        ReferenceEquals(raw.Original,raw) && ReferenceEquals(raw.Request,fact.Request) && raw.IsSignaled!=raw.NoEffectGuaranteed &&
        Enum.IsDefined(raw.Decision) && raw.IsSignaled==(raw.Decision==NeutralInterruptSignalDecision.Signaled);
    private NeutralOriginalIrqSignalFact? FindSignal(NeutralOriginalIrqSignalRequest request)
    {
        if(request is null || request.OriginalOwner is not NeutralOriginalIrqSignalFact fact ||
            !ReferenceEquals(fact.Self,fact) || !ReferenceEquals(fact.RuntimeOwner,this) || !ReferenceEquals(fact.Request,request))return null;
        return fact;
    }
}
