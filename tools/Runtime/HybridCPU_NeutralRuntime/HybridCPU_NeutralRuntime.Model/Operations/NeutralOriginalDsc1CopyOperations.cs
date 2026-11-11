using System.Runtime.InteropServices;
namespace YAKSys_Hybrid_CPU.Core;
public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalDsc1PreparationProvider
{
    private static bool Dsc1History(NeutralMappingState mapping)=>(ulong)mapping.OriginalDsc1Copies.Count==mapping.OriginalDsc1CopiesIssued &&
        mapping.OriginalDsc1Copies.All(f=>f is not null && ReferenceEquals(f.Self,f) &&
            (ReferenceEquals(f.Source,mapping)||ReferenceEquals(f.Destination,mapping)) && ReferenceEquals(f.Request.Owner,f)) &&
        mapping.OriginalDsc1Copies.Select(f=>f.Request.InvocationId).Distinct().Count()==mapping.OriginalDsc1Copies.Count;
    private bool Dsc1MappingSource(NeutralMappingState mapping,NeutralOriginalMappingAdmissionFact admission)=>
        mapping.IsActive && mapping.OriginalClose is null && ReferenceEquals(admission.Original,admission) && ReferenceEquals(mapping.OriginalAdmission,admission) &&
        ReferenceEquals(admission.Source,mapping) && admission.Request.RuntimeIncarnation==MappingClosureIncarnation &&
        _dependencies.Mappings.TryGetValue(mapping.Handle,out NeutralMappingState? original) && ReferenceEquals(original,mapping) &&
        _dependencies.Domains.TryGetValue(mapping.ParentDomain,out NeutralDomainState? parent) && ReferenceEquals(parent,admission.Parent) && parent.IsActive &&
        parent.OriginalAdmission is { } factory && ReferenceEquals(factory.Original,factory) && ReferenceEquals(factory.Source,parent) &&
        factory.Request.RuntimeIncarnation==MappingClosureIncarnation && factory.Request.InvocationId==admission.Request.DomainBindInvocationId;
    public NeutralOriginalDsc1CopyRequest? PrepareDsc1Copy(NeutralOwnedRegionMappingLease sourceMapping,long sourceOffset,
        NeutralOwnedRegionMappingLease destinationMapping,long destinationOffset,ReadOnlyMemory<byte> source,Memory<byte> destination)
        => PrepareDsc1Copy(new NeutralOriginalDsc1Preparation(), sourceMapping, sourceOffset, destinationMapping, destinationOffset, source, destination);
    public NeutralOriginalDsc1CopyRequest? PrepareDsc1Copy(NeutralOriginalDsc1Preparation preparation,
        NeutralOwnedRegionMappingLease sourceMapping,long sourceOffset,
        NeutralOwnedRegionMappingLease destinationMapping,long destinationOffset,ReadOnlyMemory<byte> source,Memory<byte> destination)
    {
        // This bounded software producer accepts array custody only. A custom
        // MemoryManager must not execute callbacks under the native effect lock.
        if(!MemoryMarshal.TryGetArray(source,out ArraySegment<byte> inputBytes) || !MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination,out ArraySegment<byte> outputBytes) ||
            inputBytes.Array is null || outputBytes.Array is null)return null;
        lock(_lifecycleGate)
        {
            if (preparation is null || _dependencies.Mappings.Values.Any(m => m.OriginalDsc1Copies.Any(f =>
                f.Preparation?.InvocationId == preparation.InvocationId))) return null;
            if(source.Length is <=0 or >1048576 || source.Length!=destination.Length || source.Span.Overlaps(destination.Span) ||
                sourceOffset<0 || destinationOffset<0 || sourceMapping.DomainLease!=destinationMapping.DomainLease ||
                !sourceMapping.Slice.Access.HasFlag(NeutralMemoryAccess.Read) || !destinationMapping.Slice.Access.HasFlag(NeutralMemoryAccess.Write) ||
                sourceOffset>sourceMapping.Slice.Length || source.Length>sourceMapping.Slice.Length-sourceOffset ||
                destinationOffset>destinationMapping.Slice.Length || destination.Length>destinationMapping.Slice.Length-destinationOffset ||
                !_dependencies.Mappings.TryGetValue(sourceMapping.Handle,out NeutralMappingState? input) || input.Lease!=sourceMapping ||
                !_dependencies.Mappings.TryGetValue(destinationMapping.Handle,out NeutralMappingState? output) || output.Lease!=destinationMapping || ReferenceEquals(input,output) ||
                input.OriginalAdmission is not { } ia || output.OriginalAdmission is not { } oa ||
                !Dsc1MappingSource(input,ia) || !Dsc1MappingSource(output,oa) || !Dsc1History(input) || !Dsc1History(output) ||
                input.OriginalDsc1Copies.Count>=256 || output.OriginalDsc1Copies.Count>=256)return null;
            input.OriginalDsc1Copies.EnsureCapacity(input.OriginalDsc1Copies.Count+1);output.OriginalDsc1Copies.EnsureCapacity(output.OriginalDsc1Copies.Count+1);
            NeutralOriginalDsc1CopyFact fact =new NeutralOriginalDsc1CopyFact(this,input,output,ia,oa);fact.Self=fact;
            fact.Preparation=preparation;
            NeutralOriginalDsc1CopyRequest request =new NeutralOriginalDsc1CopyRequest(fact,source,destination);fact.Request=request;
            input.OriginalDsc1Copies.Add(fact);output.OriginalDsc1Copies.Add(fact);
            input.OriginalDsc1CopiesIssued++;output.OriginalDsc1CopiesIssued++;return request;
        }
    }
    public NeutralOriginalDsc1CopyObservation? CopyDsc1(NeutralOriginalDsc1CopyRequest request)
    {
        lock(_lifecycleGate)
        {
            NeutralOriginalDsc1CopyFact? fact =FindDsc1(request);if(fact is null)return null;
            if(fact.Raw is { } retained)return ValidDsc1Raw(fact,retained)?retained:null;
            if(fact.Invoked)return null;
            // Allocate terminal facts before any byte mutation. Unknown after entry never repeats.
            NeutralOriginalDsc1CopyObservation completed =new NeutralOriginalDsc1CopyObservation(request,true,false);
            NeutralOriginalDsc1CopyObservation refused =new NeutralOriginalDsc1CopyObservation(request,false,true);
            fact.Invoked=true;
            try
            {
                if(!Dsc1MappingSource(fact.Source,fact.SourceAdmission) || !Dsc1MappingSource(fact.Destination,fact.DestinationAdmission) ||
                    !Dsc1History(fact.Source) || !Dsc1History(fact.Destination))return fact.Raw=refused;
                request.Source.Span.CopyTo(request.Destination.Span);
                return fact.Raw=completed;
            }
            finally{request.ReleaseBytes();}
        }
    }
    public NeutralOriginalDsc1CopyObservation? ResolveDsc1Copy(NeutralOriginalDsc1CopyRequest request)
    {lock(_lifecycleGate){ NeutralOriginalDsc1CopyFact? fact =FindDsc1(request);return fact?.Raw is { } raw && ValidDsc1Raw(fact,raw)?raw:null;}}
    public NeutralOriginalDsc1CopyRequest? ResolveDsc1Preparation(NeutralOriginalDsc1Preparation preparation,
        NeutralOwnedRegionMappingLease sourceMapping, NeutralOwnedRegionMappingLease destinationMapping)
    {
        lock (_lifecycleGate)
        {
            if (preparation is null || !_dependencies.Mappings.TryGetValue(sourceMapping.Handle, out NeutralMappingState? source) ||
                source.Lease != sourceMapping || !_dependencies.Mappings.TryGetValue(destinationMapping.Handle, out NeutralMappingState? destination) ||
                destination.Lease != destinationMapping || !Dsc1History(source) || !Dsc1History(destination)) return null;
            NeutralOriginalDsc1CopyFact[] matches = source.OriginalDsc1Copies.Where(f => ReferenceEquals(f.Preparation, preparation) &&
                ReferenceEquals(f.Source, source) && ReferenceEquals(f.Destination, destination)).ToArray();
            return matches.Length == 1 && FindDsc1(matches[0].Request) is { } original &&
                ReferenceEquals(original, matches[0]) && destination.OriginalDsc1Copies.Count(f => ReferenceEquals(f, original)) == 1
                ? original.Request : null;
        }
    }
    public NeutralOriginalDsc1CopyObservation? EndDsc1PreparedCustody(NeutralOriginalDsc1CopyRequest request)
    {
        lock (_lifecycleGate)
        {
            NeutralOriginalDsc1CopyFact? fact = FindDsc1(request);
            if (fact is null || !Dsc1History(fact.Source) || !Dsc1History(fact.Destination)) return null;
            if (fact.Raw is { } raw) return ValidDsc1Raw(fact, raw) ? raw : null;
            if (fact.Invoked) return null;
            NeutralOriginalDsc1CopyObservation closed = new NeutralOriginalDsc1CopyObservation(request, false, true);
            fact.Raw = closed; fact.Invoked = true; request.ReleaseBytes(); return closed;
        }
    }
    // Called by the existing mapping owner under the lifecycle lock, before
    // closing the mapping. Prepared custody cannot outlive its source lifetime.
    private bool EndPreparedDsc1Custody(NeutralMappingState mapping)
    {
        if (!Dsc1History(mapping)) return false;
        foreach (NeutralOriginalDsc1CopyFact fact in mapping.OriginalDsc1Copies)
            if (FindDsc1(fact.Request) is not { } original || !ReferenceEquals(original, fact) ||
                !Dsc1History(fact.Source) || !Dsc1History(fact.Destination) ||
                (fact.Invoked && fact.Raw is null) ||
                (fact.Raw is { } raw && !ValidDsc1Raw(fact, raw))) return false;
        // Allocate every refusal before changing any retained fact.
        (NeutralOriginalDsc1CopyFact Fact, NeutralOriginalDsc1CopyObservation Raw)[] prepared = mapping.OriginalDsc1Copies.Where(f => !f.Invoked && f.Raw is null)
            .Select(f => (Fact: f, Raw: new NeutralOriginalDsc1CopyObservation(f.Request, false, true))).ToArray();
        foreach ((NeutralOriginalDsc1CopyFact Fact, NeutralOriginalDsc1CopyObservation Raw) entry in prepared)
        {
            entry.Fact.Raw = entry.Raw;
            entry.Fact.Invoked = true;
            entry.Fact.Request.ReleaseBytes();
        }
        return true;
    }
    private NeutralOriginalDsc1CopyFact? FindDsc1(NeutralOriginalDsc1CopyRequest request)=>
        request is not null && request.Owner is NeutralOriginalDsc1CopyFact fact && ReferenceEquals(fact.Self,fact) &&
        ReferenceEquals(fact.Runtime,this) && ReferenceEquals(fact.Request,request)?fact:null;
    private static bool ValidDsc1Raw(NeutralOriginalDsc1CopyFact fact,NeutralOriginalDsc1CopyObservation raw)=>
        ReferenceEquals(raw.Original,raw) && ReferenceEquals(raw.Request,fact.Request) && raw.Completed!=raw.NoEffectGuaranteed;
}
