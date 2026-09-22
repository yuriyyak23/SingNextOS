using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

internal enum ExternalResourceBindingState
{
    Bound = 0,
    Consuming,
    Settling,
    Settled,
    Quarantined,
    CancelledPreSubmit,
}

internal sealed record ExternalOperationResourceBinding(
    ExternalOperationHandle Operation,
    ProcessHandle BudgetOwner,
    BudgetReservationHandle Lease,
    ResourceEnvelopeV1 Envelope,
    string ProviderIdentity,
    ulong ProviderGeneration,
    MeasurementContractIdentityV1 MeasurementContract,
    ExternalResourceBindingState State,
    ulong? TerminalReceiptSequence = null,
    Guid? TerminalEvidenceId = null,
    ulong? SettledAmount = null);

internal readonly record struct ExternalResourceUsageEvidence(
    uint Version,
    OperationBinding OperationBinding,
    string ProviderIdentity,
    ulong ProviderGeneration,
    MeasurementContractIdentityV1 MeasurementContract,
    Guid EvidenceId,
    ResourceClassV1 ResourceClass,
    ResourceUnitV1 Unit,
    ResourceUsageBreakdownV1 Usage,
    ulong ConsumedAmount,
    ulong ReceiptSequence)
{
    internal const uint CurrentVersion = 1;
}

