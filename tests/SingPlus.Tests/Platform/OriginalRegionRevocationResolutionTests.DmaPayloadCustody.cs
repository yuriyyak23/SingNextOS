using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;

public sealed partial class OriginalRegionRevocationResolutionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EqualTupleForeignDmaPayloadCannotReleaseAnotherKernelsReservedViews(bool late)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x =CopyKernel(true); (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) foreign =CopyKernel(true);
        Assert.Equal(x.Source,foreign.Source);Assert.Equal(x.Destination,foreign.Destination);
        int copies =0; int foreignCopies =0; int closes =0;
        foreach((RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) item in new[]{x,foreign})
        {
            PlatformDmaPrepareEvidence sp =item.Kernel.PreparePlatformDmaForDevice(item.Owner,item.Source).Value!;
            PlatformDmaPrepareEvidence dp =item.Kernel.PreparePlatformDmaForDevice(item.DestinationOwner,item.Destination).Value!;
            item.Spy.AfterOperation=name=>
            {
                if(name=="CopyDma"){if(ReferenceEquals(item.Kernel,x.Kernel))copies++;else foreignCopies++;throw new IOException("Original copy reply lost");}
                if(name=="ResolveDmaCopy")throw new IOException("Original query lost");
            };
            Assert.False(item.Kernel.ExecuteV6ProviderDmaDataMotion(item.Owner,item.DestinationOwner,item.Input,item.Output,item.Plan,item.SourceMap,item.Source,sp,item.DestinationMap,item.Destination,dp,()=>3,()=>5).IsSuccess);
        }
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        IDictionary routes =(IDictionary)x.Kernel.GetType().GetField("_dmaCopyPayloads",flags)!.GetValue(x.Kernel)!;
        IDictionary foreignRoutes =(IDictionary)foreign.Kernel.GetType().GetField("_dmaCopyPayloads",flags)!.GetValue(foreign.Kernel)!;
        object original =routes[x.Source.GrantId]!; object other =foreignRoutes[foreign.Source.GrantId]!;
        Assert.NotSame(original,other);
        Assert.Throws<InvalidOperationException>(()=>foreign.Input.Span.ToArray());
        bool swapped =false;
        if(!late)routes[x.Source.GrantId]=other;
        x.Spy.AfterOperation=name=>{if(name=="CopyDma")copies++;if(name=="CloseDmaGrant"){closes++;if(late&&!swapped){swapped=true;routes[x.Source.GrantId]=other;}}};
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(late?1:2,x.Native.ActiveDmaGrantCount);Assert.Equal(late?1:0,closes);
        Assert.False((bool)other.GetType().GetProperty("Consumed",flags)!.GetValue(other)!);
        Assert.Throws<InvalidOperationException>(()=>foreign.Input.Span.ToArray());
        Assert.Equal(2,foreign.Native.ActiveDmaGrantCount);Assert.Equal(1,foreignCopies);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        routes[x.Source.GrantId]=original;
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.All(x.Output.Span.ToArray(),b=>Assert.Equal((byte)0x5A,b));
        Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.Equal(1,copies);Assert.Equal(2,closes);Assert.Equal(0,x.Native.ActiveDmaGrantCount);
        Assert.Equal(2,foreign.Native.ActiveDmaGrantCount);Assert.Throws<InvalidOperationException>(()=>foreign.Output.Span.ToArray());
        Assert.All(foreign.Kernel.Regions.Snapshot().Where(r=>r.Handle==foreign.Input.Handle||r.Handle==foreign.Output.Handle),r=>Assert.Equal(RegionState.Owned,r.State));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalRuntimeLeaseCopyAndPartialDisposalPreserveDmaBackingUntilExactRetry(bool partial)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x =CopyKernel(true); (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) foreign =CopyKernel(true); int copies =0; int closes =0;
        foreach((RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) item in new[]{x,foreign})
        {
            PlatformDmaPrepareEvidence sp =item.Kernel.PreparePlatformDmaForDevice(item.Owner,item.Source).Value!;
            PlatformDmaPrepareEvidence dp =item.Kernel.PreparePlatformDmaForDevice(item.DestinationOwner,item.Destination).Value!;
            item.Spy.AfterOperation=name=>{if(name=="CopyDma"){if(ReferenceEquals(item.Kernel,x.Kernel))copies++;throw new IOException("Lost byte reply");}if(name=="ResolveDmaCopy")throw new IOException("Lost byte query");};
            Assert.False(item.Kernel.ExecuteV6ProviderDmaDataMotion(item.Owner,item.DestinationOwner,item.Input,item.Output,item.Plan,item.SourceMap,item.Source,sp,item.DestinationMap,item.Destination,dp,()=>3,()=>5).IsSuccess);
        }
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object Payload(RuntimeKernel kernel)=>((IDictionary)kernel.GetType().GetField("_dmaCopyPayloads",flags)!.GetValue(kernel)!).Values.Cast<object>().Single();
        object payload =Payload(x.Kernel); object other =Payload(foreign.Kernel);
        object write =payload.GetType().GetField("_write",flags)!.GetValue(payload)!;
        object foreignWrite =other.GetType().GetField("_write",flags)!.GetValue(other)!;
        x.Spy.AfterOperation=name=>{if(name=="CopyDma")copies++;if(name=="CloseDmaGrant")closes++;};
        if(partial)
        {
            object storage =write.GetType().GetField("_storage",flags)!.GetValue(write)!;
            FieldInfo route =storage.GetType().GetField("_runtimeReservation",flags)!;
            object original =route.GetValue(storage)!;
            object foreignLifetime =foreignWrite.GetType().GetField("_lifetime",flags)!.GetValue(foreignWrite)!;
            route.SetValue(storage,foreignLifetime);
            Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
            Assert.Equal(2,closes);Assert.Equal(0,x.Native.ActiveDmaGrantCount);
            Assert.False((bool)payload.GetType().GetProperty("Consumed",flags)!.GetValue(payload)!);
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            route.SetValue(storage,original); // Restore exact original lifetime; no replacement reservation.
        }
        else
        {
            IDisposable copied =(IDisposable)typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(write,null)!;
            Assert.Throws<InvalidOperationException>(()=>copied.Dispose());
            Assert.Throws<InvalidOperationException>(()=>x.Output.Span.ToArray());
        }
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.All(x.Output.Span.ToArray(),b=>Assert.Equal((byte)0x5A,b));
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.Equal(1,copies);Assert.Equal(2,closes);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(2,foreign.Native.ActiveDmaGrantCount);Assert.False((bool)other.GetType().GetProperty("Consumed",flags)!.GetValue(other)!);
        Assert.Throws<InvalidOperationException>(()=>foreign.Input.Span.ToArray());Assert.Throws<InvalidOperationException>(()=>foreign.Output.Span.ToArray());
    }    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void NativeCopySpanAcquisitionRequiresExactLiveStorageReservation(bool destination,bool foreignRoute)
    {
        (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) x =CopyKernel(true); (RuntimeKernel Kernel, RuntimeSpy Spy, NeutralDomainRuntimeFacade Native, HybridCpuPlatformAuthorityProvider Provider, ProcessHandle Owner, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, PlatformOwnedRegionSliceMapping SourceMap, PlatformOwnedRegionSliceMapping DestinationMap, PlatformDmaGrant Source, PlatformDmaGrant Destination, DataMotionPlanV1 Plan, BudgetAccountHandle Budget, ProcessHandle DestinationOwner, BudgetAccountHandle DestinationBudget) foreign =CopyKernel(true);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object Payload(RuntimeKernel kernel)=>((IDictionary)kernel.GetType().GetField("_dmaCopyPayloads",flags)!.GetValue(kernel)!).Values.Cast<object>().Single();
        PlatformDmaPrepareEvidence fs =foreign.Kernel.PreparePlatformDmaForDevice(foreign.Owner,foreign.Source).Value!;
        PlatformDmaPrepareEvidence fd =foreign.Kernel.PreparePlatformDmaForDevice(foreign.DestinationOwner,foreign.Destination).Value!;
        foreign.Spy.AfterOperation=name=>{if(name is "CopyDma" or "ResolveDmaCopy")throw new IOException("Foreign original outcome lost");};
        Assert.False(foreign.Kernel.ExecuteV6ProviderDmaDataMotion(foreign.Owner,foreign.DestinationOwner,foreign.Input,foreign.Output,foreign.Plan,foreign.SourceMap,foreign.Source,fs,foreign.DestinationMap,foreign.Destination,fd,()=>3,()=>5).IsSuccess);
        object foreignLease =Payload(foreign.Kernel).GetType().GetField(destination?"_write":"_read",flags)!.GetValue(Payload(foreign.Kernel))!;
        object foreignLifetime =foreignLease.GetType().GetField("_lifetime",flags)!.GetValue(foreignLease)!;
        PlatformDmaPrepareEvidence sp =x.Kernel.PreparePlatformDmaForDevice(x.Owner,x.Source).Value!; PlatformDmaPrepareEvidence dp =x.Kernel.PreparePlatformDmaForDevice(x.DestinationOwner,x.Destination).Value!;
        object? storage=null,original=null;FieldInfo? route=null;byte[]? output=null; int copies =0;bool cpuDenied=false,reserveDenied=false;
        x.Spy.BeforeOperation=name=>
        {
            if(name!="CopyDma")return;
            copies++;
            object payload =Payload(x.Kernel);
            object lease =payload.GetType().GetField(destination?"_write":"_read",flags)!.GetValue(payload)!;
            storage=lease.GetType().GetField("_storage",flags)!.GetValue(lease)!;
            route=storage.GetType().GetField("_runtimeReservation",flags)!;original=route.GetValue(storage);
            object write =payload.GetType().GetField("_write",flags)!.GetValue(payload)!;
            object outputStorage =write.GetType().GetField("_storage",flags)!.GetValue(write)!;
            output=(byte[])outputStorage.GetType().GetProperty("Data")!.GetValue(outputStorage)!;
            route.SetValue(storage,foreignRoute?foreignLifetime:null);
            OwnedBuffer<byte> target =destination?x.Output:x.Input;
            try{target.Span.ToArray();}catch(InvalidOperationException){cpuDenied=true;}
            MethodInfo reserve =target.GetType().GetMethod("ReserveForRuntime",flags)!;
            object access =Enum.ToObject(reserve.GetParameters()[0].ParameterType,destination?2:1);
            try{((IDisposable)reserve.Invoke(target,[access])!).Dispose();}
            catch(TargetInvocationException failure)when(failure.InnerException is InvalidOperationException){reserveDenied=true;}
        };
        Exception escaped =Record.Exception(()=>Assert.False(x.Kernel.ExecuteV6ProviderDmaDataMotion(x.Owner,x.DestinationOwner,x.Input,x.Output,x.Plan,x.SourceMap,x.Source,sp,x.DestinationMap,x.Destination,dp,()=>3,()=>5).IsSuccess));
        Assert.NotNull(output);Assert.All(output!,b=>Assert.Equal((byte)0,b));Assert.Null(escaped);Assert.Equal(1,copies);Assert.True(cpuDenied);Assert.True(reserveDenied);
        x.Spy.BeforeOperation=null;
        Assert.False(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(64UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        route!.SetValue(storage,original);
        Assert.True(x.Kernel.TerminateProcess(x.Owner).IsSuccess);
        Assert.All(x.Output.Span.ToArray(),b=>Assert.Equal((byte)0,b));
        Assert.True(x.Kernel.TerminateProcess(x.DestinationOwner).IsSuccess);
        Assert.Equal(1,copies);Assert.Equal(0,x.Native.ActiveDmaGrantCount);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.Budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL,x.Kernel.QueryBudget(x.DestinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(2,foreign.Native.ActiveDmaGrantCount);Assert.Throws<InvalidOperationException>(()=>foreign.Input.Span.ToArray());Assert.Throws<InvalidOperationException>(()=>foreign.Output.Span.ToArray());
    }}
