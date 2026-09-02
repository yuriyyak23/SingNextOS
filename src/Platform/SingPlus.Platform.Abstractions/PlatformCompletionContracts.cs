namespace SingPlus.Platform;

public readonly record struct PlatformOperationId(ulong Value);
public readonly record struct PlatformOperationGeneration(ulong Value);

public readonly record struct PlatformOperationIdentity(
    PlatformOperationId OperationId,
    PlatformOperationGeneration Generation,
    PlatformProviderDomainLease DomainLease);

public enum PlatformCompletionState
{
    Staged = 0,
    Pending,
    Draining,
    Completed,
    Cancelled,
    Closed,
    Faulted
}

public readonly record struct PlatformCompletionReceipt(
    PlatformOperationId OperationId,
    PlatformOperationGeneration Generation,
    PlatformProviderDomainLease DomainLease,
    PlatformCompletionState State)
{
    public PlatformOperationIdentity Operation =>
        new(OperationId, Generation, DomainLease);

    public bool IsTerminal => PlatformCompletionContract.IsTerminal(State);

    public bool ProvesClosure => State == PlatformCompletionState.Closed;
}

public static class PlatformCompletionContract
{
    public static bool IsTerminal(PlatformCompletionState state) =>
        state is PlatformCompletionState.Closed or PlatformCompletionState.Faulted;

    public static bool IsNonTerminal(PlatformCompletionState state) =>
        Enum.IsDefined(state) && !IsTerminal(state);

    public static PlatformAuthorityResult ValidateReceiptIdentity(
        PlatformOperationIdentity expectedOperation,
        PlatformCompletionReceipt receipt)
    {
        if (expectedOperation.OperationId.Value == 0 ||
            expectedOperation.Generation.Value == 0 ||
            receipt.OperationId.Value == 0 ||
            receipt.Generation.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Completion identities must use non-zero operation IDs and generations.");
        }

        if (!Enum.IsDefined(receipt.State))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The completion receipt contains an undefined state.");
        }

        if (receipt.OperationId != expectedOperation.OperationId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The completion receipt belongs to a different operation.");
        }

        if (receipt.Generation != expectedOperation.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The completion receipt operation generation is stale.");
        }

        if (receipt.DomainLease.LeaseId != expectedOperation.DomainLease.LeaseId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The completion receipt belongs to a different platform domain lease.");
        }

        if (receipt.DomainLease.Generation != expectedOperation.DomainLease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The completion receipt platform-domain generation is stale.");
        }

        if (receipt.DomainLease.Subject != expectedOperation.DomainLease.Subject)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The completion receipt platform-domain subject does not match the operation.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformCompletionProvider
{
    PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
        PlatformOperationIdentity operation);
}
