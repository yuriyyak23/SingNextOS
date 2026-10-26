using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

internal static class SupervisorMmioHost
{
    private sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }
    public static int Run(string[] args)
    {
        try
        {
            void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
            NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade();
            IIrqReplyLossRuntime proxy =System.Reflection.DispatchProxy.Create<IIrqReplyLossRuntime,IrqReplyLossRuntime>();
            IrqReplyLossRuntime loss =(IrqReplyLossRuntime)(object)proxy;loss.Inner=native;
            Clock clock =new Clock(); RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy),clock);
            SingProcessManifestV1 principalManifest =new SingProcessManifestV1(new(2400),new(24000),1,"mmio-supervisor",ExecutionRole.Sip,MemoryProfile.SipRegion);
            Check(kernel.CreateProcess(principalManifest).IsSuccess,"principal create"); ProcessHandle principal =new ProcessHandle(new(2400),1);
            Check(kernel.AdmitProcess(principal).IsSuccess,"principal admit");
            CapabilityId control =kernel.MintCapability(new(24000),principal,ResourceKind.KernelService,CapabilityResourceIds.ServiceSupervisor,CapabilityRights.Configure|CapabilityRights.Execute).Value!.CapabilityId;
            CapabilityAwareServiceSupervisor supervisor =kernel.CreateServiceSupervisor(principal,control,clock).Value!;
            ComponentIdentity identity =new ComponentIdentity("supervised-register-driver");const string device="device/supervised-register-driver";
            string resource =CapabilityResourceIds.MmioRegion(device,"registers",64);
            ServiceContractIdentity contract =new ServiceContractIdentity("RegisterDriver","1","register-driver-v1");
            ProvidedServiceManifestV1 provided =new ProvidedServiceManifestV1(identity.Name,contract);
            ProtocolDefinitionV1 protocol =new ProtocolDefinitionV1(contract.Name,contract.Digest,"Idle",["Done"],[new(1,"Invoke")],[new(1,"Idle","Done")]);
            ServiceRestartPolicyV1 restart =new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure,2,1,10,1000);
            bool restoreMode =args.Contains("--restore-mmio",StringComparer.Ordinal);
            ComponentAdmissionPlan Plan(ulong generation)
            {
                if(restoreMode && generation==1)
                {
                    byte[] ordinaryImage=[0x53,0x4d,1];
                    SingProcessManifestV1 ordinary =new SingProcessManifestV1(new(2401),new(24010),1,"ordinary-restore-source",ExecutionRole.Sip,MemoryProfile.SipRegion);
                    ServiceManifestV1 ordinaryManifest =new ServiceManifestV1(identity,new("1"),Convert.ToHexString(SHA256.HashData(ordinaryImage)).ToLowerInvariant(),ordinary,[provided],
                        budgetRequests:[new(ServiceBudgetDimension.CheckpointStorageBytes,64)],checkpointPolicy:new(ServiceCheckpointMode.PlannedReplacementOnly));
                    return new(ordinaryManifest,ordinaryImage,providedServices:[new(provided,protocol)]);
                }
                CapabilityRequirementV1[] requirements=[new(ResourceKind.Device,device,CapabilityRights.Configure|CapabilityRights.Read|CapabilityRights.Write),new(ResourceKind.MmioRegion,resource,CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write)];
                byte[] image=[0x53,0x4d,restoreMode?(byte)1:(byte)generation];
                SingProcessManifestV1 process =new SingProcessManifestV1(new(2401),new(24010),generation,"supervised-register-driver",ExecutionRole.Driver,MemoryProfile.SipRegion,requirements);
                ServiceManifestV1 manifest =new ServiceManifestV1(identity,new("1"),Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),process,[provided],restartPolicy:restart,requiresPlatformDomain:true,
                    platformRequirements:[new(ComponentPlatformFeatureFamily.NeutralDomains,PlatformDomainContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission),new(ComponentPlatformFeatureFamily.IoDomainBinding,PlatformDeviceLeaseContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission),new(ComponentPlatformFeatureFamily.MmioMapping,PlatformMmioLeaseContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission)],budgetRequests:[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]);
                return new(manifest,image,requirements.Select(r=>new ComponentCapabilityGrant(new(24010),r)),[new(provided,protocol)],driverResources:new(device,PlatformDeviceRights.Configure|PlatformDeviceRights.Read|PlatformDeviceRights.Write,[new(resource,0,64,PlatformMmioAccess.Read|PlatformMmioAccess.Write,SoftwareRegisterBacking:true)]));
            }
            Check(supervisor.Register(new(Plan(1),identity.Name,replacementFactory:Plan)).IsSuccess,"register");
            if(args.Contains("--lost-supervisor-mmio-map-reply",StringComparer.Ordinal))
            {
                loss.LoseMmioMap=true; KernelResult<ServiceStartReceipt> failed =supervisor.Start(identity);Check(!failed.IsSuccess,"unknown Map refuses Start");
                ComponentLifecycleSnapshot held =kernel.QueryComponent(identity).Value!;
                Check(supervisor.Query(identity).Value!.Instance is null,"no fabricated service instance");
                _=supervisor.Observe(identity);
                Check(kernel.QueryComponent(identity).Value!.State!=ComponentLifecycleState.Reclaimable,"unknown original is not closure");
                Check(loss.MmioMaps==1,"no repeated Map");
                Check(kernel.QueryBudget(held.ProcessBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"unknown Map retains backing charge");
                loss.LoseMmioMap=false; KernelResult<ServiceSupervisorSnapshot> cleaned =supervisor.Observe(identity);Check(cleaned.IsSuccess,cleaned.Message??"failed admission cleanup");
                Check(cleaned.Value!.State==ServiceLifecycleState.Failed && cleaned.Value.Instance is null,"failure metadata survives cleanup");
                Check(kernel.QueryComponent(identity).Value!.State==ComponentLifecycleState.Reclaimable,"failed admission exact reclaim");
                Check(kernel.QueryBudget(held.ProcessBudget).Value!.Usage.All(u=>u.Used==0),"failed admission budget reclaimed");
                Check(kernel.Regions.SnapshotUses().Single().State==RegionUseState.Released,"failed admission original use released");
                Check(loss.MmioMaps==1 && loss.MmioCloses==1,"Map1 Close1 no repeat");
                Console.WriteLine("Supervisor failed Start: lost Map1, no Instance, backing64 retained; original Observe Close1/use released/charge64→0; Failed retained. ISA NONE, gates OFF.");return 0;
            }
            KernelResult<ServiceStartReceipt> started =supervisor.Start(identity);Check(started.IsSuccess,started.Message??"start");
            ServiceInstanceHandle original =started.Value!.Snapshot.Instance!.Value; ComponentLifecycleSnapshot old =kernel.QueryComponent(identity).Value!;
            if(restoreMode)
            {
                CapabilityId checkpointCapability =kernel.MintCapability(new(24000),principal,ResourceKind.KernelService,CapabilityResourceIds.CheckpointAdministration,CapabilityRights.Configure).Value!.CapabilityId;
                KernelResult<OrdinaryCheckpointImage> checkpoint =kernel.CreateOrdinaryCheckpoint(principal,checkpointCapability,identity,new byte[]{17,42});Check(checkpoint.IsSuccess,checkpoint.Message??"checkpoint");
                Check(supervisor.Drain(original).Value!.ClosureProven,"ordinary source reclaim");
                bool revokeSource =args.Contains("--revoke-restore-mmio-source",StringComparer.Ordinal);
                bool deleteSource =args.Contains("--delete-restore-mmio-source",StringComparer.Ordinal);
                if(revokeSource || deleteSource)
                {
                    byte[] foreignImage=[0x46];
                    ServiceManifestV1 foreignManifest =new ServiceManifestV1(new("foreign-restore-peer"),new("1"),Convert.ToHexString(SHA256.HashData(foreignImage)).ToLowerInvariant(),
                        new SingProcessManifestV1(new(2402),new(24020),1,"foreign-peer",ExecutionRole.Sip,MemoryProfile.SipRegion),budgetRequests:[new(ServiceBudgetDimension.OwnedMemoryBytes,32)]);
                    ComponentLifecycleSnapshot foreign =kernel.AdmitComponent(new(foreignManifest,foreignImage)).Value!;
                    OwnedBuffer<byte> bytes =kernel.AllocateBuffer<byte>(foreign.Process,32).Value!;bytes.Span.Fill(99);
                    loss.LoseMmioClose=true;
                    loss.AfterMmioMap=()=>
                    {
                        loss.AfterMmioMap=null;
                        Check(revokeSource?kernel.RevokeCapability(checkpointCapability).IsSuccess:
                            kernel.DeleteOrdinaryCheckpoint(principal,checkpointCapability,checkpoint.Value!.Handle).IsSuccess,"source invalidation after effect");
                    };
                    KernelResult<OrdinaryRestoreResult> refused =kernel.RestoreOrdinaryCheckpoint(principal,checkpointCapability,checkpoint.Value!,Plan(2));
                    Check(!refused.IsSuccess,"late source denies restore receipt");
                    ComponentLifecycleSnapshot retained =kernel.QueryComponent(identity).Value!;
                    Check(!retained.Reclaimable && kernel.QueryBudget(retained.ProcessBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"unknown Close retains target charge64");
                    Check(loss.MmioMaps==1 && loss.MmioCloses==1,"late source original effect counts");
                    loss.LoseMmioClose=false;
                    Check(kernel.ObserveComponentTeardown(identity).Value!.Reclaimable,"cleanup after checkpoint authority loss");
                    Check(kernel.QueryBudget(retained.ProcessBudget).Value!.Usage.All(u=>u.Used==0),"target exact charge0");
                    Check(bytes.Span.ToArray().All(b=>b==99) && kernel.QueryBudget(foreign.ProcessBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==32,"foreign backing/charge survives");
                    Check(loss.MmioMaps==1 && loss.MmioCloses==1,"cleanup no repeat");
                    Check(kernel.FaultComponent(foreign.Identity).Value!.Reclaimable,"foreign independent cleanup");
                    Console.WriteLine("Restore source lost after Map1: receipt denied, Close1 reply lost pins64; permission-independent original cleanup64→0; foreign bytes99/charge32 survives. ISA NONE, gates OFF.");return 0;
                }
                loss.LoseMmioMap=args.Contains("--lost-restore-mmio-map-reply",StringComparer.Ordinal);
                KernelResult<OrdinaryRestoreResult> restored =kernel.RestoreOrdinaryCheckpoint(principal,checkpointCapability,checkpoint.Value!,Plan(2));
                if(loss.LoseMmioMap)
                {
                    Check(!restored.IsSuccess,"unknown restore Map refuses receipt");
                    ComponentLifecycleSnapshot target =kernel.QueryComponent(identity).Value!;
                    Check(target.Process.Generation==2 && !target.Reclaimable,"original target retained");
                    Check(kernel.QueryBudget(target.ProcessBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"restore backing charge retained");
                    loss.LoseMmioMap=false;Check(kernel.ObserveComponentTeardown(identity).Value!.Reclaimable,"original restore rollback");
                    Check(kernel.QueryBudget(target.ProcessBudget).Value!.Usage.All(u=>u.Used==0),"restore rollback charge0");
                }
                else
                {
                    Check(restored.IsSuccess,restored.Message??"restore");
                    Check(restored.Value!.Receipt.LogicalState.SequenceEqual(new byte[]{17,42}),"logical bytes only");
                    PlatformMmio32Invocation freshRead =kernel.CreateComponentDriverMmio32Invocation(identity,resource,0,false).Value!;
                    Check(kernel.AccessComponentDriverMmio32(identity,freshRead).Value==0,"new MMIO backing");
                    Check(kernel.FaultComponent(identity).Value!.Reclaimable,"fresh target reclaim");
                    Check(kernel.QueryBudget(restored.Value.Component.ProcessBudget).Value!.Usage.All(u=>u.Used==0),"restore target charge0");
                }
                Check(loss.MmioMaps==1 && loss.MmioCloses==1,"restore Map1 Close1 no repeat");
                Console.WriteLine("Ordinary SIP checkpoint → fresh driver admission: original Map1/Close1, new backing64→0; no MMIO state serialization. ISA NONE, gates OFF.");return 0;
            }
            PlatformMmio32Invocation write =kernel.CreateComponentDriverMmio32Invocation(identity,resource,0,true,42).Value!;
            Check(kernel.AccessComponentDriverMmio32(identity,write).IsSuccess,"write");
            loss.LoseMmioClose=args.Contains("--lost-supervisor-mmio-close-reply",StringComparer.Ordinal);
            Check(supervisor.ReportHealth(original,ServiceHealthState.Failed,"driver failed after command").IsSuccess,"health failure");
            if(loss.LoseMmioClose)
            {
                KernelResult<ServiceSupervisorSnapshot> pending =supervisor.Observe(identity);Check(pending.IsSuccess,"pending observe");
                Check(pending.Value!.Instance==original && pending.Value.State!=ServiceLifecycleState.Ready,"unknown Close cannot replace");
                Check(kernel.QueryBudget(old.ProcessBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"unknown Close holds charge");
                Check(kernel.Regions.SnapshotUses().Single().State!=RegionUseState.Released,"unknown Close holds original use");
                Check(loss.MmioCloses==1,"unknown Close no repeat");loss.LoseMmioClose=false;
            }
            KernelResult<ServiceSupervisorSnapshot> drained =supervisor.Observe(identity);Check(drained.IsSuccess,drained.Message??"drain");
            Check(kernel.QueryComponent(identity).Value!.State==ComponentLifecycleState.Reclaimable,"exact predecessor reclaim");
            Check(kernel.QueryBudget(old.ProcessBudget).Value!.Usage.All(u=>u.Used==0),"old charge reclaimed");
            Check(kernel.Regions.SnapshotUses().Single().State==RegionUseState.Released,"old use released");
            clock.Now=1; KernelResult<ServiceSupervisorSnapshot> replacement =supervisor.Observe(identity);Check(replacement.IsSuccess,replacement.Message??"restart");
            Check(replacement.Value!.State==ServiceLifecycleState.Ready && replacement.Value.Instance!.Value.Process.Generation==original.Process.Generation+1,"fresh generation restart");
            Check(!kernel.AccessComponentDriverMmio32(identity,write).IsSuccess,"old original cannot execute on replacement");
            ComponentLifecycleSnapshot next =kernel.QueryComponent(identity).Value!;
            PlatformMmio32Invocation read =kernel.CreateComponentDriverMmio32Invocation(identity,resource,0,false).Value!;
            KernelResult<uint> value =kernel.AccessComponentDriverMmio32(identity,read);Check(value.IsSuccess && value.Value==0,"fresh backing bytes");
            Check(kernel.FaultComponent(identity).IsSuccess,"replacement closure");
            Check(kernel.QueryBudget(next.ProcessBudget).Value!.Usage.All(u=>u.Used==0),"replacement charge reclaimed");
            Check(loss.RegisterWrites==1 && loss.RegisterReads==1 && loss.MmioCloses==2,"original effects and exact closes");
            Console.WriteLine("Supervisor MMIO: generation1 write42; unknown Close pins64/no repeat; exact reclaim64→0; generation2 fresh read0; reclaim64→0; Write1/Read1/Close2. ISA NONE, gates OFF.");return 0;
        }
        catch(Exception exception){Console.Error.WriteLine(exception);return 1;}
    }
}
