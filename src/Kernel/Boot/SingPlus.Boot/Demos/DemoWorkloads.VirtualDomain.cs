using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunVirtualDomain(EmulatorSession session)
    {
        if (!session.Options.HostModelPlatform || session.Runtime is not { } runtime)
            return KernelBootStageResult.Failure("virtual-domain requires --platform host-model; model evidence only.");
        KernelResult<ProcessHandle> admitted = session.SpawnProcess("singplus.emulator.virtual-domain", ExecutionRole.User, MemoryProfile.ManagedGc, start: false);
        if (!admitted.IsSuccess) return Failure("admit virtual owner", admitted.Error, admitted.Message);
        ProcessHandle owner = admitted.Value;
        KernelResult<SingProcess> process = runtime.Processes.Resolve(owner);
        if (!process.IsSuccess) return Failure("resolve owner", process.Error, process.Message);
        KernelResult<CapabilityDescriptorV1> cap = runtime.MintCapability(process.Value!.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure);
        if (!cap.IsSuccess) return Failure("mint create authority", cap.Error, cap.Message);
        KernelResult<VirtualDomainAuthoritySet> created = runtime.CreateVirtualDomain(owner, cap.Value!.CapabilityId, new VirtualDomainProfile(1, 4096));
        if (!created.IsSuccess) return Failure("create virtual domain", created.Error, created.Message);
        VirtualDomainAuthoritySet vm = created.Value;
        KernelResult configured = runtime.ConfigureVirtualDomain(owner, vm.Domain, vm.ConfigureCapability);
        if (!configured.IsSuccess) return Failure("configure virtual domain", configured.Error, configured.Message);
        KernelResult<BufferRecord> allocated = session.AllocateTrackedBuffer(owner, 256);
        if (!allocated.IsSuccess) return Failure("allocate guest region", allocated.Error, allocated.Message);
        OwnedBuffer<byte> buffer = allocated.Value!.Buffer;
        buffer.Span.Fill(0x5A);
        KernelResult<CapabilityDescriptorV1> memory = runtime.MintCapability(process.Value.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);
        if (!memory.IsSuccess) return Failure("mint memory authority", memory.Error, memory.Message);
        KernelResult<GuestRegionMapping> mapped = runtime.MapGuestRegion(owner, vm.Domain, vm.MemoryCapability, memory.Value!.CapabilityId,
            buffer.Handle, new GuestAddressRange(0, 256), GuestMemoryAccess.Read | GuestMemoryAccess.Write);
        if (!mapped.IsSuccess) return Failure("map guest region", mapped.Error, mapped.Message);
        KernelResult pinned = runtime.ReleaseRegion(owner, buffer);
        if (pinned.IsSuccess || pinned.Error != KernelError.PlatformBindingActive)
            return KernelBootStageResult.Failure($"Guest mapping failed to retain region: {pinned.Error}.");
        KernelResult started = runtime.StartVirtualDomain(owner, vm.Domain, vm.ExecuteCapability);
        if (!started.IsSuccess) return Failure("start virtual model", started.Error, started.Message);
        KernelResult parked = runtime.ParkVirtualDomain(owner, vm.Domain, vm.ExecuteCapability);
        if (!parked.IsSuccess) return Failure("park virtual model", parked.Error, parked.Message);
        KernelResult resumed = runtime.ResumeVirtualDomain(owner, vm.Domain, vm.ExecuteCapability);
        if (!resumed.IsSuccess) return Failure("resume virtual model", resumed.Error, resumed.Message);
        KernelResult<KernelEventEndpoint> endpoint = runtime.CreateKernelEventEndpoint(owner);
        if (!endpoint.IsSuccess) return Failure("create event endpoint", endpoint.Error, endpoint.Message);
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            Task<KernelResult<KernelEvent>> waiting = runtime.WaitForKernelEventAsync(owner, endpoint.Value, cancellation.Token).AsTask();
            cancellation.Cancel();
            try
            {
                _ = waiting.GetAwaiter().GetResult();
                return KernelBootStageResult.Failure("Cancelled event wait unexpectedly completed.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        KernelResult<VirtualDomainState> state = runtime.QueryVirtualDomain(owner, vm.Domain);
        if (!state.IsSuccess || state.Value != VirtualDomainState.Running)
            return KernelBootStageResult.Failure("Wait cancellation changed virtual domain state.");
        KernelResult injected = runtime.InjectVirtualEvent(owner, vm.Domain, vm.EventCapability, endpoint.Value);
        if (!injected.IsSuccess) return Failure("inject virtual event", injected.Error, injected.Message);
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<KernelResult<KernelEvent>> delivered = runtime.WaitForKernelEventAsync(owner, endpoint.Value, timeout.Token).AsTask();
        KernelResult<KernelEvent> received;
        try { received = delivered.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { return KernelBootStageResult.Failure("Virtual event delivery exceeded five seconds."); }
        if (!received.IsSuccess) return Failure("receive virtual event", received.Error, received.Message);
        if (received.Value!.SourceResourceId != VirtualizationResourceIds.Events(vm.Domain.DomainId))
            return KernelBootStageResult.Failure("Virtual event source identity mismatch.");
        KernelResult destroyed = runtime.DestroyVirtualDomain(owner, vm.Domain, vm.ConfigureCapability);
        if (!destroyed.IsSuccess) return Failure("destroy virtual domain", destroyed.Error, destroyed.Message);
        KernelResult stale = runtime.InjectVirtualEvent(owner, vm.Domain, vm.EventCapability, endpoint.Value);
        if (stale.IsSuccess || stale.Error != KernelError.VirtualDomainNotFound)
            return KernelBootStageResult.Failure($"Destroyed domain accepted event: {stale.Error}.");
        KernelResult closed = runtime.CloseKernelEventEndpoint(owner, endpoint.Value);
        if (!closed.IsSuccess) return Failure("close event endpoint", closed.Error, closed.Message);
        if (buffer.Span.ToArray().Any(value => value != 0x5A))
            return KernelBootStageResult.Failure("Virtual lifecycle changed guest bytes.");
        KernelResult freed = runtime.ReleaseRegion(owner, buffer);
        if (!freed.IsSuccess) return Failure("release guest region", freed.Error, freed.Message);
        session.Buffers.Remove(buffer.Handle.RegionId.Value);
        KernelResult reclaimed = session.TerminateAndReclaim(owner);
        if (!reclaimed.IsSuccess) return Failure("reclaim virtual owner", reclaimed.Error, reclaimed.Message);
        EmulatorConsole.StageDetail("Host ModelOnly: mapped guest region pinned; start/park/resume; wait cancellation preserves Running; exact event delivered; stale domain refused; bytes preserved; endpoint/domain/region/owner closed");
        return KernelBootStageResult.Success();
    }
}