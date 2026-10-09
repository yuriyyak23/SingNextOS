using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<PlatformBorrowReadGrant> CreatePlatformBorrowReadGrant(
        ProcessHandle owner,
        ProcessHandle borrower,
        PlatformDomainBinding externalDomain,
        BorrowLeaseHandle borrowLease,
        long offset,
        long length)
    {
        KernelResult<SingProcess> ownerProcess, borrowerProcess;
        RegionOwner ownerIdentity, borrowerIdentity;
        PlatformDomainIdentity platformSubject;
        BorrowLeaseAuthoritySnapshot snapshot;
        lock (_platformMemoryUseGate)
        {
        ownerProcess = Processes.Resolve(owner);
        if (!ownerProcess.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                ownerProcess.Error,
                ownerProcess.Message!);
        }

        var ownerEffect = EnsureProcessAcceptsNewEffects(ownerProcess.Value!);
        if (!ownerEffect.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                ownerEffect.Error,
                ownerEffect.Message!);
        }

        borrowerProcess = Processes.Resolve(borrower);
        if (!borrowerProcess.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                borrowerProcess.Error,
                borrowerProcess.Message!);
        }

        var borrowerEffect = EnsureProcessAcceptsNewEffects(borrowerProcess.Value!);
        if (!borrowerEffect.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                borrowerEffect.Error,
                borrowerEffect.Message!);
        }

        ownerIdentity = new RegionOwner(
            ownerProcess.Value!.DomainId,
            owner.Generation);
        borrowerIdentity = new RegionOwner(
            borrowerProcess.Value!.DomainId,
            borrower.Generation);
        var borrowValidation = Regions.ValidateBorrowLease(
            borrowLease,
            ownerIdentity,
            borrowerIdentity);
        if (!borrowValidation.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                borrowValidation.Error,
                borrowValidation.Message!);
        }

        platformSubject = PlatformIdentity(ownerProcess.Value);
        var bindingValidation = PlatformAuthority.ValidateDomain(
            externalDomain,
            platformSubject);
        if (!bindingValidation.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);
        }

        snapshot = borrowValidation.Value!;
        var sliceValidation = PlatformOwnedRegionMappingContract.ValidateSlice(
            new PlatformRegionSlice(
                new PlatformRegionIdentity(
                    snapshot.Handle.Region,
                    snapshot.Owner,
                    snapshot.ByteLength),
                offset,
                length,
                PlatformMemoryAccess.Read));
        if (!sliceValidation.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                KernelError.PlatformDenied,
                sliceValidation.Message ?? "The external borrow read range is invalid.");
        }

        var reservation = Regions.ReserveExternalBorrowReadGrant(
            borrowLease,
            ownerIdentity,
            borrowerIdentity);
        if (!reservation.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                reservation.Error,
                reservation.Message!);
        }
        }

        var grant = PlatformAuthority.CreateBorrowReadGrant(
            externalDomain,
            platformSubject,
            snapshot,
            offset,
            length,
            out var retainBorrowReservation);
        if (!grant.IsSuccess)
        {
            if (!retainBorrowReservation)
                _ = Regions.ReleaseExternalBorrowReadGrantReservation(
                    borrowLease,
                    ownerIdentity,
                    borrowerIdentity,
                    snapshot.Lifetime);
            return grant;
        }

        lock (_platformMemoryUseGate)
        {
            // This is the existing cleanup lifetime, even when public admission
            // is refused. It never upgrades a receipt to permission.
            TrackPlatformBorrowReadGrant(owner, borrower, grant.Value!);
            var publication = ValidatePlatformBorrowCreationPublication(owner, borrower,
                ownerProcess.Value!, borrowerProcess.Value!, snapshot, grant.Value!);
            if (!publication.IsSuccess)
            {
                PlatformAuthority.RefuseBorrowReadGrantPublication(grant.Value!);
                return KernelResult<PlatformBorrowReadGrant>.Fail(publication.Error, publication.Message!);
            }
            return grant;
        }
    }

    private KernelResult ValidatePlatformBorrowCreationPublication(
        ProcessHandle owner, ProcessHandle borrower, SingProcess expectedOwner, SingProcess expectedBorrower,
        BorrowLeaseAuthoritySnapshot admitted, PlatformBorrowReadGrant grant, bool readerPublication = false)
    {
        var currentOwner = Processes.Resolve(owner);
        var currentBorrower = Processes.Resolve(borrower);
        if (!currentOwner.IsSuccess) return KernelResult.Fail(currentOwner.Error, currentOwner.Message!);
        if (!currentBorrower.IsSuccess) return KernelResult.Fail(currentBorrower.Error, currentBorrower.Message!);
        if (!ReferenceEquals(currentOwner.Value, expectedOwner) || !ReferenceEquals(currentBorrower.Value, expectedBorrower))
            return KernelResult.Fail(KernelError.StaleGeneration, "Borrow creation process incarnation changed before publication.");
        var ownerEffect = EnsureProcessAcceptsNewEffects(currentOwner.Value!);
        if (!ownerEffect.IsSuccess) return ownerEffect;
        var borrowerEffect = EnsureProcessAcceptsNewEffects(currentBorrower.Value!);
        if (!borrowerEffect.IsSuccess) return borrowerEffect;
        var gate = Regions.GetExternalBorrowCompletionGate(admitted);
        if (!gate.IsSuccess) return KernelResult.Fail(gate.Error, gate.Message!);
        lock (gate.Value!)
        {
            var exact = Regions.ValidateBorrowLease(admitted.Handle, admitted.Owner, admitted.Borrower);
            if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
            if (!ReferenceEquals(exact.Value!.Lifetime, admitted.Lifetime))
                return KernelResult.Fail(KernelError.StaleGeneration, "Borrow creation loan lifetime changed before publication.");
            if (readerPublication)
            {
                var usable = Regions.ValidateExternalBorrowReaderPublicationLocked(admitted);
                if (!usable.IsSuccess) return usable;
            }
            return PlatformAuthority.ValidateBorrowReadGrantPublication(grant, PlatformIdentity(currentOwner.Value!), exact.Value!);
        }
    }

    public KernelResult<PlatformBorrowReadGrantEvidence> PreparePlatformBorrowReadGrantForExternalReader(
        ProcessHandle owner,
        PlatformBorrowReadGrant grant)
    {
        TrackedPlatformBorrowReadGrant tracked;
        SingProcess ownerProcess, borrowerProcess;
        BorrowLeaseAuthoritySnapshot admitted;
        lock (_platformMemoryUseGate)
        {
            if (!_platformBorrowReadGrants.TryGetValue(grant.GrantId, out tracked!) ||
                tracked.Grant != grant || tracked.Owner != owner)
                return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(KernelError.PlatformBindingNotFound, "Exact borrow reader cleanup tracking was not found.");
            var resolvedOwner = Processes.Resolve(owner);
            var resolvedBorrower = Processes.Resolve(tracked.Borrower);
            if (!resolvedOwner.IsSuccess) return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(resolvedOwner.Error, resolvedOwner.Message!);
            if (!resolvedBorrower.IsSuccess) return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(resolvedBorrower.Error, resolvedBorrower.Message!);
            ownerProcess = resolvedOwner.Value!;
            borrowerProcess = resolvedBorrower.Value!;
            var borrow = Regions.ValidateBorrowLease(grant.BorrowLease,
                new RegionOwner(ownerProcess.DomainId, owner.Generation),
                new RegionOwner(borrowerProcess.DomainId, tracked.Borrower.Generation));
            if (!borrow.IsSuccess) return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(borrow.Error, borrow.Message!);
            admitted = borrow.Value!;
            var ready = ValidatePlatformBorrowCreationPublication(owner, tracked.Borrower,
                ownerProcess, borrowerProcess, admitted, grant, readerPublication: true);
            if (!ready.IsSuccess) return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(ready.Error, ready.Message!);
        }

        var evidence = PlatformAuthority.PrepareBorrowReadGrantForExternalReader(grant, PlatformIdentity(ownerProcess));
        if (!evidence.IsSuccess) return evidence;
        lock (_platformMemoryUseGate)
        {
            if (!_platformBorrowReadGrants.TryGetValue(grant.GrantId, out var current) || !ReferenceEquals(current, tracked))
                return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(KernelError.StaleGeneration, "Borrow reader tracking changed before publication.");
            var ready = ValidatePlatformBorrowCreationPublication(owner, tracked.Borrower,
                ownerProcess, borrowerProcess, admitted, grant, readerPublication: true);
            return ready.IsSuccess ? evidence : KernelResult<PlatformBorrowReadGrantEvidence>.Fail(ready.Error, ready.Message!);
        }
    }

    public KernelResult<PlatformBorrowReadGrantLifecycle> QueryPlatformBorrowReadGrantLifecycle(
        ProcessHandle owner,
        PlatformBorrowReadGrant grant)
    {
        var ownerProcess = Processes.Resolve(owner);
        if (!ownerProcess.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrantLifecycle>.Fail(
                ownerProcess.Error,
                ownerProcess.Message!);
        }

        var subject = PlatformIdentity(ownerProcess.Value!);
        return PlatformAuthority.QueryBorrowReadGrantLifecycle(
            grant,
            subject);
    }

    public KernelResult RequestPlatformBorrowCompletion(
        ProcessHandle owner,
        ProcessHandle borrower,
        BorrowLeaseHandle borrowLease,
        PlatformBorrowReadGrant grant)
    {
        if (grant.BorrowLease != borrowLease)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The external read grant does not belong to the requested CPU borrow lease.");
        }

        var ownerProcess = Processes.Resolve(owner);
        if (!ownerProcess.IsSuccess)
            return KernelResult.Fail(ownerProcess.Error, ownerProcess.Message!);
        var ownerEffect = EnsureProcessAcceptsNewEffects(ownerProcess.Value!);
        if (!ownerEffect.IsSuccess) return ownerEffect;

        var borrowerProcess = Processes.Resolve(borrower);
        if (!borrowerProcess.IsSuccess)
            return KernelResult.Fail(borrowerProcess.Error, borrowerProcess.Message!);
        var borrowerEffect = EnsureProcessAcceptsNewEffects(borrowerProcess.Value!);
        if (!borrowerEffect.IsSuccess) return borrowerEffect;

        var ownerIdentity = new RegionOwner(
            ownerProcess.Value!.DomainId,
            owner.Generation);
        var borrowerIdentity = new RegionOwner(
            borrowerProcess.Value!.DomainId,
            borrower.Generation);
        var currentBorrow = Regions.ValidateBorrowLease(
            borrowLease,
            ownerIdentity,
            borrowerIdentity);
        if (!currentBorrow.IsSuccess)
            return KernelResult.Fail(currentBorrow.Error, currentBorrow.Message!);

        var subject = PlatformIdentity(ownerProcess.Value);
        TrackedPlatformBorrowReadGrant tracked;
        lock (_platformMemoryUseGate)
        {
            if (!_platformBorrowReadGrants.TryGetValue(grant.GrantId, out tracked!))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "The external borrow grant is not tracked.");
        }
        var lifecycle = PlatformAuthority.QueryBorrowReadGrantLifecycle(
            grant,
            subject);
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);

        switch (lifecycle.Value!.PlatformClosure)
        {
            case PlatformExternalClosureState.Active:
                lifecycle = PlatformAuthority.BeginBorrowReadGrantRevocation(
                    grant,
                    subject);
                break;
            case PlatformExternalClosureState.Draining:
                lifecycle = PlatformAuthority.ObserveBorrowReadGrantRevocation(
                    grant,
                    subject);
                break;
            case PlatformExternalClosureState.Faulted:
                return KernelResult.Fail(
                    KernelError.PlatformFaulted,
                    "The external read-grant closure faulted; the CPU borrow remains active and pinned.");
        }

        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);

        if (lifecycle.Value!.PlatformClosure == PlatformExternalClosureState.Faulted)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The external read-grant closure faulted; the CPU borrow remains active and pinned.");
        }

        if (lifecycle.Value.PlatformClosure != PlatformExternalClosureState.Closed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "The external read grant is draining; CPU borrow completion remains forbidden.");
        }

        return CommitTrackedPlatformBorrowCompletion(tracked, ownerProcess.Value!, borrowerProcess.Value!, allowExiting: false);
    }
}
