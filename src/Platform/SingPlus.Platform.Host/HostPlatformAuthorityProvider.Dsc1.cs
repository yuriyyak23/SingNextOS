using SingPlus.Platform;

namespace SingPlus.Platform.Host;

public sealed partial class HostPlatformAuthorityProvider
{
    // Host DSC1 is deliberately a lifecycle-only ModelOnly provider. RuntimeKernel
    // owns staging and the local reference copy; this provider never touches bytes.
    private sealed class Dsc1SubmissionRecord(
        PlatformProviderDsc1Submission submission)
    {
        public PlatformProviderDsc1Submission Submission { get; } = submission;
        public PlatformDsc1CompletionDisposition? TerminalDisposition { get; set; }
    }

    private readonly Dictionary<PlatformOperationId, Dsc1SubmissionRecord>
        _dsc1Submissions = [];
    private readonly bool _deferDsc1Completion;

    public int SubmitDsc1CopyCallCount { get; private set; }
    public int ObserveDsc1CompletionCallCount { get; private set; }
    public int CancelDsc1CallCount { get; private set; }
    public PlatformProviderDsc1Submission? LastDsc1Submission { get; private set; }
    public int ActiveDsc1SubmissionCount => _dsc1Submissions.Count;

    public PlatformAuthorityResult<PlatformProviderDsc1Submission> SubmitDsc1Copy(
        PlatformDsc1CopyRequest request)
    {
        SubmitDsc1CopyCallCount++;

        var requestValidation = PlatformDsc1ComputeContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Submission>.Fail(
                requestValidation.Status,
                requestValidation.Message!);
        }

        var domainValidation = ValidateDomain(request.DomainLease);
        if (!domainValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Submission>.Fail(
                domainValidation.Status,
                domainValidation.Message!);
        }

        var sourceValidation = ValidateActiveDsc1Mapping(request.Source.Mapping);
        if (!sourceValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Submission>.Fail(
                sourceValidation.Status,
                sourceValidation.Message!);
        }

        var destinationValidation = ValidateActiveDsc1Mapping(
            request.Destination.Mapping);
        if (!destinationValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Submission>.Fail(
                destinationValidation.Status,
                destinationValidation.Message!);
        }

        var staged = StageOperation(request.DomainLease);
        if (!staged.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Submission>.Fail(
                staged.Status,
                staged.Message!);
        }

        var pending = AdvanceOperation(
            staged.Value!,
            PlatformCompletionState.Pending);
        if (!pending.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Submission>.Fail(
                pending.Status,
                pending.Message!);
        }

        var submission = new PlatformProviderDsc1Submission(
            staged.Value!,
            request);
        _dsc1Submissions.Add(
            submission.Operation.OperationId,
            new Dsc1SubmissionRecord(submission));
        LastDsc1Submission = submission;

        if (!_deferDsc1Completion)
        {
            var completed = CompleteDsc1Copy(submission);
            if (!completed.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDsc1Submission>.Fail(
                    completed.Status,
                    completed.Message!);
            }
        }

