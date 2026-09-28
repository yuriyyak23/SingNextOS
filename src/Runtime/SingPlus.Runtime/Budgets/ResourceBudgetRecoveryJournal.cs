using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

internal enum ResourceBudgetRecoveryTransition : byte
{
    Prepared = 1,
    PossibleSubmit = 2,
    Quarantined = 3,
    CancelledPreSubmit = 4,
    SettledExact = 5,
    SettledContained = 6,
    SettledConservative = 7,
}

internal sealed record ResourceBudgetRecoveryPayload(
    BudgetReservationHandle Lease,
    ProcessHandle Owner,
    IReadOnlyList<BudgetAmount> ReservedAmounts,
    PlatformResourceCorrelation ProviderCorrelation,
    ResourceBudgetRecoveryTransition Transition,
    IReadOnlyList<BudgetAmount> ChargedAmounts);

internal sealed record ResourceBudgetRecoveryRecord(
    uint ContractVersion,
    Guid JournalEpoch,
    ulong Sequence,
    byte[] PreviousAuthenticator,
    byte[] Payload,
    byte[] Authenticator);

internal sealed record ResourceBudgetRecoveryItem(
    ResourceBudgetRecoveryPayload LastPayload,
    ulong LastSequence)
{
    internal bool RequiresReconciliation => LastPayload.Transition is
        ResourceBudgetRecoveryTransition.PossibleSubmit or
        ResourceBudgetRecoveryTransition.Quarantined;

    internal bool IsTerminal => LastPayload.Transition is
        ResourceBudgetRecoveryTransition.CancelledPreSubmit or
        ResourceBudgetRecoveryTransition.SettledExact or
        ResourceBudgetRecoveryTransition.SettledContained or
        ResourceBudgetRecoveryTransition.SettledConservative;
}

internal sealed record ResourceBudgetRecoverySnapshot(
    Guid JournalEpoch,
    ulong LastSequence,
    IReadOnlyList<ResourceBudgetRecoveryItem> Items)
{
    internal IReadOnlyList<ResourceBudgetRecoveryItem> ReconciliationBacklog =>
        Items.Where(static item => item.RequiresReconciliation).ToArray();

    internal bool BlocksFreshAdmission => Items.Any(static item => item.LastPayload.Transition is not
        (ResourceBudgetRecoveryTransition.CancelledPreSubmit or
         ResourceBudgetRecoveryTransition.SettledContained));

    internal IReadOnlyList<BudgetAmount> ConservativeRecoveryCharge
    {
        get
        {
            var totals = new Dictionary<ServiceBudgetDimension, ulong>();
            foreach (var item in Items)
            {
                IReadOnlyList<BudgetAmount> charge = item.LastPayload.Transition switch
                {
                    ResourceBudgetRecoveryTransition.CancelledPreSubmit or
                    ResourceBudgetRecoveryTransition.SettledContained => [],
                    ResourceBudgetRecoveryTransition.SettledExact => item.LastPayload.ChargedAmounts,
                    _ => item.LastPayload.ReservedAmounts,
                };
                foreach (var amount in charge)
                    totals[amount.Dimension] = checked(totals.GetValueOrDefault(amount.Dimension) + amount.Amount);
            }
            return totals.OrderBy(static item => item.Key)
                .Select(static item => new BudgetAmount(item.Key, item.Value)).ToArray();
        }
    }
}

internal interface IResourceBudgetJournalStore
{
    IReadOnlyList<byte[]> ReadFrames();
    void AppendFrame(ReadOnlySpan<byte> frame);
}

/// <summary>
/// Append-only file store. Each frame is length-prefixed and flushed to stable
/// storage before AppendFrame returns. A torn final frame fails closed on replay.
/// </summary>
internal sealed class FileResourceBudgetJournalStore(string path) : IResourceBudgetJournalStore
{
    private const int MaximumFrameBytes = 1024 * 1024;
    private readonly string _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
    private readonly object _gate = new();

    public IReadOnlyList<byte[]> ReadFrames()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            var frames = new List<byte[]>();
            Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
            while (stream.Position < stream.Length)
            {
                ReadExactly(stream, lengthBytes);
                var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                if (length <= 0 || length > MaximumFrameBytes)
                    throw new InvalidDataException("Recovery journal frame length is invalid.");
                var frame = new byte[length];
                ReadExactly(stream, frame);
                frames.Add(frame);
            }
            return frames;
        }
    }

    public void AppendFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.IsEmpty || frame.Length > MaximumFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(frame));
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, frame.Length);
            stream.Write(lengthBytes);
            stream.Write(frame);
            stream.Flush(flushToDisk: true);
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = stream.Read(destination[read..]);
            if (count == 0) throw new InvalidDataException("Recovery journal contains a torn frame.");
            read += count;
        }
    }
}

