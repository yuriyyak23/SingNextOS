using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum V6DurabilityFaultPoint
{
    None = 0,
    CrashAfterWrite,
    TornData,
    CrashAfterDataPersist,
    TornMetadata,
    CrashAfterMetadataPersist,
    ProviderLossBeforeDurable,
    CrashAfterDurableBeforePublication,
}

internal sealed record V6DurableRecoverySnapshot(
    ProviderPersistEvidenceV1? LastDurable,
    RecoveryFreshnessRecord? Freshness,
    bool HasTornOrUncommittedState,
    bool HasAmbiguousPublication = false);

/// <summary>Deterministic crash model for one named managed provider. It makes no physical-media claim.</summary>
internal sealed class V6ManagedDurableOutputModel
{
    private sealed class DurableRecord(
        ProviderPersistEvidenceV1 evidence,
        ulong runtimeGeneration,
        ulong processGeneration,
        ulong providerAdmissionGeneration)
    {
        internal ProviderPersistEvidenceV1 Evidence { get; set; } = evidence;
        internal ulong RuntimeGeneration { get; set; } = runtimeGeneration;
        internal ulong ProcessGeneration { get; set; } = processGeneration;
        internal ulong ProviderAdmissionGeneration { get; set; } = providerAdmissionGeneration;
        internal bool RequiresFreshAdmission { get; set; }
        internal bool PublicationAmbiguous { get; set; }
    }

    internal const string QualifiedProviderIdentity = "singnext:managed-durable-output-model";
    private readonly object _sync = new();
    private readonly PersistenceSemanticsV1 _semantics;
    private readonly Dictionary<(string Correlation, ulong Generation), DurableRecord> _durable = [];
    private ulong _sequence;
    private ProviderPersistEvidenceV1? _lastDurable;
    private bool _hasUncommitted;
    private bool _publishing;
    private (string Correlation, ulong Generation)? _publishingKey;
    private ulong _recoveryEpoch;

    internal V6ManagedDurableOutputModel(PersistenceSemanticsV1 semantics)
    {
        _semantics = semantics.Validate();
        if (_semantics.ProviderIdentity != QualifiedProviderIdentity ||
            _semantics.DomainClass != PersistenceDomainClassV1.NamedManagedModel)
            throw new NotSupportedException("Only the named managed durability model is executable in this contour.");
    }

