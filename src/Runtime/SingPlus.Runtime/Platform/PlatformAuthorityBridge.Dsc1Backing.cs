using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private bool IsDsc1Backing(PlatformDsc1CopySubmission submission)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
            return FindOriginalDsc1OperationLocked(submission.SubmissionId) is { OriginalBackingRequest: not null };
    }

    private static bool HasValidatedDsc1BackingClosure(Dsc1OperationRecord record) =>
        record.OriginalBackingRequest is { } request && record.OriginalBackingObservation is { } raw &&
        ReferenceEquals(raw.Request, request) && raw.NoFurtherByteUseGuaranteed && raw.Completed != raw.NoEffectGuaranteed &&
        ((raw.Completed && record.State == Dsc1OperationState.ClosedCompleted) ||
         (raw.NoEffectGuaranteed && record.State == Dsc1OperationState.ClosedCancelled));

    private KernelResult<PlatformDsc1CopySubmission> SubmitDsc1Backing(Dsc1OperationRecord record,
        PlatformDomainIdentity subject, Func<CapabilityId, CapabilityId, Func<KernelResult>, KernelResult> authorizeCommit)
    {
        try
        {
            // Request and payload are original and retained before entering the provider.
            try
            {
                var reply = record.OriginalBackingSource!.CopyDsc1Backing(record.OriginalBackingRequest!);
                lock (_dsc1Gate) if (reply.IsSuccess) record.OriginalBackingObservation = reply.Value;
            }
            catch (Exception exception) when (exception is not StackOverflowException) { }
            lock (_dsc1Gate) record.State = Dsc1OperationState.Active;
            var closed = ResolveDsc1BackingClosure(record);
            if (!closed.IsSuccess) return KernelResult<PlatformDsc1CopySubmission>.Fail(closed.Error, closed.Message!);
            var publication = authorizeCommit(record.OriginalSourceMapping.AuthorityCapabilityId,
                record.OriginalDestinationMapping.AuthorityCapabilityId, () =>
                {
                    lock (_secureDomainLifecycleGate)
                    lock (_dsc1Gate)
                    {
                        var original = ValidateDsc1OriginalOwnersLocked(record, record.LastCleanupSource);
                        if (!original.IsSuccess) return original;
                        var current = ValidateDsc1MappingUseAdmissionLocked(record.Submission.DomainBinding, subject,
                            record.Submission.Source, record.Submission.Destination);
                        if (!current.IsSuccess) return current;
                        record.PublicationAdmitted = true;
                        return KernelResult.Ok();
                    }
                });
            return publication.IsSuccess ? KernelResult<PlatformDsc1CopySubmission>.Ok(record.Submission) :
                KernelResult<PlatformDsc1CopySubmission>.Fail(publication.Error, publication.Message!);
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(KernelError.PlatformFaulted,
                "Original DSC1 backing invocation retained; unknown byte effect is not repeated.");
        }
        finally { ClearDsc1ReleaseWinner(record); }
    }

    private KernelResult<PlatformDsc1TerminalObservation> ObserveDsc1Backing(PlatformDsc1CopySubmission submission,
        PlatformDomainIdentity subject, Action? providerBoundaryReady)
    {
        Dsc1OperationRecord record;
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            var original = ResolveDsc1Operation(submission, subject, cleanup: true);
            if (!original.IsSuccess) return KernelResult<PlatformDsc1TerminalObservation>.Fail(original.Error, original.Message!);
            record = original.Value!;
            if (record.ProviderCallInFlight || record.LocalReservationsReleased)
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(KernelError.PlatformBindingDraining, "Original DSC1 transition is in flight.");
            record.ProviderCallInFlight = true;
        }
        try
        {
            providerBoundaryReady?.Invoke();
            var closed = ResolveDsc1BackingClosure(record);
            if (!closed.IsSuccess)
            {
                ClearDsc1ReleaseWinner(record);
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(closed.Error, closed.Message!);
            }
            lock (_dsc1Gate)
                return TryGetDsc1Terminal(record, out var terminal) ? KernelResult<PlatformDsc1TerminalObservation>.Ok(terminal) :
                    KernelResult<PlatformDsc1TerminalObservation>.Fail(KernelError.PlatformFaulted, "Original DSC1 byte closure is missing.");
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            ClearDsc1ReleaseWinner(record);
            return KernelResult<PlatformDsc1TerminalObservation>.Fail(KernelError.PlatformFaulted, "Original DSC1 byte query failed; custody retained.");
        }
    }

    private KernelResult ResolveDsc1BackingClosure(Dsc1OperationRecord record)
    {
        var observation = record.OriginalBackingSource!.ResolveDsc1Backing(record.OriginalBackingRequest!);
        lock (_dsc1Gate)
        {
            if (!observation.IsSuccess || observation.Value is not { } raw)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original DSC1 byte closure unavailable; custody retained.");
            if (record.OriginalBackingObservation is null) record.OriginalBackingObservation = raw;
            if (!ReferenceEquals(record.OriginalBackingObservation, raw) || !ReferenceEquals(raw.Request, record.OriginalBackingRequest) ||
                !raw.NoFurtherByteUseGuaranteed || raw.Completed == raw.NoEffectGuaranteed)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Foreign or malformed DSC1 byte closure cannot release original custody.");
        }
        var source = ReadDsc1CleanupSource(record);
        if (!source.IsSuccess) return source;
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            var original = ValidateDsc1OriginalOwnersLocked(record, record.LastCleanupSource);
            if (!original.IsSuccess) return original;
            record.State = observation.Value!.Completed ? Dsc1OperationState.ClosedCompleted : Dsc1OperationState.ClosedCancelled;
            return KernelResult.Ok();
        }
    }
}
