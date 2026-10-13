using System.Runtime.CompilerServices;
using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<OwnedBuffer<T>> AllocateBuffer<T>(ProcessHandle owner, int length) where T : unmanaged
    {
        var observations = new List<BudgetTraceObservation>();
        var result = AllocateBufferCore<T>(owner, length, observations);
        EmitBudgetObservations(observations);
        if (!result.IsSuccess) return result;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(current.Error, current.Message!);
            var accepting = EnsureProcessAcceptsNewEffects(current.Value!);
            if (!accepting.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(accepting.Error, accepting.Message!);
            if (!current.Value!.Regions.Contains(result.Value!.Handle))
                return KernelResult<OwnedBuffer<T>>.Fail(KernelError.StaleGeneration, "Allocated region changed during budget observation.");
            var region = Regions.Validate(result.Value.Handle, new RegionOwner(current.Value.DomainId, owner.Generation));
            if (!region.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(region.Error, region.Message!);
            return result;
        }
    }

    private KernelResult<OwnedBuffer<T>> AllocateBufferCore<T>(ProcessHandle owner, int length,
        List<BudgetTraceObservation>? observations = null) where T : unmanaged
    {
        lock (_platformMemoryUseGate)
        {
        if (length <= 0) return KernelResult<OwnedBuffer<T>>.Fail(KernelError.InvalidRegionState, "Buffer length must be positive.");
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(effect.Error, effect.Message!);
        if (resolved.Value!.Regions.Count >= resolved.Value.Manifest.ResourceLimits.MaxRegions)
            return KernelResult<OwnedBuffer<T>>.Fail(KernelError.InvalidRegionState, "Region limit exceeded.");
        var bytes = checked((long)length * Unsafe.SizeOf<T>());
        var budget = ReserveAttachedBudget(owner,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, checked((ulong)bytes))],
            BudgetReservationLifetime.LocalResource, observations: observations);
        if (!budget.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(budget.Error, budget.Message!);
        var descriptor = Regions.Allocate(new RegionOwner(resolved.Value.DomainId, owner.Generation), bytes, typeof(T).FullName ?? typeof(T).Name);
        var buffer = new OwnedBuffer<T>(descriptor.Handle, new T[length]);
        Regions.RegisterPayload(descriptor.Handle, buffer);
        resolved.Value.AddRegion(descriptor.Handle);
        if (budget.Value is { } reservation)
        {
            lock (_regionBudgetReservationsGate)
                _regionBudgetReservations.Add(descriptor.Handle, (owner, reservation));
            var bound = BindOriginalRegionBudget(owner, reservation, descriptor.Handle, resolved.Value.DomainId);
            if (!bound.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(bound.Error, bound.Message!);
        }
        return KernelResult<OwnedBuffer<T>>.Ok(buffer);
        }
    }

    public KernelResult<OwnedRegion<T>> AllocateRegion<T>(ProcessHandle owner, T initialValue) where T : unmanaged
    {
        var observations = new List<BudgetTraceObservation>();
        var result = AllocateRegionCore(owner, initialValue, observations);
        EmitBudgetObservations(observations);
        if (!result.IsSuccess) return result;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(current.Error, current.Message!);
            var accepting = EnsureProcessAcceptsNewEffects(current.Value!);
            if (!accepting.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(accepting.Error, accepting.Message!);
            if (!current.Value!.Regions.Contains(result.Value!.Handle))
                return KernelResult<OwnedRegion<T>>.Fail(KernelError.StaleGeneration, "Allocated region changed during budget observation.");
            var region = Regions.Validate(result.Value.Handle, new RegionOwner(current.Value.DomainId, owner.Generation));
            return region.IsSuccess ? result : KernelResult<OwnedRegion<T>>.Fail(region.Error, region.Message!);
        }
    }

    private KernelResult<OwnedRegion<T>> AllocateRegionCore<T>(ProcessHandle owner, T initialValue,
        List<BudgetTraceObservation> observations) where T : unmanaged
    {
        lock (_platformMemoryUseGate)
        {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(effect.Error, effect.Message!);
        if (resolved.Value!.Regions.Count >= resolved.Value.Manifest.ResourceLimits.MaxRegions)
            return KernelResult<OwnedRegion<T>>.Fail(KernelError.InvalidRegionState, "Region limit exceeded.");
        var bytes = checked((ulong)Unsafe.SizeOf<T>());
        var budget = ReserveAttachedBudget(owner,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, bytes)],
            BudgetReservationLifetime.LocalResource, observations: observations);
        if (!budget.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(budget.Error, budget.Message!);
        var descriptor = Regions.Allocate(new RegionOwner(resolved.Value.DomainId, owner.Generation), checked((long)bytes), typeof(T).FullName ?? typeof(T).Name);
        var region = new OwnedRegion<T>(descriptor.Handle, initialValue);
        Regions.RegisterPayload(descriptor.Handle, region);
        resolved.Value.AddRegion(descriptor.Handle);
        if (budget.Value is { } reservation)
        {
            lock (_regionBudgetReservationsGate)
                _regionBudgetReservations.Add(descriptor.Handle, (owner, reservation));
            var bound = BindOriginalRegionBudget(owner, reservation, descriptor.Handle, resolved.Value.DomainId);
            if (!bound.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(bound.Error, bound.Message!);
        }
        return KernelResult<OwnedRegion<T>>.Ok(region);
        }
    }

    public KernelResult<OwnedBuffer<T>> TransferRegion<T>(ProcessHandle source, ProcessHandle target, OwnedBuffer<T> buffer) where T : unmanaged
    {
        var observations = new List<BudgetTraceObservation>();
        var result = TransferBufferCore(source, target, buffer, observations);
        EmitBudgetObservations(observations);
        if (!result.IsSuccess) return result;
        var ready = ValidateTransferredPublication(target, result.Value!.Handle);
        return ready.IsSuccess ? result : KernelResult<OwnedBuffer<T>>.Fail(ready.Error, ready.Message!);
    }

    private KernelResult<OwnedBuffer<T>> TransferBufferCore<T>(ProcessHandle source, ProcessHandle target,
        OwnedBuffer<T> buffer, List<BudgetTraceObservation> observations) where T : unmanaged
    {
        lock (_platformMemoryUseGate)
        {
        var sourceProcess = Processes.Resolve(source);
        if (!sourceProcess.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(sourceProcess.Error, sourceProcess.Message!);
        var sourceEffect = EnsureProcessAcceptsNewEffects(sourceProcess.Value!);
        if (!sourceEffect.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(sourceEffect.Error, sourceEffect.Message!);
        var targetProcess = Processes.Resolve(target);
        if (!targetProcess.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(targetProcess.Error, targetProcess.Message!);
        var targetEffect = EnsureProcessAcceptsNewEffects(targetProcess.Value!);
        if (!targetEffect.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(targetEffect.Error, targetEffect.Message!);
        var transferable = (ITransferableOwnedPayload)buffer;
        if (!transferable.IsValidForRuntime) return KernelResult<OwnedBuffer<T>>.Fail(KernelError.InvalidRegionState, "Source ownership token has already been consumed.");
        // Validate managed custody before Region/budget mutation, not only when
        // constructing the returned ownership wrapper after that mutation.
        try { transferable.ValidateTransferForRuntime(); }
        catch (InvalidOperationException exception)
        { return KernelResult<OwnedBuffer<T>>.Fail(KernelError.RegionUseConflict,exception.Message); }
        var oldHandle = buffer.Handle;
        var targetBudget = PrepareRegionBudgetTransfer(source, target, oldHandle, observations);
        if (!targetBudget.IsSuccess) return KernelResult<OwnedBuffer<T>>.Fail(targetBudget.Error, targetBudget.Message!);
        var transfer = Regions.Transfer(oldHandle, new RegionOwner(sourceProcess.Value!.DomainId, source.Generation), new RegionOwner(targetProcess.Value!.DomainId, target.Generation));
        if (!transfer.IsSuccess)
        {
            _ = ReleaseAttachedBudget(target, targetBudget.Value, observations);
            return KernelResult<OwnedBuffer<T>>.Fail(transfer.Error, transfer.Message!);
        }
        var moved = (OwnedBuffer<T>)transferable.TransferForRuntime(transfer.Value);
        Regions.ReplacePayload(oldHandle, transfer.Value, moved);
        sourceProcess.Value.RemoveRegion(oldHandle);
        targetProcess.Value.AddRegion(transfer.Value);
        CompleteRegionBudgetTransfer(source, target, oldHandle, transfer.Value, targetBudget.Value, observations);
        return KernelResult<OwnedBuffer<T>>.Ok(moved);
        }
    }

    public KernelResult ReleaseRegion<T>(ProcessHandle owner, OwnedBuffer<T> buffer) where T : unmanaged
        => ReleaseOwnedPayload(owner, buffer);

    internal Action? BeforeRegionReleaseQualificationHook { get; set; }

    private KernelResult ReleaseOwnedPayload(ProcessHandle owner, ITransferableOwnedPayload transferable)
    {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return effect;
        if (!transferable.IsValidForRuntime) return KernelResult.Fail(KernelError.InvalidRegionState, "Ownership token has already been consumed.");
        BeforeRegionReleaseQualificationHook?.Invoke();
        var observations = new List<BudgetTraceObservation>();
        try
        {
        lock (_platformMemoryUseGate)
        {
        var current = Processes.Resolve(owner);
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        if (!ReferenceEquals(current.Value, resolved.Value))
            return KernelResult.Fail(KernelError.StaleGeneration, "Region release process incarnation changed.");
        var currentEffect = EnsureProcessAcceptsNewEffects(current.Value!);
        if (!currentEffect.IsSuccess) return currentEffect;
        if (!transferable.IsValidForRuntime) return KernelResult.Fail(KernelError.InvalidRegionState, "Ownership token has already been consumed.");
        var handle = transferable.Handle;
        var release = Regions.Release(handle, new RegionOwner(resolved.Value!.DomainId, owner.Generation));
        if (!release.IsSuccess) return release;
        transferable.InvalidateForRuntime();
        resolved.Value.RemoveRegion(handle);
        ReleaseEndedRegionBudget(handle, observations);
        return KernelResult.Ok();
        }
        }
        finally { EmitBudgetObservations(observations); }
    }

    public KernelResult<OwnedRegion<T>> TransferRegion<T>(ProcessHandle source, ProcessHandle target, OwnedRegion<T> region) where T : unmanaged
    {
        var observations = new List<BudgetTraceObservation>();
        var result = TransferOwnedRegionCore(source, target, region, observations);
        EmitBudgetObservations(observations);
        if (!result.IsSuccess) return result;
        var ready = ValidateTransferredPublication(target, result.Value!.Handle);
        return ready.IsSuccess ? result : KernelResult<OwnedRegion<T>>.Fail(ready.Error, ready.Message!);
    }

    private KernelResult ValidateTransferredPublication(ProcessHandle target, RegionHandle handle)
    {
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(target);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            var effect = EnsureProcessAcceptsNewEffects(current.Value!);
            if (!effect.IsSuccess) return effect;
            if (!current.Value!.Regions.Contains(handle))
                return KernelResult.Fail(KernelError.StaleGeneration, "Transferred region changed during budget observation.");
            var region = Regions.Validate(handle, new RegionOwner(current.Value.DomainId, target.Generation));
            return region.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(region.Error, region.Message!);
        }
    }

    private KernelResult<OwnedRegion<T>> TransferOwnedRegionCore<T>(ProcessHandle source, ProcessHandle target,
        OwnedRegion<T> region, List<BudgetTraceObservation> observations) where T : unmanaged
    {
        lock (_platformMemoryUseGate)
        {
        var sourceProcess = Processes.Resolve(source);
        if (!sourceProcess.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(sourceProcess.Error, sourceProcess.Message!);
        var sourceEffect = EnsureProcessAcceptsNewEffects(sourceProcess.Value!);
        if (!sourceEffect.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(sourceEffect.Error, sourceEffect.Message!);
        var targetProcess = Processes.Resolve(target);
        if (!targetProcess.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(targetProcess.Error, targetProcess.Message!);
        var targetEffect = EnsureProcessAcceptsNewEffects(targetProcess.Value!);
        if (!targetEffect.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(targetEffect.Error, targetEffect.Message!);
        var transferable = (ITransferableOwnedPayload)region;
        if (!transferable.IsValidForRuntime) return KernelResult<OwnedRegion<T>>.Fail(KernelError.InvalidRegionState, "Source ownership token has already been consumed.");
        var oldHandle = region.Handle;
        var targetBudget = PrepareRegionBudgetTransfer(source, target, oldHandle, observations);
        if (!targetBudget.IsSuccess) return KernelResult<OwnedRegion<T>>.Fail(targetBudget.Error, targetBudget.Message!);
        var transfer = Regions.Transfer(oldHandle, new RegionOwner(sourceProcess.Value!.DomainId, source.Generation), new RegionOwner(targetProcess.Value!.DomainId, target.Generation));
        if (!transfer.IsSuccess)
        {
            _ = ReleaseAttachedBudget(target, targetBudget.Value, observations);
            return KernelResult<OwnedRegion<T>>.Fail(transfer.Error, transfer.Message!);
        }
        var moved = (OwnedRegion<T>)transferable.TransferForRuntime(transfer.Value);
        Regions.ReplacePayload(oldHandle, transfer.Value, moved);
        sourceProcess.Value.RemoveRegion(oldHandle);
        targetProcess.Value.AddRegion(transfer.Value);
        CompleteRegionBudgetTransfer(source, target, oldHandle, transfer.Value, targetBudget.Value, observations);
        return KernelResult<OwnedRegion<T>>.Ok(moved);
        }
    }

    public KernelResult ReleaseRegion<T>(ProcessHandle owner, OwnedRegion<T> region) where T : unmanaged
        => ReleaseOwnedPayload(owner, region);

    public KernelResult ReturnBorrow(ProcessHandle borrower, BorrowLeaseHandle lease)
    {
        var resolved = Processes.Resolve(borrower);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return effect;
        return Regions.ReturnLoan(lease, new RegionOwner(resolved.Value!.DomainId, borrower.Generation));
    }

    public KernelResult RevokeBorrow(ProcessHandle owner, BorrowLeaseHandle lease)
    {
        var resolved = Processes.Resolve(owner);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return effect;
        return Regions.RevokeLoan(lease, new RegionOwner(resolved.Value!.DomainId, owner.Generation));
    }

    public KernelResult<RegionUseDescriptor> AcquireRegionUse(
        ProcessHandle principal,
        RegionHandle region,
        RegionUseMode mode,
        RegionUseRange range)
    {
        var acquired = AcquireOwnedRegionUse(principal,
            owner => Regions.AcquireUse(region, owner, mode, range));
        if (acquired.IsSuccess)
            RecordTrace(principal, TraceEventKind.RegionUse, null, "region-use",
                acquired.Value!.Handle.UseId.Value.ToString(), acquired.Value.State.ToString(), "acquired");
        return acquired.IsSuccess ? ValidateRegionUsePublication(principal, acquired.Value!.Handle) : acquired;
    }

    internal Action? BeforeOwnedRegionUseAdmissionQualificationHook { get; set; }

    // Trusted local RegionAuthority composition only; observations stay outside.
    private KernelResult<RegionUseDescriptor> AcquireOwnedRegionUse(ProcessHandle principal,
        Func<RegionOwner, KernelResult<RegionUseDescriptor>> acquire)
    {
        var expected = Processes.Resolve(principal);
        if (!expected.IsSuccess) return KernelResult<RegionUseDescriptor>.Fail(expected.Error, expected.Message!);
        BeforeOwnedRegionUseAdmissionQualificationHook?.Invoke();
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(principal);
            if (!current.IsSuccess) return KernelResult<RegionUseDescriptor>.Fail(current.Error, current.Message!);
            if (!ReferenceEquals(current.Value, expected.Value))
                return KernelResult<RegionUseDescriptor>.Fail(KernelError.StaleGeneration, "Region-use process incarnation changed.");
            var effect = EnsureProcessAcceptsNewEffects(current.Value!);
            return effect.IsSuccess ? acquire(new RegionOwner(current.Value!.DomainId, principal.Generation))
                : KernelResult<RegionUseDescriptor>.Fail(effect.Error, effect.Message!);
        }
    }

    private KernelResult<RegionUseDescriptor> ValidateRegionUsePublication(ProcessHandle principal, RegionUseHandle use)
    {
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(principal);
            if (!current.IsSuccess) return KernelResult<RegionUseDescriptor>.Fail(current.Error, current.Message!);
            var effect = EnsureProcessAcceptsNewEffects(current.Value!);
            return effect.IsSuccess ? Regions.ValidateUse(use, new RegionOwner(current.Value!.DomainId, principal.Generation))
                : KernelResult<RegionUseDescriptor>.Fail(effect.Error, effect.Message!);
        }
    }

    public KernelResult<RegionUseDescriptor> AcquireRegionUse<T>(
        ProcessHandle principal,
        RegionHandle region,
        RegionUseMode mode,
        long elementOffset,
        long elementLength) where T : unmanaged
        => AcquireOwnedRegionUse(principal, owner => Regions.AcquireTypedUse(
            region, owner,
            mode,
            elementOffset,
            elementLength,
            Unsafe.SizeOf<T>(),
            typeof(T).FullName ?? typeof(T).Name));

    internal Action? BeforeBorrowRegionUseAdmissionQualificationHook { get; set; }

    public KernelResult<RegionUseDescriptor> AcquireBorrowRegionUse(
        ProcessHandle owner,
        ProcessHandle borrower,
        BorrowLeaseHandle lease,
        RegionUseMode mode,
        RegionUseRange range)
    {
        var resolvedOwner = Processes.Resolve(owner);
        if (!resolvedOwner.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(resolvedOwner.Error, resolvedOwner.Message!);
        var resolvedBorrower = Processes.Resolve(borrower);
        if (!resolvedBorrower.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(resolvedBorrower.Error, resolvedBorrower.Message!);
        BeforeBorrowRegionUseAdmissionQualificationHook?.Invoke();
        KernelResult ValidateSources()
        {
            var currentOwner = Processes.Resolve(owner);
            if (!currentOwner.IsSuccess) return KernelResult.Fail(currentOwner.Error, currentOwner.Message!);
            var currentBorrower = Processes.Resolve(borrower);
            if (!currentBorrower.IsSuccess) return KernelResult.Fail(currentBorrower.Error, currentBorrower.Message!);
            if (!ReferenceEquals(currentOwner.Value, resolvedOwner.Value) || !ReferenceEquals(currentBorrower.Value, resolvedBorrower.Value))
                return KernelResult.Fail(KernelError.StaleGeneration, "Borrow-use process incarnation changed.");
            var ownerEffect = EnsureProcessAcceptsNewEffects(currentOwner.Value!);
            return ownerEffect.IsSuccess ? EnsureProcessAcceptsNewEffects(currentBorrower.Value!) : ownerEffect;
        }
        KernelResult<RegionUseDescriptor> acquired;
        lock (_platformMemoryUseGate)
        {
        var sources = ValidateSources();
        if (!sources.IsSuccess) return KernelResult<RegionUseDescriptor>.Fail(sources.Error, sources.Message!);
        acquired = Regions.AcquireBorrowUse(
            lease,
            new RegionOwner(resolvedOwner.Value!.DomainId, owner.Generation),
            new RegionOwner(resolvedBorrower.Value!.DomainId, borrower.Generation),
            mode,
            range);
        }
        if (acquired.IsSuccess)
            RecordTrace(borrower, TraceEventKind.RegionUse, null, "region-use",
                acquired.Value!.Handle.UseId.Value.ToString(), acquired.Value.State.ToString(), "borrow-acquired");
        if (!acquired.IsSuccess) return acquired;
        lock (_platformMemoryUseGate)
        {
            var sources = ValidateSources();
            return sources.IsSuccess ? Regions.ValidateBorrowUsePublication(lease,
                new RegionOwner(resolvedOwner.Value!.DomainId, owner.Generation),
                new RegionOwner(resolvedBorrower.Value!.DomainId, borrower.Generation), acquired.Value!.Handle)
                : KernelResult<RegionUseDescriptor>.Fail(sources.Error, sources.Message!);
        }
    }

    public KernelResult<RegionUseDescriptor> ValidateRegionUse(
        ProcessHandle principal,
        RegionUseHandle use)
        => ValidateRegionUsePublication(principal, use);

    public KernelResult ReleaseRegionUse(ProcessHandle principal, RegionUseHandle use)
    {
        var resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);

        var released = Regions.ReleaseUse(
            use,
            new RegionOwner(resolved.Value!.DomainId, principal.Generation));
        if (released.IsSuccess)
            RecordTrace(principal, TraceEventKind.RegionUse, null, "region-use",
                use.UseId.Value.ToString(), "Released", "released");
        return released;
    }

    public KernelResult InvalidateRegionUse(ProcessHandle principal, RegionUseHandle use)
    {
        var resolved = Processes.Resolve(principal);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);

        return Regions.InvalidateUse(
            use,
            new RegionOwner(resolved.Value!.DomainId, principal.Generation));
    }
}
