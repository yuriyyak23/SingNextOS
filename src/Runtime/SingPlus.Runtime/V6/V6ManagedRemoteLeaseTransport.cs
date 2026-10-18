using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum V6RemoteLeaseCommandKind : byte
{
    Submit = 1,
    Fence,
    EffectClosure,
    Publish,
    Reclaim,
    ProviderLoss,
}

internal readonly record struct V6RemoteLeaseCommand(
    ushort Version,
    Guid RequestId,
    V6RemoteLeaseCommandKind Kind,
    RemoteAuthorityLeaseV1 Lease,
    RemoteLeaseRightsV1 RequestedRights = RemoteLeaseRightsV1.None,
    ulong Allocation = 0,
    RemoteEffectClosureV1? Closure = null)
{
    internal const ushort CurrentVersion = 1;
}

internal sealed record V6RemoteLeaseTransportReceipt(
    Guid RequestId,
    V6RemoteLeaseCommandKind Kind,
    V6RemoteDelegatedResourcePilotState State,
    uint SubmitCount,
    uint PublishCount,
    bool ProviderLost,
    RemoteResourceEscrowV1 Escrow,
    string EvidenceDigest)
{
    internal bool AuthorizesRemoteExecution => false;
    internal bool AuthorizesPublication => false;
    internal bool AuthorizesReclaim => false;
}

internal static class V6RemoteLeaseWireCodec
{
    internal const int MaximumCommandBytes = 2048;
    internal const int MaximumResponseBytes = 2048;

    internal static byte[] Encode(V6RemoteLeaseCommand command)
    {
        Validate(command);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(command.Version);
        writer.Write(command.RequestId.ToByteArray());
        writer.Write((byte)command.Kind);
        Write(writer, command.Lease);
        writer.Write((byte)command.RequestedRights);
        writer.Write(command.Allocation);
        writer.Write(command.Closure.HasValue);
        if (command.Closure is { } closure) Write(writer, closure);
        writer.Flush();
        if (stream.Length > MaximumCommandBytes)
            throw new ArgumentException("Remote lease command exceeds the bounded wire envelope.");
        return stream.ToArray();
    }

