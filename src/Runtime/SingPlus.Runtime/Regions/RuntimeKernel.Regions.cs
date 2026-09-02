using System.Runtime.CompilerServices;
using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<OwnedBuffer<T>> AllocateBuffer<T>(ProcessHandle owner, int length) where T : unmanaged
    {
        if (length <= 0) return KernelResult<OwnedBuffer<T>>.Fail(KernelError.InvalidRegionState, "Buffer length must be positive.");
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(resolved.Error, resolved.Message!);
        if (resolved.Value!.Regions.Count >= resolved.Value.Manifest.ResourceLimits.MaxRegions)
            return KernelResult<OwnedBuffer<T>>.Fail(KernelError.InvalidRegionState, "Region limit exceeded.");
        var bytes = checked((long)length * Unsafe.SizeOf<T>());
        var descriptor = Regions.Allocate(new RegionOwner(resolved.Value.DomainId, owner.Generation), bytes, typeof(T).FullName ?? typeof(T).Name);
        var buffer = new OwnedBuffer<T>(descriptor.Handle, new T[length]);
        Regions.RegisterPayload(descriptor.Handle, buffer);
        resolved.Value.AddRegion(descriptor.Handle);
        return KernelResult<OwnedBuffer<T>>.Ok(buffer);
    }

    public KernelResult<OwnedRegion<T>> AllocateRegion<T>(ProcessHandle owner, T initialValue) where T : unmanaged
    {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(resolved.Error, resolved.Message!);
        if (resolved.Value!.Regions.Count >= resolved.Value.Manifest.ResourceLimits.MaxRegions)
            return KernelResult<OwnedRegion<T>>.Fail(KernelError.InvalidRegionState, "Region limit exceeded.");
        var descriptor = Regions.Allocate(new RegionOwner(resolved.Value.DomainId, owner.Generation), Unsafe.SizeOf<T>(), typeof(T).FullName ?? typeof(T).Name);
        var region = new OwnedRegion<T>(descriptor.Handle, initialValue);
        Regions.RegisterPayload(descriptor.Handle, region);
        resolved.Value.AddRegion(descriptor.Handle);
        return KernelResult<OwnedRegion<T>>.Ok(region);
    }

    public KernelResult<OwnedBuffer<T>> TransferRegion<T>(ProcessHandle source, ProcessHandle target, OwnedBuffer<T> buffer) where T : unmanaged
    {
        var sourceProcess = Processes.Resolve(source);
        if (!sourceProcess.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(sourceProcess.Error, sourceProcess.Message!);
        var targetProcess = Processes.Resolve(target);
        if (!targetProcess.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(targetProcess.Error, targetProcess.Message!);
        var transferable = (ITransferableOwnedPayload)buffer;
        if (!transferable.IsValidForRuntime) return KernelResult<OwnedBuffer<T>>.Fail(KernelError.InvalidRegionState, "Source ownership token has already been consumed.");
        var oldHandle = buffer.Handle;
        var transfer = Regions.Transfer(oldHandle, new RegionOwner(sourceProcess.Value!.DomainId, source.Generation), new RegionOwner(targetProcess.Value!.DomainId, target.Generation));
        if (!transfer.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(transfer.Error, transfer.Message!);
        var moved = (OwnedBuffer<T>)transferable.TransferForRuntime(transfer.Value);
        Regions.ReplacePayload(oldHandle, transfer.Value, moved);
        sourceProcess.Value.RemoveRegion(oldHandle);
        targetProcess.Value.AddRegion(transfer.Value);
        return KernelResult<OwnedBuffer<T>>.Ok(moved);
    }

    public KernelResult ReleaseRegion<T>(ProcessHandle owner, OwnedBuffer<T> buffer) where T : unmanaged
    {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var transferable = (ITransferableOwnedPayload)buffer;
        if (!transferable.IsValidForRuntime) return KernelResult.Fail(KernelError.InvalidRegionState, "Ownership token has already been consumed.");
        var handle = buffer.Handle;
        var release = Regions.Release(handle, new RegionOwner(resolved.Value!.DomainId, owner.Generation));
        if (!release.IsSuccess) return release;
        transferable.InvalidateForRuntime();
        resolved.Value.RemoveRegion(handle);
        return KernelResult.Ok();
    }

    public KernelResult<OwnedRegion<T>> TransferRegion<T>(ProcessHandle source, ProcessHandle target, OwnedRegion<T> region) where T : unmanaged
    {
        var sourceProcess = Processes.Resolve(source);
        if (!sourceProcess.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(sourceProcess.Error, sourceProcess.Message!);
        var targetProcess = Processes.Resolve(target);
        if (!targetProcess.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(targetProcess.Error, targetProcess.Message!);
        var transferable = (ITransferableOwnedPayload)region;
        if (!transferable.IsValidForRuntime) return KernelResult<OwnedRegion<T>>.Fail(KernelError.InvalidRegionState, "Source ownership token has already been consumed.");
        var oldHandle = region.Handle;
        var transfer = Regions.Transfer(oldHandle, new RegionOwner(sourceProcess.Value!.DomainId, source.Generation), new RegionOwner(targetProcess.Value!.DomainId, target.Generation));
        if (!transfer.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(transfer.Error, transfer.Message!);
        var moved = (OwnedRegion<T>)transferable.TransferForRuntime(transfer.Value);
        Regions.ReplacePayload(oldHandle, transfer.Value, moved);
        sourceProcess.Value.RemoveRegion(oldHandle);
        targetProcess.Value.AddRegion(transfer.Value);
        return KernelResult<OwnedRegion<T>>.Ok(moved);
    }

    public KernelResult ReleaseRegion<T>(ProcessHandle owner, OwnedRegion<T> region) where T : unmanaged
    {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var transferable = (ITransferableOwnedPayload)region;
        if (!transferable.IsValidForRuntime) return KernelResult.Fail(KernelError.InvalidRegionState, "Ownership token has already been consumed.");
        var handle = region.Handle;
        var release = Regions.Release(handle, new RegionOwner(resolved.Value!.DomainId, owner.Generation));
        if (!release.IsSuccess) return release;
        transferable.InvalidateForRuntime();
        resolved.Value.RemoveRegion(handle);
        return KernelResult.Ok();
    }
}
