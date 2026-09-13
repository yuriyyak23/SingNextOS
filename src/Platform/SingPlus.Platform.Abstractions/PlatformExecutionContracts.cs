namespace SingPlus.Platform;

public enum PlatformDomainExecutionState
{
    Ready = 0,
    Running,
    Parked,
}

public enum PlatformDomainExecutionTransition
{
    Start = 0,
    Park,
    Resume,
}

public readonly record struct PlatformDomainExecutionTransitionResult(
    PlatformProviderDomainLease DomainLease,
    PlatformDomainExecutionTransition Transition,
    PlatformDomainExecutionState State);

public static class PlatformDomainExecutionContract
{
    public static PlatformDomainExecutionState ExpectedState(
        PlatformDomainExecutionTransition transition) =>
        transition switch
        {
            PlatformDomainExecutionTransition.Start => PlatformDomainExecutionState.Running,
            PlatformDomainExecutionTransition.Park => PlatformDomainExecutionState.Parked,
            PlatformDomainExecutionTransition.Resume => PlatformDomainExecutionState.Running,
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };

    public static PlatformAuthorityResult ValidateTransition(
        PlatformDomainExecutionTransition transition)
    {
        if (!Enum.IsDefined(transition))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The platform domain execution transition is undefined.");
        }

        return PlatformAuthorityResult.Ok();
    }

    public static PlatformAuthorityResult ValidateResult(
        PlatformProviderDomainLease expectedLease,
        PlatformDomainExecutionTransition expectedTransition,
        PlatformDomainExecutionTransitionResult result)
    {
        var transitionValidation = ValidateTransition(expectedTransition);
        if (!transitionValidation.IsSuccess) return transitionValidation;

        if (!Enum.IsDefined(result.Transition) || !Enum.IsDefined(result.State))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The platform provider returned an undefined execution transition result.");
        }

        if (result.DomainLease.LeaseId != expectedLease.LeaseId)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The platform execution result belongs to a different provider domain lease.");
        }

        if (result.DomainLease.Generation != expectedLease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The platform execution result carries a stale provider domain generation.");
        }

        if (result.DomainLease.Subject != expectedLease.Subject)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The platform execution result belongs to a different local subject.");
        }

        if (result.Transition != expectedTransition)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The platform provider acknowledged a different execution transition.");
        }

        var expectedState = ExpectedState(expectedTransition);
        if (result.State != expectedState)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The platform provider returned an unexpected execution state.");
        }

        return PlatformAuthorityResult.Ok();
    }
}

public interface IPlatformDomainExecutionProvider
{
    PlatformAuthorityResult<PlatformDomainExecutionTransitionResult> TransitionDomainExecution(
        PlatformProviderDomainLease domainLease,
        PlatformDomainExecutionTransition transition);
}