/// <summary>
/// Durable authenticated recovery evidence for ResourceBudgetAuthority. The
/// journal is not a ledger: replay never materializes an account, capability or
/// live lease and can only identify terminal history or reconciliation backlog.
/// </summary>
internal sealed class ResourceBudgetRecoveryJournal
{
    internal const uint ContractVersion = 1;
    private const int AuthenticatorBytes = 32;
    private readonly IResourceBudgetJournalStore _store;
    private readonly byte[] _key;
    private readonly object _gate = new();
    private Guid _epoch;
    private ulong _lastSequence;
    private byte[] _lastAuthenticator = new byte[AuthenticatorBytes];

    internal ResourceBudgetRecoveryJournal(
        IResourceBudgetJournalStore store,
        ReadOnlySpan<byte> authenticationKey,
        Guid? newJournalEpoch = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (authenticationKey.Length < AuthenticatorBytes)
            throw new ArgumentException("Recovery journal authentication keys require at least 256 bits.", nameof(authenticationKey));
        _key = authenticationKey.ToArray();

        var recovered = ReplayCore(_store.ReadFrames());
        if (recovered.LastSequence == 0)
        {
            _epoch = newJournalEpoch ?? Guid.NewGuid();
            if (_epoch == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(newJournalEpoch));
        }
        else
        {
            _epoch = recovered.JournalEpoch;
            _lastSequence = recovered.LastSequence;
            _lastAuthenticator = DecodeFrame(_store.ReadFrames()[^1]).Authenticator;
        }
    }

    internal ResourceBudgetRecoveryRecord Append(ResourceBudgetRecoveryPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidatePayload(payload);
        lock (_gate)
        {
            var current = ReplayCore(_store.ReadFrames());
            var prior = current.Items.SingleOrDefault(item => item.LastPayload.Lease == payload.Lease);
            if (prior is null && payload.Transition != ResourceBudgetRecoveryTransition.Prepared)
                throw new InvalidDataException("Recovery journal lease history must begin with Prepared.");
            if (prior is not null &&
                (prior.LastPayload.Owner != payload.Owner ||
                 !prior.LastPayload.ReservedAmounts.SequenceEqual(payload.ReservedAmounts) ||
                 prior.LastPayload.ProviderCorrelation != payload.ProviderCorrelation ||
                 !IsAllowedTransition(prior.LastPayload.Transition, payload.Transition)))
                throw new InvalidDataException("Recovery journal append is stale, widened, or an invalid lease transition.");
            if (_lastSequence == ulong.MaxValue)
                throw new InvalidOperationException("Recovery journal sequence space is exhausted.");
            var sequence = _lastSequence + 1;
            var payloadBytes = EncodePayload(payload);
            var authenticator = Authenticate(_epoch, sequence, _lastAuthenticator, payloadBytes);
            var record = new ResourceBudgetRecoveryRecord(ContractVersion, _epoch, sequence,
                _lastAuthenticator.ToArray(), payloadBytes, authenticator);
            _store.AppendFrame(EncodeFrame(record));
            _lastSequence = sequence;
            _lastAuthenticator = authenticator;
            return record;
        }
    }

    internal ResourceBudgetRecoverySnapshot Replay()
    {
        lock (_gate) return ReplayCore(_store.ReadFrames());
    }

    private ResourceBudgetRecoverySnapshot ReplayCore(IReadOnlyList<byte[]> frames)
    {
        if (frames.Count == 0) return new ResourceBudgetRecoverySnapshot(Guid.Empty, 0, []);
        var items = new Dictionary<BudgetReservationHandle, ResourceBudgetRecoveryItem>();
        Guid epoch = Guid.Empty;
        ulong expectedSequence = 1;
        var previous = new byte[AuthenticatorBytes];
        foreach (var frame in frames)
        {
            var record = DecodeFrame(frame);
            if (record.ContractVersion != ContractVersion || record.JournalEpoch == Guid.Empty ||
                record.Sequence != expectedSequence ||
                !CryptographicOperations.FixedTimeEquals(record.PreviousAuthenticator, previous))
                throw new InvalidDataException("Recovery journal version, epoch, sequence, or hash chain is invalid.");
            if (epoch == Guid.Empty) epoch = record.JournalEpoch;
            if (epoch != record.JournalEpoch)
                throw new InvalidDataException("Recovery journal epoch changed within one chain.");
            var expected = Authenticate(epoch, record.Sequence, previous, record.Payload);
            if (!CryptographicOperations.FixedTimeEquals(expected, record.Authenticator))
                throw new InvalidDataException("Recovery journal authentication failed.");

            var payload = DecodePayload(record.Payload);
            ValidatePayload(payload);
            if (items.TryGetValue(payload.Lease, out var prior))
            {
                if (prior.LastPayload.Owner != payload.Owner ||
                    !prior.LastPayload.ReservedAmounts.SequenceEqual(payload.ReservedAmounts) ||
                    prior.LastPayload.ProviderCorrelation != payload.ProviderCorrelation ||
                    !IsAllowedTransition(prior.LastPayload.Transition, payload.Transition))
                    throw new InvalidDataException("Recovery journal contains a stale, widened, or invalid lease transition.");
            }
            else if (payload.Transition != ResourceBudgetRecoveryTransition.Prepared)
            {
                throw new InvalidDataException("Recovery journal lease history does not begin with Prepared.");
            }
            items[payload.Lease] = new ResourceBudgetRecoveryItem(payload, record.Sequence);
            previous = record.Authenticator;
            expectedSequence++;
        }
        return new ResourceBudgetRecoverySnapshot(epoch, expectedSequence - 1,
            items.Values.OrderBy(static item => item.LastPayload.Lease.ReservationId.Value).ToArray());
    }

