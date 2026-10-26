using SingNext.Boot.Core;
using SingPlus.Platform.HybridCpu.Boot;

namespace SingNext.CxlLab;

// Explicit transport fault injection, after a real successful three-hop commit.
internal sealed class FaultInjectedHdmHardware(LabHdmHardware inner, LabVm vm, bool resetAfterCommit, Action committedWitness) : IHdmDecoderHardware
{
    public int HopCount=>inner.HopCount;
    public bool Injected { get; private set; }
    public ulong ResetBefore { get; private set; }
    public ulong ResetAfter { get; private set; }
    public BootFailure Validate(int hop,BootMappingRequest request,BootResetSnapshot reset)=>inner.Validate(hop,request,reset);
    public BootFailure Stage(int hop,BootMappingRequest request,BootResetSnapshot reset)=>inner.Stage(hop,request,reset);
    public BootFailure Commit(int hop,BootResetSnapshot reset)
    {
        var result=inner.Commit(hop,reset);
        if(result!=BootFailure.None||hop!=HopCount-1||Injected)return result;
        var read=inner.Readback(hop,reset);
        if(!read.IsSuccess)return read.Failure;
        committedWitness();Injected=true;
        if(!resetAfterCommit)return BootFailure.PartialCommit;
        ResetBefore=vm.ObservedResetSequence;
        vm.WarmResetToShell();
        ResetAfter=vm.ObservedResetSequence;
        return BootFailure.ResetObserved;
    }
    public BootResult<HdmDecoderState> Readback(int hop,BootResetSnapshot reset)=>inner.Readback(hop,reset);
    public BootFailure Disable(int hop,BootResetSnapshot reset)=>inner.Disable(hop,reset);
}
