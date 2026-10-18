using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    // Payload reservations only; provider/bridge own admission and completion.
    private abstract class DmaCopyPayload : IDisposable
    {
        protected DmaCopyPayload(RuntimeKernel ownerKernel,ProcessHandle source, ProcessHandle destination, PlatformDmaGrant sourceGrant,
            PlatformDmaPrepareEvidence sourcePrepare, PlatformDmaGrant destinationGrant, PlatformDmaPrepareEvidence destinationPrepare)
        { OwnerKernel=ownerKernel;Original = this; Source = source; Destination = destination; SourceGrant = sourceGrant; SourcePrepare = sourcePrepare; DestinationGrant = destinationGrant; DestinationPrepare = destinationPrepare; }
        internal RuntimeKernel OwnerKernel { get; }
        internal DmaCopyPayload Original { get; }
        internal ProcessHandle Source { get; }
        internal ProcessHandle Destination { get; }
        internal PlatformDmaGrant SourceGrant { get; }
        internal PlatformDmaPrepareEvidence SourcePrepare { get; }
        internal PlatformDmaGrant DestinationGrant { get; }
        internal PlatformDmaPrepareEvidence DestinationPrepare { get; }
        internal bool EffectPossible { get; set; }
        internal bool Consumed { get; set; }
        internal abstract PlatformDmaCopyBacking Backing { get; }
        internal abstract bool Matches(object input, object output);
        internal abstract void ReleaseSourceAfterClosure();
        public abstract void Dispose();
    }
    private sealed class DmaCopyPayload<T> : DmaCopyPayload where T : unmanaged
    {
        private readonly OwnedBuffer<T> _input, _output;
        private readonly RuntimeBufferLease<T> _read, _write;
        internal DmaCopyPayload(RuntimeKernel ownerKernel,ProcessHandle source, ProcessHandle destination, OwnedBuffer<T> input, OwnedBuffer<T> output,
            PlatformDmaGrant sourceGrant, PlatformDmaPrepareEvidence sourcePrepare, PlatformDmaGrant destinationGrant, PlatformDmaPrepareEvidence destinationPrepare)
            : base(ownerKernel,source,destination,sourceGrant,sourcePrepare,destinationGrant,destinationPrepare)
        {
            _input = input; _output = output;
            _read = input.ReserveForRuntime(RuntimeBufferAccess.Read);
            RuntimeBufferLease<T>? write = null;
            try { write = output.ReserveForRuntime(RuntimeBufferAccess.Write); _write = write; Backing = new(_read.ReadOnlyBytes,_write.WritableBytes); }
            catch { write?.Dispose(); _read.Dispose(); throw; }
        }
        internal override PlatformDmaCopyBacking Backing { get; }
        internal override bool Matches(object input,object output) => ReferenceEquals(_input,input) && ReferenceEquals(_output,output);
        internal override void ReleaseSourceAfterClosure() => _read.Dispose();
        public override void Dispose() { _read.Dispose(); _write.Dispose(); Consumed = true; }
    }
    private readonly Dictionary<PlatformDmaGrantId,DmaCopyPayload> _dmaCopyPayloads = [];
    private readonly HashSet<PlatformDmaGrantId> _dmaCopyPayloadCalls = [];
    private bool IsOriginalDmaCopyPayload(DmaCopyPayload payload) => ReferenceEquals(payload.Original,payload) && ReferenceEquals(payload.OwnerKernel,this);
    private bool HasOriginalDmaCopyPayloadRoute(PlatformDmaGrant grant) => _dmaCopyPayloads.All(entry =>
        entry.Key!=grant.GrantId && entry.Value.SourceGrant.GrantId!=grant.GrantId && entry.Value.DestinationGrant.GrantId!=grant.GrantId ||
        IsOriginalDmaCopyPayload(entry.Value) && entry.Key==entry.Value.SourceGrant.GrantId);

    private KernelResult<DmaCopyPayload> ReserveDmaCopyPayload<T>(ProcessHandle source, ProcessHandle destination,
        OwnedBuffer<T> input, OwnedBuffer<T> output, PlatformDmaGrant sourceGrant, PlatformDmaPrepareEvidence sourcePrepare,
        PlatformDmaGrant destinationGrant, PlatformDmaPrepareEvidence destinationPrepare, bool cleanupOnly) where T : unmanaged
    {
        lock (_platformMemoryUseGate)
        {
            if (_dmaCopyPayloads.TryGetValue(sourceGrant.GrantId,out var original))
                return IsOriginalDmaCopyPayload(original) && (!original.Consumed || cleanupOnly) && original.Matches(input,output) &&
                    original.Source == source && original.Destination == destination && original.SourceGrant == sourceGrant &&
                    original.DestinationGrant == destinationGrant && original.SourcePrepare == sourcePrepare && original.DestinationPrepare == destinationPrepare
                    ? KernelResult<DmaCopyPayload>.Ok(original) : KernelResult<DmaCopyPayload>.Fail(KernelError.StaleGeneration,"Original DMA payload reservation changed; no repeat.");
            try
            {
                _dmaCopyPayloads.EnsureCapacity(_dmaCopyPayloads.Count + 1);
                var payload = new DmaCopyPayload<T>(this,source,destination,input,output,sourceGrant,sourcePrepare,destinationGrant,destinationPrepare);
                _dmaCopyPayloads.Add(sourceGrant.GrantId,payload);
                return KernelResult<DmaCopyPayload>.Ok(payload);
            }
            catch (InvalidOperationException exception) { return KernelResult<DmaCopyPayload>.Fail(KernelError.RegionUseConflict,exception.Message); }
            catch (OutOfMemoryException) { return KernelResult<DmaCopyPayload>.Fail(KernelError.CapacityExhausted,"DMA backing reservation capacity exhausted before effect."); }
        }
    }
}
