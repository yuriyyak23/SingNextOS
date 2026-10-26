using HybridCPU.Compiler.Core.Target.Runtime;
using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunChildDomain(EmulatorSession session, bool execute = false)
    {
        if (!session.Options.HybridCpuChildPlatform || session.Runtime is not { } runtime)
            return KernelBootStageResult.Failure("child-domain requires --platform hybridcpu-child.");
        KernelResult<ProcessHandle> spawned = session.SpawnProcess("singplus.emulator.child", ExecutionRole.User, MemoryProfile.ManagedGc, start: false);
        if (!spawned.IsSuccess) return Failure("admit owner", spawned.Error, spawned.Message);
        ProcessHandle owner = spawned.Value;
        KernelResult<SingProcess> process = runtime.Processes.Resolve(owner);
        if (!process.IsSuccess) return Failure("resolve", process.Error, process.Message);
        KernelResult<CapabilityDescriptorV1> minted = runtime.MintCapability(process.Value!.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure);
        if (!minted.IsSuccess) return Failure("mint create", minted.Error, minted.Message);
        KernelResult<VirtualDomainAuthoritySet> created = runtime.CreateVirtualDomain(owner, minted.Value!.CapabilityId, new VirtualDomainProfile(1, 16 * 1024 * 1024));
        if (created.IsSuccess)
        {
            KernelResult configured = runtime.ConfigureVirtualDomain(owner, created.Value.Domain, created.Value.ConfigureCapability);
            if (!configured.IsSuccess) return Failure("configure", configured.Error, configured.Message);
            if (execute)
            {
                byte[] package;
                try
                {
                    using FileStream image = new FileStream(session.Options.GuestArtifactPath ?? Path.Combine(AppContext.BaseDirectory, "guest.hce"), FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (image.Length is <= 0 or > 65536) return KernelBootStageResult.Failure("Guest sample image exceeds bounded package size.");
                    package = new byte[checked((int)image.Length)];
                    image.ReadExactly(package);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { return KernelBootStageResult.Failure($"Guest artifact read failed: {exception.Message}"); }
                HybridCpuRestrictedImageV1 inspected = new HybridCpuRestrictedImageBuilderV1().Inspect(package);
                if (!inspected.StartupContractSatisfied)
                    return KernelBootStageResult.Failure($"HCEXE preflight refused ({inspected.Status}): {string.Join("; ", inspected.Diagnostics)}; no guest mapping or artifact bind attempted.");
                KernelResult<BufferRecord> allocated = session.AllocateTrackedBuffer(owner, 16 * 1024 * 1024);
                if (!allocated.IsSuccess) return Failure("allocate guest memory", allocated.Error, allocated.Message);
                OwnedBuffer<byte> buffer = allocated.Value!.Buffer;
                KernelResult<CapabilityDescriptorV1> capability = runtime.MintCapability(process.Value.DomainId, owner, ResourceKind.MemoryRegion,
                    CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Execute);
                if (!capability.IsSuccess) return Failure("guest memory capability", capability.Error, capability.Message);
                KernelResult<GuestRegionMapping> mapping = runtime.MapGuestRegion(owner, created.Value.Domain, created.Value.MemoryCapability,
                    capability.Value!.CapabilityId, buffer.Handle, new GuestAddressRange(0, buffer.Length),
                    GuestMemoryAccess.Read | GuestMemoryAccess.Write | GuestMemoryAccess.Execute);
                if (!mapping.IsSuccess) return Failure("guest map", mapping.Error, mapping.Message);
                KernelResult bound = runtime.BindVirtualDomainExecutable(owner, created.Value.Domain, created.Value.ExecuteCapability,
                    mapping.Value.Mapping, package, 100000);
                if (!bound.IsSuccess) return Failure("bind guest artifact", bound.Error, bound.Message);
                KernelResult started = runtime.StartVirtualDomain(owner, created.Value.Domain, created.Value.ExecuteCapability);
                if (!started.IsSuccess) return Failure("start guest artifact", started.Error, started.Message);
                KernelResult<VirtualDomainState> state = runtime.QueryVirtualDomain(owner, created.Value.Domain);
                if (!state.IsSuccess || state.Value != VirtualDomainState.Running)
                    return KernelBootStageResult.Failure("Guest execution admission did not reach Running.");
                EmulatorConsole.StageDetail("real HCEXE sample bound and started through RuntimeKernel/ExternalRuntime V3; Running is lifecycle evidence, not a returned CLR result");
            }
            KernelResult closed = runtime.DestroyVirtualDomain(owner, created.Value.Domain, created.Value.ConfigureCapability);
            if (!closed.IsSuccess) return Failure("destroy", closed.Error, closed.Message);
            EmulatorConsole.StageDetail("actual child-domain create/configure/destroy completed; no artifact execution claimed");
        }
        else
        {
            if (execute) return Failure("create executable child", created.Error, created.Message);
            if (created.Error is not (KernelError.PlatformUnsupported or KernelError.PlatformDenied))
                return Failure("create child domain", created.Error, created.Message);
            EmulatorConsole.StageDetail($"actual child-domain admission refused: {created.Error}: {created.Message}; feature checks preserved");
        }
        KernelResult reclaimed = session.TerminateAndReclaim(owner);
        if (!reclaimed.IsSuccess) return Failure("reclaim owner and retained parent binding", reclaimed.Error, reclaimed.Message);
        EmulatorConsole.StageDetail("child-domain probe owner and retained parent binding reclaimed");
        return KernelBootStageResult.Success();
    }
}
