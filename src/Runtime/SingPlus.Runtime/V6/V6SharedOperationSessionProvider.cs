using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>
/// A narrow provider seam for an operation already owned by SingNext. The packaged
/// session handles receipt ordering; only the existing owner commits submission.
/// </summary>
internal sealed class V6SharedOperationSessionProvider(
    RuntimeKernel kernel, ResourceAdmissionCommit commit,
    Hc.ExternalDomainLease scope, Hc.ExternalGenerationSet generations,
    Func<OperationBinding, KernelResult> execute) : Hc.IExternalOperationProvider,
    Hc.IExternalOperationCancellationProvider
{
    private readonly object sync = new();
    private Hc.ExternalOperationRequest? request;
    private OperationBinding? committedBinding;
    private bool issued;
    private bool cancellationRequested;
    private bool executionAccepted;
    private Hc.ExternalOperationStage delivered = Hc.ExternalOperationStage.Admitted;

    internal KernelResult AttachCommittedBinding(OperationBinding binding)
    {
        lock (sync)
        {
            if (request is null || issued || committedBinding is not null ||
                binding.Operation != commit.Operation)
                return KernelResult.Fail(KernelError.InvalidTransition,
                    "The shared session requires one exact committed owner binding.");
            var owner = kernel.QueryExternalOperation(commit.Principal, commit.Operation);
            if (!owner.IsSuccess || owner.Value!.State != ExternalOperationState.Submitted ||
                owner.Value.Disposition != ExternalOperationDisposition.Active ||
                owner.Value.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "The existing SingNext owner has not committed this exact binding.");
            committedBinding = binding;
            return KernelResult.Ok();
        }
    }

    public Hc.ExternalOperationProviderPollResult Admit(Hc.ExternalOperationSemanticRequest semantic)
    {
        ArgumentNullException.ThrowIfNull(semantic);
        lock (sync)
        {
            if (request is not null || semantic.ContractVersion != Hc.ExternalOperationContract.Version ||
                semantic.EffectClass != Hc.ExternalEffectClass.NonIdempotent ||
                semantic.VisibilityRequirement != Hc.ExternalVisibilityRequirement.StagedOutput ||
                semantic.CancellationMode != Hc.ExternalCancellationMode.ExactAcknowledgement ||
                semantic.ReplayEffectClass != Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish)
                return Failure(Hc.ExternalOperationProviderPollStatus.Stale);
            var owner = kernel.QueryExternalOperation(commit.Principal, commit.Operation);
            if (!owner.IsSuccess || owner.Value!.State != ExternalOperationState.Admitted)
                return Failure(Hc.ExternalOperationProviderPollStatus.Stale);
            request = new Hc.ExternalOperationRequest(
                new(new(Guid.NewGuid()), new(commit.Operation.Generation.Value)), scope,
                semantic.ContractVersion, generations, semantic.Correlation, semantic.EffectClass,
                semantic.VisibilityRequirement, semantic.CancellationMode);
            return new(Hc.ExternalOperationProviderPollStatus.Receipt, generations,
                new Hc.ExternalOperationAdmissionReceipt(request, Hc.ExternalRuntimeOutcome.Succeeded));
        }
    }

    public Hc.ExternalOperationProviderPollResult Submit(Hc.ExternalOperationRequest presented)
    {
        OperationBinding binding;
        lock (sync)
        {
            if (request is null || presented != request || committedBinding is not { } exact ||
                issued || cancellationRequested)
                return Failure(Hc.ExternalOperationProviderPollStatus.Stale);
            var owner = kernel.QueryExternalOperation(commit.Principal, commit.Operation);
            if (!owner.IsSuccess || owner.Value!.State != ExternalOperationState.Submitted ||
                owner.Value.Disposition != ExternalOperationDisposition.Active ||
                owner.Value.Binding != exact)
                return Failure(Hc.ExternalOperationProviderPollStatus.Stale);
            issued = true;
            binding = exact;
        }
        // The callback may cause an external effect. A missing/failed receipt is
        // ambiguous and can never be retried through this instance.
        try
        {
            var result = execute(binding);
            if (result.IsSuccess)
            {
                lock (sync)
                {
                    executionAccepted = true;
                    delivered = Hc.ExternalOperationStage.Submitted;
                }
            }
            return new(Hc.ExternalOperationProviderPollStatus.Receipt, generations,
                new Hc.ExternalOperationProgressReceipt(request,
                    Hc.ExternalOperationStage.Submitted,
                    result.IsSuccess ? Hc.ExternalRuntimeOutcome.Succeeded : Hc.ExternalRuntimeOutcome.Faulted));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return Failure(Hc.ExternalOperationProviderPollStatus.Faulted);
        }
    }

    public Hc.ExternalOperationProviderPollResult Poll(Hc.ExternalOperationRequest presented)
    {
        lock (sync)
        {
            if (request is null || presented != request)
                return Failure(Hc.ExternalOperationProviderPollStatus.Stale);
            if (!executionAccepted || committedBinding is not { } binding)
                return new(Hc.ExternalOperationProviderPollStatus.Pending, generations);
            var owner = kernel.QueryExternalOperation(commit.Principal, commit.Operation);
            if (!owner.IsSuccess || owner.Value!.Binding != binding)
                return Failure(Hc.ExternalOperationProviderPollStatus.Stale);
            if (owner.Value.Disposition is not (ExternalOperationDisposition.Active or
                ExternalOperationDisposition.Completed or ExternalOperationDisposition.Published))
                return Failure(Hc.ExternalOperationProviderPollStatus.Faulted);
            if (delivered == Hc.ExternalOperationStage.Submitted &&
                owner.Value.State is ExternalOperationState.DeviceComplete or ExternalOperationState.Visible or
                    ExternalOperationState.Published or ExternalOperationState.Released)
            {
                delivered = Hc.ExternalOperationStage.DeviceComplete;
                return new(Hc.ExternalOperationProviderPollStatus.Receipt, generations,
                    new Hc.ExternalOperationCompletionReceipt(request, Hc.ExternalRuntimeOutcome.Succeeded));
            }
            if (delivered == Hc.ExternalOperationStage.DeviceComplete &&
                owner.Value.State is ExternalOperationState.Visible or ExternalOperationState.Published or
                    ExternalOperationState.Released)
            {
                delivered = Hc.ExternalOperationStage.Visible;
                return new(Hc.ExternalOperationProviderPollStatus.Receipt, generations,
                    new Hc.ExternalOperationProgressReceipt(request, Hc.ExternalOperationStage.Visible,
                        Hc.ExternalRuntimeOutcome.Succeeded));
            }
            if (delivered == Hc.ExternalOperationStage.Visible &&
                owner.Value.State is ExternalOperationState.Published or ExternalOperationState.Released &&
                owner.Value.Disposition == ExternalOperationDisposition.Published)
            {
                delivered = Hc.ExternalOperationStage.Published;
                return new(Hc.ExternalOperationProviderPollStatus.Receipt, generations,
                    new Hc.ExternalOperationPublicationReceipt(request, Hc.ExternalRuntimeOutcome.Succeeded));
            }
            if (delivered == Hc.ExternalOperationStage.Published &&
                owner.Value.State == ExternalOperationState.Released &&
                owner.Value.Disposition == ExternalOperationDisposition.Published)
            {
                delivered = Hc.ExternalOperationStage.Released;
                return new(Hc.ExternalOperationProviderPollStatus.Receipt, generations,
                    new Hc.ExternalOperationReleaseReceipt(request, Hc.ExternalRuntimeOutcome.Closed));
            }
            return new(Hc.ExternalOperationProviderPollStatus.Pending, generations);
        }
    }

    public Hc.ExternalOperationProviderPollResult Cancel(Hc.ExternalOperationRequest presented)
    {
        var receipt = RequestCancellation(presented);
        return receipt.Outcome == Hc.ExternalOperationCancellationOutcome.Stale
            ? Failure(Hc.ExternalOperationProviderPollStatus.Stale)
            : new(Hc.ExternalOperationProviderPollStatus.Pending, generations);
    }

    public Hc.ExternalOperationCancellationReceipt RequestCancellation(Hc.ExternalOperationRequest presented)
    {
        lock (sync)
        {
            if (request is null || presented != request || cancellationRequested)
                return new(presented, Hc.ExternalOperationCancellationOutcome.Stale, generations);
            cancellationRequested = true;
            var owner = kernel.QueryExternalOperation(commit.Principal, commit.Operation);
            if (!owner.IsSuccess)
                return new(presented, Hc.ExternalOperationCancellationOutcome.Ambiguous, generations);
            if (!issued && !commit.SubmitStarted &&
                owner.Value!.State == ExternalOperationState.Admitted)
            {
                var cancelled = kernel.CancelExternalOperation(commit.Principal, commit.Operation,
                    providerCancellationSupported: false);
                if (!cancelled.IsSuccess || cancelled.Value!.Disposition != ExternalOperationDisposition.Cancelled)
                    return new(presented, Hc.ExternalOperationCancellationOutcome.Ambiguous, generations);
                var released = kernel.ReleaseExternalOperation(commit.Principal, commit.Operation,
                    new(ProviderResourcesClosed: false, ProviderUnavailable: false));
                if (released.IsSuccess)
                    commit.Dispose();
                var budget = kernel.Budgets.Query(commit.Lease);
                return new(presented, released.IsSuccess && budget.IsSuccess &&
                    budget.Value!.State == BudgetReservationState.CancelledPreSubmit
                    ? Hc.ExternalOperationCancellationOutcome.ConfirmedBeforeSubmit
                    : Hc.ExternalOperationCancellationOutcome.Ambiguous, generations);
            }
            if (owner.Value!.State is ExternalOperationState.Submitted or ExternalOperationState.DeviceComplete or
                ExternalOperationState.Visible)
                _ = kernel.CancelExternalOperation(commit.Principal, commit.Operation,
                    providerCancellationSupported: false);
            return new(presented, Hc.ExternalOperationCancellationOutcome.Ambiguous, generations);
        }
    }

    private Hc.ExternalOperationProviderPollResult Failure(Hc.ExternalOperationProviderPollStatus status) =>
        request is null
            ? new(Hc.ExternalOperationProviderPollStatus.Pending, generations)
            : new(status, generations,
                new Hc.ExternalOperationAdmissionReceipt(request, Hc.ExternalRuntimeOutcome.Faulted));
}
