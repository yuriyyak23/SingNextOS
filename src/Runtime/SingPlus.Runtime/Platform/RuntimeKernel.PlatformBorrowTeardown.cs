using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private sealed record TrackedPlatformBorrowReadGrant(
        ProcessHandle Owner,
        ProcessHandle Borrower,
        PlatformBorrowReadGrant Grant);

    private readonly Dictionary<PlatformBorrowReadGrantId, TrackedPlatformBorrowReadGrant>
        _platformBorrowReadGrants = [];

    private void TrackPlatformBorrowReadGrant(
        ProcessHandle owner,
        ProcessHandle borrower,
        PlatformBorrowReadGrant grant)
    {
        lock (_platformMemoryUseGate) _platformBorrowReadGrants.Add(
            grant.GrantId,
            new TrackedPlatformBorrowReadGrant(owner, borrower, grant));
    }

    private void UntrackPlatformBorrowReadGrant(PlatformBorrowReadGrant grant)
    {
        lock (_platformMemoryUseGate) _platformBorrowReadGrants.Remove(grant.GrantId);
    }

    private KernelResult<int> AdvancePlatformBorrowReadGrantsForProcess(
        ProcessHandle process)
    {
        TrackedPlatformBorrowReadGrant[] affected;
        lock (_platformMemoryUseGate) affected = _platformBorrowReadGrants.Values
            .Where(record => record.Owner == process || record.Borrower == process)
            .OrderBy(static record => record.Grant.GrantId.Value)
            .ToArray();

        foreach (TrackedPlatformBorrowReadGrant record in affected)
        {
            KernelResult progress = AdvanceTrackedBorrowReadGrantForTeardown(process, record);
            if (progress.IsSuccess)
                continue;

            if (progress.Error == KernelError.PlatformBindingDraining)
            {
                continue;
            }

            return KernelResult<int>.Fail(progress.Error, progress.Message!);
        }

        KernelResult<SingProcess> current = Processes.Resolve(process);
        if (!current.IsSuccess) return KernelResult<int>.Fail(current.Error, current.Message!);
        // A create may own its Region reservation before a provider receipt or
        // public grant exists. Do not reach bulk loan reclaim past that owner.
        return KernelResult<int>.Ok(Regions.CountExternalBorrowReservationsForDomain(current.Value!.DomainId));
    }

    private KernelResult AdvanceTrackedBorrowReadGrantForTeardown(
        ProcessHandle exitingProcess,
        TrackedPlatformBorrowReadGrant tracked)
    {
        SingProcess owner;
        SingProcess borrower;
        lock (_platformMemoryUseGate)
        {
        if (!_platformBorrowReadGrants.TryGetValue(tracked.Grant.GrantId, out TrackedPlatformBorrowReadGrant? exact) || !ReferenceEquals(exact, tracked))
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "Borrow snapshot changed; reread authoritative reservations.");
            KernelResult<SingProcess> ownerProcess = Processes.Resolve(tracked.Owner);
        if (!ownerProcess.IsSuccess)
            return KernelResult.Fail(ownerProcess.Error, ownerProcess.Message!);

            KernelResult<SingProcess> borrowerProcess = Processes.Resolve(tracked.Borrower);
        if (!borrowerProcess.IsSuccess)
            return KernelResult.Fail(borrowerProcess.Error, borrowerProcess.Message!);

            RegionOwner ownerIdentity = new RegionOwner(
            ownerProcess.Value!.DomainId,
            tracked.Owner.Generation);
            RegionOwner borrowerIdentity = new RegionOwner(
            borrowerProcess.Value!.DomainId,
            tracked.Borrower.Generation);
            KernelResult<BorrowLeaseAuthoritySnapshot> currentBorrow = Regions.ValidateBorrowLease(
            tracked.Grant.BorrowLease,
            ownerIdentity,
            borrowerIdentity);
        if (!currentBorrow.IsSuccess)
            return KernelResult.Fail(currentBorrow.Error, currentBorrow.Message!);

        owner = ownerProcess.Value;
        borrower = borrowerProcess.Value;
        }
        PlatformDomainIdentity platformSubject = PlatformIdentity(owner);
        KernelResult<PlatformBorrowReadGrantLifecycle> lifecycle = PlatformAuthority.QueryBorrowReadGrantLifecycle(
            tracked.Grant,
            platformSubject);
        if (!lifecycle.IsSuccess)
        {
            lock (_platformMemoryUseGate)
                if (!_platformBorrowReadGrants.TryGetValue(tracked.Grant.GrantId, out TrackedPlatformBorrowReadGrant? exact) || !ReferenceEquals(exact, tracked))
                    return KernelResult.Fail(KernelError.PlatformBindingDraining, "Borrow query snapshot changed; reread authoritative reservations.");
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);
        }

        switch (lifecycle.Value!.PlatformClosure)
        {
            case PlatformExternalClosureState.Active:
                lifecycle = PlatformAuthority.BeginBorrowReadGrantRevocation(
                    tracked.Grant,
                    platformSubject);
                break;
            case PlatformExternalClosureState.Draining:
                lifecycle = PlatformAuthority.ObserveBorrowReadGrantRevocation(
                    tracked.Grant,
                    platformSubject);
                break;
            case PlatformExternalClosureState.Faulted:
                return KernelResult.Fail(
                    KernelError.PlatformFaulted,
                    "External borrow read-grant closure faulted during process teardown.");
        }

        lock (_platformMemoryUseGate)
        {
            // A competing actual owner commit may have settled this snapshot.
            // Absence is not closure evidence; the caller rereads Region truth.
            if (!_platformBorrowReadGrants.TryGetValue(tracked.Grant.GrantId, out TrackedPlatformBorrowReadGrant? exact) || !ReferenceEquals(exact, tracked))
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Borrow snapshot changed during progression.");
        }
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);

        if (lifecycle.Value!.PlatformClosure == PlatformExternalClosureState.Faulted)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "External borrow read-grant closure faulted during process teardown.");
        }

        if (lifecycle.Value.PlatformClosure != PlatformExternalClosureState.Closed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "External borrow read grant is still draining during process teardown.");
        }

        if (exitingProcess != tracked.Owner && exitingProcess != tracked.Borrower)
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "Process teardown attempted to close an unrelated external borrow read grant.");
        KernelResult completion = CommitTrackedPlatformBorrowCompletion(tracked, owner, borrower, allowExiting: true);
        if (!completion.IsSuccess && completion.Error == KernelError.PlatformBindingNotFound)
        {
            lock (_platformMemoryUseGate)
                if (!_platformBorrowReadGrants.TryGetValue(tracked.Grant.GrantId, out TrackedPlatformBorrowReadGrant? exact) || !ReferenceEquals(exact, tracked))
                    return KernelResult.Fail(KernelError.PlatformBindingDraining, "Borrow completion snapshot changed; reread authoritative reservations.");
        }
        return completion;
    }

    private KernelResult CommitTrackedPlatformBorrowCompletion(
        TrackedPlatformBorrowReadGrant tracked, SingProcess expectedOwner, SingProcess expectedBorrower,
        bool allowExiting)
    {
        lock (_platformMemoryUseGate)
        {
            if (!_platformBorrowReadGrants.TryGetValue(tracked.Grant.GrantId, out TrackedPlatformBorrowReadGrant? current) ||
                !ReferenceEquals(current, tracked))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Exact tracked borrow grant already settled or changed.");
            KernelResult<SingProcess> owner = Processes.Resolve(tracked.Owner);
            KernelResult<SingProcess> borrower = Processes.Resolve(tracked.Borrower);
            if (!owner.IsSuccess) return KernelResult.Fail(owner.Error, owner.Message!);
            if (!borrower.IsSuccess) return KernelResult.Fail(borrower.Error, borrower.Message!);
            if (!ReferenceEquals(owner.Value, expectedOwner) || !ReferenceEquals(borrower.Value, expectedBorrower))
                return KernelResult.Fail(KernelError.StaleGeneration, "Borrow completion process incarnation changed.");
            if (!allowExiting)
            {
                KernelResult ownerEffect = EnsureProcessAcceptsNewEffects(owner.Value!);
                if (!ownerEffect.IsSuccess) return ownerEffect;
                KernelResult borrowerEffect = EnsureProcessAcceptsNewEffects(borrower.Value!);
                if (!borrowerEffect.IsSuccess) return borrowerEffect;
            }
            RegionOwner ownerIdentity = new RegionOwner(owner.Value!.DomainId, tracked.Owner.Generation);
            RegionOwner borrowerIdentity = new RegionOwner(borrower.Value!.DomainId, tracked.Borrower.Generation);
            KernelResult<BorrowLeaseAuthoritySnapshot> borrow = Regions.ValidateBorrowLease(tracked.Grant.BorrowLease, ownerIdentity, borrowerIdentity);
            if (!borrow.IsSuccess) return KernelResult.Fail(borrow.Error, borrow.Message!);
            KernelResult<object> gate = Regions.GetExternalBorrowCompletionGate(borrow.Value!);
            if (!gate.IsSuccess) return KernelResult.Fail(gate.Error, gate.Message!);
            lock (gate.Value!)
            {
                KernelResult committed = PlatformAuthority.CommitBorrowReadGrantCompletion(
                    tracked.Grant, PlatformIdentity(owner.Value!), borrow.Value!, Regions);
                if (!committed.IsSuccess) return committed;
                UntrackPlatformBorrowReadGrant(tracked.Grant);
                return KernelResult.Ok();
            }
        }
    }
}
