using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Authority;

/// <summary>Canonical adapter-owned correlation; backend identity remains opaque.</summary>
internal sealed class AdapterAuthorityRecord<TNeutralLease, TExternalLease>
    where TNeutralLease : struct
    where TExternalLease : struct
{
    public required TNeutralLease NeutralLease { get; init; }
    public TExternalLease? ExternalLease { get; set; }
    public AdapterResourceLifecycle Lifecycle { get; set; } = AdapterResourceLifecycle.Opening;
    public AdapterOperationIdentity? Reservation { get; set; }
}