    private static bool IsAllowedTransition(ResourceBudgetRecoveryTransition prior, ResourceBudgetRecoveryTransition next) =>
        (prior, next) switch
        {
            (ResourceBudgetRecoveryTransition.Prepared, ResourceBudgetRecoveryTransition.PossibleSubmit) => true,
            (ResourceBudgetRecoveryTransition.Prepared, ResourceBudgetRecoveryTransition.CancelledPreSubmit) => true,
            (ResourceBudgetRecoveryTransition.PossibleSubmit, ResourceBudgetRecoveryTransition.Quarantined) => true,
            (ResourceBudgetRecoveryTransition.PossibleSubmit, ResourceBudgetRecoveryTransition.SettledExact or
                ResourceBudgetRecoveryTransition.SettledContained or
                ResourceBudgetRecoveryTransition.SettledConservative) => true,
            (ResourceBudgetRecoveryTransition.Quarantined, ResourceBudgetRecoveryTransition.Quarantined) => true,
            (ResourceBudgetRecoveryTransition.Quarantined, ResourceBudgetRecoveryTransition.SettledExact or
                ResourceBudgetRecoveryTransition.SettledContained or
                ResourceBudgetRecoveryTransition.SettledConservative) => true,
            _ => false,
        };

    private static void ValidatePayload(ResourceBudgetRecoveryPayload payload)
    {
        if (payload.Lease.ReservationId.Value == 0 || payload.Lease.Generation.Value == 0 ||
            payload.Owner.ProcessId.Value == 0 || payload.Owner.Generation == 0 ||
            payload.ProviderCorrelation.CorrelationId.Value == 0 || payload.ProviderCorrelation.Generation.Value == 0 ||
            !Enum.IsDefined(payload.Transition))
            throw new InvalidDataException("Recovery journal payload identity, generation, or transition is invalid.");
        ValidateAmounts(payload.ReservedAmounts, allowEmpty: false);
        ValidateAmounts(payload.ChargedAmounts, allowEmpty: true);

        var reserved = payload.ReservedAmounts.ToDictionary(static amount => amount.Dimension, static amount => amount.Amount);
        if (payload.ChargedAmounts.Any(amount => !reserved.TryGetValue(amount.Dimension, out var limit) || amount.Amount > limit))
            throw new InvalidDataException("Recovery journal charge exceeds its reserved dimension.");

        if (payload.Transition is not (ResourceBudgetRecoveryTransition.SettledExact or
                ResourceBudgetRecoveryTransition.SettledConservative) && payload.ChargedAmounts.Count != 0)
            throw new InvalidDataException("Only exact or conservative settlement may carry a charge.");
        if (payload.Transition == ResourceBudgetRecoveryTransition.SettledConservative &&
            !payload.ChargedAmounts.SequenceEqual(payload.ReservedAmounts))
            throw new InvalidDataException("Conservative settlement must charge the complete reserved envelope.");
    }

    private static void ValidateAmounts(IReadOnlyList<BudgetAmount> amounts, bool allowEmpty)
    {
        if ((!allowEmpty && amounts.Count == 0) ||
            amounts.Any(static amount => !Enum.IsDefined(amount.Dimension) || amount.Amount == 0) ||
            amounts.Select(static amount => amount.Dimension).Distinct().Count() != amounts.Count ||
            !amounts.SequenceEqual(amounts.OrderBy(static amount => amount.Dimension)))
            throw new InvalidDataException("Recovery journal amounts must be positive, unique and canonically ordered.");
    }

    private byte[] Authenticate(Guid epoch, ulong sequence, ReadOnlySpan<byte> previous, ReadOnlySpan<byte> payload)
    {
        using var hmac = new HMACSHA256(_key);
        using var stream = new MemoryStream();
        stream.Write(epoch.ToByteArray());
        WriteUInt64(stream, sequence);
        stream.Write(previous);
        stream.Write(payload);
        return hmac.ComputeHash(stream.ToArray());
    }

