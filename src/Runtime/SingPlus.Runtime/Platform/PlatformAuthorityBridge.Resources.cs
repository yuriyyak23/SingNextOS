using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private sealed class ResourceRecord(PlatformResourceReservation reservation)
    {
        public PlatformResourceReservation Reservation { get; } = reservation;
        public PlatformResourceSubmissionBinding? Binding { get; set; }
        public PlatformResourceUsageEvidence? LastEvidence { get; set; }
        public bool CancelledPreSubmit { get; set; }
    }

    private readonly Dictionary<PlatformResourceReservationId, ResourceRecord> _resourceReservations = [];

    internal KernelResult<PlatformResourceReservation> PrepareResource(
        PlatformDomainBinding domain,
        PlatformDomainIdentity expectedSubject,
        PlatformResourceCorrelation correlation,
        ResourceEnvelopeV1 envelope)
    {
        var domainValidation = ValidateDomain(domain, expectedSubject);
        if (!domainValidation.IsSuccess)
            return KernelResult<PlatformResourceReservation>.Fail(domainValidation.Error, domainValidation.Message!);
        if (_provider is not IPlatformResourceProvider provider ||
            !_featureManifest.Supports(PlatformFeatureFamily.ExternalResourceAccounting,
                PlatformResourceContract.ContractVersion, PlatformFeatureAvailability.Executable))
            return KernelResult<PlatformResourceReservation>.Fail(KernelError.PlatformUnsupported,
                "The platform provider does not expose executable resource contract v1.");

        var providerDomain = _domains[domain.BindingId].ProviderLease;
        var request = new PlatformResourceAdmissionRequest(PlatformResourceContract.ContractVersion,
            correlation, providerDomain, envelope);
        var requestValidation = PlatformResourceContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
            return FromProviderFailure<PlatformResourceReservation>(requestValidation.Status, requestValidation.Message);
        var result = provider.PrepareResource(request);
        if (!result.IsSuccess)
            return FromProviderFailure<PlatformResourceReservation>(result.Status, result.Message);
        var validation = PlatformResourceContract.ValidateReservation(request, result.Value!);
        if (!validation.IsSuccess)
        {
            _ = provider.CancelPreparedResource(result.Value!);
            return KernelResult<PlatformResourceReservation>.Fail(KernelError.PlatformFaulted,
                validation.Message ?? "Provider returned malformed resource reservation evidence.");
        }
        if (_resourceReservations.ContainsKey(result.Value!.ReservationId))
            return KernelResult<PlatformResourceReservation>.Fail(KernelError.DuplicateIdentity,
                "Provider reused an active resource reservation identity.");
        _resourceReservations.Add(result.Value.ReservationId, new ResourceRecord(result.Value));
        return KernelResult<PlatformResourceReservation>.Ok(result.Value);
    }

    internal KernelResult CancelPreparedResource(PlatformResourceReservation reservation)
    {
        if (!TryResolveResource(reservation, out var record, out var failure)) return failure;
        if (record.CancelledPreSubmit) return KernelResult.Ok();
        if (record.Binding is not null)
            return KernelResult.Fail(KernelError.InvalidTransition,
                "A resource reservation bound to a provider operation requires reconciliation, not cancellation.");
        var result = ((IPlatformResourceProvider)_provider!).CancelPreparedResource(reservation);
        if (!result.IsSuccess) return FromProviderFailure(result.Status, result.Message);
        record.CancelledPreSubmit = true;
        return KernelResult.Ok();
    }

    internal KernelResult<PlatformResourceSubmissionBinding> BindResourceSubmission(
        PlatformResourceReservation reservation,
        PlatformOperationIdentity operation)
    {
        if (!TryResolveResource(reservation, out var record, out var failure))
            return KernelResult<PlatformResourceSubmissionBinding>.Fail(failure.Error, failure.Message!);
        if (record.CancelledPreSubmit)
            return KernelResult<PlatformResourceSubmissionBinding>.Fail(KernelError.InvalidTransition,
                "A cancelled provider resource reservation cannot be rebound.");
        var result = ((IPlatformResourceProvider)_provider!).BindResourceSubmission(reservation, operation);
        if (!result.IsSuccess)
            return FromProviderFailure<PlatformResourceSubmissionBinding>(result.Status, result.Message);
        var validation = PlatformResourceContract.ValidateSubmissionBinding(reservation, result.Value!);
        if (!validation.IsSuccess)
            return KernelResult<PlatformResourceSubmissionBinding>.Fail(KernelError.PlatformFaulted,
                validation.Message ?? "Provider returned malformed resource submission binding.");
        if (record.Binding is { } existing && existing != result.Value)
            return KernelResult<PlatformResourceSubmissionBinding>.Fail(KernelError.StaleGeneration,
                "Provider changed an existing resource submission binding.");
        record.Binding = result.Value;
        return KernelResult<PlatformResourceSubmissionBinding>.Ok(result.Value);
    }

    internal KernelResult<PlatformResourceUsageEvidence> ReconcileResourceUsage(
        PlatformResourceSubmissionBinding binding)
    {
        if (!TryResolveResource(binding.Reservation, out var record, out var failure))
            return KernelResult<PlatformResourceUsageEvidence>.Fail(failure.Error, failure.Message!);
        if (record.Binding != binding)
            return KernelResult<PlatformResourceUsageEvidence>.Fail(KernelError.StaleGeneration,
                "Resource reconciliation binding is stale.");
        var result = ((IPlatformResourceProvider)_provider!).ReconcileResourceUsage(binding);
        if (!result.IsSuccess)
            return FromProviderFailure<PlatformResourceUsageEvidence>(result.Status, result.Message);
        var validation = PlatformResourceContract.ValidateUsageEvidence(binding, result.Value!);
        if (!validation.IsSuccess)
            return KernelResult<PlatformResourceUsageEvidence>.Fail(KernelError.PlatformFaulted,
                validation.Message ?? "Provider returned malformed resource usage evidence.");
        if (record.LastEvidence is { } previous)
        {
            if (result.Value!.ReceiptSequence < previous.ReceiptSequence)
                return KernelResult<PlatformResourceUsageEvidence>.Fail(KernelError.StaleGeneration,
                    "Provider resource evidence sequence was reordered.");
            if (result.Value.ReceiptSequence == previous.ReceiptSequence && result.Value != previous)
                return KernelResult<PlatformResourceUsageEvidence>.Fail(KernelError.PlatformFaulted,
                    "Duplicate provider resource evidence conflicts with prior evidence.");
            if (previous.IsTerminal && result.Value != previous)
                return KernelResult<PlatformResourceUsageEvidence>.Fail(KernelError.InvalidTransition,
                    "Terminal provider resource evidence cannot change.");
        }
        record.LastEvidence = result.Value;
        return KernelResult<PlatformResourceUsageEvidence>.Ok(result.Value);
    }

    private bool TryResolveResource(
        PlatformResourceReservation reservation,
        out ResourceRecord record,
        out KernelResult failure)
    {
        record = null!;
        if (_provider is not IPlatformResourceProvider ||
            !_resourceReservations.TryGetValue(reservation.ReservationId, out var candidate) ||
            candidate.Reservation != reservation)
        {
            failure = KernelResult.Fail(KernelError.StaleGeneration,
                "Platform resource reservation identity or generation is stale.");
            return false;
        }
        record = candidate;
        failure = KernelResult.Ok();
        return true;
    }
}
