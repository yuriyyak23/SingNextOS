using System.Buffers.Binary;
namespace YAKSys_Hybrid_CPU.Core;
public sealed partial class NeutralDomainRuntimeFacade : INeutralOriginalMmioRegistersProvider
{
    private bool IntactMmioAccesses(NeutralMmioState source) =>
        (ulong)source.Accesses.Count==source.AccessesIssued && source.Accesses.Distinct(ReferenceEqualityComparer.Instance).Count()==source.Accesses.Count &&
        source.Accesses.All(f=>ReferenceEquals(f.Original,f) && ReferenceEquals(f.Source,source) && ReferenceEquals(f.Admission,source.OriginalAdmission) &&
            ReferenceEquals(f.Request.Original,f.Request) && f.Request.Mapping==source.Lease && f.Request.MapInvocationId==f.Admission.Request.InvocationId &&
            f.Raw is {SourceFenceMatched:true} raw && ReferenceEquals(raw.Request,f.Request) &&
            (raw.Completed && !raw.NoEffectGuaranteed && raw.Value is not null || !raw.Completed && raw.NoEffectGuaranteed && raw.Value is null));
    public NeutralOriginalMmio32Observation AccessMmio32(NeutralOriginalMmio32Request request)
    {
        lock(_lifecycleGate)
        {
            NeutralOriginalMmio32Observation Refuse(bool known)=>new(request,false,known,false,null);
            if(!ReferenceEquals(request.Original,request) || request.RuntimeIncarnation!=MappingClosureIncarnation ||
                !_dependencies.Mmio.TryGetValue(request.Mapping.Handle,out NeutralMmioState? source))return Refuse(true);
            if(source.Accesses.Any(f=>f.Request.InvocationId==request.InvocationId))return ResolveMmio32(request) ?? Refuse(false);
            if(!IntactMmioAccesses(source))return Refuse(false);
            if(source.Lease!=request.Mapping || !source.IsActive || source.OriginalClose is not null || source.OriginalAdmission is not { } admission ||
                admission.Request.InvocationId!=request.MapInvocationId || !OriginalMmioSources(source,admission,request.RuntimeIncarnation) ||
                admission.Request.Backing is not {IsActive:true} backing || source.AccessesIssued==ulong.MaxValue)return Refuse(true);
            NeutralOriginalMmio32Fact fact =new NeutralOriginalMmio32Fact(request,source);source.Accesses.Add(fact);source.AccessesIssued++;
            NeutralMmioAccess access =request.Write?NeutralMmioAccess.Write:NeutralMmioAccess.Read;
            if(!request.Write && request.Value!=0 || (source.Lease.Access&access)!=access || request.Offset%4!=0 || request.Offset<source.Lease.Range.Offset ||
                request.Offset>source.Lease.Range.Offset+source.Lease.Range.Length-4 || request.Offset>backing.ByteLength-4)
            { NeutralOriginalMmio32Observation refused =new NeutralOriginalMmio32Observation(request,true,true,false,null);fact.Raw=refused;return refused; }
            Span<byte> span =backing.Bytes.Slice(checked((int)request.Offset),4);
            if(request.Write)BinaryPrimitives.WriteUInt32LittleEndian(span,request.Value);
            NeutralOriginalMmio32Observation raw =new NeutralOriginalMmio32Observation(request,true,false,true,BinaryPrimitives.ReadUInt32LittleEndian(span));
            fact.Raw=raw;return raw;
        }
    }
    public NeutralOriginalMmio32Observation? ResolveMmio32(NeutralOriginalMmio32Request request)
    {
        lock(_lifecycleGate)
        {
            if(!ReferenceEquals(request.Original,request) || request.RuntimeIncarnation!=MappingClosureIncarnation ||
                !_dependencies.Mmio.TryGetValue(request.Mapping.Handle,out NeutralMmioState? source) || source.Lease!=request.Mapping || !IntactMmioAccesses(source) ||
                source.OriginalAdmission is not { } admission || !OriginalMmioSources(source,admission,request.RuntimeIncarnation,false))return null;
            NeutralOriginalMmio32Fact[] facts =source.Accesses.Where(f=>ReferenceEquals(f.Request,request)).ToArray();
            return facts.Length==1?facts[0].Raw:null;
        }
    }
}
