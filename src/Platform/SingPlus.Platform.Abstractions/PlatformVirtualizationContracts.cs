namespace SingPlus.Platform;

public static class PlatformVirtualizationContract { public const uint ContractVersion = 1; }
public readonly record struct PlatformProviderVirtualDomainLeaseId(ulong Value);
public readonly record struct PlatformProviderVirtualDomainLease(
    PlatformProviderVirtualDomainLeaseId LeaseId,
    PlatformProviderLeaseGeneration Generation,
    PlatformDomainIdentity Owner);
public readonly record struct PlatformVirtualDomainProfile(int VirtualProcessorCount, long MaximumGuestMemoryBytes);
public enum PlatformVirtualDomainTransition { Configure = 0, Start, Park, Resume, BeginDrain, Close }

public interface IPlatformVirtualizationProvider
{
    /// <summary>NotAccepted with no lease guarantees this invocation produced no effect.
    /// Other failures do not establish containment or closure.</summary>
    PlatformAuthorityResult<PlatformProviderVirtualDomainLease> CreateVirtualDomain(PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile);
    PlatformAuthorityResult TransitionVirtualDomain(PlatformProviderVirtualDomainLease lease, PlatformVirtualDomainTransition transition);
    PlatformAuthorityResult RevokeVirtualDomain(PlatformProviderVirtualDomainLease lease);
}
