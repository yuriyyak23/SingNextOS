using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed class CapabilityAuthority
{
    private sealed class CapabilityRecord(CapabilityDescriptorV1 descriptor)
    {
        public CapabilityDescriptorV1 Descriptor { get; } = descriptor;
        public bool Revoked { get; set; }
    }

    private readonly Dictionary<CapabilityId, CapabilityRecord> _records = [];
    private readonly Dictionary<DomainId, ulong> _domainEpochs = [];
    private ulong _nextId = 1;

    internal CapabilityDescriptorV1 Mint(
        DomainId issuerDomainId,
        DomainId subjectDomainId,
        ResourceKind resourceKind,
        string resourceId,
        CapabilityRights rights,
        ulong generation)
    {
        if (rights == CapabilityRights.None) throw new ArgumentOutOfRangeException(nameof(rights));
        if (string.IsNullOrWhiteSpace(resourceId)) throw new ArgumentException("Resource id is required.", nameof(resourceId));
        var id = new CapabilityId(_nextId++);
        var epoch = CurrentEpoch(subjectDomainId);
        var descriptor = new CapabilityDescriptorV1(id, issuerDomainId, subjectDomainId, resourceKind, resourceId, rights, generation, epoch);
        _records.Add(id, new CapabilityRecord(descriptor));
        return descriptor;
    }

    internal KernelResult<CapabilityDescriptorV1> Delegate(
        CapabilityId sourceId,
        DomainId delegatorDomain,
        DomainId targetDomain,
        CapabilityRights rights,
        ulong targetGeneration)
    {
        if (!_records.TryGetValue(sourceId, out var source))
            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapabilityNotFound, $"Capability {sourceId} does not exist.");
        var validation = ValidateRecord(source, delegatorDomain, source.Descriptor.Generation, CapabilityRights.Delegate);
        if (!validation.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(validation.Error, validation.Message!);
        if ((source.Descriptor.Rights & rights) != rights || rights == CapabilityRights.None)
            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DelegationDenied, "Delegated rights must be a non-empty subset of the source capability.");

        var delegated = Mint(delegatorDomain, targetDomain, source.Descriptor.ResourceKind, source.Descriptor.ResourceId, rights, targetGeneration);
        return KernelResult<CapabilityDescriptorV1>.Ok(delegated);
    }

    public KernelResult<CapabilityDescriptorV1> Validate(CapabilityId id, DomainId subject, ulong generation, CapabilityRights requiredRights)
    {
        if (!_records.TryGetValue(id, out var record))
            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapabilityNotFound, $"Capability {id} does not exist.");
        var result = ValidateRecord(record, subject, generation, requiredRights);
        return result.IsSuccess ? KernelResult<CapabilityDescriptorV1>.Ok(record.Descriptor) : KernelResult<CapabilityDescriptorV1>.Fail(result.Error, result.Message!);
    }

    public KernelResult Revoke(CapabilityId id)
    {
        if (!_records.TryGetValue(id, out var record)) return KernelResult.Fail(KernelError.CapabilityNotFound, $"Capability {id} does not exist.");
        record.Revoked = true;
        return KernelResult.Ok();
    }

    public void RevokeAllForDomain(DomainId domainId)
    {
        _domainEpochs[domainId] = CurrentEpoch(domainId) + 1;
        foreach (var record in _records.Values)
        {
            if (record.Descriptor.SubjectDomainId == domainId) record.Revoked = true;
        }
    }

    public IReadOnlyList<CapabilityDescriptorV1> SnapshotForDomain(DomainId domainId) =>
        _records.Values.Where(r => !r.Revoked && r.Descriptor.SubjectDomainId == domainId).Select(static r => r.Descriptor).OrderBy(static d => d.CapabilityId.Value).ToArray();

    private KernelResult ValidateRecord(CapabilityRecord record, DomainId subject, ulong generation, CapabilityRights requiredRights)
    {
        var descriptor = record.Descriptor;
        if (descriptor.SubjectDomainId != subject) return KernelResult.Fail(KernelError.WrongCapabilitySubject, "Capability subject does not match the caller domain.");
        if (descriptor.Generation != generation) return KernelResult.Fail(KernelError.StaleGeneration, "Capability generation is stale.");
        if (record.Revoked || descriptor.RevocationEpoch != CurrentEpoch(subject)) return KernelResult.Fail(KernelError.CapabilityRevoked, "Capability has been revoked.");
        if ((descriptor.Rights & requiredRights) != requiredRights) return KernelResult.Fail(KernelError.InsufficientRights, "Capability does not provide the required rights.");
        return KernelResult.Ok();
    }

    private ulong CurrentEpoch(DomainId domainId) => _domainEpochs.TryGetValue(domainId, out var epoch) ? epoch : 0;
}
