using System.Runtime.InteropServices;
namespace YAKSys_Hybrid_CPU.Core;
public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalDsc1CopyProvider
{
    private static bool Dsc1History(NeutralMappingState mapping)=>(ulong)mapping.OriginalDsc1Copies.Count==mapping.OriginalDsc1CopiesIssued &&
        mapping.OriginalDsc1Copies.All(f=>f is not null && ReferenceEquals(f.Self,f) &&
            (ReferenceEquals(f.Source,mapping)||ReferenceEquals(f.Destination,mapping)) && ReferenceEquals(f.Request.Owner,f)) &&
        mapping.OriginalDsc1Copies.Select(f=>f.Request.InvocationId).Distinct().Count()==mapping.OriginalDsc1Copies.Count;
    private bool Dsc1MappingSource(NeutralMappingState mapping,NeutralOriginalMappingAdmissionFact admission)=>
        mapping.IsActive && mapping.OriginalClose is null && ReferenceEquals(mapping.OriginalAdmission,admission) &&
        ReferenceEquals(admission.Source,mapping) && admission.Request.RuntimeIncarnation==MappingClosureIncarnation &&
        _dependencies.Mappings.TryGetValue(mapping.Handle,out var original) && ReferenceEquals(original,mapping) &&
        _dependencies.Domains.TryGetValue(mapping.ParentDomain,out var parent) && ReferenceEquals(parent,admission.Parent) && parent.IsActive &&
        parent.OriginalAdmission is { } factory && ReferenceEquals(factory.Source,parent) &&
        factory.Request.RuntimeIncarnation==MappingClosureIncarnation && factory.Request.InvocationId==admission.Request.DomainBindInvocationId;
    public NeutralOriginalDsc1CopyRequest? PrepareDsc1Copy(NeutralOwnedRegionMappingLease sourceMapping,long sourceOffset,
        NeutralOwnedRegionMappingLease destinationMapping,long destinationOffset,ReadOnlyMemory<byte> source,Memory<byte> destination)
    {
        // This bounded software producer accepts array custody only. A custom
        // MemoryManager must not execute callbacks under the native effect lock.
        if(!MemoryMarshal.TryGetArray(source,out var inputBytes) || !MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination,out var outputBytes) ||
            inputBytes.Array is null || outputBytes.Array is null)return null;
        lock(_lifecycleGate)
        {
            if(source.Length is <=0 or >1048576 || source.Length!=destination.Length || source.Span.Overlaps(destination.Span) ||
                sourceOffset<0 || destinationOffset<0 || sourceMapping.DomainLease!=destinationMapping.DomainLease ||
                !sourceMapping.Slice.Access.HasFlag(NeutralMemoryAccess.Read) || !destinationMapping.Slice.Access.HasFlag(NeutralMemoryAccess.Write) ||
                sourceOffset>sourceMapping.Slice.Length || source.Length>sourceMapping.Slice.Length-sourceOffset ||
                destinationOffset>destinationMapping.Slice.Length || destination.Length>destinationMapping.Slice.Length-destinationOffset ||
                !_dependencies.Mappings.TryGetValue(sourceMapping.Handle,out var input) || input.Lease!=sourceMapping ||
                !_dependencies.Mappings.TryGetValue(destinationMapping.Handle,out var output) || output.Lease!=destinationMapping || ReferenceEquals(input,output) ||
                input.OriginalAdmission is not { } ia || output.OriginalAdmission is not { } oa ||
                !Dsc1MappingSource(input,ia) || !Dsc1MappingSource(output,oa) || !Dsc1History(input) || !Dsc1History(output) ||
                input.OriginalDsc1Copies.Count>=256 || output.OriginalDsc1Copies.Count>=256)return null;
            input.OriginalDsc1Copies.EnsureCapacity(input.OriginalDsc1Copies.Count+1);output.OriginalDsc1Copies.EnsureCapacity(output.OriginalDsc1Copies.Count+1);
            var fact=new NeutralOriginalDsc1CopyFact(this,input,output,ia,oa);fact.Self=fact;
            var request=new NeutralOriginalDsc1CopyRequest(fact,source,destination);fact.Request=request;
            input.OriginalDsc1Copies.Add(fact);output.OriginalDsc1Copies.Add(fact);
            input.OriginalDsc1CopiesIssued++;output.OriginalDsc1CopiesIssued++;return request;
        }
    }
    public NeutralOriginalDsc1CopyObservation? CopyDsc1(NeutralOriginalDsc1CopyRequest request)
    {
        lock(_lifecycleGate)
        {
            var fact=FindDsc1(request);if(fact is null)return null;
            if(fact.Raw is { } retained)return ValidDsc1Raw(fact,retained)?retained:null;
            if(fact.Invoked)return null;
            // Allocate terminal facts before any byte mutation. Unknown after entry never repeats.
            var completed=new NeutralOriginalDsc1CopyObservation(request,true,false);
            var refused=new NeutralOriginalDsc1CopyObservation(request,false,true);
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
    {lock(_lifecycleGate){var fact=FindDsc1(request);return fact?.Raw is { } raw && ValidDsc1Raw(fact,raw)?raw:null;}}
    private NeutralOriginalDsc1CopyFact? FindDsc1(NeutralOriginalDsc1CopyRequest request)=>
        request is not null && request.Owner is NeutralOriginalDsc1CopyFact fact && ReferenceEquals(fact.Self,fact) &&
        ReferenceEquals(fact.Runtime,this) && ReferenceEquals(fact.Request,request)?fact:null;
    private static bool ValidDsc1Raw(NeutralOriginalDsc1CopyFact fact,NeutralOriginalDsc1CopyObservation raw)=>
        ReferenceEquals(raw.Original,raw) && ReferenceEquals(raw.Request,fact.Request) && raw.Completed!=raw.NoEffectGuaranteed;
}
