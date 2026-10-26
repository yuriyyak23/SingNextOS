using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed partial class PlatformDsc1ComputeTests
{
    [Theory]
    [InlineData(true,false)] [InlineData(true,true)] [InlineData(false,false)] [InlineData(false,true)]
    public void ForeignStagingArrayCannotPublishOrConsumeOriginalDsc1Backing(bool sourceFault,bool discard)
    {
        Scenario Build(IPlatformAuthorityProvider provider,ulong id,ulong domain,out BudgetAccountHandle account)
        {
            BudgetAccountHandle captured=default;
            Scenario result =CreateScenario(provider,id,domain,32,(kernel,subject)=>
            {
                ProcessHandle admin =TestFixtures.Create(kernel,id+1,domain+1).Handle;
                CapabilityId cap =kernel.MintCapability(new(domain+1),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
                captured=kernel.AdmitProcessBudget(admin,cap,subject,"dsc1-lifetime",[new(ServiceBudgetDimension.OwnedMemoryBytes,128)]).Value!.ProcessBudget;
            },originalSlices:true);
            account=captured;return result;
        }
        IOriginalDsc1Runtime runtime =DispatchProxy.Create<IOriginalDsc1Runtime,Dsc1NativeSpy>(); Dsc1NativeSpy spy =(Dsc1NativeSpy)(object)runtime;spy.Native=new();
        IOriginalDsc1Runtime peerRuntime =DispatchProxy.Create<IOriginalDsc1Runtime,Dsc1NativeSpy>(); Dsc1NativeSpy peerSpy =(Dsc1NativeSpy)(object)peerRuntime;peerSpy.Native=new();
        Scenario x =Build(new HybridCpuPlatformAuthorityProvider(runtime),2591,25910,out BudgetAccountHandle budget);
        Scenario peer =Build(new HybridCpuPlatformAuthorityProvider(peerRuntime),2595,25950,out BudgetAccountHandle peerBudget);
        x.Source.Span.Fill(0x6B);peer.Source.Span.Fill(0x7D);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        IDictionary Inventory(RuntimeKernel kernel)=>(IDictionary)kernel.GetType().GetField("_dsc1PayloadOperations",flags)!.GetValue(kernel)!;
        object Lease(object payload,string name)=>payload.GetType().GetProperty(name)!.GetValue(payload)!;
        ulong Used(RuntimeKernel kernel,BudgetAccountHandle account)=>kernel.QueryBudget(account).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        KernelResult<PlatformDsc1CopySubmission> livePeer =SubmitWhole(peer);Assert.True(livePeer.IsSuccess,livePeer.Message);
        object peerPayload =Assert.Single(Inventory(peer.Kernel).Values.Cast<object>());
        object livePeerLease =Lease(peerPayload,"SourceLease");
        KernelResult<PlatformDsc1CopySubmission> submission =SubmitWhole(x);Assert.True(submission.IsSuccess,submission.Message);
        object payload =Assert.Single(Inventory(x.Kernel).Values.Cast<object>());
        string arrayName =sourceFault?"StagedInput":"StagedOutput";
        byte[] originalArray =(byte[])payload.GetType().GetProperty(arrayName)!.GetValue(payload)!;
        byte[] peerArray =(byte[])peerPayload.GetType().GetProperty(arrayName)!.GetValue(peerPayload)!;
        FieldInfo field =payload.GetType().GetField("<"+arrayName+">k__BackingField",flags)!;
        field.SetValue(payload,peerArray);
        KernelResult ended=default;KernelResult<PlatformDsc1CopyReceipt> observed=default;
        Exception escaped =Record.Exception(()=>{if(discard)ended=x.Kernel.TerminateProcess(x.Subject);else observed=x.Kernel.ObservePlatformDsc1Copy(x.Subject,submission.Value);});
        Assert.Null(escaped);Assert.False(discard?ended.IsSuccess:observed.IsSuccess);
        Assert.Single(Inventory(x.Kernel));Assert.Equal(128UL,Used(x.Kernel,budget));Assert.Equal(1,spy.Copies);
        Assert.Equal(128UL,Used(peer.Kernel,peerBudget));Assert.True((bool)livePeerLease.GetType().GetProperty("IsActive",flags)!.GetValue(livePeerLease)!);
        Assert.All(originalArray,b=>Assert.Equal((byte)0x6B,b));
        Assert.All(peerArray,b=>Assert.Equal((byte)0x7D,b));
        field.SetValue(payload,originalArray);
        if(!discard)
        {
            KernelResult<PlatformDsc1CopyReceipt> retry =x.Kernel.ObservePlatformDsc1Copy(x.Subject,submission.Value);Assert.True(retry.IsSuccess,retry.Message);
            Assert.All(x.Destination.Span.ToArray(),b=>Assert.Equal(retry.Value.OutputPublished?(byte)0x6B:(byte)0,b));
            Assert.Equal(64UL,Used(x.Kernel,budget));
        }
        Assert.True(x.Kernel.TerminateProcess(x.Subject).IsSuccess);Assert.Empty(Inventory(x.Kernel));
        Assert.Equal(1,spy.Copies);Assert.Equal(0UL,Used(x.Kernel,budget));
        Assert.All(originalArray,b=>Assert.Equal((byte)0,b));Assert.All(peerArray,b=>Assert.Equal((byte)0x7D,b));
        Assert.All(x.Kernel.Regions.Snapshot().Where(r=>r.Handle==x.Source.Handle||r.Handle==x.Destination.Handle),r=>Assert.Equal(RegionState.Released,r.State));
        Assert.Equal(128UL,Used(peer.Kernel,peerBudget));Assert.Equal(1,peerSpy.Copies);
        Assert.True(peer.Kernel.ObservePlatformDsc1Copy(peer.Subject,livePeer.Value).IsSuccess);
        Assert.All(peer.Destination.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        Assert.True(peer.Kernel.TerminateProcess(peer.Subject).IsSuccess);Assert.Equal(0UL,Used(peer.Kernel,peerBudget));Assert.Equal(1,peerSpy.Copies);
    }
}



