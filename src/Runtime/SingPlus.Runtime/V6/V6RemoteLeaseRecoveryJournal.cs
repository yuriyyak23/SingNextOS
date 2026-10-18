using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum V6RemoteLeaseRecoveryTransition : byte
{
    OwnerRestart = 1,
    Issued,
    Submitted,
    Fenced,
    ProviderLost,
    EffectClosed,
    Published,
    Reclaimed,
}

internal sealed record V6RemoteLeaseRecoveryPayload(
    V6RemoteLeaseRecoveryTransition Transition,
    string OwnerHostIdentity,
    ulong OwnerIncarnation,
    ulong OwnerEpoch,
    RemoteAuthorityLeaseV1? Lease = null,
    RemoteResourceEscrowV1 Escrow = default,
    ulong ProviderGeneration = 0,
    RemoteEffectClosureV1? Closure = null);

internal enum V6RemoteLeaseRestartDisposition : byte
{
    ExpiredWithoutEffect = 1,
    QuarantinedAwaitingClosure,
    ClosureVerifiedReclaimOnly,
    TerminalReclaimed,
}

internal sealed record V6RemoteLeaseRecoveryItem(
    V6RemoteLeaseRecoveryPayload LastPayload,
    ulong LastSequence);

internal sealed record V6RemoteLeaseRestartItem(
    RemoteAuthorityLeaseV1 Lease,
    V6RemoteLeaseRestartDisposition Disposition,
    RemoteResourceEscrowV1 Escrow,
    RemoteEffectClosureV1? Closure)
{
    internal bool AuthorizesExecution => false;
    internal bool AuthorizesPublication => false;
    internal bool AuthorizesReclaim => false;
    internal bool RequiresExactClosure =>
        Disposition == V6RemoteLeaseRestartDisposition.QuarantinedAwaitingClosure;
}

internal sealed record V6RemoteLeaseRecoverySnapshot(
    Guid JournalEpoch,
    ulong LastSequence,
    string? OwnerHostIdentity,
    ulong OwnerIncarnationHighWatermark,
    ulong OwnerEpochHighWatermark,
    IReadOnlyList<V6RemoteLeaseRecoveryItem> Items);

internal sealed record V6RemoteLeaseRestartPlan(
    string OwnerHostIdentity,
    ulong FreshOwnerIncarnation,
    ulong FreshOwnerEpoch,
    IReadOnlyList<V6RemoteLeaseRestartItem> Items)
{
    internal bool RestoresLeaseAuthority => false;
    internal bool AllowsOldLeasePublication => false;
}

/// <summary>
/// Authenticated append-only recovery evidence for the managed P11 contour. Replay preserves
/// owner incarnation/epoch high-watermarks and reconciliation obligations, but never recreates
/// a live remote lease or grants execution, publication, or reclaim authority.
/// </summary>
internal sealed class V6RemoteLeaseRecoveryJournal
{
    private sealed record Frame(uint Version, Guid Epoch, ulong Sequence,
        byte[] PreviousAuthenticator, byte[] Payload, byte[] Authenticator);

    internal const uint ContractVersion = 1;
    private const int AuthenticatorBytes = 32;
    private readonly IResourceBudgetJournalStore _store;
    private readonly byte[] _key;
    private readonly object _sync = new();
    private Guid _epoch;
    private ulong _lastSequence;
    private byte[] _lastAuthenticator = new byte[AuthenticatorBytes];

    internal V6RemoteLeaseRecoveryJournal(IResourceBudgetJournalStore store,
        ReadOnlySpan<byte> authenticationKey, Guid? newJournalEpoch = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (authenticationKey.Length < AuthenticatorBytes)
            throw new ArgumentException("Remote lease journal authentication keys require at least 256 bits.",
                nameof(authenticationKey));
        _key = authenticationKey.ToArray();
        var frames = _store.ReadFrames();
        var recovered = ReplayCore(frames);
        if (recovered.LastSequence == 0)
        {
            _epoch = newJournalEpoch ?? Guid.NewGuid();
            if (_epoch == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(newJournalEpoch));
        }
        else
        {
            _epoch = recovered.JournalEpoch;
            _lastSequence = recovered.LastSequence;
            _lastAuthenticator = DecodeFrame(frames[^1]).Authenticator;
        }
    }

