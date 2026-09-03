using SingPlus.Contracts;
using SingPlus.Platform;

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
        PlatformBorrowReadGrant grant) =>
        _platformBorrowReadGrants.Add(
            grant.GrantId,
            new TrackedPlatformBorrowReadGrant(owner, borrower, grant));

    private void UntrackPlatformBorrowReadGrant(PlatformBorrowReadGrant grant) =>
        _platformBorrowReadGrants.Remove(grant.GrantId);

    private KernelResult<int> AdvancePlatformBorrowReadGrantsForProcess(
        ProcessHandle process)
    {
        var affected = _platformBorrowReadGrants.Values
            .Where(record => record.Owner == process || record.Borrower == process)
            .OrderBy(static record => record.Grant.GrantId.Value)
            .ToArray();
        var pending = 0;

        foreach (var record in affected)
        {
            var progress = AdvanceTrackedBorrowReadGrantForTeardown(process, record);
            if (progress.IsSuccess)
                continue;

            if (progress.Error == KernelError.PlatformBindingDraining)
            {
                pending++;
                continue;
            }

            return KernelResult<int>.Fail(progress.Error, progress.Message!);
        }

        return KernelResult<int>.Ok(pending);
    }

    private KernelResult AdvanceTrackedBorrowReadGrantForTeardown(
        ProcessHandle exitingProcess,
        TrackedPlatformBorrowReadGrant tracked)
    {
        var ownerProcess = Processes.Resolve(tracked.Owner);
        if (!ownerProcess.IsSuccess)
            return KernelResult.Fail(ownerProcess.Error, ownerProcess.Message!);

        var borrowerProcess = Processes.Resolve(tracked.Borrower);
        if (!borrowerProcess.IsSuccess)
            return KernelResult.Fail(borrowerProcess.Error, borrowerProcess.Message!);

        var ownerIdentity = new RegionOwner(
            ownerProcess.Value!.DomainId,
            tracked.Owner.Generation);
        var borrowerIdentity = new RegionOwner(
            borrowerProcess.Value!.DomainId,
            tracked.Borrower.Generation);
        var currentBorrow = Regions.ValidateBorrowLease(
            tracked.Grant.BorrowLease,
            ownerIdentity,
            borrowerIdentity);
        if (!currentBorrow.IsSuccess)
            return KernelResult.Fail(currentBorrow.Error, currentBorrow.Message!);

        var platformSubject = new PlatformDomainIdentity(
            ownerProcess.Value.DomainId,
            tracked.Owner.Generation);
        var lifecycle = PlatformAuthority.QueryBorrowReadGrantLifecycle(
            tracked.Grant,
            platformSubject);
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);

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

        var revalidatedBorrow = Regions.ValidateBorrowLease(
            tracked.Grant.BorrowLease,
            ownerIdentity,
            borrowerIdentity);
        if (!revalidatedBorrow.IsSuccess)
            return KernelResult.Fail(revalidatedBorrow.Error, revalidatedBorrow.Message!);

        var exactValidation = PlatformAuthority.ValidateBorrowReadGrantLocalAuthority(
            tracked.Grant,
            platformSubject,
            revalidatedBorrow.Value!);
        if (!exactValidation.IsSuccess) return exactValidation;

        var releaseReservation = Regions.ReleaseExternalBorrowReadGrantReservation(
            tracked.Grant.BorrowLease,
            ownerIdentity,
            borrowerIdentity,
            revalidatedBorrow.Value!.Lifetime);
        if (!releaseReservation.IsSuccess) return releaseReservation;

        var reclaimGrant = PlatformAuthority.MarkBorrowReadGrantReclaimed(
            tracked.Grant,
            platformSubject);
        if (!reclaimGrant.IsSuccess)
        {
            _ = Regions.ReserveExternalBorrowReadGrant(
                tracked.Grant.BorrowLease,
                ownerIdentity,
                borrowerIdentity);
            return reclaimGrant;
        }

        KernelResult completeBorrow;
        if (exitingProcess == tracked.Owner)
        {
            completeBorrow = Regions.RevokeLoan(
                tracked.Grant.BorrowLease,
                ownerIdentity);
        }
        else if (exitingProcess == tracked.Borrower)
        {
            completeBorrow = Regions.ReturnLoan(
                tracked.Grant.BorrowLease,
                borrowerIdentity);
        }
        else
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "Process teardown attempted to close an unrelated external borrow read grant.");
        }

        if (!completeBorrow.IsSuccess) return completeBorrow;

        UntrackPlatformBorrowReadGrant(tracked.Grant);
        return KernelResult.Ok();
    }
}
