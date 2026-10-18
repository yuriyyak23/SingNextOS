using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunVirtualDomain(EmulatorSession session)
    {
        if (!session.Options.HostModelPlatform || session.Runtime is not { } runtime)
            return KernelBootStageResult.Failure("virtual-domain requires --platform host-model; model evidence only.");
        var admitted = session.SpawnProcess("singplus.emulator.virtual-domain", ExecutionRole.User, MemoryProfile.ManagedGc, start: false);
        if (!admitted.IsSuccess) return Failure("admit virtual owner", admitted.Error, admitted.Message);
        var owner = admitted.Value;
        var process = runtime.Processes.Resolve(owner);
        if (!process.IsSuccess) return Failure("resolve owner", process.Error, process.Message);
        var cap = runtime.MintCapability(process.Value!.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure);
        if (!cap.IsSuccess) return Failure("mint create authority", cap.Error, cap.Message);
        var created = runtime.CreateVirtualDomain(owner, cap.Value!.CapabilityId, new VirtualDomainProfile(1, 4096));
        if (!created.IsSuccess) return Failure("create virtual domain", created.Error, created.Message);
        var vm = created.Value;
        var configured = runtime.ConfigureVirtualDomain(owner, vm.Domain, vm.ConfigureCapability);
        if (!configured.IsSuccess) return Failure("configure virtual domain", configured.Error, configured.Message);
        var allocated = session.AllocateTrackedBuffer(owner, 256);
        if (!allocated.IsSuccess) return Failure("allocate guest region", allocated.Error, allocated.Message);
        var buffer = allocated.Value!.Buffer;
        buffer.Span.Fill(0x5A);
        var memory = runtime.MintCapability(process.Value.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);
        if (!memory.IsSuccess) return Failure("mint memory authority", memory.Error, memory.Message);
        var mapped = runtime.MapGuestRegion(owner, vm.Domain, vm.MemoryCapability, memory.Value!.CapabilityId,
            buffer.Handle, new GuestAddressRange(0, 256), GuestMemoryAccess.Read | GuestMemoryAccess.Write);
        if (!mapped.IsSuccess) return Failure("map guest region", mapped.Error, mapped.Message);
        var pinned = runtime.ReleaseRegion(owner, buffer);
        if (pinned.IsSuccess || pinned.Error != KernelError.PlatformBindingActive)
            return KernelBootStageResult.Failure($"Guest mapping failed to retain region: {pinned.Error}.");
        var started = runtime.StartVirtualDomain(owner, vm.Domain, vm.ExecuteCapability);
        if (!started.IsSuccess) return Failure("start virtual model", started.Error, started.Message);
        var parked = runtime.ParkVirtualDomain(owner, vm.Domain, vm.ExecuteCapability);
        if (!parked.IsSuccess) return Failure("park virtual model", parked.Error, parked.Message);
        var resumed = runtime.ResumeVirtualDomain(owner, vm.Domain, vm.ExecuteCapability);
        if (!resumed.IsSuccess) return Failure("resume virtual model", resumed.Error, resumed.Message);
        var endpoint = runtime.CreateKernelEventEndpoint(owner);
        if (!endpoint.IsSuccess) return Failure("create event endpoint", endpoint.Error, endpoint.Message);
        using (var cancellation = new CancellationTokenSource())
        {
            var waiting = runtime.WaitForKernelEventAsync(owner, endpoint.Value, cancellation.Token).AsTask();
            cancellation.Cancel();
            try
            {
                _ = waiting.GetAwaiter().GetResult();
                return KernelBootStageResult.Failure("Cancelled event wait unexpectedly completed.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        var state = runtime.QueryVirtualDomain(owner, vm.Domain);
        if (!state.IsSuccess || state.Value != VirtualDomainState.Running)
            return KernelBootStageResult.Failure("Wait cancellation changed virtual domain state.");
        var injected = runtime.InjectVirtualEvent(owner, vm.Domain, vm.EventCapability, endpoint.Value);
        if (!injected.IsSuccess) return Failure("inject virtual event", injected.Error, injected.Message);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var delivered = runtime.WaitForKernelEventAsync(owner, endpoint.Value, timeout.Token).AsTask();
        KernelResult<KernelEvent> received;
        try { received = delivered.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { return KernelBootStageResult.Failure("Virtual event delivery exceeded five seconds."); }
        if (!received.IsSuccess) return Failure("receive virtual event", received.Error, received.Message);
        if (received.Value!.SourceResourceId != VirtualizationResourceIds.Events(vm.Domain.DomainId))
            return KernelBootStageResult.Failure("Virtual event source identity mismatch.");
        var destroyed = runtime.DestroyVirtualDomain(owner, vm.Domain, vm.ConfigureCapability);
        if (!destroyed.IsSuccess) return Failure("destroy virtual domain", destroyed.Error, destroyed.Message);
        var stale = runtime.InjectVirtualEvent(owner, vm.Domain, vm.EventCapability, endpoint.Value);
        if (stale.IsSuccess || stale.Error != KernelError.VirtualDomainNotFound)
            return KernelBootStageResult.Failure($"Destroyed domain accepted event: {stale.Error}.");
        var closed = runtime.CloseKernelEventEndpoint(owner, endpoint.Value);
        if (!closed.IsSuccess) return Failure("close event endpoint", closed.Error, closed.Message);
        if (buffer.Span.ToArray().Any(value => value != 0x5A))
            return KernelBootStageResult.Failure("Virtual lifecycle changed guest bytes.");
        var freed = runtime.ReleaseRegion(owner, buffer);
        if (!freed.IsSuccess) return Failure("release guest region", freed.Error, freed.Message);
        session.Buffers.Remove(buffer.Handle.RegionId.Value);
        var reclaimed = session.TerminateAndReclaim(owner);
        if (!reclaimed.IsSuccess) return Failure("reclaim virtual owner", reclaimed.Error, reclaimed.Message);
        EmulatorConsole.StageDetail("Host ModelOnly: mapped guest region pinned; start/park/resume; wait cancellation preserves Running; exact event delivered; stale domain refused; bytes preserved; endpoint/domain/region/owner closed");
        return KernelBootStageResult.Success();
    }
}