    internal V6RemoteLeaseRecoverySnapshot Replay()
    {
        lock (_sync) return ReplayCore(_store.ReadFrames());
    }

    internal V6RemoteLeaseRecoverySnapshot Append(V6RemoteLeaseRecoveryPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateShape(payload);
        lock (_sync)
        {
            var current = ReplayCore(_store.ReadFrames());
            ValidateAppend(current, payload);
            AppendCore(payload);
            return ReplayCore(_store.ReadFrames());
        }
    }

    internal V6RemoteLeaseRestartPlan RecordOwnerRestart(
        string ownerHostIdentity, ulong freshOwnerIncarnation, ulong freshOwnerEpoch)
    {
        ValidateHost(ownerHostIdentity);
        lock (_sync)
        {
            var current = ReplayCore(_store.ReadFrames());
            if (current.OwnerHostIdentity is not null &&
                !string.Equals(current.OwnerHostIdentity, ownerHostIdentity, StringComparison.Ordinal))
                throw new InvalidDataException("Remote lease journal belongs to another owner host.");
            if (freshOwnerIncarnation <= current.OwnerIncarnationHighWatermark ||
                freshOwnerEpoch <= current.OwnerEpochHighWatermark)
                throw new InvalidDataException(
                    "Owner restart must advance the persisted incarnation and epoch high-watermarks.");
            var restart = new V6RemoteLeaseRecoveryPayload(
                V6RemoteLeaseRecoveryTransition.OwnerRestart, ownerHostIdentity,
                freshOwnerIncarnation, freshOwnerEpoch);
            AppendCore(restart);
            var recovered = ReplayCore(_store.ReadFrames());
            return new(ownerHostIdentity, freshOwnerIncarnation, freshOwnerEpoch,
                recovered.Items.Select(ToRestartItem).ToArray());
        }
    }

    private void AppendCore(V6RemoteLeaseRecoveryPayload payload)
    {
        if (_lastSequence == ulong.MaxValue)
            throw new InvalidOperationException("Remote lease journal sequence space is exhausted.");
        var sequence = _lastSequence + 1;
        var bytes = EncodePayload(payload);
        var authenticator = Authenticate(_epoch, sequence, _lastAuthenticator, bytes);
        _store.AppendFrame(EncodeFrame(new(ContractVersion, _epoch, sequence,
            _lastAuthenticator.ToArray(), bytes, authenticator)));
        _lastSequence = sequence;
        _lastAuthenticator = authenticator;
    }

    private V6RemoteLeaseRecoverySnapshot ReplayCore(IReadOnlyList<byte[]> frames)
    {
        if (frames.Count == 0)
            return new(Guid.Empty, 0, null, 0, 0, []);
        var items = new Dictionary<Guid, V6RemoteLeaseRecoveryItem>();
        Guid epoch = Guid.Empty;
        ulong expectedSequence = 1;
        var previous = new byte[AuthenticatorBytes];
        string? owner = null;
        ulong incarnation = 0;
        ulong ownerEpoch = 0;
        foreach (var encoded in frames)
        {
            var frame = DecodeFrame(encoded);
            if (frame.Version != ContractVersion || frame.Epoch == Guid.Empty ||
                frame.Sequence != expectedSequence ||
                !CryptographicOperations.FixedTimeEquals(frame.PreviousAuthenticator, previous))
                throw new InvalidDataException(
                    "Remote lease journal version, epoch, sequence, or hash chain is invalid.");
            epoch = epoch == Guid.Empty ? frame.Epoch : epoch;
            if (frame.Epoch != epoch)
                throw new InvalidDataException("Remote lease journal epoch changed within one chain.");
            var expected = Authenticate(epoch, frame.Sequence, previous, frame.Payload);
            if (!CryptographicOperations.FixedTimeEquals(expected, frame.Authenticator))
                throw new InvalidDataException("Remote lease journal authentication failed.");
            var payload = DecodePayload(frame.Payload);
            ValidateShape(payload);
            Apply(payload, frame.Sequence, items, ref owner, ref incarnation, ref ownerEpoch);
            previous = frame.Authenticator;
            expectedSequence++;
        }
        return new(epoch, expectedSequence - 1, owner, incarnation, ownerEpoch,
            items.Values.OrderBy(static item => item.LastPayload.Lease!.Value.LeaseId).ToArray());
    }

