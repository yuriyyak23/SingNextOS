using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal sealed class ServiceRegistry
{
    internal sealed class Record
    {
        public required ServiceEndpointDescriptor Descriptor { get; set; }
        public required ProcessHandle Provider { get; init; }
        public required IReadOnlyList<CapabilityRequirementV1> RequiredCapabilities { get; init; }
        public required ProtocolDefinitionV1 Protocol { get; init; }
        public ResponseProtocolDefinitionV1? ResponseProtocol { get; init; }
    }

    private readonly Dictionary<ServiceId, Record> _records = [];
    private readonly Dictionary<string, ServiceId> _names = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private ulong _nextId = 1;

    internal KernelResult<ServiceEndpointDescriptor> Register(
        ProcessHandle provider,
        string name,
        ServiceContractIdentity contract,
        ProtocolDefinitionV1 protocol,
        ResponseProtocolDefinitionV1? responseProtocol,
        IReadOnlyList<CapabilityRequirementV1> requiredCapabilities)
    {
        lock (_gate)
        {
        if (string.IsNullOrWhiteSpace(name) || contract.Name != protocol.ContractName || contract.Digest != protocol.ContractDigest)
            return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.ServiceContractMismatch, "Service identity and protocol contract do not match.");
        if (_names.ContainsKey(name)) return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.DuplicateIdentity, $"Service '{name}' is already registered.");

        var id = new ServiceId(_nextId++);
        var descriptor = new ServiceEndpointDescriptor(
            new ServiceIdentity(id, name), new ServiceGeneration(1), contract,
            $"sing-service/{id.Value}", ServiceAvailability.Accepting);
        _records.Add(id, new Record { Descriptor = descriptor, Provider = provider, RequiredCapabilities = requiredCapabilities.ToArray(), Protocol = protocol, ResponseProtocol = responseProtocol });
        _names.Add(name, id);
        return KernelResult<ServiceEndpointDescriptor>.Ok(descriptor);
        }
    }

    internal KernelResult<Record> Resolve(ServiceEndpointDescriptor descriptor)
    {
        lock (_gate)
        {
        if (!_records.TryGetValue(descriptor.Service.Id, out var record))
            return KernelResult<Record>.Fail(KernelError.ServiceNotFound, "Service endpoint was not found.");
        if (record.Descriptor.Service != descriptor.Service ||
            record.Descriptor.Generation != descriptor.Generation ||
            record.Descriptor.Contract != descriptor.Contract)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Service endpoint generation is stale.");
        if (record.Descriptor.Availability != ServiceAvailability.Accepting)
            return KernelResult<Record>.Fail(KernelError.ServiceUnavailable, "Service is not accepting sessions.");
        return KernelResult<Record>.Ok(record);
        }
    }

    internal KernelResult<ServiceEndpointDescriptor[]> ResolveByContract(ServiceContractIdentity contract)
    {
        lock (_gate)
            return KernelResult<ServiceEndpointDescriptor[]>.Ok(_records.Values
                .Where(x => x.Descriptor.Contract == contract && x.Descriptor.Availability == ServiceAvailability.Accepting)
                .Select(x => x.Descriptor).ToArray());
    }

    internal KernelResult<ServiceEndpointDescriptor> ResolveByServiceName(string name)
    {
        lock (_gate)
        {
            if (!_names.TryGetValue(name, out var id) || !_records.TryGetValue(id, out var record) || record.Descriptor.Availability != ServiceAvailability.Accepting)
                return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.ServiceNotFound, $"Service '{name}' was not found or is unavailable.");
            return KernelResult<ServiceEndpointDescriptor>.Ok(record.Descriptor);
        }
    }

    internal KernelResult SetAvailability(ServiceIdentity service, ServiceAvailability availability)
    {
        lock (_gate)
        {
        if (!_records.TryGetValue(service.Id, out var record) || record.Descriptor.Service != service)
            return KernelResult.Fail(KernelError.ServiceNotFound, "Service was not found.");
        record.Descriptor = record.Descriptor with { Availability = availability };
        return KernelResult.Ok();
        }
    }

    internal void RetireForProvider(ProcessHandle provider)
    {
        lock (_gate)
        {
            foreach (var record in _records.Values.Where(x => x.Provider == provider))
                record.Descriptor = record.Descriptor with { Generation = new ServiceGeneration(record.Descriptor.Generation.Value + 1), Availability = ServiceAvailability.Unavailable };
        }
    }

    internal void UnregisterForProvider(ProcessHandle provider)
    {
        lock (_gate)
        {
            foreach (var pair in _records.Where(x => x.Value.Provider == provider).ToArray())
            {
                _records.Remove(pair.Key);
                _names.Remove(pair.Value.Descriptor.Service.Name);
            }
        }
    }

    internal Record[] Snapshot()
    {
        lock (_gate) return _records.Values.ToArray();
    }
}
