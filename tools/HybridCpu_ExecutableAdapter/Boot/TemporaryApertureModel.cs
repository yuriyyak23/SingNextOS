namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;

internal enum ApertureFailure { None, InvalidRange, InterleaveForbidden, Overlap, ActiveMappingExists, DecoderUnavailable, CommitFailed, Timeout, LinkLost, ReadFault, StaleGeneration, NotFound }
internal sealed record DecoderHop(string Name, bool Available, bool Enabled = false);
internal sealed record TemporaryMapping(ulong FirmwareGeneration, ulong MappingGeneration, ulong HpaBase, ulong HpaBytes, ulong SourceOffset, ulong SourceBytes, bool Active);
internal sealed record ApertureResult(TemporaryMapping? Mapping, ApertureFailure Failure, IReadOnlyList<string> Trace);

internal sealed class TemporaryApertureModel
{
    private ulong _firmwareGeneration = 1; private ulong _mappingGeneration; private TemporaryMapping? _active;
    public ulong FirmwareGeneration => _firmwareGeneration;

    public ApertureResult Create(ulong hpaBase, ulong hpaBytes, ulong sourceOffset, ulong sourceBytes, ulong capacity,
        IReadOnlyList<(ulong Base, ulong Bytes)> reserved, IReadOnlyList<DecoderHop> hops, bool interleaved, int failCommitOrdinal = 0)
    {
        var trace = new List<string>(); var touched = new List<DecoderHop>();
        if (_active is not null) return new(null, ApertureFailure.ActiveMappingExists, trace);
        if (interleaved) return new(null, ApertureFailure.InterleaveForbidden, trace);
        if (failCommitOrdinal < 0 || failCommitOrdinal > hops.Count) throw new ArgumentOutOfRangeException(nameof(failCommitOrdinal));
        if (hpaBytes < 16UL * 1024 * 1024 || sourceBytes == 0 || sourceBytes > hpaBytes || hpaBase > ulong.MaxValue - hpaBytes ||
            (hpaBase & 4095) != 0 || (sourceOffset & 4095) != 0 || sourceOffset > capacity || sourceBytes > capacity - sourceOffset)
            return new(null, ApertureFailure.InvalidRange, trace);
        if (reserved.Any(x => x.Bytes == 0 || x.Base > ulong.MaxValue - x.Bytes)) return new(null, ApertureFailure.InvalidRange, trace);
        if (reserved.Any(x => hpaBase < x.Base + x.Bytes && x.Base < hpaBase + hpaBytes)) return new(null, ApertureFailure.Overlap, trace);
        if (hops.Count == 0 || hops.Any(x => !x.Available || x.Enabled)) return new(null, ApertureFailure.DecoderUnavailable, trace);
        for (var i = 0; i < hops.Count; i++)
        {
            trace.Add($"Commit:{hops[i].Name}#{i + 1}");
            if (failCommitOrdinal == i + 1)
            {
                foreach (var hop in touched.AsEnumerable().Reverse()) trace.Add($"Rollback:{hop.Name}");
                return new(null, ApertureFailure.CommitFailed, trace);
            }
            touched.Add(hops[i]);
        }
        _active = new(_firmwareGeneration, checked(++_mappingGeneration), hpaBase, hpaBytes, sourceOffset, sourceBytes, true);
        return new(_active, ApertureFailure.None, trace);
    }

    public ApertureFailure Validate(TemporaryMapping mapping) => _active == mapping && mapping.Active && mapping.FirmwareGeneration == _firmwareGeneration ? ApertureFailure.None : ApertureFailure.StaleGeneration;
    public ApertureFailure Read(TemporaryMapping mapping, ulong offset, ulong bytes, ApertureFailure injectedFailure = ApertureFailure.None)
    {
        if (Validate(mapping) != ApertureFailure.None) return ApertureFailure.StaleGeneration;
        if (bytes == 0 || offset > mapping.SourceBytes || bytes > mapping.SourceBytes - offset) return ApertureFailure.InvalidRange;
        if (injectedFailure is not (ApertureFailure.None or ApertureFailure.Timeout or ApertureFailure.LinkLost or ApertureFailure.ReadFault))
            throw new ArgumentOutOfRangeException(nameof(injectedFailure));
        if (injectedFailure == ApertureFailure.None) return ApertureFailure.None;
        _active = null;
        _mappingGeneration = checked(_mappingGeneration + 1);
        return injectedFailure;
    }
    public ApertureFailure Destroy(TemporaryMapping mapping) { if (Validate(mapping) != ApertureFailure.None) return ApertureFailure.StaleGeneration; _active = null; _mappingGeneration++; return ApertureFailure.None; }
    public void Reset() { _firmwareGeneration++; _mappingGeneration = 0; _active = null; }
}