    private static void Apply(V6RemoteLeaseRecoveryPayload payload, ulong sequence,
        IDictionary<Guid, V6RemoteLeaseRecoveryItem> items, ref string? owner,
        ref ulong incarnation, ref ulong ownerEpoch)
    {
        if (owner is not null && !string.Equals(owner, payload.OwnerHostIdentity, StringComparison.Ordinal))
            throw new InvalidDataException("Remote lease journal mixes owner hosts.");
        owner ??= payload.OwnerHostIdentity;
        if (payload.Transition == V6RemoteLeaseRecoveryTransition.OwnerRestart)
        {
            if (payload.OwnerIncarnation <= incarnation || payload.OwnerEpoch <= ownerEpoch)
                throw new InvalidDataException("Remote lease journal owner restart is not monotonic.");
            incarnation = payload.OwnerIncarnation;
            ownerEpoch = payload.OwnerEpoch;
            return;
        }

        var lease = payload.Lease!.Value;
        if (payload.Transition == V6RemoteLeaseRecoveryTransition.Issued)
        {
            if (items.ContainsKey(lease.LeaseId))
                throw new InvalidDataException("Remote lease identity was reused.");
            if (incarnation != 0 && (lease.OwnerIncarnation != incarnation || lease.OwnerEpoch != ownerEpoch))
                throw new InvalidDataException("New remote lease does not use the persisted owner generation.");
            incarnation = lease.OwnerIncarnation;
            ownerEpoch = lease.OwnerEpoch;
        }
        else
        {
            if (!items.TryGetValue(lease.LeaseId, out var prior))
                throw new InvalidDataException("Remote lease history does not begin with issuance.");
            ValidateTransition(prior.LastPayload, payload, incarnation, ownerEpoch);
        }
        items[lease.LeaseId] = new(payload, sequence);
    }

    private static void ValidateAppend(V6RemoteLeaseRecoverySnapshot current,
        V6RemoteLeaseRecoveryPayload payload)
    {
        if (current.OwnerHostIdentity is not null &&
            !string.Equals(current.OwnerHostIdentity, payload.OwnerHostIdentity, StringComparison.Ordinal))
            throw new InvalidDataException("Remote lease journal belongs to another owner host.");
        if (payload.Transition == V6RemoteLeaseRecoveryTransition.OwnerRestart)
        {
            if (payload.OwnerIncarnation <= current.OwnerIncarnationHighWatermark ||
                payload.OwnerEpoch <= current.OwnerEpochHighWatermark)
                throw new InvalidDataException("Remote lease owner restart is stale.");
            return;
        }
        var lease = payload.Lease!.Value;
        var prior = current.Items.SingleOrDefault(item =>
            item.LastPayload.Lease!.Value.LeaseId == lease.LeaseId);
        if (payload.Transition == V6RemoteLeaseRecoveryTransition.Issued)
        {
            if (prior is not null || current.OwnerIncarnationHighWatermark != 0 &&
                (lease.OwnerIncarnation != current.OwnerIncarnationHighWatermark ||
                 lease.OwnerEpoch != current.OwnerEpochHighWatermark))
                throw new InvalidDataException("Remote lease issuance reuses identity or stale owner generations.");
            return;
        }
        if (prior is null)
            throw new InvalidDataException("Remote lease history does not begin with issuance.");
        ValidateTransition(prior.LastPayload, payload,
            current.OwnerIncarnationHighWatermark, current.OwnerEpochHighWatermark);
    }

