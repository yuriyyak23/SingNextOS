namespace SingNext.Boot.Core;

public readonly record struct BootResetSnapshot(ulong ResetEpoch, ulong ResetSequence);
public readonly record struct BootPciAddress(ushort Segment, byte Bus, byte Device, byte Function);

public interface IBootClock
{
    ulong MonotonicTicks { get; }
}

public interface IBootResetControl
{
    BootResetSnapshot Observe();
    BootFailure RequestTerminalReset();
}

public interface IBootDmaIsolation
{
    BootResult<ulong> EstablishDenyByDefault(BootPciAddress address, BootResetSnapshot reset);
    bool IsCurrent(ulong isolationGeneration, BootResetSnapshot reset);
}

public interface IBootPciConfiguration
{
    BootResult<uint> Read32(BootPciAddress address, ushort offset, BootResetSnapshot reset);
}

public interface IBootCxlTransport
{
    BootResult<int> Execute(byte opcode, ReadOnlySpan<byte> request, Span<byte> response, ulong deadlineTicks, BootResetSnapshot reset);
}

public interface IBootTemporaryMapping
{
    BootResult<BootMappingEvidence> Map(BootMappingRequest request, BootResetSnapshot reset);
    BootFailure Retire(BootMappingEvidence evidence, BootResetSnapshot reset);
}

public interface IBootProtectedState
{
    /// <summary>Returns only an authenticated committed record or a failure.</summary>
    BootResult<ProtectedBootEnvelope> ReadAuthenticated(BootStateDomain domain, BootResetSnapshot reset);
    BootFailure WriteCandidate(ProtectedBootEnvelope candidate, BootResetSnapshot reset);
    BootFailure FlushCandidate(BootStateDomain domain, ulong durableSequence, BootResetSnapshot reset);
    /// <summary>Returns only an authenticated durable, uncommitted candidate or a failure.</summary>
    BootResult<ProtectedBootEnvelope> ReadCandidateAuthenticated(BootStateDomain domain, ulong durableSequence, BootResetSnapshot reset);
    /// <summary>Atomically publishes the commit marker for the already verified durable candidate.</summary>
    BootFailure PublishCommitMarker(BootStateDomain domain, ulong durableSequence, BootResetSnapshot reset);
}

public interface IBootRecoverySource
{
    BootResult<int> ReadAuthenticated(Span<byte> destination, BootResetSnapshot reset);
}

public readonly record struct BootMappingRequest(
    ulong HpaBase, ulong HpaBytes, ulong SourceOffset, ulong SourceBytes, ulong PersistentCapacityBytes);

public readonly record struct BootMappingEvidence(
    ulong BootMappingGeneration, ulong ResetEpoch, ulong HpaBase, ulong HpaBytes, ulong SourceOffset, ulong SourceBytes);