    private static byte[] EncodePayload(ResourceBudgetRecoveryPayload payload)
    {
        using var stream = new MemoryStream();
        WriteUInt64(stream, payload.Lease.ReservationId.Value);
        WriteUInt64(stream, payload.Lease.Generation.Value);
        WriteUInt64(stream, payload.Owner.ProcessId.Value);
        WriteUInt64(stream, payload.Owner.Generation);
        WriteUInt64(stream, payload.ProviderCorrelation.CorrelationId.Value);
        WriteUInt64(stream, payload.ProviderCorrelation.Generation.Value);
        stream.WriteByte((byte)payload.Transition);
        WriteAmounts(stream, payload.ReservedAmounts);
        WriteAmounts(stream, payload.ChargedAmounts);
        return stream.ToArray();
    }

    private static ResourceBudgetRecoveryPayload DecodePayload(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        var lease = new BudgetReservationHandle(new BudgetReservationId(ReadUInt64(stream)), new BudgetGeneration(ReadUInt64(stream)));
        var owner = new ProcessHandle(new ProcessId(ReadUInt64(stream)), ReadUInt64(stream));
        var correlation = new PlatformResourceCorrelation(new PlatformResourceCorrelationId(ReadUInt64(stream)),
            new PlatformResourceCorrelationGeneration(ReadUInt64(stream)));
        var transition = (ResourceBudgetRecoveryTransition)ReadByte(stream);
        var reserved = ReadAmounts(stream);
        var charged = ReadAmounts(stream);
        if (stream.Position != stream.Length) throw new InvalidDataException("Recovery journal payload has trailing bytes.");
        return new ResourceBudgetRecoveryPayload(lease, owner, reserved, correlation, transition, charged);
    }

    private static byte[] EncodeFrame(ResourceBudgetRecoveryRecord record)
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, record.ContractVersion);
        stream.Write(record.JournalEpoch.ToByteArray());
        WriteUInt64(stream, record.Sequence);
        WriteBytes(stream, record.PreviousAuthenticator);
        WriteBytes(stream, record.Payload);
        WriteBytes(stream, record.Authenticator);
        return stream.ToArray();
    }

    private static ResourceBudgetRecoveryRecord DecodeFrame(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        var version = ReadUInt32(stream);
        Span<byte> epochBytes = stackalloc byte[16];
        ReadExactly(stream, epochBytes);
        var epoch = new Guid(epochBytes);
        var sequence = ReadUInt64(stream);
        var previous = ReadBytes(stream, AuthenticatorBytes);
        var payload = ReadBytes(stream, 64 * 1024);
        var authenticator = ReadBytes(stream, AuthenticatorBytes);
        if (previous.Length != AuthenticatorBytes || authenticator.Length != AuthenticatorBytes || stream.Position != stream.Length)
            throw new InvalidDataException("Recovery journal frame shape is invalid.");
        return new ResourceBudgetRecoveryRecord(version, epoch, sequence, previous, payload, authenticator);
    }

    private static void WriteAmounts(Stream stream, IReadOnlyList<BudgetAmount> amounts)
    {
        WriteUInt32(stream, checked((uint)amounts.Count));
        foreach (var amount in amounts)
        {
            WriteUInt32(stream, (uint)amount.Dimension);
            WriteUInt64(stream, amount.Amount);
        }
    }

    private static BudgetAmount[] ReadAmounts(Stream stream)
    {
        var count = ReadUInt32(stream);
        if (count > 64) throw new InvalidDataException("Recovery journal amount vector is too large.");
        var amounts = new BudgetAmount[count];
        for (var index = 0; index < amounts.Length; index++)
            amounts[index] = new BudgetAmount((ServiceBudgetDimension)ReadUInt32(stream), ReadUInt64(stream));
        return amounts;
    }

    private static void WriteBytes(Stream stream, ReadOnlySpan<byte> bytes)
    {
        WriteUInt32(stream, checked((uint)bytes.Length));
        stream.Write(bytes);
    }

    private static byte[] ReadBytes(Stream stream, int maximum)
    {
        var length = ReadUInt32(stream);
        if (length > maximum) throw new InvalidDataException("Recovery journal byte field is too large.");
        var bytes = new byte[length];
        ReadExactly(stream, bytes);
        return bytes;
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        ReadExactly(stream, bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ReadExactly(stream, bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static byte ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value < 0 ? throw new InvalidDataException("Recovery journal ended unexpectedly.") : (byte)value;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = stream.Read(destination[read..]);
            if (count == 0) throw new InvalidDataException("Recovery journal ended unexpectedly.");
            read += count;
        }
    }
}