    private static void ValidateTransition(V6RemoteLeaseRecoveryPayload prior,
        V6RemoteLeaseRecoveryPayload next, ulong currentIncarnation, ulong currentEpoch)
    {
        if (prior.Lease != next.Lease || prior.ProviderGeneration != next.ProviderGeneration ||
            prior.Escrow.ParentAllocation != next.Escrow.ParentAllocation ||
            prior.Escrow.DelegatedAllocation != next.Escrow.DelegatedAllocation)
            throw new InvalidDataException("Remote lease recovery identity or immutable allocation changed.");
        if (next.Lease!.Value.OwnerIncarnation != currentIncarnation ||
            next.Lease.Value.OwnerEpoch != currentEpoch)
        {
            if (next.Transition is not (V6RemoteLeaseRecoveryTransition.EffectClosed or
                V6RemoteLeaseRecoveryTransition.Reclaimed) ||
                prior.Transition == V6RemoteLeaseRecoveryTransition.Issued &&
                next.Transition == V6RemoteLeaseRecoveryTransition.Reclaimed)
                throw new InvalidDataException(
                    "After owner restart an old lease requires exact closure before reclaim; issuance alone does not prove no effect.");
        }
        var allowed = (prior.Transition, next.Transition) switch
        {
            (V6RemoteLeaseRecoveryTransition.Issued,
                V6RemoteLeaseRecoveryTransition.Submitted or
                V6RemoteLeaseRecoveryTransition.Fenced or
                V6RemoteLeaseRecoveryTransition.ProviderLost or
                V6RemoteLeaseRecoveryTransition.EffectClosed or
                V6RemoteLeaseRecoveryTransition.Reclaimed) => true,
            (V6RemoteLeaseRecoveryTransition.Submitted,
                V6RemoteLeaseRecoveryTransition.Fenced or
                V6RemoteLeaseRecoveryTransition.ProviderLost) => true,
            (V6RemoteLeaseRecoveryTransition.Fenced,
                V6RemoteLeaseRecoveryTransition.EffectClosed or
                V6RemoteLeaseRecoveryTransition.ProviderLost) => true,
            (V6RemoteLeaseRecoveryTransition.ProviderLost,
                V6RemoteLeaseRecoveryTransition.EffectClosed) => true,
            (V6RemoteLeaseRecoveryTransition.EffectClosed,
                V6RemoteLeaseRecoveryTransition.Published or
                V6RemoteLeaseRecoveryTransition.Reclaimed) => true,
            (V6RemoteLeaseRecoveryTransition.Published,
                V6RemoteLeaseRecoveryTransition.Reclaimed) => true,
            _ => false,
        };
        if (!allowed)
            throw new InvalidDataException("Remote lease recovery transition is invalid or terminal.");
        if (prior.Escrow.ConsumedAllocation != 0 &&
            next.Escrow.ConsumedAllocation != prior.Escrow.ConsumedAllocation)
            throw new InvalidDataException("Remote lease consumed allocation changed after submit.");
    }