    internal KernelResult<ProviderPersistEvidenceV1> PersistAndPublish(
        DurableOutputBindingV1 binding,
        ulong priorRuntimeGeneration,
        ulong priorProcessGeneration,
        ulong priorProviderAdmissionGeneration,
        Func<ulong> currentProviderGeneration,
        Func<ulong> currentMediaGeneration,
        Func<PersistenceDomainClassV1> currentDomain,
        Func<KernelResult> publish,
        V6DurabilityFaultPoint fault = V6DurabilityFaultPoint.None)
    {
        ArgumentNullException.ThrowIfNull(currentProviderGeneration);
        ArgumentNullException.ThrowIfNull(currentMediaGeneration);
        ArgumentNullException.ThrowIfNull(currentDomain);
        ArgumentNullException.ThrowIfNull(publish);
        try { binding.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<ProviderPersistEvidenceV1>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (binding.Semantics != _semantics || priorRuntimeGeneration == 0 || priorProcessGeneration == 0 || priorProviderAdmissionGeneration == 0)
            return KernelResult<ProviderPersistEvidenceV1>.Fail(KernelError.StaleGeneration, "The persistence binding or recovery tuple is stale.");

        ProviderPersistEvidenceV1 evidence;
        ulong publicationEpoch;
        lock (_sync)
        {
            var key = (binding.OperationCorrelation, binding.OperationGeneration);
            if (_durable.TryGetValue(key, out var existing))
            {
                evidence = existing.Evidence;
                var match = PersistEvidenceMatcherV1.Match(binding, evidence);
                if (match != PersistEvidenceMatchCodeV1.Exact)
                    return KernelResult<ProviderPersistEvidenceV1>.Fail(
                        match == PersistEvidenceMatchCodeV1.ContentMismatch
                            ? KernelError.ReplayDiverged : KernelError.StaleGeneration,
                        $"Duplicate durable operation changed its exact binding: {match}.");
                if (evidence.PublishedSequence != 0)
                    return KernelResult<ProviderPersistEvidenceV1>.Ok(evidence);
                if (existing.PublicationAmbiguous)
                    return KernelResult<ProviderPersistEvidenceV1>.Fail(KernelError.ExternalEffectUncontained,
                        "Publication outcome is ambiguous and requires explicit owner reconciliation.");
                if (existing.RequiresFreshAdmission &&
                    (priorRuntimeGeneration <= existing.RuntimeGeneration ||
                     priorProcessGeneration <= existing.ProcessGeneration ||
                     priorProviderAdmissionGeneration <= existing.ProviderAdmissionGeneration))
                    return KernelResult<ProviderPersistEvidenceV1>.Fail(KernelError.StaleGeneration,
                        "Crash recovery requires fresh runtime, process, and provider admission generations.");
                var current = ValidateCurrentPersistence(currentProviderGeneration,
                    currentMediaGeneration, currentDomain);
                if (!current.IsSuccess)
                    return KernelResult<ProviderPersistEvidenceV1>.Fail(current.Error, current.Message!);
                if (existing.RequiresFreshAdmission)
                {
                    existing.RuntimeGeneration = priorRuntimeGeneration;
                    existing.ProcessGeneration = priorProcessGeneration;
                    existing.ProviderAdmissionGeneration = priorProviderAdmissionGeneration;
                    existing.RequiresFreshAdmission = false;
                }
            }
            else
            {
                var write = Next();
                _hasUncommitted = true;
                if (fault is V6DurabilityFaultPoint.CrashAfterWrite or V6DurabilityFaultPoint.TornData)
                    return Crash("Crash or torn data before the data persist barrier.");
                var data = Next();
                if (fault == V6DurabilityFaultPoint.CrashAfterDataPersist)
                    return Crash("Crash after data persistence but before metadata persistence.");
                if (fault == V6DurabilityFaultPoint.TornMetadata)
                    return Crash("Torn metadata cannot form a durable commit.");
                var metadata = Next();
                if (fault == V6DurabilityFaultPoint.CrashAfterMetadataPersist)
                    return Crash("Crash before the durable commit record.");
                if (fault == V6DurabilityFaultPoint.ProviderLossBeforeDurable)
                    return Crash("Provider was lost before durable confirmation.");

                var current = ValidateCurrentPersistence(currentProviderGeneration,
                    currentMediaGeneration, currentDomain);
                if (!current.IsSuccess)
                    return Crash(current.Message!, current.Error);

                evidence = new(1, _semantics.ProviderIdentity, _semantics.MediaIdentity,
                    binding.OperationCorrelation, binding.ContentDigest, binding.MetadataDigest,
                    _semantics.DomainClass, PersistEvidenceAssuranceV1.ModelOnly,
                    _semantics.ProviderGeneration, _semantics.MediaGeneration,
                    binding.OperationGeneration, binding.RecoveryGeneration,
                    write, data, metadata, Next(), 0);
                evidence.Validate();
                _durable.Add(key, new(evidence, priorRuntimeGeneration,
                    priorProcessGeneration, priorProviderAdmissionGeneration));
                _lastDurable = evidence;
                _hasUncommitted = false;
                if (fault == V6DurabilityFaultPoint.CrashAfterDurableBeforePublication)
                {
                    _durable[key].RequiresFreshAdmission = true;
                    return KernelResult<ProviderPersistEvidenceV1>.Fail(KernelError.PlatformUnavailable,
                        "Crash after durable commit and before publication.");
                }
            }
            if (_publishing)
                return KernelResult<ProviderPersistEvidenceV1>.Fail(KernelError.InvalidTransition, "Publication is already in progress.");
            _publishing = true;
            _publishingKey = key;
            publicationEpoch = _recoveryEpoch;
        }

        KernelResult publication;
        try { publication = publish(); }
        catch (Exception exception) { publication = KernelResult.Fail(KernelError.PlatformUnavailable, exception.Message); }
        // A successful callback may have had an external effect. Observe the
        // provider/media/domain tuple again before recording publication.
        var afterPublication = publication.IsSuccess
            ? ValidateCurrentPersistence(currentProviderGeneration,
                currentMediaGeneration, currentDomain)
            : KernelResult.Ok();
        lock (_sync)
        {
            _publishing = false;
            _publishingKey = null;
            var record = _durable[(binding.OperationCorrelation, binding.OperationGeneration)];
            if (_recoveryEpoch != publicationEpoch || !publication.IsSuccess ||
                !afterPublication.IsSuccess)
            {
                record.PublicationAmbiguous = true;
                record.RequiresFreshAdmission = true;
                return KernelResult<ProviderPersistEvidenceV1>.Fail(
                    _recoveryEpoch != publicationEpoch ? KernelError.ExternalEffectUncontained :
                        publication.IsSuccess ? afterPublication.Error : publication.Error,
                    _recoveryEpoch != publicationEpoch
                        ? "Recovery crossed a possible publication effect; explicit owner reconciliation is required."
                        : publication.IsSuccess ? afterPublication.Message! : publication.Message!);
            }
            evidence = evidence with { PublishedSequence = Next() };
            record.Evidence = evidence;
            if (_lastDurable is null || evidence.DurableSequence >= _lastDurable.Value.DurableSequence)
                _lastDurable = evidence;
            return KernelResult<ProviderPersistEvidenceV1>.Ok(evidence);
        }
    }

    internal V6DurableRecoverySnapshot Recover()
    {
        lock (_sync)
        {
            _recoveryEpoch++;
            if (_publishingKey is { } key)
                _durable[key].PublicationAmbiguous = true;
            var torn = _hasUncommitted;
            _hasUncommitted = false;
            foreach (var record in _durable.Values)
                if (record.Evidence.PublishedSequence == 0)
                    record.RequiresFreshAdmission = true;
            if (_lastDurable is not { } evidence)
                return new(null, null, torn, _durable.Values.Any(record => record.PublicationAmbiguous));
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{evidence.ProviderIdentity}|{evidence.MediaIdentity}|{evidence.OperationCorrelation}|{evidence.OperationGeneration}|{evidence.DurableSequence}")));
            var admission = _durable[(evidence.OperationCorrelation, evidence.OperationGeneration)];
            return new(evidence, new(1, evidence.OperationCorrelation, digest,
                admission.RuntimeGeneration, admission.ProcessGeneration,
                admission.ProviderAdmissionGeneration,
                evidence.RecoveryGeneration), torn,
                _durable.Values.Any(record => record.PublicationAmbiguous));
        }
    }

    internal KernelResult ReconcileAmbiguousPublication(
        DurableOutputBindingV1 binding, Func<KernelResult> confirmNoPublication)
    {
        ArgumentNullException.ThrowIfNull(confirmNoPublication);
        try { binding.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult.Fail(KernelError.InvalidMessage, exception.Message); }
        var key = (binding.OperationCorrelation, binding.OperationGeneration);
        ProviderPersistEvidenceV1 observed;
        ulong observedEpoch;
        lock (_sync)
        {
            if (_publishing || !_durable.TryGetValue(key, out var record) ||
                !record.PublicationAmbiguous ||
                PersistEvidenceMatcherV1.Match(binding, record.Evidence) != PersistEvidenceMatchCodeV1.Exact)
                return KernelResult.Fail(KernelError.InvalidTransition,
                    "Exact ambiguous publication must be idle before reconciliation.");
            observed = record.Evidence;
            observedEpoch = _recoveryEpoch;
        }
        KernelResult closure;
        try { closure = confirmNoPublication(); }
        catch (Exception exception)
        { return KernelResult.Fail(KernelError.PlatformDenied, exception.Message); }
        if (!closure.IsSuccess) return closure;
        lock (_sync)
        {
            if (_publishing || _recoveryEpoch != observedEpoch ||
                !_durable.TryGetValue(key, out var record) ||
                !record.PublicationAmbiguous || record.Evidence != observed)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Publication changed while closure was confirmed.");
            record.PublicationAmbiguous = false;
            record.RequiresFreshAdmission = true;
            return KernelResult.Ok();
        }
    }

    private ulong Next() => ++_sequence;

    private KernelResult ValidateCurrentPersistence(
        Func<ulong> currentProviderGeneration,
        Func<ulong> currentMediaGeneration,
        Func<PersistenceDomainClassV1> currentDomain)
    {
        try
        {
            if (currentProviderGeneration() != _semantics.ProviderGeneration ||
                currentMediaGeneration() != _semantics.MediaGeneration)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Provider or media generation changed before durable publication.");
            if (currentDomain() != _semantics.DomainClass)
                return KernelResult.Fail(KernelError.PlatformDenied,
                    "Persistence domain was downgraded before durable publication.");
            return KernelResult.Ok();
        }
        catch (Exception exception)
        { return KernelResult.Fail(KernelError.PlatformUnavailable, exception.Message); }
    }

    private KernelResult<ProviderPersistEvidenceV1> Crash(string message, KernelError error = KernelError.PlatformUnavailable) =>
        KernelResult<ProviderPersistEvidenceV1>.Fail(error, message);
}