public sealed partial class ExternalOperationAuthority
{
    internal KernelResult<ExternalOperationResourceBinding> BindResourceLease(
        ExternalOperationHandle operation,
        ProcessHandle budgetOwner,
        BudgetReservationHandle lease,
        ResourceEnvelopeV1 envelope,
        string providerIdentity,
        ulong providerGeneration)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess)
                return KernelResult<ExternalOperationResourceBinding>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State != ExternalOperationState.Admitted || record.ResourceBinding is not null)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidTransition,
                    "An exact resource lease can bind once to an Admitted external operation.");
            if (budgetOwner.ProcessId.Value == 0 || budgetOwner.Generation == 0 ||
                lease.ReservationId.Value == 0 || lease.Generation.Value == 0 ||
                string.IsNullOrWhiteSpace(providerIdentity) || providerGeneration == 0)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidMessage,
                    "Resource binding identity or generation is incomplete.");
            ResourceEnvelopeV1 canonical;
            try { canonical = envelope.Canonicalize(); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
            { return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidMessage, exception.Message); }
            var binding = new ExternalOperationResourceBinding(operation, budgetOwner, lease, canonical,
                providerIdentity, providerGeneration,
                new MeasurementContractIdentityV1(1, $"{providerIdentity}:measurement",
                    "1", canonical.ResourceClass, canonical.Unit).Canonicalize(),
                ExternalResourceBindingState.Bound);
            record.ResourceBinding = binding;
            AddTransition(record, record.State, "ResourceLeaseBound");
            return KernelResult<ExternalOperationResourceBinding>.Ok(binding);
        }
    }

    internal KernelResult<ExternalOperationResourceBinding> MarkResourceConsumption(
        ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess)
                return KernelResult<ExternalOperationResourceBinding>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            var binding = record.ResourceBinding;
            if (binding is null || binding.State != ExternalResourceBindingState.Bound ||
                record.State != ExternalOperationState.Submitted)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidTransition,
                    "Resource consumption requires the exact bound Submitted operation.");
            binding = binding with { State = ExternalResourceBindingState.Consuming };
            record.ResourceBinding = binding;
            return KernelResult<ExternalOperationResourceBinding>.Ok(binding);
        }
    }

    internal KernelResult<ExternalOperationResourceBinding> BeginResourceSettlement(
        ExternalResourceUsageEvidence evidence)
    {
        lock (_gate)
        {
            if (evidence.Version != ExternalResourceUsageEvidence.CurrentVersion ||
                evidence.ReceiptSequence == 0 || string.IsNullOrWhiteSpace(evidence.ProviderIdentity) ||
                evidence.ProviderGeneration == 0 || evidence.EvidenceId == Guid.Empty)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidMessage,
                    "Usage evidence version or identity is invalid.");
            var resolved = Resolve(evidence.OperationBinding.Operation);
            if (!resolved.IsSuccess)
                return KernelResult<ExternalOperationResourceBinding>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            var binding = record.ResourceBinding;
            if (binding is null || record.Binding != evidence.OperationBinding)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.StaleGeneration,
                    "Usage evidence does not match the exact operation binding generation.");
            if (binding.ProviderIdentity != evidence.ProviderIdentity ||
                binding.ProviderGeneration != evidence.ProviderGeneration)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.StaleGeneration,
                    "Usage evidence provider identity or generation is stale.");
            MeasurementContractIdentityV1 measurement;
            try { measurement = evidence.MeasurementContract.Canonicalize(); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
            { return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidMessage, exception.Message); }
            if (binding.MeasurementContract != measurement)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.StaleGeneration,
                    "Usage evidence measurement contract identity is stale or belongs to another contour.");
            ulong normalized;
            try { normalized = ResourceChargeabilityMatrixV1.For(evidence.ResourceClass).Normalize(evidence.Usage); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
            { return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidMessage, exception.Message); }
            if (binding.Envelope.ResourceClass != evidence.ResourceClass ||
                binding.Envelope.Unit != evidence.Unit || evidence.ConsumedAmount > binding.Envelope.Amount)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidMessage,
                    "Usage evidence changes the resource dimension or exceeds the bound envelope.");
            if (measurement.ResourceClass != evidence.ResourceClass || measurement.Unit != evidence.Unit ||
                normalized != evidence.ConsumedAmount)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidMessage,
                    "Usage evidence does not match the bound chargeability policy normalization.");
            if (binding.TerminalReceiptSequence == evidence.ReceiptSequence)
                return binding.State == ExternalResourceBindingState.Settled &&
                       binding.TerminalEvidenceId == evidence.EvidenceId &&
                       binding.SettledAmount == evidence.ConsumedAmount
                    ? KernelResult<ExternalOperationResourceBinding>.Ok(binding)
                    : KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidTransition,
                        "Duplicate evidence conflicts with the terminal settlement.");
            if (binding.State is not (ExternalResourceBindingState.Consuming or ExternalResourceBindingState.Quarantined) ||
                record.State is not (ExternalOperationState.DeviceComplete or ExternalOperationState.Visible or ExternalOperationState.Published))
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidTransition,
                    "Usage settlement requires exact completion and a consuming or quarantined lease.");
            binding = binding with
            {
                State = ExternalResourceBindingState.Settling,
                TerminalReceiptSequence = evidence.ReceiptSequence,
                TerminalEvidenceId = evidence.EvidenceId,
                SettledAmount = evidence.ConsumedAmount
            };
            record.ResourceBinding = binding;
            return KernelResult<ExternalOperationResourceBinding>.Ok(binding);
        }
    }

    internal KernelResult<ExternalOperationResourceBinding> CompleteResourceSettlement(
        ExternalOperationHandle operation, BudgetReservationHandle lease, bool success)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess)
                return KernelResult<ExternalOperationResourceBinding>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            var binding = record.ResourceBinding;
            if (binding is null || binding.Lease != lease || binding.State != ExternalResourceBindingState.Settling)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.StaleGeneration,
                    "Settlement completion does not match the reserved binding transition.");
            binding = binding with { State = success ? ExternalResourceBindingState.Settled : ExternalResourceBindingState.Quarantined };
            record.ResourceBinding = binding;
            AddTransition(record, record.State, success ? "ResourceSettled" : "ResourceSettlementQuarantined");
            return KernelResult<ExternalOperationResourceBinding>.Ok(binding);
        }
    }

    internal KernelResult<ExternalOperationResourceBinding> MarkResourceQuarantined(
        ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess)
                return KernelResult<ExternalOperationResourceBinding>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            var binding = record.ResourceBinding;
            if (binding is null)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidTransition,
                    "External operation has no resource binding.");
            if (binding.State == ExternalResourceBindingState.Quarantined)
                return KernelResult<ExternalOperationResourceBinding>.Ok(binding);
            if (binding.State is not (ExternalResourceBindingState.Bound or ExternalResourceBindingState.Consuming or ExternalResourceBindingState.Settling))
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidTransition,
                    "Terminal resource settlement cannot be replaced by quarantine.");
            binding = binding with { State = ExternalResourceBindingState.Quarantined };
            record.ResourceBinding = binding;
            AddTransition(record, record.State, "ResourceQuarantined");
            return KernelResult<ExternalOperationResourceBinding>.Ok(binding);
        }
    }

    internal KernelResult<ExternalOperationResourceBinding> MarkResourceCancelledPreSubmit(
        ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess)
                return KernelResult<ExternalOperationResourceBinding>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            var binding = record.ResourceBinding;
            if (binding is null)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidTransition,
                    "External operation has no resource binding.");
            if (binding.State == ExternalResourceBindingState.CancelledPreSubmit)
                return KernelResult<ExternalOperationResourceBinding>.Ok(binding);
            if (binding.State != ExternalResourceBindingState.Bound)
                return KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidTransition,
                    "Only an unconsumed binding can cancel before submit.");
            binding = binding with { State = ExternalResourceBindingState.CancelledPreSubmit };
            record.ResourceBinding = binding;
            AddTransition(record, record.State, "ResourceCancelledBeforeSubmit");
            return KernelResult<ExternalOperationResourceBinding>.Ok(binding);
        }
    }

    internal KernelResult<ExternalOperationResourceBinding> QueryResourceBinding(ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess)
                return KernelResult<ExternalOperationResourceBinding>.Fail(resolved.Error, resolved.Message!);
            return resolved.Value!.ResourceBinding is { } binding
                ? KernelResult<ExternalOperationResourceBinding>.Ok(binding)
                : KernelResult<ExternalOperationResourceBinding>.Fail(KernelError.InvalidTransition,
                    "External operation has no resource binding.");
        }
    }
}

