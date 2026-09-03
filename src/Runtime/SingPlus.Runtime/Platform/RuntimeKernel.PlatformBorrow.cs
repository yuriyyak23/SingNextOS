using SingPlus.Contracts;
using SingPlus.Platform;

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
        var ownerProcess = Processes.Resolve(owner);
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

        var borrowerProcess = Processes.Resolve(borrower);
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

        var ownerIdentity = new RegionOwner(
            ownerProcess.Value!.DomainId,
            owner.Generation);
        var borrowerIdentity = new RegionOwner(
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

        var platformSubject = new PlatformDomainIdentity(
            borrowerProcess.Value.DomainId,
            borrower.Generation);
        var bindingValidation = PlatformAuthority.ValidateDomain(
            externalDomain,
            platformSubject);
        if (!bindingValidation.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);
        }

        var snapshot = borrowValidation.Value!;
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

        var grant = PlatformAuthority.CreateBorrowReadGrant(
            externalDomain,
            platformSubject,
            snapshot,
            offset,
            length);
        if (!grant.IsSuccess)
        {
            _ = Regions.ReleaseExternalBorrowReadGrantReservation(
                borrowLease,
                ownerIdentity,
                borrowerIdentity,
                snapshot.Lifetime);
            return grant;
        }

        return grant;
    }

    public KernelResult<PlatformBorrowReadGrantEvidence> PreparePlatformBorrowReadGrantForExternalReader(
        ProcessHandle borrower,
        PlatformBorrowReadGrant grant)
    {
        var borrowerProcess = Processes.Resolve(borrower);
        if (!borrowerProcess.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(
                borrowerProcess.Error,
                borrowerProcess.Message!);
        }

        var effect = EnsureProcessAcceptsNewEffects(borrowerProcess.Value!);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(
                effect.Error,
                effect.Message!);
        }

        var subject = new PlatformDomainIdentity(
            borrowerProcess.Value!.DomainId,
            borrower.Generation);
        return PlatformAuthority.PrepareBorrowReadGrantForExternalReader(
            grant,
            subject);
    }

    public KernelResult<PlatformBorrowReadGrantLifecycle> QueryPlatformBorrowReadGrantLifecycle(
        ProcessHandle borrower,
        PlatformBorrowReadGrant grant)
    {
        var borrowerProcess = Processes.Resolve(borrower);
        if (!borrowerProcess.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrantLifecycle>.Fail(
                borrowerProcess.Error,
                borrowerProcess.Message!);
        }

        var subject = new PlatformDomainIdentity(
            borrowerProcess.Value!.DomainId,
            borrower.Generation);
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

        var subject = new PlatformDomainIdentity(
            borrowerProcess.Value.DomainId,
            borrower.Generation);
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

        var revalidatedBorrow = Regions.ValidateBorrowLease(
            borrowLease,
            ownerIdentity,
            borrowerIdentity);
        if (!revalidatedBorrow.IsSuccess)
            return KernelResult.Fail(revalidatedBorrow.Error, revalidatedBorrow.Message!);

        var exactValidation = PlatformAuthority.ValidateBorrowReadGrantLocalAuthority(
            grant,
            subject,
            revalidatedBorrow.Value!);
        if (!exactValidation.IsSuccess) return exactValidation;

        var releaseReservation = Regions.ReleaseExternalBorrowReadGrantReservation(
            borrowLease,
            ownerIdentity,
            borrowerIdentity,
            revalidatedBorrow.Value!.Lifetime);
        if (!releaseReservation.IsSuccess) return releaseReservation;

        var reclaimGrant = PlatformAuthority.MarkBorrowReadGrantReclaimed(
            grant,
            subject);
        if (!reclaimGrant.IsSuccess)
        {
            _ = Regions.ReserveExternalBorrowReadGrant(
                borrowLease,
                ownerIdentity,
                borrowerIdentity);
            return reclaimGrant;
        }

        return Regions.ReturnLoan(
            borrowLease,
            borrowerIdentity);
    }
}