    internal static V6RemoteLeaseCommand DecodeCommand(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > MaximumCommandBytes)
            throw new ArgumentException("Remote lease command size is invalid.");
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            var command = new V6RemoteLeaseCommand(reader.ReadUInt16(),
                new Guid(ReadExact(reader, 16)), (V6RemoteLeaseCommandKind)reader.ReadByte(),
                ReadLease(reader), (RemoteLeaseRightsV1)reader.ReadByte(), reader.ReadUInt64(),
                reader.ReadBoolean() ? ReadClosure(reader) : null);
            if (stream.Position != stream.Length)
                throw new ArgumentException("Remote lease command contains trailing bytes.");
            Validate(command);
            return command;
        }
        catch (EndOfStreamException exception)
        { throw new ArgumentException("Remote lease command is truncated.", exception); }
    }

    internal static byte[] EncodeResponse(V6RemoteLeaseCommand command,
        KernelResult<V6RemoteDelegatedResourcePilotSnapshot> result, string commandDigest)
    {
        var snapshot = result.Value;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(V6RemoteLeaseCommand.CurrentVersion);
        writer.Write(command.RequestId.ToByteArray());
        writer.Write((byte)command.Kind);
        writer.Write(result.IsSuccess);
        writer.Write((int)result.Error);
        WriteBounded(writer, result.Message ?? string.Empty, 512);
        writer.Write((byte)(snapshot?.State ?? 0));
        writer.Write(snapshot?.SubmitCount ?? 0);
        writer.Write(snapshot?.PublishCount ?? 0);
        writer.Write(snapshot?.ProviderLost ?? false);
        var escrow = snapshot?.Escrow ?? default;
        writer.Write(escrow.Version);
        writer.Write(escrow.ParentAllocation);
        writer.Write(escrow.DelegatedAllocation);
        writer.Write(escrow.ConsumedAllocation);
        writer.Write(escrow.ReturnedAllocation);
        writer.Flush();
        var evidence = Evidence(commandDigest, stream.ToArray());
        writer.Write(evidence);
        writer.Flush();
        if (stream.Length > MaximumResponseBytes)
            throw new ArgumentException("Remote lease response exceeds the bounded wire envelope.");
        return stream.ToArray();
    }

    internal static KernelResult<V6RemoteLeaseTransportReceipt> DecodeResponse(
        ReadOnlyMemory<byte> payload, V6RemoteLeaseCommand expectedCommand, string commandDigest)
    {
        if (payload.IsEmpty || payload.Length > MaximumResponseBytes)
            return KernelResult<V6RemoteLeaseTransportReceipt>.Fail(KernelError.InvalidMessage,
                "Remote lease response size is invalid.");
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt16() != V6RemoteLeaseCommand.CurrentVersion)
                throw new NotSupportedException("Remote lease response version is unsupported.");
            var request = new Guid(ReadExact(reader, 16));
            var kind = (V6RemoteLeaseCommandKind)reader.ReadByte();
            var success = reader.ReadBoolean();
            var error = (KernelError)reader.ReadInt32();
            var message = ReadBounded(reader, 512);
            var state = (V6RemoteDelegatedResourcePilotState)reader.ReadByte();
            var submitCount = reader.ReadUInt32();
            var publishCount = reader.ReadUInt32();
            var providerLost = reader.ReadBoolean();
            var escrow = new RemoteResourceEscrowV1(reader.ReadUInt16(), reader.ReadUInt64(),
                reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
            var evidenceOffset = checked((int)stream.Position);
            var evidence = reader.ReadString();
            if (stream.Position != stream.Length || !Enum.IsDefined(kind) || !Enum.IsDefined(error) ||
                evidence.Length != 64 || evidence.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException("Remote lease response is malformed.");
            if (request != expectedCommand.RequestId || kind != expectedCommand.Kind ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(evidence),
                    Convert.FromHexString(Evidence(commandDigest, payload.Span[..evidenceOffset]))))
                throw new ArgumentException("Remote lease response does not match the exact request or wire evidence.");
            if (success)
            {
                if (!Enum.IsDefined(state))
                    throw new ArgumentException("Remote lease response state is malformed.");
                escrow.Validate();
                return KernelResult<V6RemoteLeaseTransportReceipt>.Ok(new(request, kind, state,
                    submitCount, publishCount, providerLost, escrow, evidence));
            }
            return KernelResult<V6RemoteLeaseTransportReceipt>.Fail(error,
                string.IsNullOrEmpty(message) ? "Remote lease owner rejected the command." : message);
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or
                                           IOException or NotSupportedException)
        {
            return KernelResult<V6RemoteLeaseTransportReceipt>.Fail(KernelError.InvalidMessage,
                exception.Message);
        }
    }

    internal static string Digest(ReadOnlySpan<byte> payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));

    private static void Validate(V6RemoteLeaseCommand command)
    {
        if (command.Version != V6RemoteLeaseCommand.CurrentVersion || command.RequestId == Guid.Empty ||
            !Enum.IsDefined(command.Kind))
            throw new NotSupportedException("Remote lease command version, request, or kind is unsupported.");
        command.Lease.Validate();
        var submit = command.Kind == V6RemoteLeaseCommandKind.Submit;
        var closure = command.Kind is V6RemoteLeaseCommandKind.EffectClosure or
            V6RemoteLeaseCommandKind.Reclaim;
        if (submit != (command.RequestedRights != RemoteLeaseRightsV1.None && command.Allocation != 0) ||
            closure != command.Closure.HasValue ||
            !submit && (command.RequestedRights != RemoteLeaseRightsV1.None || command.Allocation != 0))
            throw new ArgumentException("Remote lease command payload does not match its operation kind.");
    }

    private static void Write(BinaryWriter writer, RemoteAuthorityLeaseV1 lease)
    {
        writer.Write(lease.Version); writer.Write(lease.LeaseId.ToByteArray());
        writer.Write(lease.OwnerHostIdentity); writer.Write(lease.OwnerIncarnation);
        writer.Write(lease.RemoteHostIdentity); writer.Write(lease.RemoteIncarnation);
        writer.Write(lease.ResourceCorrelationDigest); writer.Write((byte)lease.Rights);
        writer.Write(lease.OwnerEpoch); writer.Write(lease.LeaseGeneration);
        writer.Write(lease.IssuedAtOwnerSequence); writer.Write(lease.NotAfterOwnerSequence);
    }

    private static RemoteAuthorityLeaseV1 ReadLease(BinaryReader reader) => new(
        reader.ReadUInt16(), new Guid(ReadExact(reader, 16)), ReadBounded(reader, 128),
        reader.ReadUInt64(), ReadBounded(reader, 128), reader.ReadUInt64(),
        ReadBounded(reader, 64), (RemoteLeaseRightsV1)reader.ReadByte(), reader.ReadUInt64(),
        reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());

    private static void Write(BinaryWriter writer, RemoteEffectClosureV1 closure)
    {
        writer.Write(closure.Version); writer.Write(closure.LeaseId.ToByteArray());
        writer.Write(closure.OwnerEpoch); writer.Write(closure.LeaseGeneration);
        writer.Write(closure.ProviderGeneration); writer.Write(closure.ClosureSequence);
        writer.Write(closure.Fenced); writer.Write(closure.EffectClosed);
    }

    private static RemoteEffectClosureV1 ReadClosure(BinaryReader reader) => new(
        reader.ReadUInt16(), new Guid(ReadExact(reader, 16)), reader.ReadUInt64(),
        reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(),
        reader.ReadBoolean(), reader.ReadBoolean());

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var value = reader.ReadBytes(count);
        return value.Length == count ? value : throw new EndOfStreamException();
    }

    private static string ReadBounded(BinaryReader reader, int maximumBytes)
    {
        var value = reader.ReadString();
        if (Encoding.UTF8.GetByteCount(value) > maximumBytes)
            throw new ArgumentException("Remote lease wire string exceeds its bound.");
        return value;
    }

    private static void WriteBounded(BinaryWriter writer, string value, int maximumBytes)
    {
        while (value.Length != 0 && Encoding.UTF8.GetByteCount(value) > maximumBytes)
            value = value[..^1];
        writer.Write(value);
    }

    private static string Evidence(string commandDigest, ReadOnlySpan<byte> responsePrefix)
    {
        var commandBytes = Convert.FromHexString(commandDigest);
        var payload = new byte[commandBytes.Length + responsePrefix.Length];
        commandBytes.CopyTo(payload, 0);
        responsePrefix.CopyTo(payload.AsSpan(commandBytes.Length));
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }
}

