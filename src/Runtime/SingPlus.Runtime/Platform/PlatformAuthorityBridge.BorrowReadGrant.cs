using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public readonly record struct PlatformBorrowReadGrantId(ulong Value);
public readonly record struct PlatformBorrowReadGrantGeneration(ulong Value);

public readonly record struct PlatformBorrowReadGrant(
    PlatformBorrowReadGrantId GrantId,
    PlatformBorrowReadGrantGeneration Generation,
    BorrowLeaseHandle BorrowLease,
    PlatformDomainBinding DomainBinding,
    long Offset,
    long Length)
{
    public RegionHandle Region => BorrowLease.Region;
    public PlatformMemoryAccess Access => PlatformMemoryAccess.Read;
}

public readonly record struct PlatformBorrowReadGrantEvidence(
    PlatformBorrowReadGrant Grant,
    PlatformMemoryVisibilityOutcome Outcome)
{
    public PlatformMemoryConsumerClass Consumer =>
        PlatformMemoryConsumerClass.ExternalExecutionDomain;

    public PlatformMemoryVisibilityRequirement Requirement =>
        PlatformMemoryVisibilityRequirement.PublicationFence;

    public bool IsSatisfied =>
        PlatformMemoryVisibilityContract.IsSatisfied(Requirement, Outcome);
}

public readonly record struct PlatformBorrowReadGrantLifecycle(
    PlatformBorrowReadGrant Grant,
    PlatformExternalClosureState PlatformClosure,
    bool LocalReservationReleased)
{
    public bool BorrowCompletionAllowed =>
        PlatformClosure == PlatformExternalClosureState.Closed &&
        LocalReservationReleased;
}

public sealed partial class PlatformAuthorityBridge
{
    private sealed class BorrowReadGrantRecord(
        PlatformBorrowReadGrant grant,
        PlatformOwnedRegionSliceMapping mapping,
        RegionOwner owner,
        RegionOwner borrower,
        BorrowLeaseLifetime borrowLifetime)
    {
        public PlatformBorrowReadGrant Grant { get; } = grant;
        public PlatformOwnedRegionSliceMapping Mapping { get; } = mapping;
        public RegionOwner Owner { get; } = owner;
        public RegionOwner Borrower { get; } = borrower;
        public BorrowLeaseLifetime BorrowLifetime { get; } = borrowLifetime;
    }

    private readonly Dictionary<PlatformBorrowReadGrantId, BorrowReadGrantRecord>
        _borrowReadGrants = [];
    private ulong _nextBorrowReadGrantId = 1;

