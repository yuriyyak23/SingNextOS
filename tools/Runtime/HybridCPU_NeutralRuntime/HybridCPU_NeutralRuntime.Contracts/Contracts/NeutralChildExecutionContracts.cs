namespace YAKSys_Hybrid_CPU.Core;

public readonly record struct NeutralExecutableArtifactHandle(ulong Value);
public readonly record struct NeutralExecutableArtifactEpoch(ulong Value);
public readonly record struct NeutralChildExecutionGeneration(ulong Value);
public readonly record struct NeutralChildStartOperationId(ulong Value);
public readonly record struct NeutralChildStartOperationGeneration(ulong Value);

public readonly record struct NeutralExecutableArtifactRequest(
    NeutralChildDomainLease ChildLease,
    NeutralGuestMappingLease GuestMapping,
    ReadOnlyMemory<byte> ImmutablePackage,
    int MaximumExecutionSteps);

/// <summary>Artifact admission evidence only; never child, memory, or completion authority.</summary>
public readonly record struct NeutralExecutableArtifactReceipt(
    NeutralExecutableArtifactHandle ArtifactHandle,
    NeutralExecutableArtifactEpoch ArtifactEpoch,
    NeutralChildDomainHandle ChildHandle,
    NeutralChildDomainEpoch ChildEpoch,
    NeutralGuestMappingHandle MappingHandle,
    NeutralGuestMappingEpoch MappingEpoch,
    NeutralDomainBindingHandle ParentHandle,
    NeutralDomainBindingEpoch ParentEpoch,
    string ContentDigest,
    int MaximumExecutionSteps);

public readonly record struct NeutralChildExecutionStartRequest(
    NeutralChildDomainLease ChildLease,
    NeutralExecutableArtifactReceipt Artifact,
    NeutralChildStartOperationId OperationId,
    NeutralChildStartOperationGeneration OperationGeneration);

/// <summary>Terminal evidence that the exact admitted artifact retired guest work.</summary>
public readonly record struct NeutralChildExecutionReceipt(
    NeutralExecutableArtifactHandle ArtifactHandle,
    NeutralExecutableArtifactEpoch ArtifactEpoch,
    NeutralChildDomainHandle ChildHandle,
    NeutralChildDomainEpoch ChildEpoch,
    NeutralGuestMappingHandle MappingHandle,
    NeutralGuestMappingEpoch MappingEpoch,
    NeutralDomainBindingHandle ParentHandle,
    NeutralDomainBindingEpoch ParentEpoch,
    string ContentDigest,
    NeutralChildStartOperationId StartOperationId,
    NeutralChildStartOperationGeneration StartOperationGeneration,
    NeutralChildExecutionGeneration ExecutionGeneration,
    int RetiredWorkUnits,
    ulong RetiredSequence,
    ulong LastRetiredCodeOffset,
    bool IsTerminal);

public static class NeutralChildExecutionContract
{
    public const uint ContractVersion = 1;
    public const int MaximumArtifactBytes = 64 * 1024 * 1024;

