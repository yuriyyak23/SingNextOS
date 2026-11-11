using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;
using SingPlus.Tests.Platform;

namespace SingPlus.Tests.Contracts;

public sealed partial class ComputeServiceSessionMigrationTests
{
    [Theory]
    [InlineData(false,false,false)] [InlineData(true,false,false)] [InlineData(false,true,false)] [InlineData(false,true,true)]
    public async Task ActualSoftwareReturnInitialProcessRefusalClosesOriginalWithoutReservation(bool exitingCaller,bool holdUse,bool preparationRefusal)
    {
        PlatformDsc1ComputeTests.IOriginalDsc1Runtime proxy =global::System.Reflection.DispatchProxy.Create<SingPlus.Tests.Platform.PlatformDsc1ComputeTests.IOriginalDsc1Runtime,
            SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy>();
        PlatformDsc1ComputeTests.Dsc1NativeSpy spy =(SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy)(object)proxy;spy.Native=new();
        ComponentScenario s =CreateComponentScenario(new SingPlus.Platform.HybridCpu.HybridCpuPlatformAuthorityProvider(proxy));
        SingProcess source =s.Kernel.Processes.Resolve(s.Service).Value!;
        SingProcess blocked =s.Kernel.Processes.Resolve(exitingCaller?s.Caller:s.Service).Value!;
        ProcessState prior =blocked.State;
        Task<OwnedBuffer<byte>> reply =ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session)).CopyAsync(s.Source,s.Destination).AsTask();
        RegionHandle old =s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==s.Destination.Handle.RegionId).Handle;
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) charge =Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(old));
        OwnedBuffer<byte> independent =s.Kernel.AllocateBuffer<byte>(s.Caller,4).Value!;independent.Span.Fill(99);
        RegionUseDescriptor? use=null;
        SingPlus.Sip.PreparedOwnedTransfer? independentPreparation=null;
        s.Kernel.SessionInvocationSettlementReservedHook=()=>
        {
            s.Kernel.SessionInvocationSettlementReservedHook=null;
            if(!preparationRefusal)blocked.SetState(ProcessState.Exiting);
            else
                independentPreparation=s.Kernel.Regions.CaptureTransferPayload(old,new RegionOwner(source.DomainId,s.Service.Generation)).Value!.PrepareTransferForRuntime();
            if(holdUse)use=s.Kernel.Regions.AcquireUse(old,new RegionOwner(source.DomainId,s.Service.Generation),RegionUseMode.ExclusiveWrite,new(0,16)).Value!;
        };
        Assert.Equal(preparationRefusal ? KernelError.RegionUseConflict : holdUse ? KernelError.InvalidTransition : KernelError.PlatformFaulted,s.Host.ProcessNextCopy().Error);
        if(holdUse)
        {
            Assert.False(reply.IsCompleted);Assert.True(s.Host.HasPendingCopy);
            BindingFlags flags =global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
            EndpointSessionInvocationRegistry invocations =(EndpointSessionInvocationRegistry)typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(s.Kernel)!;
            IDictionary records =(global::System.Collections.IDictionary)typeof(EndpointSessionInvocationRegistry).GetField("_ownedRecords",flags)!.GetValue(invocations)!;
            object record =records[(s.Session.SessionId,s.Session.Generation,new EndpointSessionInvocationId(1))]!;
            ResponseRegistry.OriginalPublication publication =(ResponseRegistry.OriginalPublication)record.GetType().GetProperty("OriginalResponse")!.GetValue(record)!;
            PropertyInfo property =publication.Completion.GetType().GetProperty("OriginalInitialOwnership")!;
            ResponseRegistry.InitialOwnership original =(ResponseRegistry.InitialOwnership)property.GetValue(publication.Completion)!;
            Assert.True(s.Kernel.Regions.ReleaseUse(use!.Handle,use.Principal).IsSuccess);
            independentPreparation?.Dispose(); // Independent original custody closure, not failure compensation.
            property.SetValue(publication.Completion,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
            Assert.False(s.Host.AdvancePendingCopy().IsSuccess);Assert.False(reply.IsCompleted);
            Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
            property.SetValue(publication.Completion,original);
            Assert.Equal(KernelError.PlatformFaulted,s.Host.AdvancePendingCopy().Error);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reply.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1,spy.Copies);Assert.False(s.Host.HasPendingCopy);
        Assert.Equal(RegionState.Released,s.Kernel.Regions.Snapshot().Single(r=>r.Handle==old).State);
        Assert.Empty(s.Kernel.Budgets.OriginalRegionCharges(new(old.RegionId,new(old.Generation.Value+1))));
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal((byte)99,independent.Span[0]);Assert.Equal(16,s.Source.Span.Length);
        blocked.SetState(prior);
        Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualSoftwareReturnPreRegionRefusalRetainsOwnOutcomeAndCleansWithoutRepeat(bool changeRoute)
    {
        PlatformDsc1ComputeTests.IOriginalDsc1Runtime proxy =global::System.Reflection.DispatchProxy.Create<SingPlus.Tests.Platform.PlatformDsc1ComputeTests.IOriginalDsc1Runtime,
            SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy>();
        PlatformDsc1ComputeTests.Dsc1NativeSpy spy =(SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy)(object)proxy;spy.Native=new();
        ComponentScenario s =CreateComponentScenario(new SingPlus.Platform.HybridCpu.HybridCpuPlatformAuthorityProvider(proxy));
        BindingFlags flags =global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
        IComputeService client =ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session));
        Task<OwnedBuffer<byte>> reply =client.CopyAsync(s.Source,s.Destination).AsTask();
        RegionHandle old =s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==s.Destination.Handle.RegionId).Handle;
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) charge =Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(old));
        OwnedBuffer<byte> independent =s.Kernel.AllocateBuffer<byte>(s.Caller,4).Value!;independent.Span.Fill(99);
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) foreignCharge =Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(independent.Handle));
        IDictionary routes =(global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_regionBudgetReservations",flags)!.GetValue(s.Kernel)!;
        ResponseRegistry responses =(ResponseRegistry)typeof(RuntimeKernel).GetProperty("Responses",flags)!.GetValue(s.Kernel)!;
        MethodInfo core =typeof(RuntimeKernel).GetMethod("TransferRegionForIpcCore",flags)!;
        EndpointSessionRegistry.Record session =s.Kernel.EndpointSessions.Resolve(s.Session,s.Caller).Value!;
        SingProcess service =s.Kernel.Processes.Resolve(s.Service).Value!;
        SingProcess caller =s.Kernel.Processes.Resolve(s.Caller).Value!;
        ITransferableOwnedPayload input =s.Kernel.Regions.CaptureTransferPayload(old,new RegionOwner(service.DomainId,s.Service.Generation)).Value!;
        ResponseRegistry.TransferProducer? fact=null;
        int attempts =0;
        s.Kernel.SessionInvocationSettlementReservedHook=()=>
        {
            s.Kernel.SessionInvocationSettlementReservedHook=null;
            ResponseRegistry.OriginalPublication original =responses.CapturePublication(s.Service,session.ServiceEndpoint,1).Value!;
            KernelResult<ResponseEnvelope> result =responses.Publish(service,caller,session.ServiceEndpoint,1,input,
                (source,target,region,admit,noEffect,prepared)=>
                {
                    attempts++;
                    KernelResult Admission()
                    {
                        if(changeRoute)routes[old]=(foreignCharge.Owner,foreignCharge.Reservation);
                        return changeRoute ? admit!() : KernelResult.Fail(KernelError.PlatformDenied,"Exact preparation refusal");
                    }
                    KernelResult<RegionHandle> moved =(KernelResult<RegionHandle>)core.Invoke(s.Kernel,[source,target,region,(Func<KernelResult>)Admission,null,noEffect,prepared,null,original])!;
                    responses.RetainTransferComposition(original,prepared,moved);
                    fact=(ResponseRegistry.TransferProducer)original.Completion.GetType().GetProperty("OriginalTransferProducer")!.GetValue(original.Completion)!;
                    return moved;
                });
            Assert.False(result.IsSuccess);
            throw new IOException("Original pre-Region result acknowledgement lost");
        };
        Assert.Throws<IOException>(()=>s.Host.ProcessNextCopy());
        Assert.NotNull(fact);Assert.Null(fact.Raw);Assert.NotNull(fact.PreRegionRefusal);
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(fact.TargetReservation!.Value).Value!.State);
        Assert.Equal(old,s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==old.RegionId).Handle);
        KernelError? refusal =fact.PreRegionRefusal;fact.PreRegionRefusal=null;
        Assert.False(s.Host.AdvancePendingCopy().IsSuccess);Assert.False(reply.IsCompleted);
        Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        fact.PreRegionRefusal=refusal;
        Assert.Equal(KernelError.PlatformFaulted,s.Host.AdvancePendingCopy().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reply.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1,spy.Copies);Assert.Equal(1,attempts);Assert.False(s.Host.HasPendingCopy);
        Assert.Equal(RegionState.Released,s.Kernel.Regions.Snapshot().Single(r=>r.Handle==old).State);
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal((byte)99,independent.Span[0]);
        if(changeRoute)Assert.Equal((foreignCharge.Owner,foreignCharge.Reservation),routes[old]);
        Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
    }

    [Fact]
    public async Task ActualSoftwareCopyReturningBudgetRefusalClosesSourceWithoutReturnTransfer()
    {
        PlatformDsc1ComputeTests.IOriginalDsc1Runtime proxy =global::System.Reflection.DispatchProxy.Create<SingPlus.Tests.Platform.PlatformDsc1ComputeTests.IOriginalDsc1Runtime,
            SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy>();
        PlatformDsc1ComputeTests.Dsc1NativeSpy spy =(SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy)(object)proxy;spy.Native=new();
        ComponentScenario s =CreateComponentScenario(new SingPlus.Platform.HybridCpu.HybridCpuPlatformAuthorityProvider(proxy));
        BudgetAccountHandle budget =s.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        s.Source.Span.Fill(0x72);
        Task<OwnedBuffer<byte>> reply =ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session)).CopyAsync(s.Source,s.Destination).AsTask();
        RegionHandle old =s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==s.Destination.Handle.RegionId).Handle;
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) charge =Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(old));
        OwnedBuffer<byte> independent =s.Kernel.AllocateBuffer<byte>(s.Caller,48).Value!;independent.Span.Fill(99);
        Assert.Equal(KernelError.PlatformFaulted,s.Host.ProcessNextCopy().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reply.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1,spy.Copies);Assert.False(s.Host.HasPendingCopy);
        Assert.Equal(RegionState.Released,s.Kernel.Regions.Snapshot().Single(r=>r.Handle==old).State);
        Assert.Equal(old,s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==old.RegionId).Handle);
        Assert.Empty(s.Kernel.Budgets.OriginalRegionCharges(new(old.RegionId,new(old.Generation.Value+1))));
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal(0UL,s.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal((byte)99,independent.Span[0]);Assert.Equal(16,s.Source.Span.Length);
        Assert.False(s.Host.AdvancePendingCopy().IsSuccess);Assert.Equal(1,spy.Copies);
        Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
        Assert.True(s.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);Assert.Equal(0,spy.Native.ActiveBindingCount);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)]
    public async Task ActualSoftwareCopyFailedOwnershipResponseClosesOriginalQuantitiesWithoutRepeat(int failure)
    {
        PlatformDsc1ComputeTests.IOriginalDsc1Runtime proxy =global::System.Reflection.DispatchProxy.Create<SingPlus.Tests.Platform.PlatformDsc1ComputeTests.IOriginalDsc1Runtime,
            SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy>();
        PlatformDsc1ComputeTests.Dsc1NativeSpy spy =(SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy)(object)proxy;spy.Native=new();
        ComponentScenario s =CreateComponentScenario(new SingPlus.Platform.HybridCpu.HybridCpuPlatformAuthorityProvider(proxy));
        BudgetAccountHandle serviceBudget =s.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        BindingFlags flags =global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
        ProcessHandle foreign =TestFixtures.Create(s.Kernel,933,9330).Handle;
        OwnedBuffer<byte> independent =s.Kernel.AllocateBuffer<byte>(foreign,4).Value!;independent.Span.Fill(99);
        s.Source.Span.Fill(0x72);
        IComputeService client =ComputeServiceClient.Create(new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session));
        Task<OwnedBuffer<byte>> reply =client.CopyAsync(s.Source,s.Destination).AsTask();
        RegionHandle old =s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==s.Destination.Handle.RegionId).Handle;
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) charge =Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(old));
        Assert.Equal(s.Service,charge.Owner);
        RegionHandle next =new RegionHandle(old.RegionId,new(old.Generation.Value+1));
        IDictionary routes =(global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_regionBudgetReservations",flags)!.GetValue(s.Kernel)!;
        routes[next]=(charge.Owner,charge.Reservation);
        ResponseRegistry.OriginalPublication? terminalPublication = null;
        object? terminalInput = null;
        if(failure==1)spy.AfterCopy=()=> { spy.AfterCopy=null;routes.Remove(old); };
        if(failure>=2)
        {
            SingProcess service =s.Kernel.Processes.Resolve(s.Service).Value!;
            ITransferableOwnedPayload input =s.Kernel.Regions.CaptureTransferPayload(old,new RegionOwner(service.DomainId,s.Service.Generation)).Value!;
            EndpointSessionRegistry.Record session =s.Kernel.EndpointSessions.Resolve(s.Session,s.Caller).Value!;
            s.Kernel.SessionInvocationSettlementReservedHook=()=>
            {
                s.Kernel.SessionInvocationSettlementReservedHook=null;
                if(failure>=5)
                {
                    if(failure>=6)routes.Remove(next);
                    ResponseRegistry responses =(ResponseRegistry)typeof(RuntimeKernel).GetProperty("Responses",flags)!.GetValue(s.Kernel)!;
                    ResponseRegistry.OriginalPublication original =responses.CapturePublication(s.Service,session.ServiceEndpoint,1).Value!;
                    MethodInfo core =typeof(RuntimeKernel).GetMethod("TransferRegionForIpcCore",flags)!;
                    Assert.Throws<IOException>(()=>responses.Publish(service,s.Kernel.Processes.Resolve(s.Caller).Value!,session.ServiceEndpoint,1,input,
                        (source,target,region,admit,noEffect,prepared)=>
                        {
                            KernelResult<RegionHandle> result =(KernelResult<RegionHandle>)core.Invoke(s.Kernel,[source,target,region,admit,null,noEffect,prepared,null,original])!;
                            Assert.Equal(failure>=6,result.IsSuccess);
                            if(failure==7)responses.RetainTransferComposition(original,prepared,result);
                            throw new IOException("Original quantitative outcome lost before final composition retention");
                        }));
                    throw new IOException("Outer original settlement never started");
                }
                Assert.Equal(KernelError.PlatformFaulted,s.Kernel.PublishResponse(s.Service,session.ServiceEndpoint,1,input).Error);
                throw new IOException("Original returning response effect retained across reentry");
            };
            Assert.Throws<IOException>(()=>s.Host.ProcessNextCopy());
            Assert.True(s.Host.HasPendingCopy);
            Assert.Equal(1,spy.Copies);
            EndpointSessionInvocationRegistry invocations =(EndpointSessionInvocationRegistry)typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(s.Kernel)!;
            IDictionary owned =(global::System.Collections.IDictionary)typeof(EndpointSessionInvocationRegistry).GetField("_ownedRecords",flags)!.GetValue(invocations)!;
            object invocationRecord =owned[(s.Session.SessionId,s.Session.Generation,new EndpointSessionInvocationId(1))]!;
            ResponseRegistry.OriginalPublication publication =(ResponseRegistry.OriginalPublication)invocationRecord.GetType().GetProperty("OriginalResponse")!.GetValue(invocationRecord)!;
            ResponseRegistry.TransferProducer producer =(ResponseRegistry.TransferProducer)publication.Completion.GetType().GetProperty("OriginalTransferProducer")!.GetValue(publication.Completion)!;
            terminalPublication = publication;
            terminalInput = producer.Prepared.Source;
            Assert.Equal(next,producer.Raw!.Value.Value);
            if(failure==8)
                publication.Completion.GetType().GetProperty("FailedTransferCleanupCompleted")!.SetValue(publication.Completion,true);
            if(failure>=5)
            {
                if(failure==7)Assert.True(producer.Composition!.Value.IsSuccess);else Assert.Null(producer.Composition);
                Assert.Equal(failure>=6 ? BudgetReservationState.Released : BudgetReservationState.Active,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
                if(failure==7)
                {
                    KernelResult<RegionHandle>? originalOutcome =producer.Composition;
                    producer.Composition=KernelResult<RegionHandle>.Ok(independent.Handle);
                    Assert.False(s.Host.AdvancePendingCopy().IsSuccess);Assert.False(reply.IsCompleted);
                    Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(producer.TargetReservation!.Value).Value!.State);
                    producer.Composition=originalOutcome;
                }
            }
            if(failure==2)
            {
                KernelResult<RegionHandle>? saved =producer.Raw;producer.Raw=null;
                Assert.False(s.Host.AdvancePendingCopy().IsSuccess);
                Assert.True(s.Host.HasPendingCopy);Assert.False(reply.IsCompleted);
                Assert.Equal(BudgetReservationState.Quarantined,s.Kernel.QueryBudget(producer.TargetReservation!.Value).Value!.State);
                producer.Raw=saved;
            }
            if(failure==3)
            {
                RegionUseDescriptor use =s.Kernel.Regions.AcquireUse(next,producer.TargetOwner,RegionUseMode.ExclusiveWrite,new(0,16)).Value!;
                Assert.False(s.Host.AdvancePendingCopy().IsSuccess);
                Assert.True(s.Host.HasPendingCopy);Assert.False(reply.IsCompleted);
                Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
                Assert.Equal(BudgetReservationState.Quarantined,s.Kernel.QueryBudget(producer.TargetReservation!.Value).Value!.State);
                Assert.True(s.Kernel.Regions.ReleaseUse(use.Handle,producer.TargetOwner).IsSuccess);
            }
            if(failure==4)
            {
                PropertyInfo property =publication.Completion.GetType().GetProperty("OriginalTransferProducer")!;
                object clone =typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(producer,null)!;
                property.SetValue(publication.Completion,clone);
                Assert.False(s.Host.AdvancePendingCopy().IsSuccess);
                Assert.True(s.Host.HasPendingCopy);Assert.False(reply.IsCompleted);
                Assert.Equal(BudgetReservationState.Quarantined,s.Kernel.QueryBudget(producer.TargetReservation!.Value).Value!.State);
                property.SetValue(publication.Completion,producer);
            }
        }
        if(failure>=9)
        {
            if(failure==14)_=s.Kernel.FaultComponent(new ComponentIdentity("compute-session-test"));
            else if(failure==13)_=s.Kernel.TerminateProcess(s.Caller);
            else Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
            Assert.Equal(1,spy.Copies);
            Assert.Equal((byte)99,independent.Span[0]);
            if(failure is >=10 and <=12)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reply.WaitAsync(TimeSpan.FromSeconds(5)));
                ResponseRegistry.OriginalPublication original =terminalPublication!;
                ResponseRegistry.TransferProducer producer =(ResponseRegistry.TransferProducer)original.Completion.GetType().GetProperty("OriginalTransferProducer")!.GetValue(original.Completion)!;
                PropertyInfo receiptProperty =original.Completion.GetType().GetProperty("OriginalChannelCancellation")!;
                object receipt =receiptProperty.GetValue(original.Completion)!;
                RegionUseHandle? blocked =null;
                if(failure==12)blocked=s.Kernel.Regions.AcquireUse(next,producer.TargetOwner,RegionUseMode.ExclusiveWrite,new(0,16)).Value!.Handle;
                else receiptProperty.SetValue(original.Completion,failure==10 ? typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(receipt,null) : null);
                Assert.False(s.Host.AdvancePendingCopy().IsSuccess);
                Assert.True(s.Host.HasPendingCopy);
                Assert.Equal(RegionState.Owned,s.Kernel.Regions.Snapshot().Single(r=>r.Handle==next).State);
                Assert.Equal(BudgetReservationState.Active,s.Kernel.QueryBudget(producer.TargetReservation!.Value).Value!.State);
                Assert.Equal((byte)99,independent.Span[0]);Assert.Equal(1,spy.Copies);
                if(blocked is { } use)Assert.True(s.Kernel.Regions.ReleaseUse(use,producer.TargetOwner).IsSuccess);
                else receiptProperty.SetValue(original.Completion,receipt);
            }
        }
        if(failure==13)
        {
            if(s.Host.HasPendingCopy)Assert.Equal(KernelError.PlatformFaulted,s.Host.AdvancePendingCopy().Error);
            if(s.Kernel.Processes.Resolve(s.Caller).IsSuccess)Assert.True(s.Kernel.TerminateProcess(s.Caller).IsSuccess);
            Assert.Equal(ProcessState.Exited,s.Kernel.Processes.ResolveTerminalState(s.Caller).Value);
        }
        else Assert.Equal(KernelError.PlatformFaulted,failure>=2 ? s.Host.AdvancePendingCopy().Error : s.Host.ProcessNextCopy().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reply.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1,spy.Copies);
        Assert.False(s.Host.HasPendingCopy);
        if(failure==8)
        {
            ResponseRegistry responses =(ResponseRegistry)typeof(RuntimeKernel).GetProperty("Responses",flags)!.GetValue(s.Kernel)!;
            ResponseRegistry.OriginalPublication original =terminalPublication!;
            ResponseEnvelope terminal =responses.ReadFailedTransferCompletion(original).Value!;
            original.Completion.GetType().GetProperty("FailedTransferCleanupCompleted")!.SetValue(original.Completion,false);
            Assert.True(responses.IsOriginalFailedTransferCompletion(original,terminal));
            PropertyInfo route =original.Completion.GetType().GetProperty("FailedTransferCompletion")!;
            object canonical =route.GetValue(original.Completion)!;
            foreach(bool copy in new[]{true,false})
            {
                route.SetValue(original.Completion,copy ? typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(canonical,null) : null);
                Assert.False(responses.ReadFailedTransferCompletion(original).IsSuccess);
                Assert.False(responses.CompleteFailedTransfer(original,terminalInput!).IsSuccess);
                Assert.Equal(1,spy.Copies);
                Assert.Equal((byte)99,independent.Span[0]);
                route.SetValue(original.Completion,canonical);
                Assert.Same(terminal,responses.CompleteFailedTransfer(original,terminalInput!).Value);
            }
        }
        Assert.Equal(RegionState.Released,s.Kernel.Regions.Snapshot().Single(r=>r.Handle==next).State);
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) target =Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(next));
        Assert.Equal(s.Caller,target.Owner);
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(target.Reservation).Value!.State);
        Assert.Equal(0UL,s.Kernel.QueryBudget(serviceBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if(failure>=6)Assert.False(routes.Contains(next));else Assert.Equal((charge.Owner,charge.Reservation),routes[next]);
        Assert.Equal((byte)99,independent.Span[0]);
        if(failure==13)
        {
            Assert.False(s.Source.IsValid);
            Assert.Equal(RegionState.Released,s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==s.Source.Handle.RegionId).State);
        }
        else Assert.Equal(16,s.Source.Span.Length);
        Assert.False(s.Host.AdvancePendingCopy().IsSuccess);
        Assert.Equal(1,spy.Copies);
        if(failure<9)Assert.True(s.Kernel.CloseSession(s.Caller,s.Session).IsSuccess);
        else
        {
            Assert.Equal(0,s.Kernel.EndpointSessions.ActivePinCount(s.Session));
            Assert.Equal(EndpointSessionState.Closed,s.Kernel.EndpointSessions.SnapshotLifecycleTrace(s.Session)[^1].State);
        }
        Assert.True(s.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);
        Assert.Equal(0,spy.Native.ActiveBindingCount);
    }

    [Fact]
    public async Task CallerCancellationAfterActualSoftwareByteEffectClosesOriginalCustodyAndNeverPublishesDestination()
    {
        PlatformDsc1ComputeTests.IOriginalDsc1Runtime proxy =global::System.Reflection.DispatchProxy.Create<SingPlus.Tests.Platform.PlatformDsc1ComputeTests.IOriginalDsc1Runtime,
            SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy>();
        PlatformDsc1ComputeTests.Dsc1NativeSpy spy =(SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy)(object)proxy; spy.Native=new();
        ComponentScenario scenario =CreateComponentScenario(new SingPlus.Platform.HybridCpu.HybridCpuPlatformAuthorityProvider(proxy));
        BudgetAccountHandle budget =scenario.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        ulong Used()=>scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        ProcessHandle foreign =TestFixtures.Create(scenario.Kernel,2581,2582).Handle;
        OwnedBuffer<byte> independent =scenario.Kernel.AllocateBuffer<byte>(foreign,16).Value!;
        scenario.Source.Span.Fill(0x67);
        using CancellationTokenSource cancellation =new CancellationTokenSource();
        spy.AfterCopy=()=>
        {
            spy.AfterCopy=null;
            Assert.Equal(64UL,Used());
            Assert.Throws<InvalidOperationException>(()=>{_=scenario.Source.Span.Length;});
            cancellation.Cancel();
        };
        IComputeService client =ComputeServiceClient.Create(new RuntimeSipClientTransport(scenario.Kernel,scenario.Caller,scenario.Session,cancellation.Token));
        Task<OwnedBuffer<byte>> pending =client.CopyAsync(scenario.Source,scenario.Destination).AsTask();
        KernelResult settled =scenario.Host.ProcessNextCopy(); Assert.True(settled.IsSuccess,settled.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1,spy.Copies); Assert.Equal(0UL,Used()); Assert.False(scenario.Host.HasPendingCopy);
        Assert.True(scenario.Source.IsValid); Assert.Equal((byte)0x67,scenario.Source.Span[0]);
        Assert.False(scenario.Destination.IsValid); Assert.Equal(16,independent.Span.Length);
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller,scenario.Session).IsSuccess);
        Assert.True(scenario.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount); Assert.Equal(0,spy.Native.ActiveBindingCount);
    }
    [Fact]
    public async Task ActualSoftwareServiceRetriesOriginalResponseSettlementAfterThrowWithoutCopyRepeat()
    {
        PlatformDsc1ComputeTests.IOriginalDsc1Runtime proxy =global::System.Reflection.DispatchProxy.Create<SingPlus.Tests.Platform.PlatformDsc1ComputeTests.IOriginalDsc1Runtime,
            SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy>();
        PlatformDsc1ComputeTests.Dsc1NativeSpy spy =(SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy)(object)proxy; spy.Native=new();
        ComponentScenario scenario =CreateComponentScenario(new SingPlus.Platform.HybridCpu.HybridCpuPlatformAuthorityProvider(proxy));
        BudgetAccountHandle budget =scenario.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        ulong Used()=>scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        scenario.Source.Span.Fill(0x72);
        IComputeService client =ComputeServiceClient.Create(new RuntimeSipClientTransport(scenario.Kernel,scenario.Caller,scenario.Session));
        Task<OwnedBuffer<byte>> pending =client.CopyAsync(scenario.Source,scenario.Destination).AsTask();
        scenario.Kernel.SessionInvocationSettlementReservedHook=()=>
        {
            scenario.Kernel.SessionInvocationSettlementReservedHook=null;
            throw new IOException("Settlement interrupted before response effect");
        };
        Assert.Throws<IOException>(()=>scenario.Host.ProcessNextCopy());
        Assert.True(scenario.Host.HasPendingCopy); Assert.Equal(1,spy.Copies);
        Assert.Equal(16UL,Used()); Assert.True(scenario.Source.IsValid);
        Assert.False(pending.IsCompleted);
        KernelResult resumed =scenario.Host.AdvancePendingCopy(); Assert.True(resumed.IsSuccess,resumed.Message);
        OwnedBuffer<byte> returned =await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(returned.Span.ToArray(),b=>Assert.Equal((byte)0x72,b));
        Assert.Equal(1,spy.Copies); Assert.Equal(0UL,Used()); Assert.False(scenario.Host.HasPendingCopy);
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller,scenario.Session).IsSuccess);
        Assert.True(scenario.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount); Assert.Equal(0,spy.Native.ActiveBindingCount);
    }
    [Fact]
    public async Task ActualServiceUnknownSubmitRetainsOriginalContinuationAndSettlesWithoutRepeat()
    {
        PlatformDsc1ComputeTests.IOriginalDsc1Runtime proxy =global::System.Reflection.DispatchProxy.Create<SingPlus.Tests.Platform.PlatformDsc1ComputeTests.IOriginalDsc1Runtime,
            SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy>();
        PlatformDsc1ComputeTests.Dsc1NativeSpy spy =(SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy)(object)proxy;
        spy.Native=new();spy.LoseCopyReply=true;spy.LoseQuery=true;
        ComponentScenario scenario =CreateComponentScenario(new SingPlus.Platform.HybridCpu.HybridCpuPlatformAuthorityProvider(proxy));
        BudgetAccountHandle budget =scenario.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        ulong Used()=>scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        IComputeService client =ComputeServiceClient.Create(new RuntimeSipClientTransport(scenario.Kernel,scenario.Caller,scenario.Session));
        Task<OwnedBuffer<byte>> pending =client.CopyAsync(scenario.Source,scenario.Destination).AsTask();
        Assert.Equal(KernelError.PlatformFaulted,scenario.Host.ProcessNextCopy().Error);
        Assert.Equal(1,spy.Copies);
        Assert.Equal(64UL,Used());
        Assert.False(scenario.Host.AdvancePendingCopy().IsSuccess);
        Assert.False(pending.IsCompleted);
        object original =typeof(RuntimeComputeServiceHost).GetField("_pending",
            global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(scenario.Host)!;
        EndpointSessionInvocationHandle invocation =(EndpointSessionInvocationHandle)original.GetType().GetProperty("Invocation")!.GetValue(original)!;
        object copy =typeof(object).GetMethod("MemberwiseClone",
            global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original,null)!;
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ConsumeComputeCopyInvocationClosure(invocation,scenario.Service,copy).Error);
        Assert.Equal(KernelError.WrongSessionOwner,
            scenario.Kernel.ConsumeComputeCopyInvocationClosure(invocation,scenario.Caller,original).Error);
        Assert.True(scenario.Kernel.SnapshotSessionInvocationConsequence(invocation).Value.UnclosedPossibleEffect);
        Assert.Equal(64UL,Used());
        spy.LoseQuery=false;
        KernelResult settled =scenario.Host.AdvancePendingCopy();
        Assert.Equal(KernelError.PlatformFaulted,settled.Error); // original request failure remains failure
        Assert.Equal(1,spy.Copies);
        Assert.Equal(0UL,Used());
        Exception responseFailure =await Assert.ThrowsAnyAsync<Exception>(async()=>await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsNotType<TimeoutException>(responseFailure);
        Assert.True(scenario.Source.IsValid);
        Assert.False(scenario.Destination.IsValid);
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller,scenario.Session).IsSuccess);
        KernelResult<ComponentLifecycleSnapshot> teardown =scenario.Kernel.FaultComponent(new ComponentIdentity("compute-session-test"));
        Assert.True(teardown.IsSuccess,teardown.Message);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);
        Assert.Equal(0,spy.Native.ActiveBindingCount);
        KernelResult callerTeardown =scenario.Kernel.TerminateProcess(scenario.Caller);
        Assert.True(callerTeardown.IsSuccess,callerTeardown.Message);
        Assert.False(scenario.Source.IsValid);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HostCreationRefusesSourceLossAfterComponentAuthoritySnapshot(bool drain)
    {
        ComponentScenario scenario = CreateComponentScenario(new HostPlatformAuthorityProvider());
        ComponentIdentity identity = new ComponentIdentity("compute-session-test");
        CapabilityId capability = Assert.Single(scenario.Kernel.QueryComponent(identity).Value!.Capabilities);
        scenario.Kernel.AfterComputeComponentAuthoritySnapshotQualificationHook = () =>
        {
            scenario.Kernel.AfterComputeComponentAuthoritySnapshotQualificationHook = null;
            if (drain) Assert.True(scenario.Kernel.DrainComponent(identity).Value!.Reclaimable);
            else Assert.True(scenario.Kernel.RevokeCapability(capability).IsSuccess);
        };
        KernelResult<RuntimeComputeServiceHost> host = RuntimeComputeServiceHost.CreateForComponent(scenario.Kernel, identity, scenario.Session);
        Assert.Equal(KernelError.MissingCapability, host.Error);
        Assert.Null(host.Value);
        ComponentLifecycleSnapshot component = scenario.Kernel.QueryComponent(identity).Value!;
        if (drain)
        {
            Assert.Empty(component.Capabilities);
            Assert.Equal(ProcessState.Exited, scenario.Kernel.Processes.ResolveTerminalState(scenario.Service).Value);
            Assert.True(scenario.Kernel.RetireReclaimableComponent(identity, scenario.Service).IsSuccess);
        }
        else Assert.Equal(ComponentLifecycleState.Running, component.State);
    }

    [Fact]
    public async Task ComponentDiscoverySessionTypedClientAndDsc1LifecycleCompose()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 900, 9000);
        ProtocolDefinitionV1 protocol = IComputeServiceProtocol.CreateDefinition();
        ServiceContractIdentity contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        ProvidedServiceManifestV1 serviceRegistration = new ProvidedServiceManifestV1("compute", contract);
        CapabilityRequirementV1 internalCompute = new CapabilityRequirementV1(ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute);
        byte[] image = new byte[] { 0x43, 0x50, 0x55 };
        ServiceManifestV1 serviceManifest = new ServiceManifestV1(
            new ComponentIdentity("compute-service"), new ComponentVersion("1"), Digest(image),
            TestFixtures.Manifest(901, 9010, capabilities: [internalCompute]), [serviceRegistration], requiresPlatformDomain: true,
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, 96), new(ServiceBudgetDimension.ExternalOperations, 4), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 4096)]);
        KernelResult<ComponentLifecycleSnapshot> component = kernel.AdmitComponent(new ComponentAdmissionPlan(
            serviceManifest, image,
            [new ComponentCapabilityGrant(new DomainId(9000), internalCompute)],
            [new ComponentProvidedServiceRegistration(serviceRegistration, protocol, IComputeServiceResponseProtocol.Definition, [internalCompute])]));
        Assert.True(component.IsSuccess, component.Message);

        ProcessHandle caller = TestFixtures.Create(kernel, 902, 9020).Handle;
        AdmitCallerBudget(kernel, caller, 9020);
        CapabilityId callerCompute = kernel.MintCapability(new DomainId(9020), caller, ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute).Value!.CapabilityId;
        OwnedBuffer<byte> source = kernel.AllocateBuffer<byte>(caller, 32).Value!;
        OwnedBuffer<byte> destination = kernel.AllocateBuffer<byte>(caller, 32).Value!;
        source.Span.Fill(0x5A);
        ServiceEndpointDescriptor discovered = Assert.Single(kernel.ResolveByContract(contract).Value!);
        EndpointSessionHandle session = kernel.OpenSession(caller, discovered, [callerCompute]).Value;
        RuntimeComputeServiceHost host = RuntimeComputeServiceHost.CreateForComponent(kernel, serviceManifest.Identity, session).Value!;
        IComputeService client = ComputeServiceClient.Create(new RuntimeSipClientTransport(kernel, caller, session));

        Task<OwnedBuffer<byte>> pending = client.CopyAsync(source, destination).AsTask();
        Assert.True(host.ProcessNextCopy().IsSuccess);
        OwnedBuffer<byte> returned = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(returned.Span.ToArray(), value => Assert.Equal((byte)0x5A, value));
        Assert.Equal(1, provider.SubmitDsc1CopyCallCount);
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
    }

    [Fact]
    public void StaleSessionRejectsBeforeBorrowMoveOrProviderSubmission()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        (RuntimeKernel Kernel, ProcessHandle Caller, EndpointSessionHandle Session, OwnedBuffer<byte> Source, OwnedBuffer<byte> Destination) scenario = CreateMinimalSession(provider);
        EndpointSessionHandle stale = scenario.Session with { Generation = new EndpointSessionGeneration(scenario.Session.Generation.Value + 1) };
        IComputeService client = IComputeServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, stale));
        RegionHandle oldSource = scenario.Source.Handle;
        RegionHandle oldDestination = scenario.Destination.Handle;

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => client.CopyAsync(scenario.Source, scenario.Destination).AsTask().GetAwaiter().GetResult());
        Assert.Contains(nameof(KernelError.StaleGeneration), failure.Message, StringComparison.Ordinal);
        Assert.Equal(oldSource, scenario.Source.Handle);
        Assert.Equal(oldDestination, scenario.Destination.Handle);
        Assert.True(scenario.Source.IsValid);
        Assert.True(scenario.Destination.IsValid);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
    }

    [Fact]
    public void MissingCallerComputeAuthorityRejectsSessionBeforeOwnershipIngress()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        ComponentScenario scenario = CreateComponentScenario(provider);
        ProcessHandle otherCaller = TestFixtures.Create(scenario.Kernel, 923, 9230).Handle;
        OwnedBuffer<byte> source = scenario.Kernel.AllocateBuffer<byte>(otherCaller, 8).Value!;
        OwnedBuffer<byte> destination = scenario.Kernel.AllocateBuffer<byte>(otherCaller, 8).Value!;

        KernelResult<EndpointSessionHandle> denied = scenario.Kernel.OpenSession(otherCaller, scenario.Descriptor);

        Assert.Equal(KernelError.MissingCapability, denied.Error);
        Assert.True(source.IsValid);
        Assert.True(destination.IsValid);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
    }

    [Fact]
    public async Task CancellationBeforeProviderAcceptanceReturnsBorrowAndPublishesNoDestination()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        ComponentScenario scenario = CreateComponentScenario(provider);
        IComputeService client = IComputeServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        Task<OwnedBuffer<byte>> pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();
        Assert.False(scenario.Destination.IsValid);

        KernelResult cancelled = scenario.Host.CancelNextRequestBeforeAcceptance();

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.True(scenario.Source.IsValid);
        Assert.False(scenario.Destination.IsValid);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task ServiceDrainRejectsNewSessionsButCommittedResponseSurvivesProviderTeardown()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        ComponentScenario scenario = CreateComponentScenario(provider);
        scenario.Source.Span.Fill(0x33);
        IComputeService client = IComputeServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        Task<OwnedBuffer<byte>> pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();
        Assert.True(scenario.Kernel.SetServiceAvailability(scenario.Descriptor.Service, ServiceAvailability.Draining).IsSuccess);
        Assert.Equal(KernelError.ServiceUnavailable, scenario.Kernel.OpenSession(scenario.Caller, scenario.Descriptor, [scenario.CallerCapability]).Error);
        Assert.True(scenario.Host.ProcessNextCopy().IsSuccess);
        OwnedBuffer<byte> returned = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Service).IsSuccess);
        Assert.True(returned.IsValid);
        Assert.All(returned.Span.ToArray(), value => Assert.Equal((byte)0x33, value));
    }

    private static (RuntimeKernel Kernel, ProcessHandle Caller, EndpointSessionHandle Session, SingPlus.Sip.OwnedBuffer<byte> Source, SingPlus.Sip.OwnedBuffer<byte> Destination) CreateMinimalSession(HostPlatformAuthorityProvider provider)
    {
        RuntimeKernel kernel = new RuntimeKernel(provider);
        ProcessHandle caller = TestFixtures.Create(kernel, 910, 9100).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 911, 9110).Handle;
        CapabilityId capability = kernel.MintCapability(new DomainId(9100), caller, ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol = IComputeServiceProtocol.CreateDefinition();
        ServiceContractIdentity contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "compute", contract, protocol, IComputeServiceResponseProtocol.Definition, [new CapabilityRequirementV1(ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute)]).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        return (kernel, caller, session, kernel.AllocateBuffer<byte>(caller, 8).Value!, kernel.AllocateBuffer<byte>(caller, 8).Value!);
    }

    [Fact]
    public async Task ActualPairSendLateFailureRetainsOriginalSessionPinAcrossCallerFinallyAndClose()
    {
        ComponentScenario scenario =CreateComponentScenario(new HostPlatformAuthorityProvider());
        RuntimeKernel kernel =scenario.Kernel;
        EndpointSessionRegistry.Record sessionRecord =kernel.EndpointSessions.Resolve(scenario.Session,scenario.Caller).Value!;
        OwnedBuffer<byte> independent =kernel.AllocateBuffer<byte>(scenario.Caller,16).Value!;
        independent.Span.Fill(99);
        ProtocolDefinitionV1 protocol =new ProtocolDefinitionV1("independent-send","1","Ready",null,[new(1,"Ping")],[new(1,"Ready","Ready")]);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) foreign =kernel.CreateChannel(scenario.Caller,scenario.Service,protocol,4).Value;
        Assert.True(kernel.Send(scenario.Caller,scenario.Service,foreign.Left,1).IsSuccess);
        IDictionary routes =(global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_ipcBudgetReservations",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel)!;
        (ChannelId ChannelId, ulong) foreignKey =(foreign.Left.ChannelId,1UL);
        object foreignFact =routes[foreignKey]!;
        routes[(sessionRecord.Channel.ChannelId,1UL)]=foreignFact;
        RuntimeSipClientTransport transport =new RuntimeSipClientTransport(kernel,scenario.Caller,scenario.Session);
        IComputeService client =ComputeServiceClient.Create(transport);
        await Assert.ThrowsAsync<ArgumentException>(()=>client.CopyAsync(scenario.Source,scenario.Destination).AsTask());
        Assert.True(transport.HasPendingOwnershipPair);
        IDictionary ownedInvocations =(global::System.Collections.IDictionary)typeof(EndpointSessionInvocationRegistry).GetField("_ownedRecords",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(typeof(RuntimeKernel).GetProperty("SessionInvocations",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel))!;
        (EndpointSessionId SessionId, EndpointSessionGeneration Generation, EndpointSessionInvocationId) invocationKey =(scenario.Session.SessionId,scenario.Session.Generation,new EndpointSessionInvocationId(1));
        object originalInvocation =ownedInvocations[invocationKey]!;
        ownedInvocations.Remove(invocationKey);
        Assert.True(transport.HasPendingOwnershipPair);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>client.CopyAsync(scenario.Source,scenario.Destination).AsTask());
        ownedInvocations[invocationKey]=originalInvocation; // Restore test routing only; same original, no effect replay.
        (object Fact, object? Lifetime, ChannelId Channel, ulong Sequence, ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) originalCharge =Assert.Single(kernel.Budgets.OriginalQueuedCharges(),charge=>charge.Channel==sessionRecord.Channel.ChannelId);
        Assert.NotNull(originalCharge.Lifetime);
        Assert.Equal(BudgetReservationState.Quarantined,kernel.QueryBudget(originalCharge.Reservation).Value!.State);
        Assert.False(kernel.Channels.HasEndedOriginalQueuedLifetime(originalCharge.Lifetime,originalCharge.Channel,originalCharge.Sequence,originalCharge.Fact));
        RegionDescriptor transferred =kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==scenario.Destination.Handle.RegionId);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>client.CopyAsync(scenario.Source,scenario.Destination).AsTask());
        Assert.Equal(transferred,kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==scenario.Destination.Handle.RegionId));
        OriginalSessionSendCustody custody =(OriginalSessionSendCustody)typeof(RuntimeSipClientTransport).GetField("_pairSendCustody",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(transport)!;
        EndpointSessionInvocationRegistry producerRegistry =(EndpointSessionInvocationRegistry)typeof(RuntimeKernel).GetProperty("SessionInvocations",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel)!;
        KernelResult<(KernelResult<RegionHandle>? Raw, BudgetReservationHandle? Reservation, object? BudgetOwner)> producerFact =producerRegistry.ReadOriginalTransferProducer(custody.Read(kernel)!);
        Assert.True(producerFact.IsSuccess);
        Assert.True(producerFact.Value.Raw!.Value.IsSuccess);
        Assert.Equal(transferred.Handle,producerFact.Value.Raw.Value.Value);
        Assert.Same(kernel.Budgets.OriginalRegionCharges(transferred.Handle).Single().Fact,producerFact.Value.BudgetOwner);
        OriginalSessionSendCustody copied =(OriginalSessionSendCustody)typeof(object).GetMethod("MemberwiseClone",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.Invoke(custody,null)!;
        Assert.Equal(KernelError.WrongSessionOwner,(await kernel.InvokeSessionOwnershipPairAsync(scenario.Caller,scenario.Session,IComputeServiceProtocol.Message_CopyAsync,scenario.Source,scenario.Destination,CancellationToken.None,copied)).Error);
        EndpointSessionInvocationHandle invocation =new EndpointSessionInvocationHandle(scenario.Session,new(1),new(1));
        EndpointSessionInvocationRegistry.InvocationConsequenceSnapshot consequence =kernel.SnapshotSessionInvocationConsequence(invocation)!.Value;
        Assert.True(consequence.UnclosedPossibleEffect);
        Assert.False(consequence.Delivered);
        Assert.False(consequence.ServiceAccepted);
        Assert.Equal(KernelError.ResponseNotPending,kernel.ReceiveSessionRequest(scenario.Service,scenario.Session).Error);
        Assert.True(transport.WithdrawPendingOwnershipPair().IsSuccess);
        Assert.True(kernel.Channels.HasEndedOriginalQueuedLifetime(originalCharge.Lifetime,originalCharge.Channel,originalCharge.Sequence,originalCharge.Fact));
        Assert.True(transport.WithdrawPendingOwnershipPair().IsSuccess);
        Assert.True(transport.HasPendingOwnershipPair);
        Assert.Equal(BudgetReservationState.Quarantined,kernel.QueryBudget(originalCharge.Reservation).Value!.State);
        Assert.Equal(1,kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Single(sessionRecord.PendingSendPins);
        Assert.False(scenario.Destination.IsValid);
        Assert.Throws<InvalidOperationException>(()=>{_=scenario.Source.Span[0];});
        Assert.Equal((byte)99,independent.Span[0]);
        Assert.True(kernel.Receive(scenario.Service,foreign.Right).IsSuccess);
        Assert.Equal(KernelError.SessionDraining,kernel.CloseSession(scenario.Caller,scenario.Session).Error);
        Assert.Equal(1,kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(EndpointSessionState.Draining,kernel.EndpointSessions.SnapshotLifecycleTrace(scenario.Session)[^1].State);
        Assert.Single(sessionRecord.PendingSendPins);
        EndpointSessionPin originalPin =Assert.Single(sessionRecord.PendingSendPins);
        EndpointSessionPin copiedPin =(EndpointSessionPin)typeof(object).GetMethod("MemberwiseClone",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.Invoke(originalPin,null)!;
        copiedPin.Dispose();
        originalPin.Dispose();
        Assert.Equal(1,kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.True(kernel.ReleaseRegion(scenario.Caller,independent).IsSuccess);
        Assert.Equal(1,kernel.EndpointSessions.ActivePinCount(scenario.Session));
        RegionHandle targetRegion =kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==scenario.Destination.Handle.RegionId).Handle;
        RegionUseDescriptor blocked =kernel.AcquireRegionUse(scenario.Service,targetRegion,RegionUseMode.ExclusiveWrite,new(0,16)).Value!;
        Assert.Equal(KernelError.RegionUseConflict,transport.CleanupPendingOwnershipPair().Error);
        Assert.Equal(16,scenario.Source.Span.Length); // Loan closed independently; destination still pinned.
        Assert.Equal(1,kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(BudgetReservationState.Quarantined,kernel.QueryBudget(originalCharge.Reservation).Value!.State);
        BudgetAccountHandle serviceBudget =kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        ulong TargetUsed()=>kernel.QueryBudget(serviceBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(16UL,TargetUsed());
        Assert.True(kernel.ReleaseRegionUse(scenario.Service,blocked.Handle).IsSuccess);
        Assert.True(transport.CleanupPendingOwnershipPair().IsSuccess);
        Assert.Equal(0UL,TargetUsed());
        Assert.Equal(BudgetReservationState.Released,kernel.QueryBudget(originalCharge.Reservation).Value!.State);
        Assert.Equal(0,kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.False(transport.HasPendingOwnershipPair);
        Assert.Equal(EndpointSessionState.Closed,kernel.EndpointSessions.SnapshotLifecycleTrace(scenario.Session)[^1].State);
        Assert.True(transport.CleanupPendingOwnershipPair().IsSuccess);
        Assert.Equal(foreignFact,routes[(sessionRecord.Channel.ChannelId,1UL)]); // Foreign entry never compensated.
    }
    [Fact]
    public void ExactPairSendRefusalConsumesIdentityBeforeFreshActualCopy()
    {
        ComponentScenario scenario =CreateComponentScenario(new HostPlatformAuthorityProvider());
        IComputeService client =ComputeServiceClient.Create(new RuntimeSipClientTransport(scenario.Kernel,scenario.Caller,scenario.Session));
        RegionUseDescriptor blocked =scenario.Kernel.AcquireRegionUse(scenario.Caller,scenario.Destination.Handle,RegionUseMode.ExclusiveWrite,new(0,16)).Value!;
        Assert.Throws<InvalidOperationException>(()=>client.CopyAsync(scenario.Source,scenario.Destination).AsTask().GetAwaiter().GetResult());
        EndpointSessionInvocationHandle original =new EndpointSessionInvocationHandle(scenario.Session,new(1),new(1));
        EndpointSessionInvocationRegistry.InvocationConsequenceSnapshot refused =scenario.Kernel.SnapshotSessionInvocationConsequence(original)!.Value;
        Assert.False(refused.UnclosedPossibleEffect);
        Assert.False(refused.Delivered);
        Assert.Equal(0,scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.True(scenario.Destination.IsValid);
        Assert.True(scenario.Kernel.ReleaseRegionUse(scenario.Caller,blocked.Handle).IsSuccess);
        Task<OwnedBuffer<byte>> pending =client.CopyAsync(scenario.Source,scenario.Destination).AsTask();
        Assert.Throws<InvalidOperationException>(()=>client.CopyAsync(scenario.Source,scenario.Destination).AsTask().GetAwaiter().GetResult());
        Assert.True(scenario.Host.ProcessNextCopy().IsSuccess);
        Assert.NotNull(pending.GetAwaiter().GetResult());
        EndpointSessionInvocationHandle fresh =new EndpointSessionInvocationHandle(scenario.Session,new(2),new(1));
        Assert.Equal(ResponsePublicationStatus.Published,scenario.Kernel.SnapshotSessionInvocationConsequence(fresh)!.Value.TerminalStatus);
        Assert.False(scenario.Kernel.SnapshotSessionInvocationConsequence(original)!.Value.Delivered);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualClientFailedQuantitativeCompositionClosesOriginalPairAndPreservesForeignRoute(bool loseSourceRoute)
    {
        ComponentScenario s =CreateComponentScenario(new HostPlatformAuthorityProvider());
        BindingFlags flags =global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes =(global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_regionBudgetReservations",flags)!.GetValue(s.Kernel)!;
        RegionHandle old =s.Destination.Handle;
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) sourceCharge =Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(old));
        RegionHandle next =new RegionHandle(old.RegionId,new(old.Generation.Value+1));
        routes[next]=(sourceCharge.Owner,sourceCharge.Reservation);
        ProcessHandle foreign =TestFixtures.Create(s.Kernel,928,9280).Handle;
        OwnedBuffer<byte> independent =s.Kernel.AllocateBuffer<byte>(foreign,4).Value!;
        independent.Span.Fill(99);
        RuntimeSipClientTransport transport =new RuntimeSipClientTransport(s.Kernel,s.Caller,s.Session);
        IComputeService client =ComputeServiceClient.Create(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>client.CopyAsync(s.Source,s.Destination).AsTask());
        Assert.True(transport.HasPendingOwnershipPair);
        Assert.False(s.Host.ProcessNextCopy().IsSuccess); // No service/DSC effect was published.
        Assert.Equal(next,s.Kernel.Regions.Snapshot().Single(r=>r.Handle.RegionId==old.RegionId).Handle);
        if(loseSourceRoute) routes.Remove(old);
        Assert.True(transport.CleanupPendingOwnershipPair().IsSuccess);
        Assert.True(transport.CleanupPendingOwnershipPair().IsSuccess);
        Assert.Equal(0,s.Kernel.EndpointSessions.ActivePinCount(s.Session));
        Assert.Equal(16,s.Source.Span.Length);
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(sourceCharge.Reservation).Value!.State);
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) target =Assert.Single(s.Kernel.Budgets.OriginalRegionCharges(next));
        Assert.Equal(BudgetReservationState.Released,s.Kernel.QueryBudget(target.Reservation).Value!.State);
        Assert.Equal(RegionState.Released,s.Kernel.Regions.Snapshot().Single(r=>r.Handle==next).State);
        Assert.Equal((sourceCharge.Owner,sourceCharge.Reservation),routes[next]);
        Assert.Equal((byte)99,independent.Span[0]);
        OwnedBuffer<byte> fresh =s.Kernel.AllocateBuffer<byte>(s.Caller,16).Value!;
        Task<OwnedBuffer<byte>> reply =client.CopyAsync(s.Source,fresh).AsTask();
        Assert.True(s.Host.ProcessNextCopy().IsSuccess);
        Assert.NotNull(await reply);
    }

    [Theory]
    [InlineData(true,0)] [InlineData(false,0)]
    [InlineData(true,1)] [InlineData(true,2)] [InlineData(true,3)] [InlineData(true,4)]
    [InlineData(true,5)] [InlineData(true,6)] [InlineData(true,7)]
    [InlineData(true,8)]
    [InlineData(true,9)] [InlineData(true,10)]
    [InlineData(true,11)] [InlineData(true,12)]
    public void LostPreEnqueueTransferReplyCleansSameOriginalProducerWithoutRepeat(bool retainResult,int failure)
    {
        ComponentScenario s =CreateComponentScenario(new HostPlatformAuthorityProvider());
        RuntimeKernel kernel =s.Kernel;
        EndpointSessionRegistry.Record session =kernel.EndpointSessions.Resolve(s.Session,s.Caller).Value!;
        EndpointSessionPin pin =kernel.EndpointSessions.AcquirePin(s.Session,s.Caller,s.Service).Value!;
        Assert.True(kernel.EndpointSessions.RetainOriginalSendPin(pin).IsSuccess);
        BindingFlags flags =global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic;
        EndpointSessionInvocationRegistry invocations =(EndpointSessionInvocationRegistry)typeof(RuntimeKernel).GetProperty("SessionInvocations",flags)!.GetValue(kernel)!;
        EndpointSessionInvocationRegistry.OriginalSend original =invocations.PrepareOriginalSend(s.Session,s.Caller,s.Service,1,IComputeServiceProtocol.Message_CopyAsync,pin,s.Source,s.Destination,null);
        OriginalSessionSendCustody custody =new OriginalSessionSendCustody(kernel,s.Caller,s.Session);
        custody.Retain(kernel,s.Caller,s.Session,original);
        RuntimeSipClientTransport transport =new RuntimeSipClientTransport(kernel,s.Caller,s.Session);
        typeof(RuntimeSipClientTransport).GetField("_pairSendCustody",flags)!.SetValue(transport,custody);
        BudgetReservationSnapshot ipc =kernel.Budgets.ReserveIfAttached(s.Caller,[new(ServiceBudgetDimension.IpcMessages,1),new(ServiceBudgetDimension.IpcBytes,64)],BudgetReservationLifetime.IpcQueued).Value!;
        Assert.True(kernel.Channels.RetainSendBudget(session.Channel,s.Caller,ipc.Reservation).IsSuccess);
        object lifetime =kernel.Channels.PrepareOriginalQueuedLifetime(session.Channel).Value!;
        object charge =kernel.Budgets.BindOriginalQueuedCharge(s.Caller,s.Service,ipc.Reservation,session.Channel.ChannelId,1,lifetime).Value!;
        Assert.True(kernel.Channels.BindOriginalQueuedBudgetOwner(lifetime,charge));
        kernel.Channels.CaptureQueuedLifetime(session.Channel.ChannelId,1);
        int effects =0;
        (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) sourceCharge =Assert.Single(kernel.Budgets.OriginalRegionCharges(s.Destination.Handle));
        RegionHandle old =s.Destination.Handle;
        IDictionary routes =(global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_regionBudgetReservations",flags)!.GetValue(kernel)!;
        RegionHandle next =new RegionHandle(old.RegionId,new(old.Generation.Value+1));
        if(failure!=0) routes[next]=(sourceCharge.Owner,sourceCharge.Reservation);
        ProcessHandle foreign =TestFixtures.Create(kernel,927,9270).Handle;
        OwnedBuffer<byte> independent =kernel.AllocateBuffer<byte>(foreign,4).Value!;
        independent.Span.Fill(99);
        RegionUseHandle? use=null;
        global::System.Collections.IDictionary? reservationRoutes=null;
        object? removedSource=null;
        MethodInfo producer =typeof(RuntimeKernel).GetMethod("TransferRegionForIpcCore",flags)!;
        Assert.Throws<IOException>(()=>kernel.Channels.SendOwnershipPair(kernel.Processes.Resolve(s.Caller).Value!,kernel.Processes.Resolve(s.Service).Value!,session.Channel,IComputeServiceProtocol.Message_CopyAsync,s.Source,s.Destination,[s.CallerCapability],
            (source,target,region,admission,noEffect,prepared)=>
            {
                Assert.True(invocations.BindOriginalSendTransfer(original,prepared));
                effects++;
                Func<KernelResult> prepare=()=>
                {
                    KernelResult<(KernelResult<RegionHandle>? Raw, BudgetReservationHandle? Reservation, object? BudgetOwner)> before =invocations.ReadOriginalTransferProducer(original);
                    Assert.True(before.IsSuccess);
                    Assert.Null(before.Value.Raw);
                    Assert.NotNull(before.Value.Reservation);
                    Assert.NotNull(before.Value.BudgetOwner);
                    if(failure==8)
                        Assert.True(kernel.Budgets.BindOriginalRegionCharge(s.Service,before.Value.Reservation!.Value,
                            independent.Handle,kernel.Processes.Resolve(foreign).Value!.DomainId,
                            kernel.Regions.CaptureOriginalAccountingOwner(independent.Handle)!).IsSuccess);
                    return admission?.Invoke() ?? KernelResult.Ok();
                };
                KernelResult<RegionHandle> result =(KernelResult<RegionHandle>)producer.Invoke(kernel,[source,target,region,prepare,null,noEffect,prepared,original,null])!;
                Assert.Equal(failure==0,result.IsSuccess);
                (KernelResult<RegionHandle>? Raw, BudgetReservationHandle? Reservation, object? BudgetOwner) raw =invocations.ReadOriginalTransferProducer(original).Value;
                Assert.Equal(next,raw.Raw!.Value.Value);
                if(failure!=8) Assert.Same(kernel.Budgets.OriginalRegionCharges(next).Single().Fact,raw.BudgetOwner);
                if(failure==2) routes.Remove(old);
                if(failure==3)
                {
                    reservationRoutes=(global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations",flags)!.GetValue(kernel.Budgets)!;
                    removedSource=reservationRoutes[sourceCharge.Reservation.ReservationId];
                    reservationRoutes.Remove(sourceCharge.Reservation.ReservationId);
                }
                if(failure==4)
                    use=kernel.Regions.AcquireUse(next,new RegionOwner(target.DomainId,target.Generation),RegionUseMode.ExclusiveWrite,new(0,16)).Value!.Handle;
                if(retainResult) invocations.RetainOriginalSendTransferResult(original,prepared,result);
                throw new IOException("original-transfer-reply-lost-before-enqueue");
            }));
        Assert.True(kernel.Budgets.QuarantineLease(s.Caller,ipc.Reservation).IsSuccess);
        Assert.True(transport.HasPendingOwnershipPair);
        Assert.False(invocations.ReadPendingOriginalSend(original,s.Caller).IsSuccess); // No fabricated queue raw.
        if(!retainResult)
        {
            Assert.False(transport.CleanupPendingOwnershipPair().IsSuccess);
            Assert.True(transport.HasPendingOwnershipPair);
            Assert.Equal(1,kernel.EndpointSessions.ActivePinCount(s.Session));
            Assert.Equal(BudgetReservationState.Quarantined,kernel.QueryBudget(ipc.Reservation).Value!.State);
            Assert.Equal(1,effects);
            Assert.Throws<InvalidOperationException>(()=>{_=s.Source.Span.Length;});
            return;
        }
        (KernelResult<RegionHandle>? Raw, BudgetReservationHandle? Reservation, object? BudgetOwner) targetFact =invocations.ReadOriginalTransferProducer(original).Value;
        if(failure==8)
        {
            Assert.False(transport.CleanupPendingOwnershipPair().IsSuccess);
            Assert.True(transport.HasPendingOwnershipPair);
            Assert.Equal(1,kernel.EndpointSessions.ActivePinCount(s.Session));
            Assert.Equal(16,s.Source.Span.Length);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==next).State);
            Assert.Equal(BudgetReservationState.Released,kernel.QueryBudget(sourceCharge.Reservation).Value!.State);
            Assert.Equal(BudgetReservationState.Quarantined,kernel.QueryBudget(targetFact.Reservation!.Value).Value!.State);
            Assert.Equal((byte)99,independent.Span[0]);
            Assert.Equal((sourceCharge.Owner,sourceCharge.Reservation),routes[next]);
            Assert.False(transport.CleanupPendingOwnershipPair().IsSuccess);
            Assert.Equal(1,effects);
            return;
        }
        MethodInfo clone =typeof(object).GetMethod("MemberwiseClone",flags)!;
        Assert.Equal(KernelError.WrongSessionOwner,kernel.CleanupOriginalSessionSend((OriginalSessionSendCustody)clone.Invoke(custody,null)!).Error);
        if(failure==11)
        {
            IDictionary regionRoutes =(global::System.Collections.IDictionary)typeof(RegionAuthority).GetField("_regions",flags)!.GetValue(kernel.Regions)!;
            object before =regionRoutes[next.RegionId]!;
            object foreignRecord =clone.Invoke(before,null)!;
            regionRoutes[next.RegionId]=foreignRecord;
            Assert.False(transport.CleanupPendingOwnershipPair().IsSuccess);
            Assert.Equal(RegionState.Owned,foreignRecord.GetType().GetProperty("State")!.GetValue(foreignRecord));
            Assert.Equal(BudgetReservationState.Quarantined,kernel.QueryBudget(targetFact.Reservation!.Value).Value!.State);
            Assert.Equal(BudgetReservationState.Active,kernel.QueryBudget(sourceCharge.Reservation).Value!.State);
            Assert.Equal(16,s.Source.Span.Length);
            Assert.Equal(1,kernel.EndpointSessions.ActivePinCount(s.Session));
            regionRoutes[next.RegionId]=before;
        }
        if(failure==12)
        {
            IDictionary channels =(global::System.Collections.IDictionary)typeof(ChannelRegistry).GetField("_ownedChannels",flags)!.GetValue(kernel.Channels)!;
            object record =channels[session.Channel.ChannelId]!;
            PropertyInfo property =record.GetType().GetProperty("PendingBorrowProjection")!;
            object before =property.GetValue(record)!;
            property.SetValue(record,clone.Invoke(before,null));
            Assert.False(transport.CleanupPendingOwnershipPair().IsSuccess);
            Assert.Throws<InvalidOperationException>(()=>{_=s.Source.Span.Length;});
            Assert.Equal(RegionState.Loaned,kernel.Regions.Snapshot().Single(r=>r.Handle==s.Source.Handle).State);
            Assert.Equal(BudgetReservationState.Released,kernel.QueryBudget(targetFact.Reservation!.Value).Value!.State);
            Assert.Equal(BudgetReservationState.Released,kernel.QueryBudget(sourceCharge.Reservation).Value!.State);
            Assert.Equal(1,kernel.EndpointSessions.ActivePinCount(s.Session));
            property.SetValue(record,before);
        }
        if(failure is 5 or 6 or 7 or 9 or 10)
        {
            object record =original.Record;
            PropertyInfo property =record.GetType().GetProperty(failure switch
            {
                5=>"OriginalSendTransfer",6=>"OriginalSendTargetReservationOwner",7=>"OriginalSendTargetRegionOwner",
                9=>"OriginalSendSourceCharges",_=>"OriginalSendTransferResult"
            })!;
            object before =property.GetValue(record)!;
            property.SetValue(record,failure switch
            {
                7=>new RegionOwner(new DomainId(9999),s.Service.Generation),9=>null,
                10=>KernelResult<RegionHandle>.Ok(independent.Handle),_=>clone.Invoke(before,null)
            });
            Assert.False(transport.CleanupPendingOwnershipPair().IsSuccess);
            Assert.True(transport.HasPendingOwnershipPair);
            Assert.Equal(1,kernel.EndpointSessions.ActivePinCount(s.Session));
            Assert.Equal(BudgetReservationState.Quarantined,kernel.QueryBudget(targetFact.Reservation!.Value).Value!.State);
            Assert.Equal((byte)99,independent.Span[0]);
            property.SetValue(record,before); // Same retained original; no new reservation or effect.
        }
        if(failure is 3 or 4)
        {
            Assert.False(transport.CleanupPendingOwnershipPair().IsSuccess);
            Assert.Equal(1,kernel.EndpointSessions.ActivePinCount(s.Session));
            Assert.True(transport.HasPendingOwnershipPair);
            Assert.Equal(16,s.Source.Span.Length); // Loan closure is independent of destination retirement.
            Assert.Equal(failure==3 ? BudgetReservationState.Released : BudgetReservationState.Quarantined,
                kernel.QueryBudget(targetFact.Reservation!.Value).Value!.State);
            if(failure==4) Assert.Equal(BudgetReservationState.Released,kernel.QueryBudget(sourceCharge.Reservation).Value!.State);
            if(failure==3) reservationRoutes![sourceCharge.Reservation.ReservationId]=removedSource;
            else Assert.True(kernel.Regions.ReleaseUse(use!.Value,new RegionOwner(kernel.Processes.Resolve(s.Service).Value!.DomainId,s.Service.Generation)).IsSuccess);
        }
        Assert.True(transport.CleanupPendingOwnershipPair().IsSuccess);
        Assert.True(transport.CleanupPendingOwnershipPair().IsSuccess);
        Assert.Equal(1,effects);
        Assert.False(transport.HasPendingOwnershipPair);
        Assert.Equal(0,kernel.EndpointSessions.ActivePinCount(s.Session));
        Assert.Equal(16,s.Source.Span.Length);
        Assert.False(s.Destination.IsValid);
        Assert.Equal(BudgetReservationState.Released,kernel.QueryBudget(sourceCharge.Reservation).Value!.State);
        Assert.Equal(BudgetReservationState.Released,kernel.QueryBudget(targetFact.Reservation!.Value).Value!.State);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==next).State);
        Assert.Equal((byte)99,independent.Span[0]);
        if(failure!=0) Assert.Equal((sourceCharge.Owner,sourceCharge.Reservation),routes[next]);
        Assert.Equal(BudgetReservationState.Released,kernel.QueryBudget(ipc.Reservation).Value!.State);
        BudgetAccountHandle targetBudget =kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        Assert.Equal(0UL,kernel.QueryBudget(targetBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        OwnedBuffer<byte> fresh =kernel.AllocateBuffer<byte>(s.Caller,16).Value!;
        IComputeService client =ComputeServiceClient.Create(transport);
        Task<OwnedBuffer<byte>> nextCall =client.CopyAsync(s.Source,fresh).AsTask();
        Assert.True(s.Host.ProcessNextCopy().IsSuccess);
        Assert.NotNull(nextCall.GetAwaiter().GetResult());
        Assert.Equal(2UL,kernel.Channels.GetEndpoint(session.Channel).Value!.Sequence);
    }
    private static ComponentScenario CreateComponentScenario(SingPlus.Platform.IPlatformAuthorityProvider provider, TimeProvider? clock = null, bool pinBudget = false)
    {
        RuntimeKernel kernel = new RuntimeKernel(provider, clock);
        _ = TestFixtures.Create(kernel, 920, 9200);
        ProtocolDefinitionV1 protocol = IComputeServiceProtocol.CreateDefinition();
        ServiceContractIdentity contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        ProvidedServiceManifestV1 registration = new ProvidedServiceManifestV1("compute", contract);
        CapabilityRequirementV1 internalCompute = new CapabilityRequirementV1(ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute);
        byte[] image = new byte[] { 9, 2, 0 };
        ServiceManifestV1 manifest = new ServiceManifestV1(new ComponentIdentity("compute-session-test"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(921, 9210, capabilities: [internalCompute]), [registration], requiresPlatformDomain: true,
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, provider is SingPlus.Platform.IPlatformOriginalDsc1CopyBackingProvider ? 128UL : 96UL), new(ServiceBudgetDimension.ExternalOperations, 4), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 4096), .. (pinBudget ? new ServiceBudgetRequestV1[] { new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 32) } : [])]);
        KernelResult<ComponentLifecycleSnapshot> admitted = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, [new ComponentCapabilityGrant(new DomainId(9200), internalCompute)], [new ComponentProvidedServiceRegistration(registration, protocol, IComputeServiceResponseProtocol.Definition, [internalCompute])]));
        Assert.True(admitted.IsSuccess, admitted.Message);
        ProcessHandle caller = TestFixtures.Create(kernel, 922, 9220).Handle;
        AdmitCallerBudget(kernel, caller, 9220, trace: clock is not null);
        CapabilityId callerCapability = kernel.MintCapability(new DomainId(9220), caller, ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute).Value!.CapabilityId;
        ServiceEndpointDescriptor descriptor = kernel.ResolveByServiceName("compute").Value;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [callerCapability]).Value;
        RuntimeComputeServiceHost host = RuntimeComputeServiceHost.CreateForComponent(kernel, manifest.Identity, session).Value!;
        return new ComponentScenario(kernel, caller, admitted.Value!.Process, descriptor, session, callerCapability, kernel.AllocateBuffer<byte>(caller, 16).Value!, kernel.AllocateBuffer<byte>(caller, 16).Value!, host);
    }

    private sealed record ComponentScenario(RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, ServiceEndpointDescriptor Descriptor, EndpointSessionHandle Session, CapabilityId CallerCapability, SingPlus.Sip.OwnedBuffer<byte> Source, SingPlus.Sip.OwnedBuffer<byte> Destination, RuntimeComputeServiceHost Host);

    private static void AdmitCallerBudget(RuntimeKernel kernel, ProcessHandle caller, ulong domain, bool trace = false)
    {
        CapabilityId configure = kernel.MintCapability(new DomainId(domain), caller, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        KernelResult<ProcessBudgetAdmission> admitted = kernel.AdmitProcessBudget(caller, configure, caller, "compute-caller",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 4096),
             .. (trace ? new BudgetAmount[]{new(ServiceBudgetDimension.TraceTelemetryBufferBytes,8192)} : [])]);
        Assert.True(admitted.IsSuccess, admitted.Message);
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}





