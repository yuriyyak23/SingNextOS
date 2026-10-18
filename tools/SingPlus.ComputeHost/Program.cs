using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Platform.Host;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using YAKSys_Hybrid_CPU.Core;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;

var referenceModel=args.Contains("--reference-model",StringComparer.Ordinal);
var native=new NeutralDomainRuntimeFacade();
var lostReply=args.Contains("--lost-copy-reply",StringComparer.Ordinal);
if(referenceModel&&lostReply)throw new ArgumentException("Lost byte reply requires original software backing producer.");
ComputeReplyLossRuntime? observationLoss=null;
INeutralDomainRuntime runtime=native;
if(lostReply)
{
    var proxy=global::System.Reflection.DispatchProxy.Create<IComputeReplyLossRuntime,ComputeReplyLossRuntime>();
    observationLoss=(ComputeReplyLossRuntime)(object)proxy;observationLoss.Native=native;observationLoss.LoseResponses=true;runtime=proxy;
}
IPlatformAuthorityProvider provider=referenceModel ? new HostPlatformAuthorityProvider() : new HybridCpuPlatformAuthorityProvider(runtime);
var kernel=new RuntimeKernel(provider);
void Check(bool value,string operation){if(!value)throw new InvalidOperationException(operation);}
ProcessHandle Create(ulong id,ulong domain)
{
    var manifest=new SingProcessManifestV1(new(id),new(domain),1,$"compute-host-{id}",ExecutionRole.Sip,MemoryProfile.SipRegion);
    var result=kernel.CreateProcess(manifest);Check(result.IsSuccess,result.Message??"create");return new(manifest.ProcessId,manifest.Generation);
}
var admin=Create(2401,24010);var caller=Create(2402,24020);
var ac=kernel.MintCapability(new(24010),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
var callerBudget=kernel.AdmitProcessBudget(admin,ac,caller,"compute-caller",[
    new(ServiceBudgetDimension.OwnedMemoryBytes,64),new(ServiceBudgetDimension.ExternalOperations,4),
    new(ServiceBudgetDimension.IpcMessages,4),new(ServiceBudgetDimension.IpcBytes,4096)]).Value!.ProcessBudget;
var requirement=new CapabilityRequirementV1(ResourceKind.Compute,CapabilityResourceIds.Dsc1Copy,CapabilityRights.Execute);
var protocol=ComputeServiceClient.CreateProtocol();var contract=new ServiceContractIdentity(protocol.ContractName,"1",protocol.ContractDigest);
var registration=new ProvidedServiceManifestV1("compute",contract);byte[] image=[0x43,0x50,0x55];
var identity=new ComponentIdentity("software-compute-host");
var process=new SingProcessManifestV1(new(2403),new(24030),1,"software-compute-host",ExecutionRole.Sip,MemoryProfile.SipRegion,[requirement]);
var manifest=new ServiceManifestV1(identity,new ComponentVersion("1"),Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),process,[registration],requiresPlatformDomain:true,
    budgetRequests:[new(ServiceBudgetDimension.OwnedMemoryBytes,referenceModel ? 96UL : 128UL),new(ServiceBudgetDimension.ExternalOperations,4),new(ServiceBudgetDimension.IpcMessages,4),new(ServiceBudgetDimension.IpcBytes,4096)]);
var admitted=kernel.AdmitComponent(new ComponentAdmissionPlan(manifest,image,[new ComponentCapabilityGrant(new(24010),requirement)],
    [new ComponentProvidedServiceRegistration(registration,protocol,ComputeServiceClient.ResponseProtocol,[requirement])]));
