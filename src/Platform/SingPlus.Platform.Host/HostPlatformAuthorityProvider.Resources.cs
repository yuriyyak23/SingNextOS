using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Platform.Host;

public sealed partial class HostPlatformAuthorityProvider
{
    private enum ResourceReservationState
    {
        Prepared,
        Bound,
        Cancelled,
        Terminal,
    }

    private sealed class ResourceReservationRecord(PlatformResourceReservation reservation)
    {
        public PlatformResourceReservation Reservation { get; } = reservation;
        public ResourceReservationState State { get; set; }
        public PlatformResourceSubmissionBinding? Binding { get; set; }
        public PlatformResourceUsageEvidence? TerminalEvidence { get; set; }
        public ulong NextReceiptSequence { get; set; } = 1;
    }

    private readonly Dictionary<PlatformResourceReservationId, ResourceReservationRecord> _resourceReservations = [];
    private ulong _nextResourceReservationId = 1;

    public int PrepareResourceCallCount { get; private set; }
    public int BindResourceSubmissionCallCount { get; private set; }
    public int ReconcileResourceUsageCallCount { get; private set; }

    public PlatformAuthorityResult<PlatformResourceReservation> PrepareResource(
        PlatformResourceAdmissionRequest request)
    {
        PrepareResourceCallCount++;
        var validation = PlatformResourceContract.ValidateRequest(request);
        if (!validation.IsSuccess)
            return PlatformAuthorityResult<PlatformResourceReservation>.Fail(validation.Status, validation.Message!);
        var domain = ValidateDomain(request.DomainLease);
        if (!domain.IsSuccess)
            return PlatformAuthorityResult<PlatformResourceReservation>.Fail(domain.Status, domain.Message!);
        if (!_featureManifest.Supports(PlatformFeatureFamily.ExternalResourceAccounting,
                PlatformResourceContract.ContractVersion, PlatformFeatureAvailability.Executable))
            return PlatformAuthorityResult<PlatformResourceReservation>.Fail(PlatformAuthorityStatus.Unsupported,
                "Host resource accounting is not enabled for this provider profile.");
        if (request.Envelope.Family != ResourceDimensionFamilyV1.Time ||
            request.Envelope.ResourceClass != ResourceClassV1.ComputeTime ||
            request.Envelope.Unit != ResourceUnitV1.Nanoseconds ||
            !string.Equals(request.Envelope.SemanticScope, "host:compute-v1", StringComparison.Ordinal))
            return PlatformAuthorityResult<PlatformResourceReservation>.Fail(PlatformAuthorityStatus.Unsupported,
                "The host executable resource profile supports only host:compute-v1 ComputeTime/Nanoseconds.");
        if (_nextResourceReservationId == 0)
            return PlatformAuthorityResult<PlatformResourceReservation>.Fail(PlatformAuthorityStatus.Faulted,
                "Host resource reservation identity space is exhausted.");

        var id = _nextResourceReservationId;
        _nextResourceReservationId = id == ulong.MaxValue ? 0 : id + 1;
        var reservation = new PlatformResourceReservation(PlatformResourceContract.ContractVersion,
            new PlatformResourceReservationId(id), new PlatformResourceReservationGeneration(1),
            request.Correlation, request.DomainLease, request.Envelope);
        _resourceReservations.Add(reservation.ReservationId, new ResourceReservationRecord(reservation));
        return PlatformAuthorityResult<PlatformResourceReservation>.Ok(reservation);
    }