internal sealed class V6RemoteLeaseOwnerEndpoint(V6RemoteDelegatedResourcePilot pilot)
{
    private sealed record Cached(string CommandDigest, byte[] Response);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Cached> _requests = [];

    internal KernelResult<byte[]> Handle(ReadOnlyMemory<byte> payload)
    {
        V6RemoteLeaseCommand command;
        try { command = V6RemoteLeaseWireCodec.DecodeCommand(payload); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        { return KernelResult<byte[]>.Fail(KernelError.InvalidMessage, exception.Message); }
        var digest = V6RemoteLeaseWireCodec.Digest(payload.Span);
        lock (_sync)
        {
            if (_requests.TryGetValue(command.RequestId, out var cached))
            {
                if (cached.CommandDigest != digest)
                    return KernelResult<byte[]>.Fail(KernelError.DuplicateIdentity,
                        "A remote request identity was reused with a different command payload.");
                if (command.Kind is V6RemoteLeaseCommandKind.Submit or V6RemoteLeaseCommandKind.Publish &&
                    RemoteAuthorityLeaseEvaluatorV1.Evaluate(command.Lease, pilot.Query().Current) !=
                    RemoteLeaseAdmissionCodeV1.Eligible)
                    return KernelResult<byte[]>.Fail(KernelError.StaleGeneration,
                        "The cached response cannot authorize a stale lease after owner state changed.");
                return KernelResult<byte[]>.Ok(cached.Response.ToArray());
            }
            var result = Execute(command);
            var response = V6RemoteLeaseWireCodec.EncodeResponse(command, result, digest);
            _requests.Add(command.RequestId, new(digest, response));
            return KernelResult<byte[]>.Ok(response.ToArray());
        }
    }

    internal V6RemoteDelegatedResourcePilotSnapshot Query() => pilot.Query();

    internal KernelResult<V6RemoteLeaseRestartPlan> RestartOwner(
        V6RemoteLeaseRecoveryJournal journal, ulong freshOwnerIncarnation, ulong freshOwnerEpoch)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var ownerHostIdentity = pilot.Query().Lease.OwnerHostIdentity;
        try
        {
            // Endpoint dispatch and the live generation transition share this lock.
            // The possibly failing journal write remains outside it.
            lock (_sync)
                pilot.RebootOwner(freshOwnerIncarnation, freshOwnerEpoch);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return KernelResult<V6RemoteLeaseRestartPlan>.Fail(KernelError.StaleGeneration, exception.Message);
        }
        try
        {
            return KernelResult<V6RemoteLeaseRestartPlan>.Ok(
                journal.RecordOwnerRestart(ownerHostIdentity, freshOwnerIncarnation, freshOwnerEpoch));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            return KernelResult<V6RemoteLeaseRestartPlan>.Fail(KernelError.DependencyUnavailable,
                $"Live owner was fenced, but authenticated restart recording failed: {exception.Message}");
        }
    }

    private KernelResult<V6RemoteDelegatedResourcePilotSnapshot> Execute(V6RemoteLeaseCommand command) =>
        command.Kind switch
        {
            V6RemoteLeaseCommandKind.Submit => pilot.Submit(command.Lease,
                command.RequestedRights, command.Allocation),
            V6RemoteLeaseCommandKind.Fence => pilot.Fence(command.Lease),
            V6RemoteLeaseCommandKind.EffectClosure => pilot.ObserveEffectClosure(command.Closure!.Value),
            V6RemoteLeaseCommandKind.Publish => pilot.Publish(command.Lease),
            V6RemoteLeaseCommandKind.Reclaim => pilot.Reclaim(command.Closure!.Value),
            V6RemoteLeaseCommandKind.ProviderLoss => pilot.RecordProviderLoss(command.Lease),
            _ => KernelResult<V6RemoteDelegatedResourcePilotSnapshot>.Fail(
                KernelError.InvalidMessage, "Remote lease command is unsupported."),
        };
}

