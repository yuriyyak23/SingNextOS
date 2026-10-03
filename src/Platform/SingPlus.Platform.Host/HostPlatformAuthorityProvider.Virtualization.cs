using SingPlus.Platform;

namespace SingPlus.Platform.Host;

public sealed partial class HostPlatformAuthorityProvider
{
    private sealed class VirtualDomainRecord(PlatformProviderVirtualDomainLease lease)
    {
        public PlatformProviderVirtualDomainLease Lease { get; } = lease;
        public PlatformVirtualDomainTransition? LastTransition { get; set; }
    }

    private readonly object _virtualDomainGate = new();
    private readonly Dictionary<PlatformProviderVirtualDomainLeaseId, VirtualDomainRecord> _virtualDomains = [];
    private ulong _nextVirtualDomainId = 1;
    public int CreateVirtualDomainCallCount { get; private set; }
    public int RevokeVirtualDomainCallCount { get; private set; }

    public PlatformAuthorityResult<PlatformProviderVirtualDomainLease> CreateVirtualDomain(PlatformDomainIdentity owner, PlatformVirtualDomainProfile profile)
    {
        lock (_virtualDomainGate)
        {
        CreateVirtualDomainCallCount++;
        if (!PlatformDomainContract.ValidateSubject(owner).IsSuccess || profile.VirtualProcessorCount <= 0 || profile.MaximumGuestMemoryBytes <= 0)
            return PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Fail(PlatformAuthorityStatus.Denied, "Virtual-domain owner and profile must be bounded and materialized.");
        if (_nextVirtualDomainId == ulong.MaxValue) return PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Fail(PlatformAuthorityStatus.Denied, "Model-domain identity space is exhausted.");
        var lease = new PlatformProviderVirtualDomainLease(new PlatformProviderVirtualDomainLeaseId(_nextVirtualDomainId++), new PlatformProviderLeaseGeneration(1), owner);
        _virtualDomains.Add(lease.LeaseId, new VirtualDomainRecord(lease));
        return PlatformAuthorityResult<PlatformProviderVirtualDomainLease>.Ok(lease);
        }
    }

    public PlatformAuthorityResult TransitionVirtualDomain(PlatformProviderVirtualDomainLease lease, PlatformVirtualDomainTransition transition)
    {
        lock (_virtualDomainGate)
        {
        if (!_virtualDomains.TryGetValue(lease.LeaseId, out var record) || record.Lease != lease)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Virtual-domain lease is stale.");
        record.LastTransition = transition;
        return PlatformAuthorityResult.Ok();
        }
    }

    public PlatformAuthorityResult RevokeVirtualDomain(PlatformProviderVirtualDomainLease lease)
    {
        lock (_virtualDomainGate)
        {
        RevokeVirtualDomainCallCount++;
        if (!_virtualDomains.TryGetValue(lease.LeaseId, out var record) || record.Lease != lease)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Virtual-domain lease is stale.");
        _virtualDomains.Remove(lease.LeaseId);
        return PlatformAuthorityResult.Ok();
        }
    }
}
