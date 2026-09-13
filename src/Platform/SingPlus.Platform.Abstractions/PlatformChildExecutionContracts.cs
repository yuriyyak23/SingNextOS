namespace SingPlus.Platform;

public readonly record struct PlatformExecutableArtifactId(ulong Value);
public readonly record struct PlatformExecutableArtifactGeneration(ulong Value);
public readonly record struct PlatformChildExecutionGeneration(ulong Value);
public readonly record struct PlatformChildStartOperationId(ulong Value);
public readonly record struct PlatformChildStartOperationGeneration(ulong Value);

public readonly record struct PlatformExecutableArtifactRequest(
    PlatformProviderChildDomainLease ChildLease,
    PlatformProviderGuestRegionMappingLease GuestMapping,
    ReadOnlyMemory<byte> ImmutablePackage,
    int MaximumExecutionSteps);

public readonly record struct PlatformExecutableArtifactReceipt(
    PlatformExecutableArtifactId ArtifactId,
    PlatformExecutableArtifactGeneration ArtifactGeneration,
    PlatformProviderChildDomainLeaseId ChildLeaseId,
    PlatformProviderLeaseGeneration ChildGeneration,
    PlatformProviderGuestRegionMappingLeaseId MappingLeaseId,
    PlatformProviderLeaseGeneration MappingGeneration,
    PlatformProviderDomainLeaseId ParentLeaseId,
    PlatformProviderLeaseGeneration ParentGeneration,
    string ContentDigest,
    int MaximumExecutionSteps);

public readonly record struct PlatformChildExecutionStartRequest(
    PlatformProviderChildDomainLease ChildLease,
    PlatformExecutableArtifactReceipt Artifact,
    PlatformChildStartOperationId OperationId,
    PlatformChildStartOperationGeneration OperationGeneration);

/// <summary>Terminal retired-work evidence; never authority or a local reclaim grant.</summary>
public readonly record struct PlatformChildExecutionReceipt(
    PlatformExecutableArtifactId ArtifactId,
    PlatformExecutableArtifactGeneration ArtifactGeneration,
    PlatformProviderChildDomainLeaseId ChildLeaseId,
    PlatformProviderLeaseGeneration ChildGeneration,
    PlatformProviderGuestRegionMappingLeaseId MappingLeaseId,
    PlatformProviderLeaseGeneration MappingGeneration,
    PlatformProviderDomainLeaseId ParentLeaseId,
    PlatformProviderLeaseGeneration ParentGeneration,
    string ContentDigest,
    PlatformChildStartOperationId StartOperationId,
    PlatformChildStartOperationGeneration StartOperationGeneration,
    PlatformChildExecutionGeneration ExecutionGeneration,
    int RetiredWorkUnits,
    ulong RetiredSequence,
    ulong LastRetiredCodeOffset,
    bool IsTerminal);

public static class PlatformChildExecutionContract
{
    public const uint ContractVersion = 1;

    public static PlatformAuthorityResult ValidateRequest(PlatformExecutableArtifactRequest request)
    {
        if (request.ChildLease != request.GuestMapping.ChildLease)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain, "Artifact mapping belongs to another child.");
        if ((request.ChildLease.Intent.Authority.ChildAuthority &
             (PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory)) !=
            (PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Artifact admission requires execution and guest-memory authority.");
        if (request.ImmutablePackage.IsEmpty || request.ImmutablePackage.Length > NeutralArtifactLimit ||
            request.ImmutablePackage.Length > request.GuestMapping.GuestRange.ByteLength || request.MaximumExecutionSteps <= 0)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Artifact and finite execution bound must fit the exact guest mapping.");
        return PlatformAuthorityResult.Ok();
    }

    public const int NeutralArtifactLimit = 64 * 1024 * 1024;

    public static PlatformAuthorityResult ValidateAdmissionReceipt(
        PlatformExecutableArtifactRequest request, PlatformExecutableArtifactReceipt receipt)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess) return requestValidation;
        if (receipt.ArtifactId.Value == 0 || receipt.ContentDigest.Length != 64 ||
            receipt.MaximumExecutionSteps != request.MaximumExecutionSteps)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Artifact admission evidence is empty or does not match the request.");
        if (receipt.ChildLeaseId != request.ChildLease.LeaseId ||
            receipt.MappingLeaseId != request.GuestMapping.LeaseId ||
            receipt.ParentLeaseId != request.ChildLease.ParentDomainLease.LeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain,
                "Artifact admission evidence identifies another child, mapping, or parent.");
        if (receipt.ArtifactGeneration.Value == 0 ||
            receipt.ChildGeneration != request.ChildLease.Generation ||
            receipt.MappingGeneration != request.GuestMapping.Generation ||
            receipt.ParentGeneration != request.ChildLease.ParentDomainLease.Generation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale,
                "Artifact admission evidence has a stale generation.");
        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateStart(PlatformChildExecutionStartRequest request)
    {
        PlatformProviderChildDomainLease child = request.ChildLease;
        PlatformExecutableArtifactReceipt artifact = request.Artifact;
        if (request.OperationId.Value == 0 || request.OperationGeneration.Value == 0)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                "Executable Start operation identity is empty.");
        if (artifact.ChildLeaseId != child.LeaseId || artifact.ParentLeaseId != child.ParentDomainLease.LeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain,
                "Executable artifact belongs to another child or parent.");
        if (artifact.ChildGeneration != child.Generation ||
            artifact.ParentGeneration != child.ParentDomainLease.Generation ||
            artifact.ArtifactGeneration.Value == 0)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale,
                "Executable artifact has a stale child, parent, or artifact generation.");
        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateExecution(
        PlatformChildExecutionStartRequest request, PlatformChildExecutionReceipt receipt)
    {
        PlatformExecutableArtifactReceipt artifact = request.Artifact;
        var start = ValidateStart(request);
        if (!start.IsSuccess) return start;
        if (receipt.ArtifactId != artifact.ArtifactId || receipt.ChildLeaseId != artifact.ChildLeaseId ||
            receipt.MappingLeaseId != artifact.MappingLeaseId || receipt.ParentLeaseId != artifact.ParentLeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain, "Execution evidence identifies another binding.");
        if (receipt.ArtifactGeneration != artifact.ArtifactGeneration || receipt.ChildGeneration != artifact.ChildGeneration ||
            receipt.MappingGeneration != artifact.MappingGeneration || receipt.ParentGeneration != artifact.ParentGeneration)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Execution evidence has a stale generation.");
        if (receipt.ContentDigest != artifact.ContentDigest || receipt.StartOperationId != request.OperationId ||
            receipt.StartOperationGeneration != request.OperationGeneration || receipt.ExecutionGeneration.Value == 0 ||
            receipt.RetiredWorkUnits <= 0 || receipt.RetiredSequence == 0 || !receipt.IsTerminal)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Execution evidence is incomplete or ambiguous.");
        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformChildExecutionProvider
{
    PlatformAuthorityResult<PlatformExecutableArtifactReceipt> BindExecutableArtifact(
        PlatformExecutableArtifactRequest request);
    PlatformAuthorityResult<PlatformChildExecutionReceipt> StartExecutableArtifact(
        PlatformChildExecutionStartRequest request);
}