    private static void ValidateShape(V6RemoteLeaseRecoveryPayload payload)
    {
        if (!Enum.IsDefined(payload.Transition))
            throw new InvalidDataException("Remote lease recovery transition is invalid.");
        ValidateHost(payload.OwnerHostIdentity);
        if (payload.OwnerIncarnation == 0 || payload.OwnerEpoch == 0)
            throw new InvalidDataException("Remote lease owner generations must be non-zero.");
        if (payload.Transition == V6RemoteLeaseRecoveryTransition.OwnerRestart)
        {
            if (payload.Lease is not null || payload.ProviderGeneration != 0 ||
                payload.Closure is not null || payload.Escrow != default)
                throw new InvalidDataException("Owner restart cannot carry lease authority or effect evidence.");
            return;
        }
        if (payload.Lease is not { } lease || payload.ProviderGeneration == 0)
            throw new InvalidDataException("Remote lease recovery identity is incomplete.");
        lease.Validate();
        payload.Escrow.Validate();
        if (lease.OwnerHostIdentity != payload.OwnerHostIdentity ||
            lease.OwnerIncarnation != payload.OwnerIncarnation || lease.OwnerEpoch != payload.OwnerEpoch ||
            payload.Escrow.ParentAllocation == 0 || payload.Escrow.DelegatedAllocation == 0)
            throw new InvalidDataException("Remote lease recovery owner or escrow is inconsistent.");
        var requiresClosure = payload.Transition is V6RemoteLeaseRecoveryTransition.EffectClosed or
            V6RemoteLeaseRecoveryTransition.Published ||
            payload.Transition == V6RemoteLeaseRecoveryTransition.Reclaimed &&
            payload.Escrow.ConsumedAllocation != 0;
        if (requiresClosure != payload.Closure.HasValue ||
            payload.Closure is { } closure &&
            (closure.ProviderGeneration != payload.ProviderGeneration ||
             !RemoteReclaimPredicateV1.IsSatisfied(lease, closure)))
            throw new InvalidDataException("Remote lease recovery closure is missing or stale.");
        if (payload.Transition == V6RemoteLeaseRecoveryTransition.Issued &&
            (payload.Escrow.ConsumedAllocation != 0 || payload.Escrow.ReturnedAllocation != 0) ||
            payload.Transition is not (V6RemoteLeaseRecoveryTransition.Issued or
                V6RemoteLeaseRecoveryTransition.Reclaimed) && payload.Escrow.ReturnedAllocation != 0 ||
            payload.Transition == V6RemoteLeaseRecoveryTransition.Reclaimed &&
            payload.Escrow.ReturnedAllocation !=
            payload.Escrow.DelegatedAllocation - payload.Escrow.ConsumedAllocation)
            throw new InvalidDataException("Remote lease recovery escrow does not match its lifecycle state.");
    }

    private static V6RemoteLeaseRestartItem ToRestartItem(V6RemoteLeaseRecoveryItem item)
    {
        var payload = item.LastPayload;
        var disposition = payload.Transition switch
        {
            // The journal is not atomically connected to external send. A persisted
            // Issued frame cannot prove that a submit was never attempted.
            V6RemoteLeaseRecoveryTransition.Issued =>
                V6RemoteLeaseRestartDisposition.QuarantinedAwaitingClosure,
            V6RemoteLeaseRecoveryTransition.Submitted or
            V6RemoteLeaseRecoveryTransition.Fenced or
            V6RemoteLeaseRecoveryTransition.ProviderLost =>
                V6RemoteLeaseRestartDisposition.QuarantinedAwaitingClosure,
            V6RemoteLeaseRecoveryTransition.EffectClosed or
            V6RemoteLeaseRecoveryTransition.Published =>
                V6RemoteLeaseRestartDisposition.ClosureVerifiedReclaimOnly,
            _ => V6RemoteLeaseRestartDisposition.TerminalReclaimed,
        };
        return new(payload.Lease!.Value, disposition, payload.Escrow, payload.Closure);
    }

    private byte[] Authenticate(Guid epoch, ulong sequence,
        ReadOnlySpan<byte> previous, ReadOnlySpan<byte> payload)
    {
        using var hmac = new HMACSHA256(_key);
        using var stream = new MemoryStream();
        stream.Write(epoch.ToByteArray());
        Write(stream, sequence); stream.Write(previous); stream.Write(payload);
        return hmac.ComputeHash(stream.ToArray());
    }

