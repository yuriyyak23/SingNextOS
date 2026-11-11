using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalDmaCopyCompletionProvider,IPlatformProviderIncarnationSource
{
    private static long _providerIncarnations;
    private readonly PlatformProviderIncarnation _providerIncarnation = AllocateProviderIncarnation();
    private static PlatformProviderIncarnation AllocateProviderIncarnation()
    {
        while (true)
        {
            long observed = Volatile.Read(ref _providerIncarnations);
            if (observed == long.MaxValue) throw new InvalidOperationException("Provider incarnation capacity exhausted.");
            if (Interlocked.CompareExchange(ref _providerIncarnations,observed + 1,observed) == observed)
                return new((ulong)(observed + 1));
        }
    }
    // This provider instance owns its runtime inventory. Native realm continuity is
    // checked separately by typed producers; temporary native source loss must not
    // become a permanent provider-reset fault in unrelated cleanup consumers.
    public PlatformProviderIncarnation CurrentIncarnation => _providerIncarnation;
    private sealed class ProviderCopyFact(PlatformOriginalDmaCopyRequest request,DmaGrantRecord source,DmaGrantRecord destination,
        ProviderDmaVisibilityState sourceCycle,ProviderDmaVisibilityState destinationCycle,
        NeutralOriginalDmaCopyRequest nativeRequest,PlatformProviderDmaCopySubmission submission)
    {
        public ProviderCopyFact? OwnerSelf { get; private set; }
        public void EstablishSelf() => OwnerSelf = this;
        public PlatformOriginalDmaCopyRequest Request { get; } = request;
        public DmaGrantRecord Source { get; } = source;
        public DmaGrantRecord Destination { get; } = destination;
        public ProviderDmaVisibilityState SourceCycle { get; } = sourceCycle;
        public ProviderDmaVisibilityState DestinationCycle { get; } = destinationCycle;
        public NeutralOriginalDmaCopyRequest NativeRequest { get; } = nativeRequest;
        public PlatformProviderDmaCopySubmission Submission { get; } = submission;
        public bool InFlight { get; set; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public bool Published { get; set; }
        public bool LifetimeConsumed { get; set; }
        public bool SourceCompletionObserved { get; set; }
        public bool DestinationCompletionObserved { get; set; }
        public NeutralOriginalDmaCopyObservation? Raw { get; set; }
        public NeutralOriginalDmaCopyObservation? LifetimeValidatedRaw { get; set; }
    }
    private ulong _nextProviderCopyId = 1;
    private ulong _nextProviderSubmissionId = 1;

    public PlatformAuthorityResult<PlatformProviderDmaCopySubmission> SubmitDmaCopy(PlatformProviderDmaCopySubmitRequest request) =>
        PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(PlatformAuthorityStatus.NotAccepted,"Actual copy requires original runtime backing; no effect.");
    public PlatformAuthorityResult<PlatformProviderDmaCopySubmission> SubmitDmaCopy(PlatformProviderDmaCopySubmitRequest request,PlatformDmaCopyBacking backing) => SubmitDmaCopy(new PlatformOriginalDmaCopyRequest(request,backing));

    public PlatformAuthorityResult<PlatformProviderDmaCopySubmission> SubmitDmaCopy(PlatformOriginalDmaCopyRequest request)
    {
        PlatformAuthorityResult<PlatformProviderDmaCopySubmission> Fail(string message) => PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(PlatformAuthorityStatus.Faulted,message);
        if (_runtime is not INeutralOriginalDmaCopyProvider native || _originalNativeMappingIncarnation is not { } realm)
            return PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(PlatformAuthorityStatus.NotAccepted,"Original native byte copy is unavailable; no effect.");
        PlatformAuthorityResult valid = PlatformDmaCopySubmissionContract.ValidateRequest(request.Request);
        if (!valid.IsSuccess) return PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(PlatformAuthorityStatus.NotAccepted,valid.Message!);
        ProviderCopyFact fact;
        lock (_childCreateCommitGate)
        {
            if (!_originalDmaGrants.TryGetValue(request.Request.Source.Grant.GrantId,out DmaGrantRecord? source) ||
                !_originalDmaGrants.TryGetValue(request.Request.Destination.Grant.GrantId,out DmaGrantRecord? destination)) return Fail("Original copy grant inventory absent.");
            if (!ProviderCopyHistoryIntact(source) || !ProviderCopyHistoryIntact(destination)) return Fail("Original copy history lost/copied.");
            ProviderCopyFact[] retained = source.CopyHistory.Where(old => ReferenceEquals(old.Request,request)).ToArray();
            if (retained.Length != 0)
            {
                if (retained.Length != 1 || retained[0].InFlight) return Fail("Original copy invocation pending or ambiguous.");
                fact = retained[0];
            }
            else
            {
                if (!ValidateProviderDmaGrantForVisibility(request.Request.Source.Grant).IsSuccess ||
                    !ValidateProviderDmaGrantForVisibility(request.Request.Destination.Grant).IsSuccess ||
                    source.VisibilityInFlight || destination.VisibilityInFlight ||
                    source.CopyFact is { NoEffectConsumed: false } || destination.CopyFact is { NoEffectConsumed: false } ||
                    source.VisibilityState is not { Acquired: false } a || destination.VisibilityState is not { Acquired: false } b ||
                    a.ProviderCycle != request.Request.Source.PreparedCycle || b.ProviderCycle != request.Request.Destination.PreparedCycle ||
                    source.AdmissionSource?.NativeRequest is not { } sa || destination.AdmissionSource?.NativeRequest is not { } da ||
                    OriginalPrepare(source,a) is not { } sp || OriginalPrepare(destination,b) is not { } dp ||
                    request.Backing.Source.Length != source.Grant.Range.Length || request.Backing.Destination.Length != destination.Grant.Range.Length)
                    return PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(PlatformAuthorityStatus.NotAccepted,"Original copy sources/cycles/backing unavailable; no effect.");
                if (_nextProviderCopyId is 0 or ulong.MaxValue || _nextProviderSubmissionId == 0 || _nextProviderSubmissionId >= ulong.MaxValue - 1 ||
                    source.CopyHistoryIssued == ulong.MaxValue || destination.CopyHistoryIssued == ulong.MaxValue)
                    return PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(PlatformAuthorityStatus.NotAccepted,"Copy identity/history capacity exhausted before effect.");
                source.CopyHistory.EnsureCapacity(source.CopyHistory.Count + 1); destination.CopyHistory.EnsureCapacity(destination.CopyHistory.Count + 1);
                PlatformProviderDmaCopySubmission copy = new PlatformProviderDmaCopySubmission(new(_nextProviderCopyId++),new(1),Submission(source,a),Submission(destination,b));
                NeutralOriginalDmaCopyRequest nr = new NeutralOriginalDmaCopyRequest(request.InvocationId,realm,new(sa,source.HybridCpuGrant,sp,a.NeutralCycle),
                    new(da,destination.HybridCpuGrant,dp,b.NeutralCycle),request.Backing.Source,request.Backing.Destination);
                fact = new(request,source,destination,a,b,nr,copy); fact.EstablishSelf();
                source.CopyHistory.Add(fact); destination.CopyHistory.Add(fact); source.CopyHistoryIssued++; destination.CopyHistoryIssued++;
                source.CopyFact = destination.CopyFact = fact; // Original before possible effect or callback.
            }
            if (!ProviderCopySources(fact,true)) return Fail("Original copy owner/source changed before invocation.");
            fact.InFlight = true;
        }
        try
        {
            PlatformAuthorityResult source = ValidateOriginalNativeMappingSource(); if (!source.IsSuccess) return Fail(source.Message!);
            lock (_childCreateCommitGate) if (!ProviderCopySources(fact,true)) return Fail("Copy source changed at native sentry.");
            NeutralOriginalDmaCopyObservation? raw;
            if (fact.Invoked) raw = native.ResolveDmaCopy(fact.NativeRequest);
            else
            {
                lock (_childCreateCommitGate) fact.Invoked = true;
                try { raw = native.CopyDma(fact.NativeRequest); }
                catch (Exception exception) when (exception is not StackOverflowException) { raw = native.ResolveDmaCopy(fact.NativeRequest); }
            }
            lock (_childCreateCommitGate)
            {
                if (fact.Raw is { } retained && !ReferenceEquals(retained,raw)) return Fail("Original raw changed during pure retry; retained, no repeat.");
                fact.Raw = raw; // Before source callbacks, including malformed raw.
            }
            if (!ValidCopyRaw(fact,raw)) return Fail("Original native copy outcome absent/foreign/malformed; no repeat.");
            if (raw!.NoEffectGuaranteed)
            {
                if (!ReferenceEquals(native.ResolveDmaCopy(fact.NativeRequest),raw)) return Fail("No-effect raw does not match original producing fact.");
                if (!ValidateOriginalNativeMappingSource().IsSuccess) return Fail("Exact no-effect lost original source.");
                lock (_childCreateCommitGate)
                {
                    if (!ProviderCopySources(fact,true)) return Fail("Exact no-effect source changed before consumption.");
                    fact.NoEffectConsumed = true;
                    fact.Request.ReleaseBacking();
                    return PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Fail(PlatformAuthorityStatus.NotAccepted,"Exact original copy no-effect consumed.");
                }
            }
            if (!raw.Completed || !raw.NoFurtherByteUseGuaranteed) return Fail("Original copy did not prove completion/no-further-byte-use.");
            if (!ValidateOriginalNativeMappingSource().IsSuccess) return Fail("Original copy source changed after byte effect.");
            NeutralOriginalDmaCopyObservation? current = native.ValidateDmaCopy(fact.NativeRequest);
            if (!ReferenceEquals(current,raw) || !ValidateOriginalNativeMappingSource().IsSuccess) return Fail("Original native copy current cycles changed.");
            NeutralOriginalDmaCopyObservation? final = native.ValidateDmaCopy(fact.NativeRequest);
            if (!ValidateOriginalNativeMappingSource().IsSuccess) return Fail("Original native source lost before final copy publication.");
            lock (_childCreateCommitGate)
            {
                if (!ReferenceEquals(final,raw) || !ProviderCopySources(fact,true)) return Fail("Original copy final publication source changed.");
                fact.Published = true;
                fact.Request.ReleaseBacking();
                return PlatformAuthorityResult<PlatformProviderDmaCopySubmission>.Ok(fact.Submission);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException) { return Fail($"Original copy retained for pure reconciliation: {exception.GetType().Name}."); }
        finally { lock (_childCreateCommitGate) fact.InFlight = false; }
    }

    public PlatformAuthorityResult<PlatformOriginalDmaCopyResolution> ResolveDmaCopy(PlatformOriginalDmaCopyRequest request)
    {
        PlatformAuthorityResult<PlatformOriginalDmaCopyResolution> Fail() => PlatformAuthorityResult<PlatformOriginalDmaCopyResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original copy resolution unavailable; no effect repeated.");
        ProviderCopyFact fact;
        lock (_childCreateCommitGate)
        {
            if (_runtime is not INeutralOriginalDmaCopyProvider || !_originalDmaGrants.TryGetValue(request.Request.Source.Grant.GrantId,out DmaGrantRecord? source) || !ProviderCopyHistoryIntact(source)) return Fail();
            ProviderCopyFact[] facts = source.CopyHistory.Where(old => ReferenceEquals(old.Request,request)).ToArray();
            if (facts.Length != 1 || facts[0].InFlight || !facts[0].Invoked || !ProviderCopySources(facts[0],false)) return Fail();
            fact = facts[0]; fact.InFlight = true;
        }
        try
        {
            if (!ValidateOriginalNativeMappingSource().IsSuccess) return Fail();
            INeutralOriginalDmaCopyProvider native = (INeutralOriginalDmaCopyProvider)_runtime;
            NeutralOriginalDmaCopyObservation? raw = native.ResolveDmaCopy(fact.NativeRequest);
            lock (_childCreateCommitGate)
            {
                if (fact.Raw is { } retained && !ReferenceEquals(retained,raw)) return Fail();
                fact.Raw = raw;
            }
            if (!ValidCopyRaw(fact,raw) || !ValidateOriginalNativeMappingSource().IsSuccess) return Fail();
            // An unpublished receipt still needs current admission. Completed cleanup lookup
            // does not acquire a new active permission after publication/closure.
            bool needsPublication = !fact.Published && raw is { Completed: true };
            NeutralOriginalDmaCopyObservation? final = needsPublication ? native.ValidateDmaCopy(fact.NativeRequest) : native.ResolveDmaCopy(fact.NativeRequest);
            if (!ValidateOriginalNativeMappingSource().IsSuccess) return Fail();
            lock (_childCreateCommitGate)
            {
                if (!ReferenceEquals(final,raw) || !ProviderCopySources(fact,needsPublication)) return Fail();
                if (raw!.NoEffectGuaranteed) { fact.NoEffectConsumed = true; request.ReleaseBacking(); return PlatformAuthorityResult<PlatformOriginalDmaCopyResolution>.Ok(new(request,true,null)); }
                if (!raw.Completed || !raw.NoFurtherByteUseGuaranteed) return Fail();
                fact.Published = true;
                request.ReleaseBacking();
                return PlatformAuthorityResult<PlatformOriginalDmaCopyResolution>.Ok(new(request,false,fact.Submission));
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException) { return Fail(); }
        finally { lock (_childCreateCommitGate) fact.InFlight = false; }
    }

    public PlatformAuthorityResult<PlatformOriginalDmaCopyLifetime> ResolveDmaCopyLifetime(PlatformOriginalDmaCopyRequest request)
    {
        PlatformAuthorityResult<PlatformOriginalDmaCopyLifetime> Fail() => PlatformAuthorityResult<PlatformOriginalDmaCopyLifetime>.Fail(PlatformAuthorityStatus.Faulted,"Original byte lifetime unresolved; no repeat.");
        ProviderCopyFact fact;
        lock (_childCreateCommitGate)
        {
            if (_runtime is not INeutralOriginalDmaCopyProvider || !_originalDmaGrants.TryGetValue(request.Request.Source.Grant.GrantId,out DmaGrantRecord? source) || !ProviderCopyHistoryIntact(source)) return Fail();
            ProviderCopyFact[] retained = source.CopyHistory.Where(copy => ReferenceEquals(copy.Request,request)).ToArray();
            if (retained.Length != 1 || retained[0].InFlight || !retained[0].Invoked || !ProviderCopySources(retained[0],false)) return Fail();
            fact = retained[0];
            if (fact.LifetimeConsumed) return ValidCopyRaw(fact,fact.Raw) && fact.Raw!.NoFurtherByteUseGuaranteed &&
                ReferenceEquals(fact.Raw,fact.LifetimeValidatedRaw)
                ? PlatformAuthorityResult<PlatformOriginalDmaCopyLifetime>.Ok(new(request,true)) : Fail();
            if (!Cycles()) return Fail();
            fact.InFlight = true;
        }
        try
        {
            if (!ValidateOriginalNativeMappingSource().IsSuccess) return Fail();
            INeutralOriginalDmaCopyProvider native = (INeutralOriginalDmaCopyProvider)_runtime;
            NeutralOriginalDmaCopyObservation? raw = native.ResolveDmaCopy(fact.NativeRequest);
            lock (_childCreateCommitGate)
            {
                if (fact.Raw is { } old && !ReferenceEquals(old,raw)) return Fail();
                fact.Raw = raw; // Before any source callback.
            }
            if (!ValidCopyRaw(fact,raw) || !raw!.NoFurtherByteUseGuaranteed || !ValidateOriginalNativeMappingSource().IsSuccess) return Fail();
            NeutralOriginalDmaCopyObservation? final = native.ResolveDmaCopy(fact.NativeRequest);
            if (!ValidateOriginalNativeMappingSource().IsSuccess) return Fail();
            lock (_childCreateCommitGate)
            {
                if (!ReferenceEquals(final,raw) || !ReferenceEquals(fact.Raw,raw) || !ProviderCopySources(fact,false) || !Cycles()) return Fail();
                fact.LifetimeValidatedRaw=raw;
                fact.LifetimeConsumed = true;
                request.ReleaseBacking();
                return PlatformAuthorityResult<PlatformOriginalDmaCopyLifetime>.Ok(new(request,true));
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException) { return Fail(); }
        finally { lock (_childCreateCommitGate) fact.InFlight = false; }
        bool Cycles() => (fact.Source.Revoked || _dmaVisibilityStates.TryGetValue(fact.Source.Grant.GrantId,out ProviderDmaVisibilityState? sourceCycle) && ReferenceEquals(sourceCycle,fact.SourceCycle)) &&
            (fact.Destination.Revoked || _dmaVisibilityStates.TryGetValue(fact.Destination.Grant.GrantId,out ProviderDmaVisibilityState? destinationCycle) && ReferenceEquals(destinationCycle,fact.DestinationCycle));
    }

    public PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence> ObserveOriginalDmaCopyCompletion(PlatformOriginalDmaCopyRequest request,PlatformProviderDmaSubmission submission)
    {
        lock (_childCreateCommitGate)
        {
            if (!_originalDmaGrants.TryGetValue(submission.GrantId,out DmaGrantRecord? grant) || grant.CopyFact is not { } fact ||
                !ReferenceEquals(fact.Request,request) || !ProviderCopySources(fact,false) ||
                submission != (ReferenceEquals(grant,fact.Source) ? fact.Submission.SourceSubmission : fact.Submission.DestinationSubmission))
                return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Fail(PlatformAuthorityStatus.Faulted,"Original copy completion lookup identity changed.");
        }
        return ObserveDmaCompletion(submission); // Existing original native lookup; never a new byte effect.
    }

    public PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence> ObserveDmaCompletion(PlatformProviderDmaSubmission submission)
    {
        PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence> Fail() => PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Fail(PlatformAuthorityStatus.Faulted,"Original copy completion source unavailable.");
        ProviderCopyFact fact;
        lock (_childCreateCommitGate)
        {
            if (!_originalDmaGrants.TryGetValue(submission.GrantId,out DmaGrantRecord? grant) || grant.CopyFact is not { } copy ||
                copy.InFlight || !ProviderCopySources(copy,false) || submission != (ReferenceEquals(grant,copy.Source) ? copy.Submission.SourceSubmission : copy.Submission.DestinationSubmission)) return Fail();
            fact = copy;
        }
        PlatformAuthorityResult<PlatformOriginalDmaCopyResolution> resolution = ResolveDmaCopy(fact.Request);
        if (!resolution.IsSuccess || resolution.Value.NoEffectGuaranteed) return Fail();
        lock (_childCreateCommitGate)
        {
            if (!ProviderCopySources(fact,false) || fact.Raw is not { Completed: true, NoFurtherByteUseGuaranteed: true }) return Fail();
            if (submission == fact.Submission.SourceSubmission) fact.SourceCompletionObserved = true; else fact.DestinationCompletionObserved = true;
            return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Ok(new(submission.SubmissionId,submission.Generation,submission.GrantId,submission.GrantGeneration,
                submission.PreparedCycle,submission.Range,submission.Direction,PlatformProviderDmaCompletionState.Completed));
        }
    }

    private PlatformProviderDmaSubmission Submission(DmaGrantRecord grant,ProviderDmaVisibilityState cycle) =>
        new(new(_nextProviderSubmissionId++),new(1),grant.Grant.GrantId,grant.Grant.Generation,cycle.ProviderCycle,grant.Grant.Range,grant.Grant.Direction);
    private static NeutralOriginalDmaVisibilityRequest? OriginalPrepare(DmaGrantRecord grant,ProviderDmaVisibilityState cycle) =>
        grant.VisibilityHistory.LastOrDefault(old => old.NativeObservation is { Prepare: { IsPrepared: true } } raw && raw.Prepare.Evidence.Cycle == cycle.NeutralCycle)?.NativeRequest;
    private static bool ValidCopyRaw(ProviderCopyFact fact,NeutralOriginalDmaCopyObservation? raw) => raw is not null &&
        ReferenceEquals(raw.Request,fact.NativeRequest) && raw.SourceFenceMatched && !(raw.NoEffectGuaranteed && raw.Completed) &&
        (!raw.NoEffectGuaranteed || raw.NoFurtherByteUseGuaranteed);
    private static bool ProviderCopyHistoryIntact(DmaGrantRecord grant) => ReferenceEquals(grant.OwnerSelf,grant) &&
        (ulong)grant.CopyHistory.Count == grant.CopyHistoryIssued && grant.CopyHistory.All(copy => ReferenceEquals(copy.OwnerSelf,copy) &&
            (ReferenceEquals(copy.Source,grant) || ReferenceEquals(copy.Destination,grant)));
    private bool ProviderCopySources(ProviderCopyFact fact,bool active) => ReferenceEquals(fact.OwnerSelf,fact) &&
        ProviderCopyHistoryIntact(fact.Source) && ProviderCopyHistoryIntact(fact.Destination) &&
        fact.Source.CopyHistory.Count(old => ReferenceEquals(old,fact)) == 1 && fact.Destination.CopyHistory.Count(old => ReferenceEquals(old,fact)) == 1 &&
        ReferenceEquals(fact.Source.CopyFact,fact) && ReferenceEquals(fact.Destination.CopyFact,fact) &&
        _originalDmaGrants.TryGetValue(fact.Source.Grant.GrantId,out DmaGrantRecord? source) && ReferenceEquals(source,fact.Source) &&
        _originalDmaGrants.TryGetValue(fact.Destination.Grant.GrantId,out DmaGrantRecord? destination) && ReferenceEquals(destination,fact.Destination) &&
        _dmaGrants.TryGetValue(source.Grant.GrantId,out DmaGrantRecord? sr) && ReferenceEquals(sr,source) && _dmaGrants.TryGetValue(destination.Grant.GrantId,out DmaGrantRecord? dr) && ReferenceEquals(dr,destination) &&
        IsCurrentDmaBindSource(source) && IsCurrentDmaBindSource(destination) &&
        (!active || ReferenceEquals(source.VisibilityState,fact.SourceCycle) && ReferenceEquals(destination.VisibilityState,fact.DestinationCycle) &&
            _dmaVisibilityStates.TryGetValue(source.Grant.GrantId,out ProviderDmaVisibilityState? sc) && ReferenceEquals(sc,fact.SourceCycle) &&
            _dmaVisibilityStates.TryGetValue(destination.Grant.GrantId,out ProviderDmaVisibilityState? dc) && ReferenceEquals(dc,fact.DestinationCycle) &&
            ValidateProviderDmaGrantForVisibility(source.Grant).IsSuccess && ValidateProviderDmaGrantForVisibility(destination.Grant).IsSuccess &&
            !fact.SourceCycle.Acquired && !fact.DestinationCycle.Acquired);
}