public sealed partial class RuntimeKernel
{
    internal KernelResult<BudgetReservationSnapshot> SettleResourceExternalOperation(
        ProcessHandle principal, ExternalResourceUsageEvidence evidence)
    {
        var validation = ValidateExternalOperationPrincipal(principal,
            evidence.OperationBinding.Operation, requireNewEffect: false);
        if (!validation.IsSuccess)
            return KernelResult<BudgetReservationSnapshot>.Fail(validation.Error, validation.Message!);
        var begun = ExternalOperations.BeginResourceSettlement(evidence);
        if (!begun.IsSuccess)
            return KernelResult<BudgetReservationSnapshot>.Fail(begun.Error, begun.Message!);
        var binding = begun.Value!;
        if (binding.State == ExternalResourceBindingState.Settled)
            return Budgets.Query(binding.Lease);

        var correlation = new PlatformResourceCorrelation(
            new PlatformResourceCorrelationId(binding.Operation.OperationId.Value),
            new PlatformResourceCorrelationGeneration(binding.Operation.Generation.Value));
        var recoveryTransition = ResourceBudgetRecoveryTransition.SettledExact;
        IReadOnlyList<BudgetAmount> recoveryCharge = evidence.ConsumedAmount == 0
            ? []
            : [new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, evidence.ConsumedAmount)];
        var journal = AppendResourceRecovery(binding.Lease, binding.BudgetOwner, binding.Envelope,
            correlation, recoveryTransition, recoveryCharge);
        if (!journal.IsSuccess)
        {
            _ = ExternalOperations.CompleteResourceSettlement(binding.Operation, binding.Lease, success: false);
            return KernelResult<BudgetReservationSnapshot>.Fail(journal.Error, journal.Message!);
        }

        var lease = Budgets.Query(binding.Lease);
        if (!lease.IsSuccess)
        {
            _ = ExternalOperations.CompleteResourceSettlement(binding.Operation, binding.Lease, success: false);
            return KernelResult<BudgetReservationSnapshot>.Fail(lease.Error, lease.Message!);
        }
        if (lease.Value!.State == BudgetReservationState.Quarantined)
        {
            var reconciled = Budgets.ReconcileLease(binding.BudgetOwner, binding.Lease);
            if (!reconciled.IsSuccess)
            {
                _ = ExternalOperations.CompleteResourceSettlement(binding.Operation, binding.Lease, success: false);
                return KernelResult<BudgetReservationSnapshot>.Fail(reconciled.Error, reconciled.Message!);
            }
        }
        var settled = Budgets.SettleLease(binding.BudgetOwner, binding.Lease,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, evidence.ConsumedAmount)]);
        var completed = ExternalOperations.CompleteResourceSettlement(binding.Operation, binding.Lease, settled.IsSuccess);
        if (!settled.IsSuccess)
            return KernelResult<BudgetReservationSnapshot>.Fail(settled.Error, settled.Message!);
        return completed.IsSuccess
            ? settled
            : KernelResult<BudgetReservationSnapshot>.Fail(completed.Error, completed.Message!);
    }
}
