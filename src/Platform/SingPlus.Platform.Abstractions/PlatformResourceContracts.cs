using SingPlus.Contracts;

namespace SingPlus.Platform;

public readonly record struct PlatformResourceCorrelationId(ulong Value);
public readonly record struct PlatformResourceCorrelationGeneration(ulong Value);
public readonly record struct PlatformResourceReservationId(ulong Value);
public readonly record struct PlatformResourceReservationGeneration(ulong Value);

/// <summary>
/// Evidence-only correlation chosen by the caller. It is not a capability, a
/// budget lease, or permission to submit an operation.
/// </summary>
public readonly record struct PlatformResourceCorrelation(
    PlatformResourceCorrelationId CorrelationId,
    PlatformResourceCorrelationGeneration Generation);

public readonly record struct PlatformResourceAdmissionRequest(
    uint ContractVersion,
    PlatformResourceCorrelation Correlation,
    PlatformProviderDomainLease DomainLease,
    ResourceEnvelopeV1 Envelope);

/// <summary>
/// Provider reservation evidence. Capacity and effect authority remain owned by
/// their respective runtime owners; copying this value grants no authority.
/// </summary>
public readonly record struct PlatformResourceReservation(
    uint ContractVersion,
    PlatformResourceReservationId ReservationId,
    PlatformResourceReservationGeneration Generation,
    PlatformResourceCorrelation Correlation,
    PlatformProviderDomainLease DomainLease,
    ResourceEnvelopeV1 Envelope);

public readonly record struct PlatformResourceSubmissionBinding(
    uint ContractVersion,
    PlatformResourceReservation Reservation,
    PlatformOperationIdentity Operation);

public enum PlatformResourceReconciliationState : byte
{
    Pending = 0,
    ExactUsage = 1,
    ContainedWithoutConsumption = 2,
    ConservativeWorstCase = 3,
}

/// <summary>
/// Provider usage/reconciliation evidence. Pending is deliberately non-terminal.
/// A lost or unreachable provider must return a failure or Pending; neither is
/// proof that capacity can be refunded.
/// </summary>
public readonly record struct PlatformResourceUsageEvidence(
    uint ContractVersion,
    PlatformResourceReservation Reservation,
    PlatformOperationIdentity Operation,
    PlatformResourceReconciliationState State,
    ulong ConsumedAmount,
    ulong ReceiptSequence)
{
    public bool IsTerminal => State is
        PlatformResourceReconciliationState.ExactUsage or
        PlatformResourceReconciliationState.ContainedWithoutConsumption or
        PlatformResourceReconciliationState.ConservativeWorstCase;
}

public static class PlatformResourceContract
{
    public const uint ContractVersion = 1;

    public static PlatformAuthorityResult ValidateRequest(PlatformResourceAdmissionRequest request)
    {
        if (request.ContractVersion != ContractVersion)
            return Invalid("The platform resource request uses an unsupported contract version.");

        var correlation = ValidateCorrelation(request.Correlation);
        if (!correlation.IsSuccess) return correlation;

        var domain = PlatformDomainContract.ValidateLease(request.DomainLease.Subject, request.DomainLease);
        if (!domain.IsSuccess) return domain;

        return ValidateEnvelope(request.Envelope);
    }

    public static PlatformAuthorityResult ValidateReservation(
        PlatformResourceAdmissionRequest request,
        PlatformResourceReservation reservation)
    {
        var requestValidation = ValidateRequest(request);
        if (!requestValidation.IsSuccess) return requestValidation;

        if (reservation.ContractVersion != ContractVersion ||
            reservation.ReservationId.Value == 0 ||
            reservation.Generation.Value == 0)
        {
            return Invalid("The provider resource reservation version, identity, or generation is invalid.");
        }

        if (reservation.Correlation != request.Correlation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale,
                "The provider resource reservation correlation is stale.");

        var domain = ValidateExactDomain(request.DomainLease, reservation.DomainLease);
        if (!domain.IsSuccess) return domain;

        return reservation.Envelope == request.Envelope
            ? PlatformAuthorityResult.Ok()
            : Invalid("The provider resource reservation changed the requested semantic envelope.");
    }

