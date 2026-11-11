using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

internal static class DriverDmaFlow
{
    public static Action Copy(RuntimeKernel kernel,ComponentIdentity component,string commandResource,ProcessHandle source,ProcessHandle admin,BudgetAccountHandle sourceBudget,out bool pendingCleanup,IrqReplyLossRuntime? loss=null)
    {
        pendingCleanup=false;
        void Check(bool value,string operation){if(!value)throw new InvalidOperationException(operation);}
        KernelResult<RuntimeDriverCommandCaller> command =RuntimeDriverCommandCaller.Create(kernel,component,commandResource);
        Check(command.IsSuccess,command.Message??"original command read identity");
        if(loss is {LoseCommandReply:true})loss.LoseRegister=true;
        KernelResult<uint> admittedCommand =command.Value!.ReadForDispatch();
        if(loss is {LoseCommandReply:true})
        {
            Check(!admittedCommand.IsSuccess && !command.Value.ReadForDispatch().IsSuccess &&
                !command.Value.ResolveForDispatch().IsSuccess,"unknown command read keeps same original/no repeat");
            Check(loss.RegisterReads==1 && loss.DmaCopies==0 && loss.RegisterWrites==1 &&
                kernel.QueryBudget(sourceBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==576,"unknown command acquires no DMA backing or status");
            loss.LoseRegister=false;
            if(loss.TeardownCommand)_=kernel.FaultComponent(component);
            admittedCommand=command.Value.ResolveForDispatch();
            Check(loss.RegisterReads==1,"pure original command result without read repeat");
        }
        KernelResult<ComponentLifecycleSnapshot> current =kernel.QueryComponent(component);
        if(!current.IsSuccess || current.Value!.Process!=source || current.Value.State!=ComponentLifecycleState.Running)
        {
            pendingCleanup=true;
            return ()=>
            {
                KernelResult<ComponentLifecycleSnapshot> closed =kernel.ObserveComponentTeardown(component);
                Check(closed.IsSuccess && closed.Value!.Reclaimable,"late command component exact teardown");
                Check(command.Value.ResolveMetadata().IsSuccess && !command.Value.ResolveForDispatch().IsSuccess,"original metadata survives route retirement without dispatch permission/repin");
                Check(loss is {DmaCopies:0,RegisterWrites:1} && kernel.QueryBudget(sourceBudget).Value!.Usage.All(u=>u.Used==0),"late command no dispatch or repin/charge0");
            };
        }
        Check(admittedCommand.IsSuccess,admittedCommand.Message??"command read admission");
        if(admittedCommand.Value!=17)
            return ()=>Check(loss is {DmaCopies:0} && kernel.QueryBudget(sourceBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==576,
                "unsupported command performs no DMA acquisition/effect");
        OwnedBuffer<byte> input =kernel.AllocateBuffer<byte>(source,64).Value!;input.Span.Fill(0x53);
        CapabilityId mc =kernel.MintCapability(new(23040),source,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(input.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read).Value!.CapabilityId;
        KernelResult<PlatformOwnedRegionSliceMapping> sm =kernel.MapComponentDriverOwnedRegion(component,mc,input.Handle,0,64,PlatformMemoryAccess.Read);Check(sm.IsSuccess,sm.Message??"source map");
        KernelResult<PlatformDmaGrant> sg =kernel.BindComponentDriverDma(component,sm.Value,0,64,PlatformDmaDirection.DeviceReadsMemory);Check(sg.IsSuccess,sg.Message??"source component DMA");
        SingProcessManifestV1 manifest =new SingProcessManifestV1(new(2310),new(23100),1,"driver-dma-destination",ExecutionRole.Sip,MemoryProfile.SipRegion);
        Check(kernel.CreateProcess(manifest).IsSuccess,"destination create"); ProcessHandle destination =new ProcessHandle(manifest.ProcessId,1);
        CapabilityId bc =kernel.MintCapability(new(23020),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,bc,destination,"driver-dma-destination",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
        OwnedBuffer<byte> output =kernel.AllocateBuffer<byte>(destination,64).Value!;
        PlatformDomainBinding root =kernel.BindPlatformDomain(destination).Value;
        CapabilityId dc =kernel.MintCapability(new(23100),destination,ResourceKind.Device,"device/driver-dma-destination",CapabilityRights.Configure|CapabilityRights.Write).Value!.CapabilityId;
        PlatformDeviceLease device =kernel.BindPlatformDevice(destination,root,dc,PlatformDeviceRights.Configure|PlatformDeviceRights.Write).Value;
        CapabilityId omc =kernel.MintCapability(new(23100),destination,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(output.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Write).Value!.CapabilityId;
        PlatformOwnedRegionSliceMapping dm =kernel.MapPlatformOwnedRegionSlice(destination,root,omc,output.Handle,0,64,PlatformMemoryAccess.Write).Value;
        PlatformDmaGrant dg =kernel.BindPlatformDma(destination,device,dm,0,64,PlatformDmaDirection.DeviceWritesMemory).Value;
        Action? foreignSurvives=null,foreignCleanup=null;
        if(loss is {ForeignDmaMapping:true})
        {
            SingProcessManifestV1 fm =new SingProcessManifestV1(new(2311),new(23110),1,"independent-dma-peer",ExecutionRole.Sip,MemoryProfile.SipRegion);
            Check(kernel.CreateProcess(fm).IsSuccess,"foreign create"); ProcessHandle fp =new ProcessHandle(fm.ProcessId,1);
            BudgetAccountHandle fb =kernel.AdmitProcessBudget(admin,bc,fp,"foreign-dma-peer",[new(ServiceBudgetDimension.OwnedMemoryBytes,32)]).Value!.ProcessBudget;
            OwnedBuffer<byte> bytes =kernel.AllocateBuffer<byte>(fp,32).Value!;bytes.Span.Fill(99);
            PlatformDomainBinding fr =kernel.BindPlatformDomain(fp).Value;
            CapabilityId fc =kernel.MintCapability(new(23110),fp,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(bytes.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read).Value!.CapabilityId;
            KernelResult<PlatformOwnedRegionSliceMapping> mapping =kernel.MapPlatformOwnedRegionSlice(fp,fr,fc,bytes.Handle,0,32,PlatformMemoryAccess.Read);Check(mapping.IsSuccess,"foreign original native map");
            foreignSurvives=()=>Check(bytes.Span.ToArray().All(b=>b==99) && kernel.QueryBudget(fb).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==32 &&
                !kernel.ReleaseRegion(fp,bytes).IsSuccess,"foreign original mapping/backing/charge retained independently");
            foreignCleanup=()=>{foreignSurvives();Check(kernel.TerminateProcess(fp).IsSuccess,"foreign independent exact teardown");Check(kernel.QueryBudget(fb).Value!.Usage.All(u=>u.Used==0),"foreign independent charge0");};
        }
        V6ManagedLocalityCostProvider costs =new V6ManagedLocalityCostProvider(); long now =DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Check(costs.Publish(new(1,"driver-dma-source",1,now,now+60000,64,1,0,0)).IsSuccess,"source cost");
        Check(costs.Publish(new(1,"driver-dma-destination",1,now,now+60000,64,1,0,0)).IsSuccess,"destination cost");
        RegionDescriptor region =kernel.Regions.Validate(input.Handle,new(new(23040),source.Generation)).Value!;
        DataMotionPlanV1 plan =new DataMotionPlanV1(1,"driver-dma-source","driver-dma-destination",1,1,input.Handle.Generation.Value,region.MutationEpoch.Value,64,64,DataMotionModeV1.ProviderDmaThenReleaseSource);
        KernelResult<PlatformDmaPrepareEvidence> sp =kernel.PreparePlatformDmaForDevice(source,sg.Value); KernelResult<PlatformDmaPrepareEvidence> dp =kernel.PreparePlatformDmaForDevice(destination,dg);
        Check(sp.IsSuccess && dp.IsSuccess,"prepare both original DMA legs");
        ProviderDmaDataMotionRequest<byte> original =new ProviderDmaDataMotionRequest<byte>(source,destination,input,output,plan,sm.Value,sg.Value,sp.Value,dm,dg,dp.Value);
        using CancellationTokenSource dmaCancellation =new CancellationTokenSource();
        if(loss is {CancelDmaBefore:true})dmaCancellation.Cancel();
        if(loss is {CancelDmaAfter:true})loss.AfterDmaCopy=()=>dmaCancellation.Cancel();
        KernelResult<ProviderDmaDataMotionResult<byte>> moved =kernel.MoveProviderDmaBuffers(original,costs,dmaCancellation.Token);
        if(loss is not null)loss.AfterDmaCopy=null;
        if(loss is not null && (loss.CancelDmaBefore || loss.CancelDmaAfter && !loss.LoseDmaCopy))
        {
            int expectedCopies =loss.CancelDmaBefore?0:1;
            Check(!moved.IsSuccess && moved.Error==KernelError.CancellationPending && loss.DmaCopies==expectedCopies,"cancelled original effect count/no result");
            Check(!kernel.MoveProviderDmaBuffers(original,costs).IsSuccess && !kernel.ReadProviderDmaDataMotionResult(original).IsSuccess,"cancelled original no-repeat/no success");
            Check(kernel.CleanupProviderDmaDataMotion(original).IsSuccess,"cancelled original custody/grants exact cleanup");
            Check(kernel.RevokePlatformRegionMapping(source,sm.Value).IsSuccess && kernel.RevokePlatformRegionMapping(destination,dm).IsSuccess,"cancelled original mappings exact cleanup");
            Check(kernel.ReleaseRegion(source,input).IsSuccess,"cancelled source charge release after closure");
            foreignSurvives?.Invoke();
            return ()=>
            {
                Check(kernel.TerminateProcess(destination).IsSuccess && kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"cancelled destination reclaim");
                Check(loss.DmaCopies==expectedCopies && !kernel.ReadProviderDmaDataMotionResult(original).IsSuccess,"cancelled result not republished");
                foreignCleanup?.Invoke();
            };
        }
        if(loss is not null && (loss.LoseDmaCopy || loss.MalformedDmaCopy))
        {
            Check(!moved.IsSuccess && loss.DmaCopies==1,"unknown DMA does not publish success");
            if(loss.CancelDmaAfter)Check(moved.Error!=KernelError.CancellationPending,"unknown Copy error is not relabeled as cancellation/no-effect");
            Check(!kernel.MoveProviderDmaBuffers(original,costs).IsSuccess && !kernel.ReadProviderDmaDataMotionResult(original).IsSuccess,"same original no-repeat/no result");
            Check(!kernel.CleanupProviderDmaDataMotion(original).IsSuccess,"unknown query not containment");
            Check(kernel.QueryBudget(sourceBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==640 &&
                kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"unknown original charges retained independently");
            bool malformed =loss.MalformedDmaCopy;
            loss.LoseDmaCopy=false;
            loss.MalformedDmaCopy=false;
            loss.LoseGrantClose=loss.PartialDmaGrant;
            KernelResult cleanup =kernel.CleanupProviderDmaDataMotion(original);
            if(loss.LoseGrantClose)
            {
                Check(!cleanup.IsSuccess && loss.GrantCloses>0 && loss.GrantCloses<=2 && loss.DmaCopies==1,$"partial grant Close holds original/no repeat: closes={loss.GrantCloses}; {cleanup.Message}");
                Check(kernel.QueryBudget(sourceBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==640 &&
                    kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64 && loss.RegisterWrites==1,"partial grant retirement retains both charges/no status");
                loss.LoseGrantClose=false;
                cleanup=kernel.CleanupProviderDmaDataMotion(original);
                Check(loss.GrantCloses==2,"each original grant closes once; replay is query only");
            }
            if(malformed)
            {
                Check(!cleanup.IsSuccess && !kernel.ReadProviderDmaDataMotionResult(original).IsSuccess,"different raw object is not original continuity");
                Check(loss.RegisterWrites==1 && loss.DmaCopies==1,"malformed outcome cannot publish status/repeat Copy");
                foreignCleanup?.Invoke();
                pendingCleanup=true;
                return ()=>
                {
                    Check(!kernel.CleanupProviderDmaDataMotion(original).IsSuccess && loss.DmaCopies==1,"retained original remains pinned without continuity");
                    Check(kernel.QueryBudget(sourceBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==640 &&
                        kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"malformed original backing/charges retained");
                };
            }
            Check(cleanup.IsSuccess,"same original exact byte custody cleanup");
            Check(!kernel.ReadProviderDmaDataMotionResult(original).IsSuccess && loss.DmaCopies==1,"cleanup no success/repeat");
            Check(input.Span.Length==64 && output.Span.Length==64,"original caller buffers returned after closure");
            loss.LoseMappingClose=loss.PartialDmaMapping;
            KernelResult sourceClosed =kernel.RevokePlatformRegionMapping(source,sm.Value);
            if(loss.LoseMappingClose)
            {
                Check(!sourceClosed.IsSuccess && loss.MappingCloses==1,"unknown source mapping Close effect once");
                Check(!kernel.ObservePlatformRegionMappingRevocation(source,sm.Value.Mapping).IsSuccess,"unknown original mapping query not closure");
                Check(!kernel.ReleaseRegion(source,input).IsSuccess,"mapping guard retains source Region");
                Check(kernel.QueryBudget(sourceBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==640 &&
                    kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"partial mapping retirement retains independent charges");
                Check(loss.RegisterWrites==1,"no status publication before mapping proof");
                loss.LoseMappingClose=false;
                sourceClosed=kernel.ObservePlatformRegionMappingRevocation(source,sm.Value.Mapping);
                Check(loss.MappingCloses==1 && loss.DmaCopies==1,"same-original mapping query does not repeat effect");
            }
            Check(sourceClosed.IsSuccess && kernel.RevokePlatformRegionMapping(destination,dm).IsSuccess,"failed original mappings retirement after grant closure");
            Check(kernel.ReleaseRegion(source,input).IsSuccess,"failed source release after mapping closure");
            foreignSurvives?.Invoke();
            return ()=>
            {
                Check(kernel.TerminateProcess(destination).IsSuccess,"failed destination exact teardown");
                Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"failed destination charge0");
                Check(kernel.QueryBudget(sourceBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==576,"source MMIO/driver charge preserved");
                Check(loss.DmaCopies==1 && !kernel.ReadProviderDmaDataMotionResult(original).IsSuccess,"failure status consumption cannot publish result");
                foreignCleanup?.Invoke();
                Console.WriteLine("Original DMA reply lost: Copy1; unknown source640/destination64 retained; exact cleanup→failure status255/IRQ; destination charge0; no success result.");
            };
        }
        Check(moved.IsSuccess,moved.Message??"original DMA copy");
        Check(ReferenceEquals(moved.Value!.Buffer,output) && output.Span.ToArray().All(b=>b==0x53) && !input.IsValid,"actual bytes/source lifetime");
        Check(ReferenceEquals(kernel.ReadProviderDmaDataMotionResult(original).Value,moved.Value),"original result retained");
        ulong Used(BudgetAccountHandle account)=>kernel.QueryBudget(account).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Check(Used(sourceBudget)==576 && Used(budget)==64,"independent source640→576/destination64 retained");
        foreignSurvives?.Invoke();
        return ()=>
        {
            Check(output.Span.ToArray().All(b=>b==0x53),"destination survives status/typed IRQ");
            Check(kernel.TerminateProcess(destination).IsSuccess,"destination exact teardown");
            Check(Used(budget)==0 && Used(sourceBudget)==576,"destination64→0/source MMIO preserved");
            Check(ReferenceEquals(kernel.ReadProviderDmaDataMotionResult(original).Value,moved.Value) && !output.IsValid,"result read does not repin");
            foreignCleanup?.Invoke();
            Console.WriteLine("Component command→DMA64x53→status42→typed IRQ: source640→576; destination64→64→0; original result metadata no repin.");
        };
    }
}
