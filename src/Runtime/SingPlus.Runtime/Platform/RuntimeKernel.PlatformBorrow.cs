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

            KernelResult ownerEffect = EnsureProcessAcceptsNewEffects(ownerProcess.Value!);
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

            KernelResult borrowerEffect = EnsureProcessAcceptsNewEffects(borrowerProcess.Value!);
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
            KernelResult<BorrowLeaseAuthoritySnapshot> borrowValidation = Regions.ValidateBorrowLease(
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
            KernelResult bindingValidation = PlatformAuthority.ValidateDomain(
            externalDomain,
            platformSubject);
        if (!bindingValidation.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);
        }

        snapshot = borrowValidation.Value!;
            PlatformAuthorityResult sliceValidation = PlatformOwnedRegionMappingContract.ValidateSlice(
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

            KernelResult reservation = Regions.ReserveExternalBorrowReadGrant(
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

        KernelResult<PlatformBorrowReadGrant> grant = PlatformAuthority.CreateBorrowReadGrant(
            externalDomain,
            platformSubject,
            snapshot,
            offset,
            length,
            out bool retainBorrowReservation);
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
            KernelResult publication = ValidatePlatformBorrowCreationPublication(owner, borrower,
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
        KernelResult<SingProcess> currentOwner = Processes.Resolve(owner);
        KernelResult<SingProcess> currentBorrower = Processes.Resolve(borrower);
        if (!currentOwner.IsSuccess) return KernelResult.Fail(currentOwner.Error, currentOwner.Message!);
        if (!currentBorrower.IsSuccess) return KernelResult.Fail(currentBorrower.Error, currentBorrower.Message!);
        if (!ReferenceEquals(currentOwner.Value, expectedOwner) || !ReferenceEquals(currentBorrower.Value, expectedBorrower))
            return KernelResult.Fail(KernelError.StaleGeneration, "Borrow creation process incarnation changed before publication.");
        KernelResult ownerEffect = EnsureProcessAcceptsNewEffects(currentOwner.Value!);
        if (!ownerEffect.IsSuccess) return ownerEffect;
        KernelResult borrowerEffect = EnsureProcessAcceptsNewEffects(currentBorrower.Value!);
        if (!borrowerEffect.IsSuccess) return borrowerEffect;
        KernelResult<object> gate = Regions.GetExternalBorrowCompletionGate(admitted);
        if (!gate.IsSuccess) return KernelResult.Fail(gate.Error, gate.Message!);
        lock (gate.Value!)
        {
            KernelResult<BorrowLeaseAuthoritySnapshot> exact = Regions.ValidateBorrowLease(admitted.Handle, admitted.Owner, admitted.Borrower);
            if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
            if (!ReferenceEquals(exact.Value!.Lifetime, admitted.Lifetime))
                return KernelResult.Fail(KernelError.StaleGeneration, "Borrow creation loan lifetime changed before publication.");
            if (readerPublication)
            {
                KernelResult usable = Regions.ValidateExternalBorrowReaderPublicationLocked(admitted);
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
            KernelResult<SingProcess> resolvedOwner = Processes.Resolve(owner);
            KernelResult<SingProcess> resolvedBorrower = Processes.Resolve(tracked.Borrower);
            if (!resolvedOwner.IsSuccess) return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(resolvedOwner.Error, resolvedOwner.Message!);
            if (!resolvedBorrower.IsSuccess) return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(resolvedBorrower.Error, resolvedBorrower.Message!);
            ownerProcess = resolvedOwner.Value!;
            borrowerProcess = resolvedBorrower.Value!;
            KernelResult<BorrowLeaseAuthoritySnapshot> borrow = Regions.ValidateBorrowLease(grant.BorrowLease,
                new RegionOwner(ownerProcess.DomainId, owner.Generation),
                new RegionOwner(borrowerProcess.DomainId, tracked.Borrower.Generation));
            if (!borrow.IsSuccess) return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(borrow.Error, borrow.Message!);
            admitted = borrow.Value!;
            KernelResult ready = ValidatePlatformBorrowCreationPublication(owner, tracked.Borrower,
                ownerProcess, borrowerProcess, admitted, grant, readerPublication: true);
            if (!ready.IsSuccess) return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(ready.Error, ready.Message!);
        }

        KernelResult<PlatformBorrowReadGrantEvidence> evidence = PlatformAuthority.PrepareBorrowReadGrantForExternalReader(grant, PlatformIdentity(ownerProcess));
        if (!evidence.IsSuccess) return evidence;
        lock (_platformMemoryUseGate)
        {
            if (!_platformBorrowReadGrants.TryGetValue(grant.GrantId, out TrackedPlatformBorrowReadGrant? current) || !ReferenceEquals(current, tracked))
                return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(KernelError.StaleGeneration, "Borrow reader tracking changed before publication.");
            KernelResult ready = ValidatePlatformBorrowCreationPublication(owner, tracked.Borrower,
                ownerProcess, borrowerProcess, admitted, grant, readerPublication: true);
            return ready.IsSuccess ? evidence : KernelResult<PlatformBorrowReadGrantEvidence>.Fail(ready.Error, ready.Message!);
        }
    }

    public KernelResult<PlatformBorrowReadGrantLifecycle> QueryPlatformBorrowReadGrantLifecycle(
        ProcessHandle owner,
        PlatformBorrowReadGrant grant)
    {
        KernelResult<SingProcess> ownerProcess = Processes.Resolve(owner);
        if (!ownerProcess.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrantLifecycle>.Fail(
                ownerProcess.Error,
                ownerProcess.Message!);
        }

        PlatformDomainIdentity subject = PlatformIdentity(ownerProcess.Value!);
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

        KernelResult<SingProcess> ownerProcess = Processes.Resolve(owner);
        if (!ownerProcess.IsSuccess)
            return KernelResult.Fail(ownerProcess.Error, ownerProcess.Message!);
        KernelResult ownerEffect = EnsureProcessAcceptsNewEffects(ownerProcess.Value!);
        if (!ownerEffect.IsSuccess) return ownerEffect;

        KernelResult<SingProcess> borrowerProcess = Processes.Resolve(borrower);
        if (!borrowerProcess.IsSuccess)
            return KernelResult.Fail(borrowerProcess.Error, borrowerProcess.Message!);
        KernelResult borrowerEffect = EnsureProcessAcceptsNewEffects(borrowerProcess.Value!);
        if (!borrowerEffect.IsSuccess) return borrowerEffect;

        RegionOwner ownerIdentity = new RegionOwner(
            ownerProcess.Value!.DomainId,
            owner.Generation);
        RegionOwner borrowerIdentity = new RegionOwner(
            borrowerProcess.Value!.DomainId,
            borrower.Generation);
        KernelResult<BorrowLeaseAuthoritySnapshot> currentBorrow = Regions.ValidateBorrowLease(
            borrowLease,
            ownerIdentity,
            borrowerIdentity);
        if (!currentBorrow.IsSuccess)
            return KernelResult.Fail(currentBorrow.Error, currentBorrow.Message!);

        PlatformDomainIdentity subject = PlatformIdentity(ownerProcess.Value);
        TrackedPlatformBorrowReadGrant tracked;
        lock (_platformMemoryUseGate)
        {
            if (!_platformBorrowReadGrants.TryGetValue(grant.GrantId, out tracked!))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "The external borrow grant is not tracked.");
        }
        KernelResult<PlatformBorrowReadGrantLifecycle> lifecycle = PlatformAuthority.QueryBorrowReadGrantLifecycle(
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