    public static PlatformAuthorityResult ValidateSubmissionBinding(
        PlatformResourceReservation expectedReservation,
        PlatformResourceSubmissionBinding binding)
    {
        if (binding.ContractVersion != ContractVersion || binding.Reservation != expectedReservation)
            return Invalid("The resource submission binding does not match the exact provider reservation.");

        return ValidateOperation(expectedReservation.DomainLease, binding.Operation);
    }

    public static PlatformAuthorityResult ValidateUsageEvidence(
        PlatformResourceSubmissionBinding expectedBinding,
        PlatformResourceUsageEvidence evidence)
    {
        var binding = ValidateSubmissionBinding(expectedBinding.Reservation, expectedBinding);
        if (!binding.IsSuccess) return binding;

        if (evidence.ContractVersion != ContractVersion || evidence.ReceiptSequence == 0)
            return Invalid("Resource usage evidence has an invalid version or receipt sequence.");

        if (evidence.Reservation != expectedBinding.Reservation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale,
                "Resource usage evidence does not match the exact provider reservation generation.");

        var operation = PlatformCompletionContract.ValidateReceiptIdentity(
            expectedBinding.Operation,
            new PlatformCompletionReceipt(evidence.Operation.OperationId, evidence.Operation.Generation,
                evidence.Operation.DomainLease, PlatformCompletionState.Pending));
        if (!operation.IsSuccess) return operation;

        if (!Enum.IsDefined(evidence.State))
            return Invalid("Resource usage evidence contains an undefined reconciliation state.");

        var maximum = expectedBinding.Reservation.Envelope.Amount;
        var amountValid = evidence.State switch
        {
            PlatformResourceReconciliationState.Pending => evidence.ConsumedAmount == 0,
            PlatformResourceReconciliationState.ExactUsage => evidence.ConsumedAmount <= maximum,
            PlatformResourceReconciliationState.ContainedWithoutConsumption => evidence.ConsumedAmount == 0,
            PlatformResourceReconciliationState.ConservativeWorstCase => evidence.ConsumedAmount == maximum,
            _ => false,
        };

        return amountValid
            ? PlatformAuthorityResult.Ok()
            : Invalid("Resource usage evidence amount is not canonical for its reconciliation state.");
    }

    private static PlatformAuthorityResult ValidateCorrelation(PlatformResourceCorrelation correlation) =>
        correlation.CorrelationId.Value == 0 || correlation.Generation.Value == 0
            ? Invalid("Platform resource correlation identities and generations must be non-zero.")
            : PlatformAuthorityResult.Ok();

    private static PlatformAuthorityResult ValidateEnvelope(ResourceEnvelopeV1 envelope)
    {
        try
        {
            return envelope.Canonicalize() == envelope
                ? PlatformAuthorityResult.Ok()
                : Invalid("The platform resource envelope is not canonical.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return Invalid(exception.Message);
        }
    }

    private static PlatformAuthorityResult ValidateOperation(
        PlatformProviderDomainLease expectedDomain,
        PlatformOperationIdentity operation)
    {
        if (operation.OperationId.Value == 0 || operation.Generation.Value == 0)
            return Invalid("Provider operation identities and generations must be non-zero.");

        return ValidateExactDomain(expectedDomain, operation.DomainLease);
    }

    private static PlatformAuthorityResult ValidateExactDomain(
        PlatformProviderDomainLease expected,
        PlatformProviderDomainLease actual)
    {
        if (actual.LeaseId != expected.LeaseId)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain,
                "The resource contract refers to a different provider domain lease.");
        if (actual.Generation != expected.Generation)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale,
                "The resource contract uses a stale provider domain generation.");
        if (actual.Subject != expected.Subject)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.WrongDomain,
                "The resource contract provider-domain subject does not match.");
        return PlatformAuthorityResult.Ok();
    }

    private static PlatformAuthorityResult Invalid(string message) =>
        PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, message);
}

public interface IPlatformResourceProvider
{
    PlatformAuthorityResult<PlatformResourceReservation> PrepareResource(
        PlatformResourceAdmissionRequest request);

    PlatformAuthorityResult CancelPreparedResource(
        PlatformResourceReservation reservation);

    PlatformAuthorityResult<PlatformResourceSubmissionBinding> BindResourceSubmission(
        PlatformResourceReservation reservation,
        PlatformOperationIdentity operation);

    PlatformAuthorityResult<PlatformResourceUsageEvidence> ReconcileResourceUsage(
        PlatformResourceSubmissionBinding binding);
}
