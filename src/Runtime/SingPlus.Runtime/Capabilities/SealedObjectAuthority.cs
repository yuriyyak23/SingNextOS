using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum SealedObjectState { Active, Draining, Closed }

internal static class SealTypeRegistry
{
    internal static readonly Guid SocketObjectV1 = new("58c7c5d9-22d2-4b92-9414-52d8da8226ba");
    internal static readonly Guid FileObjectV1 = new("43cd73a9-a6fe-4d5d-9208-ae8df833bf47");
    internal static readonly Guid ProcessControlV1 = new("9219ef99-f502-4efe-8497-20bb626a145e");

    internal static bool TryTypeId<TSeal>(out Guid typeId) where TSeal : ISealedContractMarker
    {
        typeId = typeof(TSeal) == typeof(SocketObjectSeal) ? SocketObjectV1 :
            typeof(TSeal) == typeof(FileObjectSeal) ? FileObjectV1 :
            typeof(TSeal) == typeof(ProcessControlSeal) ? ProcessControlV1 : Guid.Empty;
        return typeId != Guid.Empty;
    }
}

internal sealed class SealedObjectAuthority
{
    private sealed class Record
    {
        public required Guid Token { get; init; }
        public required Guid SealTypeId { get; init; }
        public required ServiceIdentity Service { get; init; }
        public required ServiceGeneration ServiceGeneration { get; init; }
        public required ProcessHandle ServiceProcess { get; init; }
        public required ProcessHandle Owner { get; init; }
        public required EndpointSessionHandle Session { get; init; }
        public required ulong ObjectGeneration { get; init; }
        public required ulong ObjectKey { get; init; }
        public required CapabilityId Capability { get; init; }
        public SealedObjectState State { get; set; } = SealedObjectState.Active;
        public int ActivePins { get; set; }
    }

    private const int CollisionRetryLimit = 8;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Record> _records = [];
    private readonly AuthorityRealmId _realm;
    private readonly int _capacity;
    private readonly Func<Guid> _tokenFactory;

    internal SealedObjectAuthority(AuthorityRealmId realm, int capacity = 65_536,
        Func<Guid>? tokenFactory = null)
    {
        if (realm.Value == Guid.Empty) throw new ArgumentException("Authority realm is required.", nameof(realm));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _realm = realm;
        _capacity = capacity;
        _tokenFactory = tokenFactory ?? Guid.NewGuid;
    }

    internal KernelResult<SealedHandle<TSeal>> Seal<TSeal>(
        ServiceEndpointDescriptor service, ProcessHandle serviceProcess, ProcessHandle owner,
        EndpointSessionHandle session, ulong objectGeneration, ulong objectKey,
        CapabilityId capability)
        where TSeal : ISealedContractMarker
    {
        lock (_gate)
        {
            if (!SealTypeRegistry.TryTypeId<TSeal>(out var sealTypeId))
                return KernelResult<SealedHandle<TSeal>>.Fail(KernelError.InvalidMessage, "Unknown sealed contract marker.");
            if (_records.Count >= _capacity)
                return KernelResult<SealedHandle<TSeal>>.Fail(KernelError.CapacityExhausted, "Sealed-object table is exhausted.");
            if (objectGeneration == 0 || objectKey == 0 || capability.Value == 0)
                return KernelResult<SealedHandle<TSeal>>.Fail(KernelError.InvalidMessage, "Sealed-object identity is incomplete.");
            Guid token = default;
            for (var attempt = 0; attempt < CollisionRetryLimit; attempt++)
            {
                token = _tokenFactory();
                if (token != Guid.Empty && !_records.ContainsKey(token)) break;
                token = default;
            }
            if (token == Guid.Empty)
                return KernelResult<SealedHandle<TSeal>>.Fail(KernelError.CapacityExhausted, "Sealed token allocation failed closed.");
            var record = new Record
            {
                Token = token,
                SealTypeId = sealTypeId,
                Service = service.Service,
                ServiceGeneration = service.Generation,
                ServiceProcess = serviceProcess,
                Owner = owner,
                Session = session,
                ObjectGeneration = objectGeneration,
                ObjectKey = objectKey,
                Capability = capability,
            };
            _records.Add(token, record);
            return KernelResult<SealedHandle<TSeal>>.Ok(new(SealedHandleContract.Version, _realm, token));
        }
    }

    internal KernelResult<SealedObjectPin<TSeal>> AcquirePin<TSeal>(
        SealedHandle<TSeal> handle, ServiceEndpointDescriptor service,
        ProcessHandle serviceProcess, ProcessHandle owner, EndpointSessionHandle session,
        ulong objectGeneration, CapabilityId capability)
        where TSeal : ISealedContractMarker
    {
        lock (_gate)
        {
            if (!SealTypeRegistry.TryTypeId<TSeal>(out _))
                return KernelResult<SealedObjectPin<TSeal>>.Fail(KernelError.InvalidMessage, "Unknown sealed contract marker.");
            var resolved = Resolve(handle, service, serviceProcess, owner, session, objectGeneration, capability);
            if (!resolved.IsSuccess)
                return KernelResult<SealedObjectPin<TSeal>>.Fail(resolved.Error, resolved.Message!);
            checked { resolved.Value!.ActivePins++; }
            return KernelResult<SealedObjectPin<TSeal>>.Ok(new(this, handle, resolved.Value.ObjectKey,
                resolved.Value.ObjectGeneration, resolved.Value.Capability));
        }
    }

