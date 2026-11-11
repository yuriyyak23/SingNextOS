using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;
public sealed class OriginalBindOutcomeTests
{
    [Theory]
    [InlineData("exact")] [InlineData("copy")] [InlineData("loss")]
    public void OriginalBindQueryNeverBindsAndRejectsHistoryLoss(string fault)
    {
        NeutralDomainRuntimeFacade runtime =new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest request =new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        Assert.Null(runtime.ResolveOriginalDomainBind(request));
        NeutralOriginalDomainBindObservation raw =runtime.Bind(request);Assert.True(raw.Observation.IsBound);
        Assert.Equal(raw,runtime.ResolveOriginalDomainBind(request));
        Assert.False(runtime.Bind(request).Observation.IsBound);Assert.Equal(1,runtime.ActiveBindingCount);
        Assert.Null(runtime.ResolveOriginalDomainBind(request with{InvocationId=Guid.NewGuid()}));
        Assert.Null(new NeutralDomainRuntimeFacade().ResolveOriginalDomainBind(request));
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        IList history =(IList)runtime.GetType().GetField("_originalBindInvocations",flags)!.GetValue(runtime)!;
        if(fault=="copy")history[0]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(history[0],null);
        if(fault=="loss")history.Clear();
        Assert.Equal(fault=="exact",runtime.ResolveOriginalDomainBind(request) is not null);
        Assert.False(runtime.Bind(request).Observation.IsBound);Assert.Equal(1,runtime.ActiveBindingCount);
    }
}
