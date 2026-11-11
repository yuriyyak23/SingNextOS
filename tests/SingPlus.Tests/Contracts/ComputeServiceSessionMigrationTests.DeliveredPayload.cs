using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;
using SpyTypes = SingPlus.Tests.Platform.PlatformDsc1ComputeTests;

namespace SingPlus.Tests.Contracts;

public sealed partial class ComputeServiceSessionMigrationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GeneratedServiceRetainsOriginalDeliveredPayloadPairThroughForeignRouteLoss(bool sourceFault)
    {
        (ComponentScenario Scenario,SpyTypes.Dsc1NativeSpy Spy) Build()
        {
            SpyTypes.IOriginalDsc1Runtime runtime =DispatchProxy.Create<SpyTypes.IOriginalDsc1Runtime,SpyTypes.Dsc1NativeSpy>();
            SpyTypes.Dsc1NativeSpy spy =(SpyTypes.Dsc1NativeSpy)(object)runtime;spy.Native=new();
            return(CreateComponentScenario(new HybridCpuPlatformAuthorityProvider(runtime)),spy);
        }
        (ComponentScenario? s, SpyTypes.Dsc1NativeSpy? spy) = Build();(ComponentScenario? peer, SpyTypes.Dsc1NativeSpy? peerSpy) = Build();
        s.Source.Span.Fill(0x63);peer.Source.Span.Fill(0x7D);
        Task<OwnedBuffer<byte>> reply =ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session)).CopyAsync(s.Source,s.Destination).AsTask();
        Task<OwnedBuffer<byte>> peerReply =ComputeServiceClient.Create(new RuntimeSipClientTransport(peer.Kernel,peer.Caller,peer.Session)).CopyAsync(peer.Source,peer.Destination).AsTask();
        peerSpy.LoseCopyReply=true;peerSpy.LoseQuery=true;
        Assert.False(peer.Host.ProcessNextCopy().IsSuccess);Assert.True(peer.Host.HasPendingCopy);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        FieldInfo route =typeof(RuntimeComputeServiceHost).GetField("_pending",flags)!;
        object? original=null, pendingOwner=null;FieldInfo? payloadRoute=null;
        spy.AfterCopy=()=>
        {
            spy.AfterCopy=null;pendingOwner=route.GetValue(s.Host)!;
            payloadRoute=pendingOwner.GetType().GetField(sourceFault?"<SourceBorrow>k__BackingField":"<Destination>k__BackingField",flags)!;
            original=payloadRoute.GetValue(pendingOwner);
            payloadRoute.SetValue(pendingOwner,payloadRoute.GetValue(route.GetValue(peer.Host)!));
        };
        Assert.False(s.Host.ProcessNextCopy().IsSuccess);Assert.NotNull(original);
        ulong Used(ComponentScenario x)=>x.Kernel.QueryBudget(x.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(64UL,Used(s));Assert.Equal(64UL,Used(peer));
        Assert.False(reply.IsCompleted);Assert.False(peerReply.IsCompleted);
        Assert.Throws<InvalidOperationException>(()=>s.Source.Span.ToArray());
        Assert.Throws<InvalidOperationException>(()=>peer.Source.Span.ToArray());
        Assert.Equal(1,spy.Copies);Assert.Equal(1,peerSpy.Copies);
        payloadRoute!.SetValue(pendingOwner,original);
        KernelResult retried =s.Host.AdvancePendingCopy();Assert.True(retried.IsSuccess);Assert.False(s.Host.HasPendingCopy);
        OwnedBuffer<byte>? output =await reply.WaitAsync(TimeSpan.FromSeconds(2));Assert.All(output.Span.ToArray(),b=>Assert.Equal((byte)0x63,b));
        Assert.Equal(0UL,Used(s));Assert.Equal(64UL,Used(peer));
        peerSpy.LoseCopyReply=false;peerSpy.LoseQuery=false;
        Assert.False(peer.Host.AdvancePendingCopy().IsSuccess);Assert.False(peer.Host.HasPendingCopy); // Original failed Submit remains rejected after cleanup.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await peerReply.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0UL,Used(peer));Assert.All(peer.Source.Span.ToArray(),b=>Assert.Equal((byte)0x7D,b));
        Assert.Equal(1,spy.Copies);Assert.Equal(1,peerSpy.Copies);
        if(output is not null)Assert.True(s.Kernel.ReleaseRegion(s.Caller,output).IsSuccess);
        foreach(ComponentScenario? x in new[]{s,peer})
        {
            Assert.True(x.Kernel.CloseSession(x.Caller,x.Session).IsSuccess);
            Assert.True(x.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
            Assert.True(x.Kernel.TerminateProcess(x.Caller).IsSuccess);
        }
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);Assert.Equal(0,peerSpy.Native.ActiveOwnedRegionMappingCount);
    }
}

