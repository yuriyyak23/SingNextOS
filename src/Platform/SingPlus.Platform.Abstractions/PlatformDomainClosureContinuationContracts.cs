namespace SingPlus.Platform;

/// <summary>Finish only retained original composed cleanup. Previously invoked
/// components are queried, never repeated. A remaining never-invoked component,
/// or fresh admission after exact no-effect, requires current original sources.
/// Success means every root lifetime is closed; partial closure is not no-effect.</summary>
public interface IPlatformOriginalDomainClosureContinuationProvider : IPlatformOriginalDomainClosureProvider
{
    PlatformAuthorityResult ContinueOriginalDomainClosure(PlatformProviderDomainLease lease);
}