    internal KernelResult Revalidate<TSeal>(SealedObjectPin<TSeal> pin,
        ServiceEndpointDescriptor service, ProcessHandle serviceProcess)
        where TSeal : ISealedContractMarker
    {
        lock (_gate)
        {
            if (!SealTypeRegistry.TryTypeId<TSeal>(out var typeId))
                return KernelResult.Fail(KernelError.InvalidMessage, "Unknown sealed contract marker.");
            return pin.IsOwnedBy(this) &&
                   _records.TryGetValue(pin.Handle.OpaqueToken, out var record) &&
                   record.State == SealedObjectState.Active && record.ActivePins > 0 &&
                   record.Service == service.Service && record.ServiceGeneration == service.Generation &&
                   record.ServiceProcess == serviceProcess && record.SealTypeId == typeId
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.StaleHandle, "Sealed object pin is no longer current.");
        }
    }

    internal KernelResult BeginClose<TSeal>(SealedObjectPin<TSeal> pin)
        where TSeal : ISealedContractMarker
    {
        lock (_gate)
        {
            if (!SealTypeRegistry.TryTypeId<TSeal>(out var typeId))
                return KernelResult.Fail(KernelError.InvalidMessage, "Unknown sealed contract marker.");
            if (!pin.IsOwnedBy(this) ||
                !_records.TryGetValue(pin.Handle.OpaqueToken, out var record) ||
                record.SealTypeId != typeId || record.ActivePins == 0 ||
                record.State != SealedObjectState.Active)
                return KernelResult.Fail(KernelError.StaleHandle, "Sealed object is stale or already closed.");
            record.State = SealedObjectState.Draining;
            return KernelResult.Ok();
        }
    }

    internal KernelResult Revoke<TSeal>(SealedHandle<TSeal> handle) where TSeal : ISealedContractMarker
    {
        lock (_gate)
        {
            if (!SealTypeRegistry.TryTypeId<TSeal>(out var typeId))
                return KernelResult.Fail(KernelError.InvalidMessage, "Unknown sealed contract marker.");
            if (!_records.TryGetValue(handle.OpaqueToken, out var record) || handle.RealmId != _realm ||
                handle.Version != SealedHandleContract.Version || record.SealTypeId != typeId)
                return KernelResult.Fail(KernelError.StaleHandle, "Sealed object is stale or forged.");
            record.State = record.ActivePins == 0 ? SealedObjectState.Closed : SealedObjectState.Draining;
            return KernelResult.Ok();
        }
    }

    internal void Release<TSeal>(SealedObjectPin<TSeal> pin) where TSeal : ISealedContractMarker
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(pin.Handle.OpaqueToken, out var record) || record.ActivePins == 0) return;
            record.ActivePins--;
            if (record.ActivePins == 0 && record.State == SealedObjectState.Draining)
                record.State = SealedObjectState.Closed;
        }
    }

    internal SealedObjectState? InspectState<TSeal>(SealedHandle<TSeal> handle)
        where TSeal : ISealedContractMarker
    {
        lock (_gate) return _records.TryGetValue(handle.OpaqueToken, out var record) ? record.State : null;
    }

    private KernelResult<Record> Resolve<TSeal>(
        SealedHandle<TSeal> handle, ServiceEndpointDescriptor service,
        ProcessHandle serviceProcess, ProcessHandle owner, EndpointSessionHandle session,
        ulong objectGeneration, CapabilityId capability)
        where TSeal : ISealedContractMarker
    {
        if (handle.Version != SealedHandleContract.Version)
            return KernelResult<Record>.Fail(KernelError.InvalidMessage, "Sealed handle version is unsupported.");
        if (handle.RealmId != _realm)
            return KernelResult<Record>.Fail(KernelError.WrongAuthorityRealm, "Sealed handle belongs to another runtime realm.");
        if (handle.OpaqueToken == Guid.Empty || !_records.TryGetValue(handle.OpaqueToken, out var record))
            return KernelResult<Record>.Fail(KernelError.ForgedCapability, "Sealed handle token is invalid.");
        if (!SealTypeRegistry.TryTypeId<TSeal>(out var typeId) || record.SealTypeId != typeId)
            return KernelResult<Record>.Fail(KernelError.StaleHandle, "Sealed handle type does not match the object.");
        if (record.Service != service.Service || record.ServiceGeneration != service.Generation || record.ServiceProcess != serviceProcess)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Sealed object service incarnation is stale.");
        if (record.Owner != owner)
            return KernelResult<Record>.Fail(KernelError.WrongCapabilitySubject, "Sealed object belongs to another caller.");
        if (record.Session != session)
            return KernelResult<Record>.Fail(KernelError.WrongSessionOwner, "Sealed object belongs to another session.");
        if (record.ObjectGeneration != objectGeneration)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Sealed object generation is stale.");
        if (record.Capability != capability)
            return KernelResult<Record>.Fail(KernelError.WrongCapabilityResource, "Capability identity is not bound to this sealed object.");
        if (record.State != SealedObjectState.Active)
            return KernelResult<Record>.Fail(KernelError.StaleHandle, "Sealed object is closed.");
        return KernelResult<Record>.Ok(record);
    }
}

internal sealed class SealedObjectPin<TSeal> : IDisposable where TSeal : ISealedContractMarker
{
    private SealedObjectAuthority? _owner;
    internal SealedObjectPin(SealedObjectAuthority owner, SealedHandle<TSeal> handle,
        ulong objectKey, ulong objectGeneration, CapabilityId capability) =>
        (_owner, Handle, ObjectKey, ObjectGeneration, Capability) =
        (owner, handle, objectKey, objectGeneration, capability);
    internal SealedHandle<TSeal> Handle { get; }
    internal ulong ObjectKey { get; }
    internal ulong ObjectGeneration { get; }
    internal CapabilityId Capability { get; }
    internal bool IsOwnedBy(SealedObjectAuthority owner) =>
        ReferenceEquals(Volatile.Read(ref _owner), owner);
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(this);
}