    internal KernelResult<PlatformBorrowReadGrant> CreateBorrowReadGrant(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        BorrowLeaseAuthoritySnapshot borrow,
        long offset,
        long length)
    {
        var expectedOwner = new RegionOwner(
            expectedSubject.DomainId,
            expectedSubject.ProcessGeneration);
        if (borrow.Owner != expectedOwner)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                KernelError.WrongPlatformDomain,
                "The external platform domain is not bound to the exact Sing region owner.");
        }

        if (!borrow.Lifetime.IsActive)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                KernelError.InvalidRegionState,
                "The CPU borrow lifetime is no longer active.");
        }

        var slice = new PlatformRegionSlice(
            new PlatformRegionIdentity(
                borrow.Handle.Region,
                borrow.Owner,
                borrow.ByteLength),
            offset,
            length,
            PlatformMemoryAccess.Read);

        var mapped = MapOwnedRegionSlice(
            binding,
            expectedSubject,
            default,
            slice);
        if (!mapped.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrant>.Fail(
                mapped.Error,
                mapped.Message!);
        }

        var grant = new PlatformBorrowReadGrant(
            new PlatformBorrowReadGrantId(_nextBorrowReadGrantId++),
            new PlatformBorrowReadGrantGeneration(1),
            borrow.Handle,
            binding,
            offset,
            length);

        _borrowReadGrants.Add(
            grant.GrantId,
            new BorrowReadGrantRecord(
                grant,
                mapped.Value!,
                borrow.Owner,
                borrow.Borrower,
                borrow.Lifetime));

        return KernelResult<PlatformBorrowReadGrant>.Ok(grant);
    }

    internal KernelResult<PlatformBorrowReadGrantEvidence> PrepareBorrowReadGrantForExternalReader(
        PlatformBorrowReadGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateBorrowReadGrantIdentity(grant, expectedSubject);
        if (!validation.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(
                validation.Error,
                validation.Message!);
        }

        var prepared = PrepareRegionMappingForConsumer(
            validation.Value!.Mapping,
            expectedSubject,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);
        if (!prepared.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrantEvidence>.Fail(
                prepared.Error,
                prepared.Message!);
        }

        return KernelResult<PlatformBorrowReadGrantEvidence>.Ok(
            new PlatformBorrowReadGrantEvidence(
                grant,
                prepared.Value!.Outcome));
    }

    internal KernelResult<PlatformBorrowReadGrantLifecycle> BeginBorrowReadGrantRevocation(
        PlatformBorrowReadGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateBorrowReadGrantIdentity(grant, expectedSubject);
        if (!validation.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrantLifecycle>.Fail(
                validation.Error,
                validation.Message!);
        }

        return ProjectBorrowReadGrantLifecycle(
            grant,
            BeginRegionMappingRevocation(
                validation.Value!.Mapping.Mapping,
                expectedSubject,
                PlatformRegionRevocationPolicy.DrainBeforeRevoke));
    }

    internal KernelResult<PlatformBorrowReadGrantLifecycle> ObserveBorrowReadGrantRevocation(
        PlatformBorrowReadGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateBorrowReadGrantIdentity(grant, expectedSubject);
        if (!validation.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrantLifecycle>.Fail(
                validation.Error,
                validation.Message!);
        }

        return ProjectBorrowReadGrantLifecycle(
            grant,
            ObserveRegionMappingRevocation(
                validation.Value!.Mapping.Mapping,
                expectedSubject));
    }

    internal KernelResult<PlatformBorrowReadGrantLifecycle> QueryBorrowReadGrantLifecycle(
        PlatformBorrowReadGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateBorrowReadGrantIdentity(grant, expectedSubject);
        if (!validation.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrantLifecycle>.Fail(
                validation.Error,
                validation.Message!);
        }

        return ProjectBorrowReadGrantLifecycle(
            grant,
            QueryRegionMappingLifecycle(
                validation.Value!.Mapping.Mapping,
                expectedSubject));
    }

    internal KernelResult ValidateBorrowReadGrantLocalAuthority(
        PlatformBorrowReadGrant grant,
        PlatformDomainIdentity expectedSubject,
        BorrowLeaseAuthoritySnapshot currentBorrow)
    {
        var validation = ValidateBorrowReadGrantIdentity(grant, expectedSubject);
        if (!validation.IsSuccess)
            return KernelResult.Fail(validation.Error, validation.Message!);

        var record = validation.Value!;
        var expectedOwner = new RegionOwner(
            expectedSubject.DomainId,
            expectedSubject.ProcessGeneration);
        if (currentBorrow.Owner != expectedOwner || record.Owner != expectedOwner)
        {
            return KernelResult.Fail(
                KernelError.WrongRegionOwner,
                "The exact Sing owner no longer matches the external platform domain.");
        }

        if (record.Grant.BorrowLease != currentBorrow.Handle)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The CPU borrow identity no longer matches the external read grant.");
        }

        if (record.Borrower != currentBorrow.Borrower)
        {
            return KernelResult.Fail(
                KernelError.WrongRegionOwner,
                "The CPU borrower no longer matches the external read grant.");
        }

        if (!ReferenceEquals(record.BorrowLifetime, currentBorrow.Lifetime) ||
            !currentBorrow.Lifetime.IsActive)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The CPU borrow lifetime no longer matches the external read grant.");
        }

        var bindingValidation = ValidateDomain(grant.DomainBinding, expectedSubject);
        if (!bindingValidation.IsSuccess) return bindingValidation;

        var mappingValidation = ValidateMappingIdentity(
            record.Mapping.Mapping,
            expectedSubject);
        if (!mappingValidation.IsSuccess) return mappingValidation;

        var lifecycle = QueryRegionMappingLifecycle(
            record.Mapping.Mapping,
            expectedSubject);
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);
        if (lifecycle.Value!.PlatformClosure != PlatformExternalClosureState.Closed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "Verified Closed completion is required before the CPU borrow can complete.");
        }

        return KernelResult.Ok();
    }

    internal KernelResult MarkBorrowReadGrantReclaimed(
        PlatformBorrowReadGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateBorrowReadGrantIdentity(grant, expectedSubject);
        if (!validation.IsSuccess)
            return KernelResult.Fail(validation.Error, validation.Message!);

        var record = validation.Value!;
        var lifecycle = QueryRegionMappingLifecycle(
            record.Mapping.Mapping,
            expectedSubject);
        if (!lifecycle.IsSuccess)
            return KernelResult.Fail(lifecycle.Error, lifecycle.Message!);
        if (lifecycle.Value!.PlatformClosure != PlatformExternalClosureState.Closed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingDraining,
                "External read-grant metadata cannot be reclaimed before verified closure.");
        }

        var markReleased = MarkRegionReservationReleased(
            record.Mapping.Mapping,
            expectedSubject);
        if (!markReleased.IsSuccess) return markReleased;

        ForgetExactMappingMetadata(record.Mapping.Mapping);
        _borrowReadGrants.Remove(grant.GrantId);
        return KernelResult.Ok();
    }

    private KernelResult<BorrowReadGrantRecord> ValidateBorrowReadGrantIdentity(
        PlatformBorrowReadGrant grant,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_borrowReadGrants.TryGetValue(grant.GrantId, out var record))
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                KernelError.PlatformBindingNotFound,
                "The external CPU-borrow read grant does not exist.");
        }

        if (record.Grant.Generation != grant.Generation)
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                KernelError.StaleGeneration,
                "The external CPU-borrow read grant generation is stale.");
        }

        if (record.Grant.DomainBinding.BindingId != grant.DomainBinding.BindingId ||
            record.Grant.DomainBinding.Subject != grant.DomainBinding.Subject ||
            grant.DomainBinding.Subject != expectedSubject)
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                KernelError.WrongPlatformDomain,
                "The external CPU-borrow read grant belongs to a different platform domain.");
        }

        if (record.Grant.DomainBinding.Generation != grant.DomainBinding.Generation)
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                KernelError.StaleGeneration,
                "The external CPU-borrow platform-binding generation is stale.");
        }

        var expectedOwner = new RegionOwner(
            expectedSubject.DomainId,
            expectedSubject.ProcessGeneration);
        if (record.Owner != expectedOwner)
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                KernelError.WrongPlatformDomain,
                "The external platform domain no longer matches the exact Sing owner.");
        }

        if (record.Grant.BorrowLease.Region.RegionId != grant.BorrowLease.Region.RegionId)
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                KernelError.PlatformDenied,
                "The external read grant refers to a different CPU-borrow region.");
        }

        if (record.Grant.BorrowLease.Region.Generation != grant.BorrowLease.Region.Generation ||
            record.Grant.BorrowLease.Generation != grant.BorrowLease.Generation)
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                KernelError.StaleGeneration,
                "The external read grant carries a stale CPU-borrow identity.");
        }

        if (record.Grant.Offset != grant.Offset ||
            record.Grant.Length != grant.Length ||
            grant.Offset < 0 ||
            grant.Length <= 0)
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                KernelError.PlatformDenied,
                "The external read grant range does not match the admitted exact range.");
        }

        if (!record.BorrowLifetime.IsActive)
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                KernelError.PlatformBindingRevoked,
                "The CPU borrow lifetime backing the external read grant is no longer active.");
        }

        var mappingIdentity = ValidateMappingIdentity(
            record.Mapping.Mapping,
            expectedSubject);
        if (!mappingIdentity.IsSuccess)
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                mappingIdentity.Error,
                mappingIdentity.Message!);
        }

        if (!_exactMappingSlices.TryGetValue(
                record.Mapping.Mapping.MappingId,
                out var slice))
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                KernelError.PlatformBindingNotFound,
                "The external read grant lost its exact mapping metadata.");
        }

        if (slice.Region.Handle != grant.BorrowLease.Region ||
            slice.Region.Owner != record.Owner ||
            slice.Offset != grant.Offset ||
            slice.Length != grant.Length ||
            slice.Access != PlatformMemoryAccess.Read ||
            record.Mapping.Access != PlatformMemoryAccess.Read)
        {
            return KernelResult<BorrowReadGrantRecord>.Fail(
                KernelError.PlatformFaulted,
                "The external read grant no longer matches its exact read-only mapping.");
        }

        return KernelResult<BorrowReadGrantRecord>.Ok(record);
    }

    private static KernelResult<PlatformBorrowReadGrantLifecycle> ProjectBorrowReadGrantLifecycle(
        PlatformBorrowReadGrant grant,
        KernelResult<PlatformRegionMappingLifecycle> lifecycle)
    {
        if (!lifecycle.IsSuccess)
        {
            return KernelResult<PlatformBorrowReadGrantLifecycle>.Fail(
                lifecycle.Error,
                lifecycle.Message!);
        }

        return KernelResult<PlatformBorrowReadGrantLifecycle>.Ok(
            new PlatformBorrowReadGrantLifecycle(
                grant,
                lifecycle.Value!.PlatformClosure,
                lifecycle.Value.LocalReservationReleased));
    }
}
