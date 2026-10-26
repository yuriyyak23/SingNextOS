using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;

namespace SingPlus.Tests.Platform;

public sealed class PlatformCompletionContractTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public void HostOperationUsesIndependentIdentityAndStartsStaged()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformProviderDomainLease domainLease = Bind(provider, 10, 1);

        PlatformAuthorityResult<PlatformOperationIdentity> staged = provider.StageOperation(domainLease);
        PlatformAuthorityResult<PlatformCompletionReceipt> observed = provider.ObserveCompletion(staged.Value!);

        Assert.True(staged.IsSuccess, staged.Message);
        Assert.True(observed.IsSuccess, observed.Message);
        Assert.NotEqual(typeof(PlatformOperationId), typeof(PlatformProviderDomainLeaseId));
        Assert.NotEqual(typeof(PlatformOperationGeneration), typeof(PlatformProviderLeaseGeneration));
        Assert.Equal(PlatformCompletionState.Staged, observed.Value!.State);
        Assert.False(observed.Value.IsTerminal);
        Assert.False(observed.Value.ProvesClosure);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void CompletedAndCancelledDoNotProveExternalClosure()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformProviderDomainLease domainLease = Bind(provider, 10, 1);

        PlatformOperationIdentity completedOperation = provider.StageOperation(domainLease).Value!;
        Assert.True(provider.AdvanceOperation(
            completedOperation,
            PlatformCompletionState.Pending).IsSuccess);
        PlatformCompletionReceipt completed = provider.AdvanceOperation(
            completedOperation,
            PlatformCompletionState.Completed).Value!;

        Assert.False(completed.IsTerminal);
        Assert.False(completed.ProvesClosure);

        PlatformCompletionReceipt closed = provider.AdvanceOperation(
            completedOperation,
            PlatformCompletionState.Closed).Value!;

        Assert.True(closed.IsTerminal);
        Assert.True(closed.ProvesClosure);

        PlatformOperationIdentity cancelledOperation = provider.StageOperation(domainLease).Value!;
        PlatformCompletionReceipt cancelled = provider.AdvanceOperation(
            cancelledOperation,
            PlatformCompletionState.Cancelled).Value!;

        Assert.False(cancelled.IsTerminal);
        Assert.False(cancelled.ProvesClosure);

        PlatformCompletionReceipt cancelledClosed = provider.AdvanceOperation(
            cancelledOperation,
            PlatformCompletionState.Closed).Value!;

        Assert.True(cancelledClosed.IsTerminal);
        Assert.True(cancelledClosed.ProvesClosure);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void FaultedIsTerminalButNeverProvesClosure()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformOperationIdentity operation = provider.StageOperation(Bind(provider, 10, 1)).Value!;

        PlatformCompletionReceipt faulted = provider.AdvanceOperation(
            operation,
            PlatformCompletionState.Faulted).Value!;
        PlatformAuthorityResult<PlatformCompletionReceipt> closeAfterFault = provider.AdvanceOperation(
            operation,
            PlatformCompletionState.Closed);

        Assert.True(faulted.IsTerminal);
        Assert.False(faulted.ProvesClosure);
        Assert.False(closeAfterFault.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Denied, closeAfterFault.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void StaleOperationGenerationIsRejectedBeforeObservation()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformOperationIdentity operation = provider.StageOperation(Bind(provider, 10, 1)).Value!;
        PlatformOperationIdentity stale = operation with
        {
            Generation = new PlatformOperationGeneration(operation.Generation.Value + 1)
        };

        PlatformAuthorityResult<PlatformCompletionReceipt> observed = provider.ObserveCompletion(stale);

        Assert.False(observed.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Stale, observed.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void WrongDomainOperationIdentityIsRejected()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformProviderDomainLease leftLease = Bind(provider, 10, 1);
        PlatformProviderDomainLease rightLease = Bind(provider, 20, 2);
        PlatformOperationIdentity operation = provider.StageOperation(leftLease).Value!;
        PlatformOperationIdentity wrongDomain = operation with { DomainLease = rightLease };

        PlatformAuthorityResult<PlatformCompletionReceipt> observed = provider.ObserveCompletion(wrongDomain);

        Assert.False(observed.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain, observed.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void MalformedReceiptStateFailsClosed()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformOperationIdentity operation = provider.StageOperation(Bind(provider, 10, 1)).Value!;
        PlatformCompletionReceipt receipt = provider.ObserveCompletion(operation).Value!;
        PlatformCompletionReceipt malformed = receipt with { State = (PlatformCompletionState)999 };

        PlatformAuthorityResult validation = provider.ValidateCompletionReceipt(operation, malformed);

        Assert.False(validation.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Faulted, validation.Status);
        Assert.False(malformed.IsTerminal);
        Assert.False(PlatformCompletionContract.IsNonTerminal(malformed.State));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void StaleReceiptGenerationCannotValidateForOperation()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformOperationIdentity operation = provider.StageOperation(Bind(provider, 10, 1)).Value!;
        PlatformCompletionReceipt current = provider.ObserveCompletion(operation).Value!;
        PlatformCompletionReceipt stale = current with
        {
            Generation = new PlatformOperationGeneration(current.Generation.Value + 1)
        };

        PlatformAuthorityResult validation = provider.ValidateCompletionReceipt(operation, stale);

        Assert.False(validation.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Stale, validation.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void WrongDomainReceiptCannotValidateForOperation()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformProviderDomainLease leftLease = Bind(provider, 10, 1);
        PlatformProviderDomainLease rightLease = Bind(provider, 20, 2);
        PlatformOperationIdentity operation = provider.StageOperation(leftLease).Value!;
        PlatformCompletionReceipt current = provider.ObserveCompletion(operation).Value!;
        PlatformCompletionReceipt receipt = current with { DomainLease = rightLease };

        PlatformAuthorityResult validation = provider.ValidateCompletionReceipt(operation, receipt);

        Assert.False(validation.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain, validation.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void StaleReceiptCannotAuthorizeCurrentOperationState()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformOperationIdentity operation = provider.StageOperation(Bind(provider, 10, 1)).Value!;
        PlatformCompletionReceipt stagedReceipt = provider.ObserveCompletion(operation).Value!;
        PlatformCompletionReceipt pendingReceipt = provider.AdvanceOperation(
            operation,
            PlatformCompletionState.Pending).Value!;

        PlatformAuthorityResult staleValidation = provider.ValidateCompletionReceipt(
            operation,
            stagedReceipt);
        PlatformAuthorityResult currentValidation = provider.ValidateCompletionReceipt(
            operation,
            pendingReceipt);

        Assert.False(staleValidation.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Stale, staleValidation.Status);
        Assert.True(currentValidation.IsSuccess, currentValidation.Message);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void RevokedDomainLeaseInvalidatesCompletionObservation()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformProviderDomainLease domainLease = Bind(provider, 10, 1);
        PlatformOperationIdentity operation = provider.StageOperation(domainLease).Value!;

        PlatformAuthorityResult revoke = provider.RevokeDomain(domainLease);
        PlatformAuthorityResult<PlatformCompletionReceipt> observed = provider.ObserveCompletion(operation);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.False(observed.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Revoked, observed.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void CompletionProviderContractDoesNotAcceptLocalCapabilityAuthority()
    {
        Type[] parameterTypes = typeof(IPlatformCompletionProvider)
            .GetMethods()
            .SelectMany(static method => method.GetParameters())
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(CapabilityId), parameterTypes);
    }

    private static PlatformProviderDomainLease Bind(
        HostPlatformAuthorityProvider provider,
        ulong domainId,
        ulong processGeneration)
    {
        PlatformAuthorityResult<PlatformProviderDomainLease> result = provider.BindDomain(
            new PlatformDomainIdentity(
                new DomainId(domainId),
                new ProcessHandle(new ProcessId(domainId), processGeneration)));

        Assert.True(result.IsSuccess, result.Message);
        return result.Value!;
    }
}