Check(admitted.IsSuccess,admitted.Message??"component admission");var serviceBudget=admitted.Value!.ProcessBudget;
var capability=kernel.MintCapability(new(24020),caller,ResourceKind.Compute,CapabilityResourceIds.Dsc1Copy,CapabilityRights.Execute).Value!.CapabilityId;
var source=kernel.AllocateBuffer<byte>(caller,32).Value!;var destination=kernel.AllocateBuffer<byte>(caller,32).Value!;
var sourceHandle=source.Handle;var destinationHandle=destination.Handle;source.Span.Fill(0x5A);
var descriptor=kernel.ResolveByContract(contract).Value!.Single();var opened=kernel.OpenSession(caller,descriptor,[capability]);Check(opened.IsSuccess,opened.Message??"open");var session=opened.Value;
var composed=RuntimeComputeServiceHost.CreateForComponent(kernel,identity,session);Check(composed.IsSuccess,composed.Message??"host");var host=composed.Value!;
var client=ComputeServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
var pending=client.CopyAsync(source,destination).AsTask();var progressed=host.ProcessNextCopy();
if(observationLoss is not null)
{
    ulong Charge(BudgetAccountHandle account)=>kernel.QueryBudget(account).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
    Check(!progressed.IsSuccess && observationLoss.Copies==1,"unknown actual software submit");
    Check(host.HasPendingCopy && !pending.IsCompleted && Charge(serviceBudget)==128,"original service custody retained");
    Check(!host.AdvancePendingCopy().IsSuccess && observationLoss.Copies==1,"unknown effect no-repeat");
    observationLoss.LoseResponses=false;
    Check(!host.AdvancePendingCopy().IsSuccess && !host.HasPendingCopy,"original failed request cleanup remains failure");
    try {await pending.WaitAsync(TimeSpan.FromSeconds(2));throw new InvalidOperationException("Failed original request unexpectedly published output");}
    catch(OperationCanceledException) { }
    Check(source.IsValid && source.Handle==sourceHandle && source.Span[0]==0x5A,"original caller borrow returned");
    Check(!destination.IsValid && Charge(serviceBudget)==0 && Charge(callerBudget)==32,"failed destination reclaimed independently");
    Check(kernel.CloseSession(caller,session).IsSuccess,"failed session Close");
    Check(kernel.FaultComponent(identity).IsSuccess,"failed component cleanup");
    var callerClosed=kernel.TerminateProcess(caller);
    if(!callerClosed.IsSuccess)Console.WriteLine(kernel.QueryProcessTeardown(caller).Value);
    Check(callerClosed.IsSuccess,callerClosed.Message??"failed caller cleanup");
    Check(Charge(serviceBudget)==0 && Charge(callerBudget)==0,"failed quantitative lifetime cleanup");
    Check(native.ActiveOwnedRegionMappingCount==0 && native.ActiveBindingCount==0 && observationLoss.Copies==1,"failed native original closure without repeat");
    Check(kernel.Regions.Snapshot().All(r=>r.State==RegionState.Released),"failed Regions released");
    Console.WriteLine("Compute original failed response: Copy1; service128->0; caller64->32->0; original borrow returned; destination unpublished; Regions/native0; gates=OFF; ISA=NONE; ProductionQualified=false");
    return;
}
Check(progressed.IsSuccess,progressed.Message??"copy dispatch");var returned=await pending;
Check(returned.Span.ToArray().All(b=>b==0x5A),"actual copied bytes");
Check(returned.Handle.RegionId==destinationHandle.RegionId && returned.Handle.Generation.Value==destinationHandle.Generation.Value+2,"original destination ingress/return generations");
Check(source.IsValid && source.Handle==sourceHandle && source.Span[0]==0x5A,"original borrow returned");
Check(!destination.IsValid,"original moved wrapper consumed");
if(referenceModel)Check(((HostPlatformAuthorityProvider)provider).SubmitDsc1CopyCallCount==1,"one lifecycle submission");
else Check(PlatformOriginalDsc1CopyBackingContract.Supports(((IPlatformFeatureProvider)provider).QueryFeatures().Resolve(PlatformFeatureFamily.Dsc1BulkCompute)),"explicit software backing profile");
ulong Used(BudgetAccountHandle budget)=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
Check(Used(callerBudget)==64 && Used(serviceBudget)==0,"staging reclaimed and destination returned");
Check(kernel.CloseSession(caller,session).IsSuccess,"session Close");
Check(kernel.FaultComponent(identity).IsSuccess,"component cleanup");Check(kernel.TerminateProcess(caller).IsSuccess,"caller cleanup");
Check(Used(callerBudget)==0 && Used(serviceBudget)==0,"quantitative lifetime closure");
Check(kernel.Regions.Snapshot().All(r=>r.State==RegionState.Released),"all buffer/staging Regions released");
if(!referenceModel)Check(native.ActiveOwnedRegionMappingCount==0 && native.ActiveBindingCount==0,"original native mappings/domain closed");
Console.WriteLine($"Compute software host: bytes=32x5A; producer={(referenceModel ? "runtime-reference/lifecycle-ModelOnly" : "neutral-software-backing/RuntimeAdmission")}; borrow=returned; destination=original; caller64->0; staging/service=0; Regions=Released; gates=OFF; ISA=NONE; ProductionQualified=false");