    public PlatformAuthorityResult CancelPreparedResource(PlatformResourceReservation reservation)
    {
        if (!TryResolveResource(reservation, out var record, out var failure)) return failure;
        if (record.State == ResourceReservationState.Cancelled) return PlatformAuthorityResult.Ok();
        if (record.State != ResourceReservationState.Prepared)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "Only a resource reservation that has not been bound to a submission may be cancelled.");
        record.State = ResourceReservationState.Cancelled;
        return PlatformAuthorityResult.Ok();
    }

    public PlatformAuthorityResult<PlatformResourceSubmissionBinding> BindResourceSubmission(
        PlatformResourceReservation reservation,
        PlatformOperationIdentity operation)
    {
        BindResourceSubmissionCallCount++;
        if (!TryResolveResource(reservation, out var record, out var failure))
            return PlatformAuthorityResult<PlatformResourceSubmissionBinding>.Fail(failure.Status, failure.Message!);
        if (record.Binding is { } existing)
            return existing.Operation == operation
                ? PlatformAuthorityResult<PlatformResourceSubmissionBinding>.Ok(existing)
                : PlatformAuthorityResult<PlatformResourceSubmissionBinding>.Fail(PlatformAuthorityStatus.Stale,
                    "The host resource reservation is already bound to another operation generation.");
        if (record.State != ResourceReservationState.Prepared)
            return PlatformAuthorityResult<PlatformResourceSubmissionBinding>.Fail(PlatformAuthorityStatus.Denied,
                "The host resource reservation is no longer bindable.");
        var operationValidation = ValidateOperation(operation);
        if (!operationValidation.IsSuccess)
            return PlatformAuthorityResult<PlatformResourceSubmissionBinding>.Fail(
                operationValidation.Status, operationValidation.Message!);
        var binding = new PlatformResourceSubmissionBinding(PlatformResourceContract.ContractVersion,
            reservation, operation);
        var validation = PlatformResourceContract.ValidateSubmissionBinding(reservation, binding);
        if (!validation.IsSuccess)
            return PlatformAuthorityResult<PlatformResourceSubmissionBinding>.Fail(validation.Status, validation.Message!);
        record.Binding = binding;
        record.State = ResourceReservationState.Bound;
        return PlatformAuthorityResult<PlatformResourceSubmissionBinding>.Ok(binding);
    }

    public PlatformAuthorityResult<PlatformResourceUsageEvidence> ReconcileResourceUsage(
        PlatformResourceSubmissionBinding binding)
    {
        ReconcileResourceUsageCallCount++;
        if (!TryResolveResource(binding.Reservation, out var record, out var failure))
            return PlatformAuthorityResult<PlatformResourceUsageEvidence>.Fail(failure.Status, failure.Message!);
        if (record.Binding != binding)
            return PlatformAuthorityResult<PlatformResourceUsageEvidence>.Fail(PlatformAuthorityStatus.Stale,
                "The host resource usage request does not match the exact submission binding.");
        if (record.TerminalEvidence is { } terminal)
            return PlatformAuthorityResult<PlatformResourceUsageEvidence>.Ok(terminal);

        var operation = ObserveCompletion(binding.Operation);
        if (!operation.IsSuccess)
            return PlatformAuthorityResult<PlatformResourceUsageEvidence>.Fail(operation.Status, operation.Message!);
        var state = operation.Value!.ProvesClosure
            ? PlatformResourceReconciliationState.ConservativeWorstCase
            : PlatformResourceReconciliationState.Pending;
        var amount = state == PlatformResourceReconciliationState.ConservativeWorstCase
            ? binding.Reservation.Envelope.Amount
            : 0UL;
        var sequence = record.NextReceiptSequence++;
        var evidence = new PlatformResourceUsageEvidence(PlatformResourceContract.ContractVersion,
            binding.Reservation, binding.Operation, state, amount, sequence);
        var validation = PlatformResourceContract.ValidateUsageEvidence(binding, evidence);
        if (!validation.IsSuccess)
            return PlatformAuthorityResult<PlatformResourceUsageEvidence>.Fail(validation.Status, validation.Message!);
        if (evidence.IsTerminal)
        {
            record.TerminalEvidence = evidence;
            record.State = ResourceReservationState.Terminal;
        }
        return PlatformAuthorityResult<PlatformResourceUsageEvidence>.Ok(evidence);
    }

    private bool TryResolveResource(
        PlatformResourceReservation reservation,
        out ResourceReservationRecord record,
        out PlatformAuthorityResult failure)
    {
        if (!_resourceReservations.TryGetValue(reservation.ReservationId, out record!))
        {
            failure = PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale,
                "Host resource reservation identity is stale.");
            return false;
        }
        if (record.Reservation.Generation != reservation.Generation || record.Reservation != reservation)
        {
            failure = PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale,
                "Host resource reservation generation or semantic envelope is stale.");
            return false;
        }
        failure = PlatformAuthorityResult.Ok();
        return true;
    }
}
