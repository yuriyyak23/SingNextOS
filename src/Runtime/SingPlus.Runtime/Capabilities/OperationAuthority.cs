using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum EffectRevocationPolicy
{
    AdmissionOnly = 0,
    CancelIfPossible = 1,
    PublicationRevocable = 2,
    GrandfatherAdmitted = 3,
}

internal readonly record struct OperationAuthorityLeaseId(ulong Value);

internal sealed class OperationAuthorityLease : IDisposable
{
    private CapabilityAuthority? _owner;

    internal OperationAuthorityLease(CapabilityAuthority owner, OperationAuthorityLeaseId id,
        CapabilityId capability, DomainId subject, ulong subjectGeneration,
        ExactResourceConstraint resource, CapabilityOperation operation,
        EndpointSessionHandle? session, EffectRevocationPolicy revocationPolicy)
    {
        _owner = owner;
        Id = id;
        Capability = capability;
        Subject = subject;
        SubjectGeneration = subjectGeneration;
        Resource = resource;
        Operation = operation;
        Session = session;
        RevocationPolicy = revocationPolicy;
    }

    internal OperationAuthorityLeaseId Id { get; }
    internal CapabilityId Capability { get; }
    internal DomainId Subject { get; }
    internal ulong SubjectGeneration { get; }
    internal ExactResourceConstraint Resource { get; }
    internal CapabilityOperation Operation { get; }
    internal EndpointSessionHandle? Session { get; }
    internal EffectRevocationPolicy RevocationPolicy { get; }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseOperationAuthority(Id);
}

internal readonly record struct EffectAdmissionAttemptId(ulong Value);

internal sealed class EffectAdmissionLease : IDisposable
{
    private OperationAuthorityLease? _capability;
    private EndpointSessionPin? _session;

    internal EffectAdmissionLease(EffectAdmissionAttemptId attempt, OperationAuthorityLease capability,
        EndpointSessionPin session)
    {
        Attempt = attempt;
        _capability = capability;
        _session = session;
    }

    internal EffectAdmissionAttemptId Attempt { get; }
    internal OperationAuthorityLease Capability => _capability ?? throw new ObjectDisposedException(nameof(EffectAdmissionLease));
    internal EndpointSessionPin Session => _session ?? throw new ObjectDisposedException(nameof(EffectAdmissionLease));

    public void Dispose()
    {
        // Reverse acquisition order: capability lease, then session pin.
        Interlocked.Exchange(ref _capability, null)?.Dispose();
        Interlocked.Exchange(ref _session, null)?.Dispose();
    }
}
