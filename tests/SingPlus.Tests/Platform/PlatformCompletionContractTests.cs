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
        var provider = new HostPlatformAuthorityProvider();
        var domainLease = Bind(provider, 10, 1);

        var staged = provider.StageOperation(domainLease);
        var observed = provider.ObserveCompletion(staged.Value!);

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
        var provider = new HostPlatformAuthorityProvider();
        var domainLease = Bind(provider, 10, 1);

        var completedOperation = provider.StageOperation(domainLease).Value!;
        Assert.True(provider.AdvanceOperation(
            completedOperation,
            PlatformCompletionState.Pending).IsSuccess);
        var completed = provider.AdvanceOperation(
            completedOperation,
            PlatformCompletionState.Completed).Value!;

        Assert.False(completed.IsTerminal);
        Assert.False(completed.ProvesClosure);

        var closed = provider.AdvanceOperation(
            completedOperation,
            PlatformCompletionState.Closed).Value!;

        Assert.True(closed.IsTerminal);
        Assert.True(closed.ProvesClosure);

        var cancelledOperation = provider.StageOperation(domainLease).Value!;
        var cancelled = provider.AdvanceOperation(
            cancelledOperation,
            PlatformCompletionState.Cancelled).Value!;

        Assert.False(cancelled.IsTerminal);
        Assert.False(cancelled.ProvesClosure);

        var cancelledClosed = provider.AdvanceOperation(
            cancelledOperation,
            PlatformCompletionState.Closed).Value!;

        Assert.True(cancelledClosed.IsTerminal);
        Assert.True(cancelledClosed.ProvesClosure);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void FaultedIsTerminalButNeverProvesClosure()
    {
        var provider = new HostPlatformAuthorityProvider();
        var operation = provider.StageOperation(Bind(provider, 10, 1)).Value!;

        var faulted = provider.AdvanceOperation(
            operation,
            PlatformCompletionState.Faulted).Value!;
        var closeAfterFault = provider.AdvanceOperation(
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
        var provider = new HostPlatformAuthorityProvider();
        var operation = provider.StageOperation(Bind(provider, 10, 1)).Value!;
        var stale = operation with
        {
            Generation = new PlatformOperationGeneration(operation.Generation.Value + 1)
        };

        var observed = provider.ObserveCompletion(stale);

        Assert.False(observed.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Stale, observed.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void WrongDomainOperationIdentityIsRejected()
    {
        var provider = new HostPlatformAuthorityProvider();
        var leftLease = Bind(provider, 10, 1);
        var rightLease = Bind(provider, 20, 2);
        var operation = provider.StageOperation(leftLease).Value!;
        var wrongDomain = operation with { DomainLease = rightLease };

        var observed = provider.ObserveCompletion(wrongDomain);

        Assert.False(observed.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain, observed.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void MalformedReceiptStateFailsClosed()
    {
        var provider = new HostPlatformAuthorityProvider();
        var operation = provider.StageOperation(Bind(provider, 10, 1)).Value!;
        var receipt = provider.ObserveCompletion(operation).Value!;
        var malformed = receipt with { State = (PlatformCompletionState)999 };

        var validation = provider.ValidateCompletionReceipt(operation, malformed);

        Assert.False(validation.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Faulted, validation.Status);
        Assert.False(malformed.IsTerminal);
        Assert.False(PlatformCompletionContract.IsNonTerminal(malformed.State));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void WrongDomainReceiptCannotValidateForOperation()
    {
        var provider = new HostPlatformAuthorityProvider();
        var leftLease = Bind(provider, 10, 1);
        var rightLease = Bind(provider, 20, 2);
        var operation = provider.StageOperation(leftLease).Value!;
        var receipt = provider.ObserveCompletion(operation).Value! with
        {
            DomainLease = rightLease
        };

        var validation = provider.ValidateCompletionReceipt(operation, receipt);

        Assert.False(validation.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain, validation.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void StaleReceiptCannotAuthorizeCurrentOperationState()
    {
        var provider = new HostPlatformAuthorityProvider();
        var operation = provider.StageOperation(Bind(provider, 10, 1)).Value!;
        var stagedReceipt = provider.ObserveCompletion(operation).Value!;
        var pendingReceipt = provider.AdvanceOperation(
            operation,
            PlatformCompletionState.Pending).Value!;

        var staleValidation = provider.ValidateCompletionReceipt(
            operation,
            stagedReceipt);
        var currentValidation = provider.ValidateCompletionReceipt(
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
        var provider = new HostPlatformAuthorityProvider();
        var domainLease = Bind(provider, 10, 1);
        var operation = provider.StageOperation(domainLease).Value!;

        var revoke = provider.RevokeDomain(domainLease);
        var observed = provider.ObserveCompletion(operation);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.False(observed.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Revoked, observed.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void CompletionProviderContractDoesNotAcceptLocalCapabilityAuthority()
    {
        var parameterTypes = typeof(IPlatformCompletionProvider)
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
        var result = provider.BindDomain(
            new PlatformDomainIdentity(new DomainId(domainId), processGeneration));

        Assert.True(result.IsSuccess, result.Message);
        return result.Value!;
    }
}