        return PlatformAuthorityResult<PlatformProviderDsc1Submission>.Ok(submission);
    }

    public PlatformAuthorityResult<PlatformProviderDsc1Completion>
        ObserveDsc1Completion(PlatformProviderDsc1Submission submission)
    {
        ObserveDsc1CompletionCallCount++;

        var validation = ValidateDsc1Submission(submission);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                validation.Status,
                validation.Message!);
        }

        return ObserveDsc1CompletionCore(submission, reapClosed: true);
    }

    public PlatformAuthorityResult<PlatformProviderDsc1Completion> CancelDsc1(
        PlatformProviderDsc1Submission submission)
    {
        CancelDsc1CallCount++;

        var validation = ValidateDsc1Submission(submission);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                validation.Status,
                validation.Message!);
        }

        var record = _dsc1Submissions[submission.Operation.OperationId];
        var receipt = ObserveCompletion(submission.Operation);
        if (!receipt.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                receipt.Status,
                receipt.Message!);
        }

        if (receipt.Value!.IsTerminal)
            return ObserveDsc1CompletionCore(submission, reapClosed: true);

        var cancelled = AdvanceOperation(
            submission.Operation,
            PlatformCompletionState.Cancelled);
        if (!cancelled.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                cancelled.Status,
                cancelled.Message!);
        }

        var closed = AdvanceOperation(
            submission.Operation,
            PlatformCompletionState.Closed);
        if (!closed.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                closed.Status,
                closed.Message!);
        }

        record.TerminalDisposition = PlatformDsc1CompletionDisposition.Cancelled;
        var completion = new PlatformProviderDsc1Completion(
            submission,
            closed.Value!,
            PlatformDsc1CompletionDisposition.Cancelled);
        ReapClosedDsc1Submission(submission);
        return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Ok(completion);
    }

    public PlatformAuthorityResult<PlatformProviderDsc1Completion> CompleteDsc1Copy(
        PlatformProviderDsc1Submission submission)
    {
        var validation = ValidateDsc1Submission(submission);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                validation.Status,
                validation.Message!);
        }

        var record = _dsc1Submissions[submission.Operation.OperationId];
        var receipt = ObserveCompletion(submission.Operation);
        if (!receipt.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                receipt.Status,
                receipt.Message!);
        }

        if (receipt.Value!.IsTerminal)
            return ObserveDsc1CompletionCore(submission, reapClosed: false);

        var completed = AdvanceOperation(
            submission.Operation,
            PlatformCompletionState.Completed);
        if (!completed.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                completed.Status,
                completed.Message!);
        }

        var closed = AdvanceOperation(
            submission.Operation,
            PlatformCompletionState.Closed);
        if (!closed.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                closed.Status,
                closed.Message!);
        }

        record.TerminalDisposition = PlatformDsc1CompletionDisposition.Completed;
        return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Ok(
            new PlatformProviderDsc1Completion(
                submission,
                closed.Value!,
                PlatformDsc1CompletionDisposition.Completed));
    }

    private PlatformAuthorityResult<PlatformProviderDsc1Completion>
        ObserveDsc1CompletionCore(
            PlatformProviderDsc1Submission submission,
            bool reapClosed)
    {
        var receipt = ObserveCompletion(submission.Operation);
        if (!receipt.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                receipt.Status,
                receipt.Message!);
        }

        var record = _dsc1Submissions[submission.Operation.OperationId];
        var disposition = receipt.Value!.State switch
        {
            PlatformCompletionState.Closed =>
                record.TerminalDisposition ?? PlatformDsc1CompletionDisposition.Faulted,
            PlatformCompletionState.Faulted => PlatformDsc1CompletionDisposition.Faulted,
            _ => PlatformDsc1CompletionDisposition.Pending,
        };

        var completion = new PlatformProviderDsc1Completion(
            submission,
            receipt.Value,
            disposition);
        if (reapClosed && receipt.Value.ProvesClosure)
            ReapClosedDsc1Submission(submission);

        return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Ok(completion);
    }

    private PlatformAuthorityResult ValidateDsc1Submission(
        PlatformProviderDsc1Submission submission)
    {
        var contract = PlatformDsc1ComputeContract.ValidateSubmission(
            submission.Request,
            submission);
        if (!contract.IsSuccess) return contract;

        if (!_dsc1Submissions.TryGetValue(
                submission.Operation.OperationId,
                out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The DSC1 model submission does not exist.");
        }

        if (record.Submission.Operation.Generation != submission.Operation.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The DSC1 model submission generation is stale.");
        }

        if (record.Submission != submission)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The DSC1 model submission identity does not match the active operation.");
        }

        return ValidateDomain(submission.Operation.DomainLease);
    }

    private PlatformAuthorityResult ValidateActiveDsc1Mapping(
        PlatformProviderRegionMappingLease mapping)
    {
        if (!_mappings.TryGetValue(mapping.MappingId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The DSC1 owned-region mapping does not exist.");
        }

        if (record.Lease.Generation != mapping.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The DSC1 owned-region mapping generation is stale.");
        }

        if (record.Lease != mapping)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.WrongDomain,
                "The DSC1 owned-region mapping does not match the active provider lease.");
        }

        return record.Revoked
            ? PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The DSC1 owned-region mapping has been revoked.")
            : PlatformAuthorityResult.Ok();
    }

    private bool HasActiveDsc1SubmissionForDomain(
        PlatformProviderDomainLease domainLease) =>
        _dsc1Submissions.Values.Any(record =>
            record.Submission.Operation.DomainLease.LeaseId == domainLease.LeaseId &&
            !PlatformCompletionContract.IsTerminal(
                _operations[record.Submission.Operation.OperationId].State));

    private bool HasActiveDsc1SubmissionForMapping(
        PlatformProviderRegionMappingLease mapping) =>
        _dsc1Submissions.Values.Any(record =>
            (record.Submission.Request.Source.Mapping.MappingId == mapping.MappingId ||
             record.Submission.Request.Destination.Mapping.MappingId == mapping.MappingId) &&
            !PlatformCompletionContract.IsTerminal(
                _operations[record.Submission.Operation.OperationId].State));

    private void ReapClosedDsc1Submission(
        PlatformProviderDsc1Submission submission)
    {
        _dsc1Submissions.Remove(submission.Operation.OperationId);
        _operations.Remove(submission.Operation.OperationId);
    }
}
