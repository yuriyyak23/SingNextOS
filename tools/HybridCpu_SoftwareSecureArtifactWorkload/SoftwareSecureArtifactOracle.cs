using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Compiler.Core.Target.Runtime;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using HybridCPU_ISE.Arch;
using YAKSys_Hybrid_CPU;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Diagnostics;

public static class SoftwareSecureArtifactOracle
{
    public static readonly string[] Schedules = ["positive", "wrong-operands", "unsupported", "lost-native-reply", "lost-projection-reply",
        "cancel-projection", "state-aba", "history-loss", "artifact-pointer-loss", "route-loss", "release-reentry", "copy-request",
        "cancel-reserved", "lost-reserved-reply"];
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(string schedule)
    {
        var model = new NeutralDomainRuntimeFacade();
        var parent = model.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var adapter = new HybridCpuExecutableChildAdapter();
        const NeutralChildAuthorityClass rights = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.GuestMemory | NeutralChildAuthorityClass.Execution;
        var childResult = adapter.CreateChildDomain(parent, new(new(1,16 * 1024 * 1024), new(rights,rights)));
        Check(childResult.IsSuccess, childResult.Reason); var child = childResult.Value;
        var source = model.MapOwnedRegion(parent, new(0,16 * 1024 * 1024,NeutralMemoryAccess.Read)).Lease;
        var mapResult = adapter.MapGuestRegion(new(child, source, new(0,16 * 1024 * 1024), NeutralGuestMemoryAccess.Read));
        Check(mapResult.IsSuccess, mapResult.Reason); var mapping = mapResult.Value;
        var image = Image();
        var artifactResult = adapter.BindExecutableArtifact(new(child, mapping, image.PackageBytes, 32));
        Check(artifactResult.IsSuccess, artifactResult.Reason); var artifact = artifactResult.Value;
        var request = new NeutralSoftwareSecureArtifactEntryRequest(child, artifact, Guid.NewGuid(), 1,
            image.InitialRegisters!.ReturnAddress + (schedule == "wrong-operands" ? 1UL : 0UL), image.InitialRegisters.StackPointer,
            schedule == "unsupported" ? 2U : 1U);
        Action? repair = null; int callbacks = 0;
        string hook = schedule == "lost-native-reply" ? "AfterSoftwareSecureArtifactNativeExecution" : "BeforeSoftwareSecureArtifactPublication";
        if (schedule is "cancel-reserved" or "lost-reserved-reply") hook = "BeforeSoftwareSecureArtifactNativeExecution";
        if (schedule is not ("positive" or "wrong-operands" or "unsupported" or "copy-request"))
            adapter.GetType().GetProperty(hook, Hidden)!.SetValue(adapter, (Action)(() =>
            {
                callbacks++;
                var fact = Fact(adapter, request);
                var childSource = Field(fact,"Child")!;
                if (schedule is "cancel-projection" or "cancel-reserved") Check(adapter.CancelSoftwareSecureArtifactEntry(request), "cancel original");
                if (schedule == "state-aba")
                {
                    var state = childSource.GetType().GetProperty("State")!;
                    state.SetValue(childSource,NeutralChildDomainState.Parked); state.SetValue(childSource,NeutralChildDomainState.Created);
                }
                if (schedule == "history-loss")
                {
                    var history = (IList)childSource.GetType().GetProperty("OriginalSoftwareArtifactEntries")!.GetValue(childSource)!;
                    var retained = history[0]; history.Clear(); repair = () => history.Add(retained);
                }
                if (schedule == "artifact-pointer-loss")
                {
                    var owner = Field(fact,"Artifact")!; var property = owner.GetType().GetProperty("SoftwareEntry")!;
                    var retained = property.GetValue(owner); property.SetValue(owner,null); repair = () => property.SetValue(owner,retained);
                }
                if (schedule == "route-loss")
                {
                    var routes = (IDictionary)adapter.GetType().GetField("artifacts",Hidden)!.GetValue(adapter)!;
                    var retained = routes[artifact.ArtifactHandle]; routes.Remove(artifact.ArtifactHandle); repair = () => routes.Add(artifact.ArtifactHandle,retained);
                }
                Check(!adapter.CloseChildDomain(child).IsSuccess, "active child pin");
                Check(!adapter.CloseExternalParent(new(Guid.NewGuid(),parent)).IsSuccess, "active parent pin");
                Check(!adapter.ReleaseExecutableArtifact(new(Guid.NewGuid(),artifact)).IsSuccess, "active release pin");
                Check(!adapter.UnmapGuestRegion(mapping).IsSuccess,"active mapping pin");
                if (schedule is "lost-native-reply" or "lost-projection-reply" or "lost-reserved-reply") throw new IOException("deterministic original reply loss");
            }));
        INeutralSoftwareSecureArtifactEntryProvider entry = adapter;
        var observed = entry.ExecuteSoftwareSecureArtifactEntry(request);
        adapter.GetType().GetProperty(hook,Hidden)!.SetValue(adapter,null);
        if (schedule == "unsupported")
        {
            Check(observed.Status == NeutralSoftwareSecureAdmissionStatus.Unsupported && observed.ResourcesClosed,"schema fail closed");
            Check(((IDictionary)adapter.GetType().GetField("softwareArtifactEntries",Hidden)!.GetValue(adapter)!).Count == 0,"no invocation");
        }
        else
        {
            if (schedule is "lost-native-reply" or "lost-projection-reply" or "history-loss" or "artifact-pointer-loss")
                Check(observed.Status == NeutralSoftwareSecureAdmissionStatus.CleanupRequired,"ambiguous response");
            if (schedule is "history-loss" or "artifact-pointer-loss")
            {
                Check(!adapter.ReleaseExecutableArtifact(new(Guid.NewGuid(),artifact)).IsSuccess,"history custody pin");
                Check(entry.ResolveSoftwareSecureArtifactEntry(request).Status == NeutralSoftwareSecureAdmissionStatus.CleanupRequired,"no hidden authority");
            }
            repair?.Invoke();
            var recovered = entry.ResolveSoftwareSecureArtifactEntry(request);
            bool noNative = schedule is "cancel-reserved" or "lost-reserved-reply";
            bool denied = noNative || schedule is "wrong-operands" or "cancel-projection" or "state-aba" or "route-loss";
            Check(recovered.Status == (denied ? NeutralSoftwareSecureAdmissionStatus.Denied : NeutralSoftwareSecureAdmissionStatus.RegisterLocalRetired), $"{schedule} status {recovered.Status}");
            Check(recovered.ResourcesClosed && !recovered.IsExecutionAllowed && ReferenceEquals(recovered.Request,request),"bounded facts");
            var fact = Fact(adapter,request);
            var provider = (SoftwareSecureComputeProvider)Field(fact,"Provider")!;
            Check(provider.OutstandingReservations == 0,"actual holds zero");
            var create = (ExternalSecureDomainCreateRequest)Field(fact,"Create")!;
            var created = provider.QueryCreate(create).Receipt!;
            var close = (ExternalOperationIdentity)Field(fact,"Close")!;
            Check(ReferenceEquals(created.Request,create) && provider.QueryClose(created.Domain,close).Receipt is { IsTerminal:true } terminal &&
                ReferenceEquals(terminal,provider.QueryClose(created.Domain,close).Receipt),"actual original domain closure");
            var reservation = (ExternalSecureExecutionReservation)Field(fact,"Reservation")!;
            Check(ReferenceEquals(provider.QueryRelease(reservation).Reservation,reservation),"actual original reservation release");
            var native = (ChildSoftwareSecureEntryObservation?)Field(fact,"NativeObservation");
            if (noNative) Check(native is null && !(bool)Field(fact,"NativeInvoked")! && (bool)Field(fact,"ReservationReleased")!,"original reservation released without native invocation");
            else Check(native!.ResourcesClosed,"native original cleanup");
            if (!noNative && schedule != "wrong-operands")
            {
                Check(native!.Result?.RegisterLocalRetirement is { EffectCount: 1 } retire &&
                    native.Register3After == retire.Register3Value && native.CoreValue == retire.Register3Value &&
                    native.Register3After == unchecked(request.Left + request.Right),"actual core sink");
                Check(native.RunId != Guid.Empty && native.ExecutionGeneration.Value > 0,"actual run identity");
                if (!denied) Check(recovered.Value == native.Register3After && recovered.Commit == 1 && recovered.MachineAttempt > 0,"retire projection");
            }
            else if (!noNative) Check(native!.Result?.RegisterLocalRetirement is null && native.Register3Before == native.Register3After,"denied operand sink unchanged");
            Check(ReferenceEquals(recovered,entry.ExecuteSoftwareSecureArtifactEntry(request)),"duplicate original no execution");
            Check(entry.ExecuteSoftwareSecureArtifactEntry(new(child,artifact,request.Operation,1,request.Left,request.Right)).Status == NeutralSoftwareSecureAdmissionStatus.CleanupRequired,"copied request refused");
            Check(!entry.CancelSoftwareSecureArtifactEntry(new(child,artifact,request.Operation,1,request.Left,request.Right)),"copy cannot cancel");
            Check(!adapter.StartExecutableArtifact(new(child,artifact,new(1),new(1))).IsSuccess,"ordinary replay refused");
            Check(entry.ExecuteSoftwareSecureArtifactEntry(new(child,artifact,Guid.NewGuid(),2,request.Left,request.Right)).Status == NeutralSoftwareSecureAdmissionStatus.Denied,"fresh operation cannot repeat");
        }
        Check(adapter.ReleaseExecutableArtifact(new(Guid.NewGuid(),artifact)).IsSuccess,"independent artifact release");
        Check(adapter.UnmapGuestRegion(mapping).IsSuccess,"independent mapping cleanup");
        Check(adapter.CloseChildDomain(child).IsSuccess,"independent child cleanup");
        Check(adapter.CloseExternalParent(new(Guid.NewGuid(),parent)).IsSuccess,"independent parent cleanup");
        Console.WriteLine($"PASS neutral-artifact={schedule} callbacks={callbacks}");
    }
    static object Fact(HybridCpuExecutableChildAdapter adapter, NeutralSoftwareSecureArtifactEntryRequest request) =>
        ((IDictionary)adapter.GetType().GetField("softwareArtifactEntries",Hidden)!.GetValue(adapter)!)[request.Operation]!;
    static object? Field(object source,string name) => source.GetType().GetField(name,Hidden)!.GetValue(source);
    static void Check(bool value,string reason) { if(!value)throw new InvalidOperationException(reason); }
    public static HybridCpuRestrictedImageV1 Image()
    {
        var bundle = new VLIW_Instruction[8];
        bundle[2] = new() { OpCode = Processor.CPU_Core.IsaOpcodeValues.ADD, Word1 = VLIW_Instruction.PackArchRegs(3,1,2) };
        byte[] bytes = MemoryMarshal.AsBytes(bundle.AsSpan()).ToArray(); const ulong address = 0x10000;
        var link = new HybridCpuStaticLinkArtifactV1(HybridCpuLinkStatusV1.Success,address,bytes,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),new string('0',64),HybridCpuRestrictedStartupOptionsV1.Production.LinkOptionsDigest,
            [new("entry",".text",HybridCpuObjectSectionKind.Code,address,(ulong)bytes.Length,256)],
            [new("Main","entry",HybridCpuSymbolBinding.Global,HybridCpuSymbolVisibility.Default,address,(ulong)bytes.Length)],[],[]);
        var image = new HybridCpuRestrictedImageBuilderV1().Build(new(link,"Main"));
        Check(image.Status == HybridCpuStartupStatusV1.Success,"public ordinary image builder"); return image;
    }
}
