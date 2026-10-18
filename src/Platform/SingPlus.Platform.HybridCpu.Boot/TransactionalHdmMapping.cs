using SingNext.Boot.Core;

namespace SingPlus.Platform.HybridCpu.Boot;

public readonly record struct HdmDecoderState(
    int Hop, ulong HpaBase, ulong HpaBytes, ulong DpaBase, ulong DpaBytes, bool Enabled, bool Locked, bool Interleaved);

public interface IHdmDecoderHardware
{
    int HopCount { get; }
    BootFailure Validate(int hop, BootMappingRequest request, BootResetSnapshot reset);
    BootFailure Stage(int hop, BootMappingRequest request, BootResetSnapshot reset);
    BootFailure Commit(int hop, BootResetSnapshot reset);
    BootResult<HdmDecoderState> Readback(int hop, BootResetSnapshot reset);
    BootFailure Disable(int hop, BootResetSnapshot reset);
}

public sealed class TransactionalHdmMapping(IHdmDecoderHardware hardware) : IBootTemporaryMapping
{
    private BootMappingEvidence? _active;
    private ulong _nextMappingGeneration;
    private bool _quarantined;

    public BootResult<BootMappingEvidence> Map(BootMappingRequest request, BootResetSnapshot reset)
    {
        if (_quarantined)
            return BootResult<BootMappingEvidence>.Fail(BootFailure.Quarantined, "Decoder state is quarantined and cannot be reused.");
        if (_active is not null)
            return BootResult<BootMappingEvidence>.Fail(BootFailure.AmbiguousState, "A temporary mapping is already active.");
        if (!ValidRequest(request) || hardware.HopCount is <= 0 or > BootLimits.MaxDecoderHops)
            return BootResult<BootMappingEvidence>.Fail(BootFailure.BoundsViolation, "Temporary mapping request is outside the single-target profile.");
        var touched = 0;
        for (var hop = 0; hop < hardware.HopCount; hop++)
        {
            var validate = hardware.Validate(hop, request, reset);
            if (validate != BootFailure.None) return FailWithCompensation(validate, touched, reset);
        }
        for (var hop = 0; hop < hardware.HopCount; hop++)
        {
            var stage = hardware.Stage(hop, request, reset);
            if (stage != BootFailure.None) return FailWithCompensation(stage, touched, reset);
            touched++;
        }
        for (var hop = 0; hop < hardware.HopCount; hop++)
        {
            var commit = hardware.Commit(hop, reset);
            if (commit != BootFailure.None)
                return FailWithCompensation(
                    commit is BootFailure.Timeout or BootFailure.LinkLost or BootFailure.ResetObserved
                        ? commit
                        : BootFailure.PartialCommit,
                    touched,
                    reset);
        }
        for (var hop = 0; hop < hardware.HopCount; hop++)
        {
            var read = hardware.Readback(hop, reset);
            if (!read.IsSuccess || !Exact(read.Value, hop, request))
                return FailWithCompensation(read.IsSuccess ? BootFailure.ReadbackMismatch : read.Failure, touched, reset);
        }
        var evidence = new BootMappingEvidence(checked(++_nextMappingGeneration), reset.ResetEpoch,
            request.HpaBase, request.HpaBytes, request.SourceOffset, request.SourceBytes);
        _active = evidence;
        return BootResult<BootMappingEvidence>.Success(evidence);
    }

    public BootFailure Retire(BootMappingEvidence evidence, BootResetSnapshot reset)
    {
        if (_quarantined || _active != evidence || evidence.ResetEpoch != reset.ResetEpoch)
        {
            _quarantined = true;
            return BootFailure.Quarantined;
        }
        var disposition = Compensate(hardware.HopCount, reset);
        _active = null;
        if (disposition != BootFailure.None) _quarantined = true;
        return disposition;
    }

    private BootResult<BootMappingEvidence> FailWithCompensation(BootFailure failure, int touched, BootResetSnapshot reset)
    {
        var compensation = Compensate(touched, reset);
        if (compensation != BootFailure.None || failure is BootFailure.Timeout or BootFailure.LinkLost or BootFailure.ResetObserved)
            _quarantined = true;
        return BootResult<BootMappingEvidence>.Fail(
            _quarantined ? BootFailure.Quarantined : failure,
            _quarantined ? "HDM state is ambiguous and quarantined." : "HDM transaction failed and was compensated.");
    }

    private BootFailure Compensate(int touched, BootResetSnapshot reset)
    {
        var ambiguous = false;
        for (var hop = touched - 1; hop >= 0; hop--)
            if (hardware.Disable(hop, reset) != BootFailure.None) ambiguous = true;
        return ambiguous ? BootFailure.Quarantined : BootFailure.None;
    }

    private static bool ValidRequest(BootMappingRequest request) =>
        request.HpaBytes >= 16UL * 1024 * 1024 && request.SourceBytes != 0 && request.SourceBytes <= request.HpaBytes &&
        (request.HpaBase & 4095) == 0 && (request.SourceOffset & 4095) == 0 &&
        request.HpaBase <= ulong.MaxValue - request.HpaBytes && request.SourceOffset <= request.PersistentCapacityBytes &&
        request.SourceBytes <= request.PersistentCapacityBytes - request.SourceOffset;

    private static bool Exact(HdmDecoderState state, int hop, BootMappingRequest request) =>
        state.Hop == hop && state.HpaBase == request.HpaBase && state.HpaBytes == request.HpaBytes &&
        state.DpaBase == request.SourceOffset && state.DpaBytes == request.SourceBytes && state.Enabled && !state.Interleaved;
}