    public static NeutralVirtualizationResult ValidateAdmission(NeutralExecutableArtifactRequest request)
    {
        var child = NeutralChildDomainContract.ValidateLease(
            request.ChildLease.ParentLease, request.ChildLease.Intent, request.ChildLease);
        if (!child.IsSuccess) return child;
        if ((request.ChildLease.Intent.Authority.ChildAuthority &
             (NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory)) !=
            (NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory))
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Denied,
                "Executable admission requires child execution and guest-memory authority.");
        if (request.GuestMapping.ChildLease != request.ChildLease)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent,
                "Executable artifact mapping belongs to another child.");
        if (request.GuestMapping.Handle.Value == 0 || request.GuestMapping.Epoch.Value == 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale,
                "Executable artifact mapping identity is empty or stale.");
        if (request.ImmutablePackage.IsEmpty || request.ImmutablePackage.Length > MaximumArtifactBytes ||
            request.ImmutablePackage.Length > request.GuestMapping.GuestRange.Length ||
            request.MaximumExecutionSteps <= 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Denied,
                "Executable artifact or finite execution bound exceeds the exact guest mapping.");
        return NeutralChildDomainContract.Ok();
    }

    public static NeutralVirtualizationResult ValidateAdmissionReceipt(
        NeutralExecutableArtifactRequest request, NeutralExecutableArtifactReceipt receipt)
    {
        var validation = ValidateAdmission(request);
        if (!validation.IsSuccess) return validation;
        if (receipt.ArtifactHandle.Value == 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Faulted, "Artifact identity is empty.");
        if (receipt.ArtifactEpoch.Value == 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Artifact epoch is empty.");
        if (receipt.ChildHandle != request.ChildLease.Handle || receipt.ParentHandle != request.ChildLease.ParentLease.Handle)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent, "Artifact receipt has a wrong child or parent.");
        if (receipt.ChildEpoch != request.ChildLease.Epoch || receipt.ParentEpoch != request.ChildLease.ParentLease.Epoch ||
            receipt.MappingEpoch != request.GuestMapping.Epoch)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Artifact receipt has a stale child, parent, or mapping epoch.");
        if (receipt.MappingHandle != request.GuestMapping.Handle || receipt.MaximumExecutionSteps != request.MaximumExecutionSteps ||
            receipt.ContentDigest.Length != 64)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Faulted, "Artifact receipt is not exact admission evidence.");
        return NeutralChildDomainContract.Ok();
    }

    public static NeutralVirtualizationResult ValidateExecutionReceipt(
        NeutralChildExecutionStartRequest request, NeutralChildExecutionReceipt receipt)
    {
        var artifact = request.Artifact;
        var start = ValidateStart(request);
        if (!start.IsSuccess) return start;
        if (receipt.ArtifactHandle != artifact.ArtifactHandle || receipt.ChildHandle != artifact.ChildHandle ||
            receipt.MappingHandle != artifact.MappingHandle || receipt.ParentHandle != artifact.ParentHandle)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent, "Execution receipt identifies different authority bindings.");
        if (receipt.ArtifactEpoch != artifact.ArtifactEpoch || receipt.ChildEpoch != artifact.ChildEpoch ||
            receipt.MappingEpoch != artifact.MappingEpoch || receipt.ParentEpoch != artifact.ParentEpoch)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale, "Execution receipt has a stale binding epoch.");
        if (receipt.ContentDigest != artifact.ContentDigest || receipt.StartOperationId != request.OperationId ||
            receipt.StartOperationGeneration != request.OperationGeneration || receipt.ExecutionGeneration.Value == 0 ||
            receipt.RetiredWorkUnits <= 0 || receipt.RetiredSequence == 0 || !receipt.IsTerminal)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Ambiguous,
                "Execution receipt does not independently prove exact terminal retired work.");
        return NeutralChildDomainContract.Ok();
    }

    public static NeutralVirtualizationResult ValidateStart(NeutralChildExecutionStartRequest request)
    {
        NeutralChildDomainLease child = request.ChildLease;
        NeutralExecutableArtifactReceipt artifact = request.Artifact;
        var childValidation = NeutralChildDomainContract.ValidateLease(child.ParentLease, child.Intent, child);
        if (!childValidation.IsSuccess) return childValidation;
        if (request.OperationId.Value == 0 || request.OperationGeneration.Value == 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Faulted,
                "Executable Start operation identity is empty.");
        if (artifact.ChildHandle != child.Handle || artifact.ParentHandle != child.ParentLease.Handle)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.WrongParent,
                "Executable artifact belongs to another child or parent.");
        if (artifact.ChildEpoch != child.Epoch || artifact.ParentEpoch != child.ParentLease.Epoch ||
            artifact.ArtifactEpoch.Value == 0)
            return NeutralChildDomainContract.Fail(NeutralVirtualizationStatus.Stale,
                "Executable artifact has a stale child, parent, or artifact epoch.");
        return NeutralChildDomainContract.Ok();
    }
}

public interface INeutralChildExecutionProvider
{
    NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> BindExecutableArtifact(
        NeutralExecutableArtifactRequest request);
    NeutralVirtualizationResult<NeutralChildExecutionReceipt> StartExecutableArtifact(
        NeutralChildExecutionStartRequest request);
}
