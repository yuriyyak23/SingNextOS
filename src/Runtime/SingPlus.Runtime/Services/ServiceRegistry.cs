using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal readonly record struct ServiceGenerationReference(
    ServiceId ServiceId,
    ServiceGeneration Generation,
    ProcessHandle Provider);

internal readonly record struct ServiceReplacementLineage(
    ServiceGenerationReference Previous,
    ServiceGenerationReference Replacement);

internal sealed class ServiceRegistry
{
    internal sealed class Record
    {
        public required ServiceEndpointDescriptor Descriptor { get; set; }
        public required ProcessHandle Provider { get; set; }
        public required IReadOnlyList<CapabilityRequirementV1> RequiredCapabilities { get; set; }
        public required ProtocolDefinitionV1 Protocol { get; set; }
        public ResponseProtocolDefinitionV1? ResponseProtocol { get; set; }
    }

    private readonly Dictionary<ServiceId, Record> _records = [];
    // The same service owner records survive routing-index loss.
    private readonly Dictionary<ServiceId, Record> _ownedRecords = [];
    private readonly Dictionary<string, ServiceId> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<ServiceId, ServiceGenerationReference> _pendingReplacement = [];
    private readonly List<ServiceReplacementLineage> _replacementLineage = [];
    private readonly object _gate = new();
    private ulong _nextId = 1;

    internal KernelResult<ServiceEndpointDescriptor> Register(
        ProcessHandle provider,
        string name,
        ServiceContractIdentity contract,
        ProtocolDefinitionV1 protocol,
        ResponseProtocolDefinitionV1? responseProtocol,
        IReadOnlyList<CapabilityRequirementV1> requiredCapabilities,
        Func<Func<KernelResult<ServiceEndpointDescriptor>>, KernelResult<ServiceEndpointDescriptor>>? commitConsumer = null)
    {
        var capturedRequirements = requiredCapabilities.ToArray();
        lock (_gate)
        {
        KernelResult<ServiceEndpointDescriptor> Commit()
        {
        if (string.IsNullOrWhiteSpace(name) || contract.Name != protocol.ContractName || contract.Digest != protocol.ContractDigest)
            return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.ServiceContractMismatch, "Service identity and protocol contract do not match.");
        var originalName = _ownedRecords.Values.FirstOrDefault(record =>
            string.Equals(record.Descriptor.Service.Name, name, StringComparison.Ordinal));
        if (originalName is not null &&
            (!_names.TryGetValue(name, out var originalId) || originalId != originalName.Descriptor.Service.Id))
            return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.PlatformFaulted,
                "Original service name routing was lost before registration.");
        if (_names.TryGetValue(name, out var retiredId))
        {
            if (!_records.TryGetValue(retiredId, out var retired) || !OwnsOriginalRecord(retired) ||
                !ReferenceEquals(retired, originalName))
                return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.PlatformFaulted, "Original service owner continuity was lost before replacement.");
            if (retired.Descriptor.Availability != ServiceAvailability.Unavailable)
                return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.DuplicateIdentity, $"Service '{name}' is already registered.");

