using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunBacking(EmulatorSession session)
    {
        if (session.Runtime is not { } runtime) return KernelBootStageResult.Failure("Runtime is unavailable.");
        KernelResult<ProcessHandle> created = session.SpawnProcess("singplus.emulator.backing", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!created.IsSuccess) return Failure("spawn", created.Error, created.Message);
        ProcessHandle owner = created.Value;
        KernelResult<BufferRecord> allocated = session.AllocateTrackedBuffer(owner, 64);
        if (!allocated.IsSuccess) return Failure("allocate", allocated.Error, allocated.Message);
        OwnedBuffer<byte> buffer = allocated.Value!.Buffer;
        RegionDescriptor descriptor = runtime.Regions.Snapshot().Single(region => region.Handle == buffer.Handle);
        KernelResult<AuthorityInspector> inspector = runtime.CreateAuthorityInspector(owner);
        if (!inspector.IsSuccess) return Failure("inspector", inspector.Error, inspector.Message);
        KernelResult<RegionBackingLeaseDescriptor> reserved = runtime.Regions.ReserveBacking(buffer.Handle, descriptor.Owner);
        if (!reserved.IsSuccess) return Failure("reserve backing", reserved.Error, reserved.Message);
        RegionBackingLeaseDescriptor lease = reserved.Value!;
        KernelResult<RegionBackingLeaseDescriptor> duplicate = runtime.Regions.ReserveBacking(buffer.Handle, descriptor.Owner);
        KernelResult premature = runtime.ReleaseRegion(owner, buffer);
        KernelResult<AuthorityInspectionExplanation> explained = inspector.Value!.WhyReclaimBlocked(buffer.Handle);
        KernelResult released = runtime.Regions.ReleaseBacking(lease.Handle, descriptor.Owner);
        if (!released.IsSuccess) return Failure("release backing", released.Error, released.Message);
        if (duplicate.IsSuccess || duplicate.Error != KernelError.PlatformBindingActive ||
            premature.IsSuccess || premature.Error != KernelError.PlatformBindingActive)
            return KernelBootStageResult.Failure("Backing lease did not interlock duplicate reservation and Region reclaim.");
        if (!explained.IsSuccess) return KernelBootStageResult.Failure("Backing explanation unavailable.");
        if (!explained.Value!.Reasons.Any(reason => reason.Kind == AuthorityBlockReasonKind.BackingLeaseActive))
            return KernelBootStageResult.Failure("Active backing lease absent from runtime reclaim explanation.");
        KernelResult<RegionBackingLeaseDescriptor> stale = runtime.Regions.ValidateBacking(lease.Handle, descriptor.Owner);
        if (stale.IsSuccess || stale.Error != KernelError.PlatformBindingNotFound)
            return KernelBootStageResult.Failure("Released backing lease remained valid.");
        KernelResult<AuthorityInspectionExplanation> after = inspector.Value!.WhyReclaimBlocked(buffer.Handle);
        if (!after.IsSuccess) return Failure("inspect released backing", after.Error, after.Message);
        if (after.Value!.Reasons.Any(reason => reason.Kind == AuthorityBlockReasonKind.BackingLeaseActive))
            return KernelBootStageResult.Failure("Released backing still blocks Region reclaim.");
        KernelResult<RegionBackingLeaseDescriptor> replacement = runtime.Regions.ReserveBacking(buffer.Handle, descriptor.Owner);
        if (!replacement.IsSuccess) return Failure("reserve replacement backing", replacement.Error, replacement.Message);
        RegionBackingLeaseDescriptor next = replacement.Value!;
        KernelResult oldRelease = runtime.Regions.ReleaseBacking(lease.Handle, descriptor.Owner);
        KernelResult<RegionBackingLeaseDescriptor> forgedGeneration = runtime.Regions.ValidateBacking(next.Handle with { Generation = next.Handle.Generation + 1 }, descriptor.Owner);
        KernelResult foreignOwner = runtime.Regions.ReleaseBacking(next.Handle,
            descriptor.Owner with { ProcessGeneration = descriptor.Owner.ProcessGeneration + 1 });
        KernelResult<RegionBackingLeaseDescriptor> stillValid = runtime.Regions.ValidateBacking(next.Handle, descriptor.Owner);
        KernelResult nextReleased = runtime.Regions.ReleaseBacking(next.Handle, descriptor.Owner);
        if (!nextReleased.IsSuccess) return Failure("release replacement backing", nextReleased.Error, nextReleased.Message);
        if (next.Handle.LeaseId == lease.Handle.LeaseId || oldRelease.IsSuccess || oldRelease.Error != KernelError.PlatformBindingNotFound ||
            forgedGeneration.IsSuccess || forgedGeneration.Error != KernelError.StaleGeneration ||
            foreignOwner.IsSuccess || foreignOwner.Error != KernelError.WrongRegionOwner || !stillValid.IsSuccess)
            return KernelBootStageResult.Failure("Backing replacement failed exact identity/generation/owner isolation.");
        EmulatorConsole.StageDetail("replacement backing: fresh lease identity; old release=PlatformBindingNotFound; wrong generation=StaleGeneration; wrong owner=WrongRegionOwner; exact replacement remains valid");
        KernelResult freed = runtime.ReleaseRegion(owner, buffer);
        if (!freed.IsSuccess) return Failure("free", freed.Error, freed.Message);
        session.Buffers.Remove(buffer.Handle.RegionId.Value);
        KernelResult reclaimed = session.TerminateAndReclaim(owner);
        if (!reclaimed.IsSuccess) return Failure("reclaim", reclaimed.Error, reclaimed.Message);
        EmulatorConsole.StageDetail("local backing lease: duplicate/reclaim=PlatformBindingActive; BackingLeaseActive explained; released handle=PlatformBindingNotFound; Region and process reclaimed");
        EmulatorConsole.StageDetail("RegionAuthority lifetime reservation only; no external backing provider or CXL residency claimed");
        return KernelBootStageResult.Success();
    }
}
