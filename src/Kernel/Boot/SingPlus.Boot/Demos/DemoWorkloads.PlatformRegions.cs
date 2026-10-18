using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunPlatformRegions(EmulatorSession session)
    {
        if (session.Runtime is not { } runtime || session.Options.PlatformProfile == "none")
            return KernelBootStageResult.Failure("platform-regions requires host-model or hybridcpu-model.");
        var created = session.SpawnProcess("singplus.emulator.platform.regions", ExecutionRole.User, MemoryProfile.ManagedGc, start: false);
        if (!created.IsSuccess) return Failure("admit", created.Error, created.Message);
        var owner = created.Value;
        var process = runtime.Processes.Resolve(owner);
        if (!process.IsSuccess) return Failure("resolve", process.Error, process.Message);
        var bound = runtime.BindPlatformDomain(owner);
        if (!bound.IsSuccess) return Failure("bind", bound.Error, bound.Message);
        session.PlatformBindings[owner.ProcessId.Value] = bound.Value;
        var allocated = session.AllocateTrackedBuffer(owner, 64);
        if (!allocated.IsSuccess) return Failure("allocate", allocated.Error, allocated.Message);
        var buffer = allocated.Value!.Buffer;
        buffer.Span.Fill(0x3C);
        var mapped = MapModelBuffer(runtime, owner, process.Value!.DomainId, bound.Value, buffer, PlatformMemoryAccess.Read);
        if (!mapped.IsSuccess) return Failure("map", mapped.Error, mapped.Message);
        var freePinned = runtime.ReleaseRegion(owner, buffer);
        if (freePinned.IsSuccess || freePinned.Error != KernelError.PlatformBindingActive)
            return KernelBootStageResult.Failure($"Mapped Region reclaim returned {freePinned.Error}.");
        var revoked = runtime.RevokePlatformRegionMapping(owner, mapped.Value);
        if (!revoked.IsSuccess) return Failure("revoke mapping", revoked.Error, revoked.Message);
        if (buffer.Span.ToArray().Any(static value => value != 0x3C))
            return KernelBootStageResult.Failure("Mapping closure changed the owner bytes.");
        var freed = runtime.ReleaseRegion(owner, buffer);
        if (!freed.IsSuccess) return Failure("release", freed.Error, freed.Message);
        session.Buffers.Remove(buffer.Handle.RegionId.Value);
        var closed = session.TerminateAndReclaim(owner);
        if (!closed.IsSuccess) return Failure("teardown", closed.Error, closed.Message);
        EmulatorConsole.StageDetail($"{session.Options.PlatformProfile}: same runtime Region/map/revoke/reclaim path; premature reclaim={freePinned.Error}; bytes preserved; subject reclaimed; model evidence only");
        return KernelBootStageResult.Success();
    }
}