            if (_pendingReplacement.ContainsKey(retiredId))
                _replacementLineage.EnsureCapacity(_replacementLineage.Count + 1);
            var replacement = retired.Descriptor with
            {
                Contract = contract,
                Availability = ServiceAvailability.Accepting,
            };
            retired.Descriptor = replacement;
            retired.Provider = provider;
            retired.RequiredCapabilities = capturedRequirements;
            retired.Protocol = protocol;
            retired.ResponseProtocol = responseProtocol;
            if (_pendingReplacement.Remove(retiredId, out var previous))
                _replacementLineage.Add(new(previous, new(retiredId, replacement.Generation, provider)));
            return KernelResult<ServiceEndpointDescriptor>.Ok(replacement);
        }

        if (_nextId == 0)
            return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.CapacityExhausted, "Service identity space is exhausted.");
        _ownedRecords.EnsureCapacity(_ownedRecords.Count + 1);
        _records.EnsureCapacity(_records.Count + 1);
        _names.EnsureCapacity(_names.Count + 1);
        var id = new ServiceId(_nextId);
        _nextId = _nextId == ulong.MaxValue ? 0 : _nextId + 1;
        var descriptor = new ServiceEndpointDescriptor(
            new ServiceIdentity(id, name), new ServiceGeneration(1), contract,
            $"sing-service/{id.Value}", ServiceAvailability.Accepting);
        var record = new Record { Descriptor = descriptor, Provider = provider, RequiredCapabilities = capturedRequirements, Protocol = protocol, ResponseProtocol = responseProtocol };
        _ownedRecords.Add(id, record);
        _records.Add(id, record);
        _names.Add(name, id);
        return KernelResult<ServiceEndpointDescriptor>.Ok(descriptor);
        }
        return commitConsumer is null ? Commit() : commitConsumer(Commit);
        }
    }

    internal KernelResult<Record> Resolve(ServiceEndpointDescriptor descriptor)
    {
        lock (_gate)
        {
        if (!_records.TryGetValue(descriptor.Service.Id, out var record))
            return KernelResult<Record>.Fail(KernelError.ServiceNotFound, "Service endpoint was not found.");
        if (!OwnsOriginalRecord(record))
            return KernelResult<Record>.Fail(KernelError.PlatformFaulted, "Original service owner continuity was lost.");
        if (record.Descriptor.Service != descriptor.Service ||
            OperabilityGeneration.Compare(record.Descriptor.Generation.Value, descriptor.Generation.Value) != GenerationMatch.Exact ||
            record.Descriptor.Contract != descriptor.Contract)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Service endpoint generation is stale.");
        if (record.Descriptor.Availability != ServiceAvailability.Accepting)
            return KernelResult<Record>.Fail(KernelError.ServiceUnavailable, "Service is not accepting sessions.");
        return KernelResult<Record>.Ok(record);
        }
    }

    internal KernelResult CommitProviderReadiness(ProcessHandle provider, IReadOnlyList<ServiceInstanceHandle> expected, Func<KernelResult> commit)
    {
        if (expected.Any(instance => instance.Process != provider))
            return KernelResult.Fail(KernelError.StaleGeneration, "Provided service process differs from the expected provider.");
        return CommitDependencyReadiness(expected, commit);
    }

    internal KernelResult CommitDependencyReadiness(IReadOnlyList<ServiceInstanceHandle> expected, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            foreach (var instance in expected)
            {
                if (!_records.TryGetValue(instance.ServiceId, out var record) ||
                    !OwnsOriginalRecord(record) || record.Provider != instance.Process || record.Descriptor.Generation != instance.Generation)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Exact provided service changed before readiness.");
                if (record.Descriptor.Availability != ServiceAvailability.Accepting)
                    return KernelResult.Fail(KernelError.ServiceUnavailable, "Provided service is not accepting at readiness publication.");
            }
            return commit();
        }
    }

    internal KernelResult<T> CommitSessionAdmission<T>(ServiceEndpointDescriptor descriptor, Func<Record, KernelResult<T>> commit)
    {
        lock (_gate)
        {
            var exact = Resolve(descriptor);
            // Trusted local channel/session commit; no provider or lifecycle callback.
            return exact.IsSuccess ? commit(exact.Value!) : KernelResult<T>.Fail(exact.Error, exact.Message!);
        }
    }

    internal KernelResult<ServiceEndpointDescriptor[]> ResolveByContract(ServiceContractIdentity contract)
    {
        lock (_gate)
            return KernelResult<ServiceEndpointDescriptor[]>.Ok(_records.Values
                .Where(x => OwnsOriginalRecord(x) && x.Descriptor.Contract == contract && x.Descriptor.Availability == ServiceAvailability.Accepting)
                .Select(x => x.Descriptor).ToArray());
    }

    internal KernelResult<ServiceEndpointDescriptor> ResolveByServiceName(string name)
    {
        lock (_gate)
        {
            if (!_names.TryGetValue(name, out var id) || !_records.TryGetValue(id, out var record) || !OwnsOriginalRecord(record) || record.Descriptor.Service.Name != name || record.Descriptor.Availability != ServiceAvailability.Accepting)
                return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.ServiceNotFound, $"Service '{name}' was not found or is unavailable.");
            return KernelResult<ServiceEndpointDescriptor>.Ok(record.Descriptor);
        }
    }

    internal KernelResult SetAvailability(ServiceIdentity service, ServiceAvailability availability)
    {
        lock (_gate)
        {
        if (!_records.TryGetValue(service.Id, out var record) || !OwnsOriginalRecord(record) || record.Descriptor.Service != service)
            return KernelResult.Fail(KernelError.ServiceNotFound, "Service was not found.");
        record.Descriptor = record.Descriptor with { Availability = availability };
        return KernelResult.Ok();
        }
    }

    internal void RetireForProvider(ProcessHandle provider)
    {
        lock (_gate)
        {
            foreach (var record in _ownedRecords.Values.Where(x => x.Provider == provider && x.Descriptor.Availability != ServiceAvailability.Unavailable))
            {
                _pendingReplacement[record.Descriptor.Service.Id] = new(
                    record.Descriptor.Service.Id,
                    record.Descriptor.Generation,
                    provider);
                record.Descriptor = record.Descriptor with { Generation = new ServiceGeneration(record.Descriptor.Generation.Value + 1), Availability = ServiceAvailability.Unavailable };
            }
        }
    }

    internal void UnregisterForProvider(ProcessHandle provider)
    {
        lock (_gate)
        {
            foreach (var pair in _ownedRecords.Where(x => x.Value.Provider == provider).ToArray())
            {
                _ownedRecords.Remove(pair.Key);
                if (_records.TryGetValue(pair.Key, out var routed) && ReferenceEquals(routed, pair.Value))
                    _records.Remove(pair.Key);
                if (_names.TryGetValue(pair.Value.Descriptor.Service.Name, out var named) && named == pair.Key)
                    _names.Remove(pair.Value.Descriptor.Service.Name);
            }
        }
    }

    internal Record[] Snapshot()
    {
        lock (_gate) return _ownedRecords.Values.ToArray();
    }

    internal KernelResult<ServiceEndpointDescriptor> ResolveForProvider(
        ProcessHandle provider, string contractName)
    {
        lock (_gate)
        {
            var matches = _records.Values.Where(record => OwnsOriginalRecord(record) && record.Provider == provider &&
                record.Protocol.ContractName == contractName &&
                record.Descriptor.Availability == ServiceAvailability.Accepting).ToArray();
            return matches.Length == 1
                ? KernelResult<ServiceEndpointDescriptor>.Ok(matches[0].Descriptor)
                : KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.ServiceNotFound,
                    "Exact active service incarnation for the provider and contract was not found.");
        }
    }

    private bool OwnsOriginalRecord(Record record) =>
        _ownedRecords.TryGetValue(record.Descriptor.Service.Id, out var original) && ReferenceEquals(original, record);

    internal ServiceReplacementLineage[] ReplacementLineageSnapshot()
    {
        lock (_gate) return _replacementLineage.ToArray();
    }
}