    private static byte[] EncodePayload(V6RemoteLeaseRecoveryPayload payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)payload.Transition); writer.Write(payload.OwnerHostIdentity);
        writer.Write(payload.OwnerIncarnation); writer.Write(payload.OwnerEpoch);
        writer.Write(payload.Lease.HasValue);
        if (payload.Lease is { } lease) Write(writer, lease);
        writer.Write(payload.Escrow.Version); writer.Write(payload.Escrow.ParentAllocation);
        writer.Write(payload.Escrow.DelegatedAllocation); writer.Write(payload.Escrow.ConsumedAllocation);
        writer.Write(payload.Escrow.ReturnedAllocation); writer.Write(payload.ProviderGeneration);
        writer.Write(payload.Closure.HasValue);
        if (payload.Closure is { } closure) Write(writer, closure);
        writer.Flush(); return stream.ToArray();
    }

    private static V6RemoteLeaseRecoveryPayload DecodePayload(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var transition = (V6RemoteLeaseRecoveryTransition)reader.ReadByte();
            var owner = reader.ReadString(); var incarnation = reader.ReadUInt64();
            var ownerEpoch = reader.ReadUInt64();
            RemoteAuthorityLeaseV1? lease = reader.ReadBoolean() ? ReadLease(reader) : null;
            var escrow = new RemoteResourceEscrowV1(reader.ReadUInt16(), reader.ReadUInt64(),
                reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
            var provider = reader.ReadUInt64();
            RemoteEffectClosureV1? closure = reader.ReadBoolean() ? ReadClosure(reader) : null;
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Remote lease recovery payload has trailing bytes.");
            return new(transition, owner, incarnation, ownerEpoch, lease, escrow, provider, closure);
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException)
        { throw new InvalidDataException("Remote lease recovery payload is truncated.", exception); }
    }

    private static byte[] EncodeFrame(Frame frame)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(frame.Version); writer.Write(frame.Epoch.ToByteArray()); writer.Write(frame.Sequence);
        WriteBytes(writer, frame.PreviousAuthenticator); WriteBytes(writer, frame.Payload);
        WriteBytes(writer, frame.Authenticator); writer.Flush(); return stream.ToArray();
    }

    private static Frame DecodeFrame(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var frame = new Frame(reader.ReadUInt32(), new Guid(ReadExact(reader, 16)),
                reader.ReadUInt64(), ReadBytes(reader, AuthenticatorBytes),
                ReadBytes(reader, 64 * 1024), ReadBytes(reader, AuthenticatorBytes));
            if (frame.PreviousAuthenticator.Length != AuthenticatorBytes ||
                frame.Authenticator.Length != AuthenticatorBytes || stream.Position != stream.Length)
                throw new InvalidDataException("Remote lease journal frame shape is invalid.");
            return frame;
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException)
        { throw new InvalidDataException("Remote lease journal frame is truncated.", exception); }
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
        reader.ReadUInt16(), new Guid(ReadExact(reader, 16)), reader.ReadString(), reader.ReadUInt64(),
        reader.ReadString(), reader.ReadUInt64(), reader.ReadString(),
        (RemoteLeaseRightsV1)reader.ReadByte(), reader.ReadUInt64(), reader.ReadUInt64(),
        reader.ReadUInt64(), reader.ReadUInt64());

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

    private static void WriteBytes(BinaryWriter writer, byte[] bytes)
    { writer.Write(bytes.Length); writer.Write(bytes); }

    private static byte[] ReadBytes(BinaryReader reader, int maximum)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximum) throw new InvalidDataException("Journal byte field is invalid.");
        return ReadExact(reader, length);
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        return bytes.Length == count ? bytes : throw new EndOfStreamException();
    }

    private static void Write(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void ValidateHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
            value.Length > 128 || value.Any(char.IsControl))
            throw new InvalidDataException("Remote lease journal owner host identity is invalid.");
    }
}
