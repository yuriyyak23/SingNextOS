using SingNext.Boot.Core;
using SingPlus.Platform.HybridCpu.Boot;

namespace SingNext.CxlLab;

// Lab only: one 256 MiB device, one decoder per hop, no interleave/DPA offset.
// QMP observes registers; the installed EFI Shell performs all MMIO writes.
internal sealed class LabHdmHardware(LabVm vm, Discovery discovery) : IHdmDecoderHardware
{
    const ulong Capacity=256UL<<20;
    readonly Component[] hops=[discovery.Device,discovery.Port,discovery.Host];
    public int HopCount=>hops.Length;
    public List<object> Operations { get; }=[];
    BootFailure Guard(int hop,BootResetSnapshot reset)
    {
        vm.Qmp("query-status");
        if(reset.ResetEpoch!=vm.ObservedResetSequence||reset.ResetSequence!=vm.ObservedResetSequence)return BootFailure.ResetObserved;
        return hop<0||hop>=hops.Length||hops[hop].HdmAddress is null?BootFailure.Unsupported:BootFailure.None;
    }
    ulong Address(int hop)=>hops[hop].HdmAddress!.Value;
    void Write(ulong address,uint value)=>HdmInvestigation.WriteRegister(vm,address,value);
    public BootFailure Validate(int hop,BootMappingRequest request,BootResetSnapshot reset)
    {
        var guard=Guard(hop,reset);if(guard!=BootFailure.None)return guard;
        if(request!=new BootMappingRequest(discovery.Hpa,Capacity,0,Capacity,Capacity)||discovery.WindowBytes<Capacity)return BootFailure.Unsupported;
        uint control=vm.Read32(Address(hop)+0x20);
        return (control&0x700)!=0?BootFailure.AmbiguousState:Guard(hop,reset);
    }
    public BootFailure Stage(int hop,BootMappingRequest request,BootResetSnapshot reset)
    {
        var failure=Validate(hop,request,reset);if(failure!=BootFailure.None)return failure;
        ulong h=Address(hop);
        foreach(var (offset,value) in new (uint,uint)[]{(0x10,(uint)request.HpaBase),(0x14,(uint)(request.HpaBase>>32)),(0x18,(uint)Capacity),(0x1c,0),(0x24,0),(0x28,0),(4,2),(0x20,0x1000)})Write(h+offset,value);
        Operations.Add(new{Operation="Stage",Hop=hop,Name=hops[hop].Name});return Guard(hop,reset);
    }
    public BootFailure Commit(int hop,BootResetSnapshot reset)
    {
        var failure=Guard(hop,reset);if(failure!=BootFailure.None)return failure;
        Write(Address(hop)+0x20,0x1200);
        Operations.Add(new{Operation="Commit",Hop=hop,Name=hops[hop].Name});
        return Guard(hop,reset);
    }
    public BootResult<HdmDecoderState> Readback(int hop,BootResetSnapshot reset)
    {
        var failure=Guard(hop,reset);if(failure!=BootFailure.None)return BootResult<HdmDecoderState>.Fail(failure,"Stale lab reset snapshot.");
        ulong h=Address(hop);uint control=vm.Read32(h+0x20);
        ulong start=((ulong)vm.Read32(h+0x14)<<32)|vm.Read32(h+0x10);
        ulong size=((ulong)vm.Read32(h+0x1c)<<32)|vm.Read32(h+0x18);
        // DPA is inferred ONLY for this zero-offset whole-device single-target profile.
        bool zeroTarget=vm.Read32(h+0x24)==0&&vm.Read32(h+0x28)==0;
        failure=Guard(hop,reset);
        if(failure!=BootFailure.None||!zeroTarget||(control&0xc00)!=0x400)return BootResult<HdmDecoderState>.Fail(failure==BootFailure.None?BootFailure.ReadbackMismatch:failure,"Invalid constrained decoder state.");
        return BootResult<HdmDecoderState>.Success(new(hop,start,size,0,size,(control&0x200)!=0&&(vm.Read32(h+4)&2)!=0,(control&0x100)!=0,(control&0xff)!=0));
    }
    public BootFailure Disable(int hop,BootResetSnapshot reset)
    {
        var failure=Guard(hop,reset);if(failure!=BootFailure.None)return failure;
        ulong h=Address(hop);Write(h+0x20,vm.Read32(h+0x20)&~0xe00u);
        Operations.Add(new{Operation="Disable",Hop=hop,Name=hops[hop].Name});
        failure=Guard(hop,reset);return failure!=BootFailure.None?failure:(vm.Read32(h+0x20)&0x600)==0?BootFailure.None:BootFailure.ReadbackMismatch;
    }
}