internal sealed class V6ManagedRemoteLeaseTransport(
    V6RemoteLeaseOwnerEndpoint owner, bool requireJournaledSubmit = false)
{
    private readonly object _sync = new();
    private V6ManagedRemoteLeaseJournaledSubmit? _journaledSubmit;
    private bool _partitioned;
    private bool _dropNextResponse;
    private bool _corruptNextResponse;
    private bool _duplicateNextDelivery;

    internal void BindJournaledSubmit(V6ManagedRemoteLeaseJournaledSubmit coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        lock (_sync)
        {
            if (!requireJournaledSubmit || _journaledSubmit is not null)
                throw new InvalidOperationException("This managed transport cannot bind another journaled submit coordinator.");
            _journaledSubmit = coordinator;
        }
    }

    internal KernelResult<V6RemoteLeaseTransportReceipt> Send(V6RemoteLeaseCommand command) =>
        SendCore(command, null);

    internal KernelResult<V6RemoteLeaseTransportReceipt> SendJournaledSubmit(
        V6RemoteLeaseCommand command, V6ManagedRemoteLeaseJournaledSubmit coordinator) =>
        SendCore(command, coordinator);

    private KernelResult<V6RemoteLeaseTransportReceipt> SendCore(
        V6RemoteLeaseCommand command, V6ManagedRemoteLeaseJournaledSubmit? coordinator)
    {
        byte[] payload;
        try { payload = V6RemoteLeaseWireCodec.Encode(command); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<V6RemoteLeaseTransportReceipt>.Fail(KernelError.InvalidMessage, exception.Message); }
        bool drop;
        bool corrupt;
        bool duplicate;
        lock (_sync)
        {
            if (command.Kind == V6RemoteLeaseCommandKind.Submit && requireJournaledSubmit &&
                (_journaledSubmit is null || !ReferenceEquals(_journaledSubmit, coordinator)))
                return KernelResult<V6RemoteLeaseTransportReceipt>.Fail(KernelError.PlatformDenied,
                    "Managed remote submit must use its bound write-ahead coordinator.");
            if (_partitioned)
                return KernelResult<V6RemoteLeaseTransportReceipt>.Fail(KernelError.DependencyUnavailable,
                    "Managed remote lease transport is partitioned before delivery.");
            drop = _dropNextResponse; _dropNextResponse = false;
            corrupt = _corruptNextResponse; _corruptNextResponse = false;
            duplicate = _duplicateNextDelivery; _duplicateNextDelivery = false;
        }
        var delivered = owner.Handle(payload);
        if (!delivered.IsSuccess)
            return KernelResult<V6RemoteLeaseTransportReceipt>.Fail(delivered.Error, delivered.Message!);
        if (duplicate)
        {
            var repeated = owner.Handle(payload);
            if (!repeated.IsSuccess || !repeated.Value!.AsSpan().SequenceEqual(delivered.Value!))
                return KernelResult<V6RemoteLeaseTransportReceipt>.Fail(KernelError.PlatformFaulted,
                    "Idempotent duplicate delivery produced a different owner response.");
        }
        if (drop)
            return KernelResult<V6RemoteLeaseTransportReceipt>.Fail(KernelError.DependencyUnavailable,
                "Managed transport dropped the response after possible owner commit.");
        byte[] response = delivered.Value!;
        if (corrupt)
        {
            response = response.ToArray();
            response[^1] = response[^1] == (byte)'0' ? (byte)'1' : (byte)'0';
        }
        var decoded = V6RemoteLeaseWireCodec.DecodeResponse(response, command,
            V6RemoteLeaseWireCodec.Digest(payload));
        return decoded.Error == KernelError.InvalidMessage
            ? KernelResult<V6RemoteLeaseTransportReceipt>.Fail(KernelError.DependencyUnavailable,
                "Remote response is invalid after possible owner commit; retry only the exact request.")
            : decoded;
    }

    internal void SetPartitioned(bool value) { lock (_sync) _partitioned = value; }
    internal void DropNextResponse() { lock (_sync) _dropNextResponse = true; }
    internal void CorruptNextResponse() { lock (_sync) _corruptNextResponse = true; }
    internal void DuplicateNextDelivery() { lock (_sync) _duplicateNextDelivery = true; }
}
