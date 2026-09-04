using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private sealed class Dsc1PayloadRecord(
        ProcessHandle owner,
        RuntimeBufferLease<byte> sourceLease,
        RuntimeBufferLease<byte> destinationLease,
        byte[] stagedOutput)
    {
        public ProcessHandle Owner { get; } = owner;
        public PlatformDsc1CopySubmission? AcceptedSubmission { get; private set; }
        public PlatformDsc1CopySubmission Submission =>
            AcceptedSubmission ?? throw new InvalidOperationException(
                "The DSC1 payload has not been bound to an accepted submission.");
        public RuntimeBufferLease<byte> SourceLease { get; } = sourceLease;
        public RuntimeBufferLease<byte> DestinationLease { get; } = destinationLease;
        public byte[] StagedOutput { get; } = stagedOutput;

        public void Accept(PlatformDsc1CopySubmission submission) =>
            AcceptedSubmission = submission;
    }

    private readonly object _dsc1PayloadGate = new();
    private readonly Dictionary<PlatformDsc1SubmissionId, Dsc1PayloadRecord>
        _dsc1PayloadOperations = [];

    /// <summary>
    /// Submits the bounded UInt8 DSC1 Copy reference model. The source is copied into
    /// private staging and subsequent owner access through <see cref="OwnedBuffer{T}"/>
    /// is rejected until exact provider completion/cancellation closure is validated.
    /// Previously acquired managed aliases cannot be revoked by this ModelOnly path.
    /// This method accepts only a provider explicitly classified as ModelOnly.
    /// </summary>
    public KernelResult<PlatformDsc1CopySubmission> SubmitPlatformDsc1Copy(
        ProcessHandle subject,
        PlatformDomainBinding binding,
        Dsc1ComputeCapability computeCapability,
        OwnedBuffer<byte> sourceBuffer,
        PlatformDsc1RegionRange source,
        OwnedBuffer<byte> destinationBuffer,
        PlatformDsc1RegionRange destination)
    {
        ArgumentNullException.ThrowIfNull(sourceBuffer);
        ArgumentNullException.ThrowIfNull(destinationBuffer);

        lock (_dsc1PayloadGate)
        {
            return SubmitPlatformDsc1CopyLocked(
                subject,
                binding,
                computeCapability,
                sourceBuffer,
                source,
                destinationBuffer,
                destination);
        }
    }

    private KernelResult<PlatformDsc1CopySubmission> SubmitPlatformDsc1CopyLocked(
        ProcessHandle subject,
        PlatformDomainBinding binding,
        Dsc1ComputeCapability computeCapability,
        OwnedBuffer<byte> sourceBuffer,
        PlatformDsc1RegionRange source,
        OwnedBuffer<byte> destinationBuffer,
        PlatformDsc1RegionRange destination)
    {

        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                effect.Error,
                effect.Message!);
        }

        var capability = CapabilityAuthority.Validate(
            computeCapability.CapabilityId,
            process.DomainId,
            subject.Generation,
            CapabilityRights.Execute);
        if (!capability.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                capability.Error,
                capability.Message!);
        }

        if (capability.Value!.ResourceKind != ResourceKind.Compute ||
            !string.Equals(
                capability.Value.ResourceId,
                CapabilityResourceIds.Dsc1Copy,
                StringComparison.Ordinal))
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                KernelError.WrongCapabilityResource,
                "The local capability does not authorize DSC1 Copy v1.");
        }

        var owner = new RegionOwner(process.DomainId, subject.Generation);
        var sourceValidation = ValidateDsc1PayloadRange(
            sourceBuffer,
            source,
            owner,
            "source");
        if (!sourceValidation.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                sourceValidation.Error,
                sourceValidation.Message!);
        }

        var destinationValidation = ValidateDsc1PayloadRange(
            destinationBuffer,
            destination,
            owner,
            "destination");
        if (!destinationValidation.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                destinationValidation.Error,
                destinationValidation.Message!);
        }

        if (sourceBuffer.Handle.RegionId == destinationBuffer.Handle.RegionId)
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                KernelError.PlatformDenied,
                "DSC1 Copy v1 requires distinct source and destination owned buffers.");
        }

        if (source.Length != destination.Length)
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                KernelError.PlatformDenied,
                "DSC1 Copy source and destination ranges must have equal byte lengths.");
        }

        RuntimeBufferLease<byte>? sourceLease = null;
        RuntimeBufferLease<byte>? destinationLease = null;
        byte[]? stagedOutput = null;
        var providerAccepted = false;
        try
        {
            sourceLease = sourceBuffer.ReserveForRuntime(RuntimeBufferAccess.Read);
            destinationLease = destinationBuffer.ReserveForRuntime(RuntimeBufferAccess.Write);
            stagedOutput = sourceLease.ReadOnlySpan
                .Slice(checked((int)source.Offset), checked((int)source.Length))
                .ToArray();

            _dsc1PayloadOperations.EnsureCapacity(
                _dsc1PayloadOperations.Count + 1);
            var record = new Dsc1PayloadRecord(
                subject,
                sourceLease,
                destinationLease,
                stagedOutput);

            var submission = PlatformAuthority.SubmitDsc1ModelCopy(
                binding,
                PlatformIdentity(process),
                computeCapability.CapabilityId,
                source,
                destination);
            if (!submission.IsSuccess)
            {
                return submission;
            }

            // From this point, any unexpected local tracking failure must leak/pin
            // custody rather than return the buffers while an accepted effect exists.
            providerAccepted = true;
            record.Accept(submission.Value!);
            _dsc1PayloadOperations.Add(
                submission.Value!.SubmissionId,
                record);
            return submission;
        }
        catch (InvalidOperationException exception)
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                providerAccepted
                    ? KernelError.PlatformFaulted
                    : KernelError.InvalidRegionState,
                providerAccepted
                    ? $"The provider accepted DSC1 work but local payload tracking failed; custody remains pinned: {exception.Message}"
                    : exception.Message);
        }
        catch (OverflowException)
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                KernelError.PlatformDenied,
                "The DSC1 Copy range cannot be represented by the local bounded buffer model.");
        }
        catch (OutOfMemoryException)
        {
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                providerAccepted
                    ? KernelError.PlatformFaulted
                    : KernelError.CapacityExhausted,
                providerAccepted
                    ? "The provider accepted DSC1 work but local payload tracking exhausted capacity; custody remains pinned."
                    : "The bounded DSC1 staging or local operation tracking capacity is exhausted.");
        }
        finally
        {
            if (!providerAccepted)
            {
                if (stagedOutput is not null) Array.Clear(stagedOutput);
                destinationLease?.Dispose();
                sourceLease?.Dispose();
            }
        }
    }

    public KernelResult<PlatformDsc1CopyReceipt> ObservePlatformDsc1Copy(
        ProcessHandle subject,
        PlatformDsc1CopySubmission submission)
    {
        lock (_dsc1PayloadGate)
            return ObservePlatformDsc1CopyLocked(subject, submission);
    }

    private KernelResult<PlatformDsc1CopyReceipt> ObservePlatformDsc1CopyLocked(
        ProcessHandle subject,
        PlatformDsc1CopySubmission submission)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var payload = ResolveDsc1Payload(subject, submission);
        if (!payload.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                payload.Error,
                payload.Message!);
        }

        var terminal = PlatformAuthority.ObserveDsc1ModelCopy(
            submission,
            PlatformIdentity(resolved.Value!));
        if (!terminal.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                terminal.Error,
                terminal.Message!);
        }

        return FinalizeDsc1Payload(
            payload.Value!,
            terminal.Value!,
            discardCompletedOutput: false);
    }

    public KernelResult<PlatformDsc1CopyReceipt> CancelPlatformDsc1Copy(
        ProcessHandle subject,
        PlatformDsc1CopySubmission submission)
    {
        lock (_dsc1PayloadGate)
            return CancelPlatformDsc1CopyLocked(subject, submission);
    }

    private KernelResult<PlatformDsc1CopyReceipt> CancelPlatformDsc1CopyLocked(
        ProcessHandle subject,
        PlatformDsc1CopySubmission submission)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var payload = ResolveDsc1Payload(subject, submission);
        if (!payload.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                payload.Error,
                payload.Message!);
        }

        var terminal = PlatformAuthority.CancelDsc1ModelCopy(
            submission,
            PlatformIdentity(resolved.Value!));
        if (!terminal.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                terminal.Error,
                terminal.Message!);
        }

        return FinalizeDsc1Payload(
            payload.Value!,
            terminal.Value!,
            discardCompletedOutput: false);
    }

    private KernelResult<PlatformDsc1CopyReceipt> FinalizeDsc1Payload(
        Dsc1PayloadRecord payload,
        PlatformDsc1TerminalObservation terminal,
        bool discardCompletedOutput)
    {
        if (payload.Submission != terminal.Submission)
        {
            return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                KernelError.PlatformFaulted,
                "The provider terminal outcome does not match the exact local DSC1 payload reservation.");
        }

        var outputPublished = terminal.Outcome == PlatformDsc1CopyOutcome.Completed &&
                              !discardCompletedOutput;

        var releaseValidation = PlatformAuthority.ValidateDsc1LocalReservationRelease(
            payload.Submission,
            payload.Submission.DomainBinding.Subject,
            terminal.Outcome);
        if (!releaseValidation.IsSuccess)
        {
            return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                releaseValidation.Error,
                releaseValidation.Message!);
        }

        if (outputPublished)
        {
            payload.StagedOutput.AsSpan().CopyTo(
                payload.DestinationLease.WritableSpan.Slice(
                    checked((int)payload.Submission.Destination.Offset),
                    checked((int)payload.Submission.Destination.Length)));
        }

        payload.DestinationLease.Dispose();
        payload.SourceLease.Dispose();
        Array.Clear(payload.StagedOutput);

        _dsc1PayloadOperations.Remove(payload.Submission.SubmissionId);
        PlatformAuthority.CommitDsc1LocalReservationRelease(payload.Submission);
        return KernelResult<PlatformDsc1CopyReceipt>.Ok(
            new PlatformDsc1CopyReceipt(
                payload.Submission,
                terminal.Outcome,
                payload.Submission.Source.Length,
                outputPublished));
    }

    private KernelResult<Dsc1PayloadRecord> ResolveDsc1Payload(
        ProcessHandle subject,
        PlatformDsc1CopySubmission submission)
    {
        if (!_dsc1PayloadOperations.TryGetValue(
                submission.SubmissionId,
                out var record))
        {
            return KernelResult<Dsc1PayloadRecord>.Fail(
                KernelError.PlatformBindingNotFound,
                "The local DSC1 payload reservation does not exist.");
        }

        if (record.Submission.Generation != submission.Generation)
        {
            return KernelResult<Dsc1PayloadRecord>.Fail(
                KernelError.StaleGeneration,
                "The local DSC1 payload reservation generation is stale.");
        }

        if (record.Owner != subject ||
            record.Submission.DomainBinding.Subject.Process != subject)
        {
            return KernelResult<Dsc1PayloadRecord>.Fail(
                KernelError.WrongPlatformDomain,
                "The local DSC1 payload reservation belongs to a different process generation.");
        }

        if (record.Submission != submission)
        {
            return KernelResult<Dsc1PayloadRecord>.Fail(
                KernelError.PlatformDenied,
                "The local DSC1 submission identity or bounded request is forged.");
        }

        return KernelResult<Dsc1PayloadRecord>.Ok(record);
    }

    private KernelResult ValidateDsc1PayloadRange(
        OwnedBuffer<byte> buffer,
        PlatformDsc1RegionRange range,
        RegionOwner expectedOwner,
        string role)
    {
        if (!buffer.IsValid)
        {
            return KernelResult.Fail(
                KernelError.InvalidRegionState,
                $"The DSC1 {role} buffer is no longer a live ownership token.");
        }

        if (buffer.Handle != range.Mapping.Region)
        {
            return KernelResult.Fail(
                buffer.Handle.RegionId == range.Mapping.Region.RegionId
                    ? KernelError.StaleGeneration
                    : KernelError.WrongCapabilityResource,
                $"The DSC1 {role} buffer does not match the mapped region identity.");
        }

        var region = Regions.Validate(buffer.Handle, expectedOwner);
        if (!region.IsSuccess) return KernelResult.Fail(region.Error, region.Message!);

        if (range.Offset < 0 ||
            range.Length <= 0 ||
            range.Length > PlatformDsc1ComputeContract.MaximumByteLength ||
            range.Offset > buffer.Length - range.Length)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                $"The DSC1 {role} range is outside the exact owned buffer or exceeds the v1 bound.");
        }

        return KernelResult.Ok();
    }

    private KernelResult AdvancePlatformDsc1ForProcess(ProcessHandle subject)
    {
        lock (_dsc1PayloadGate)
        {
            var resolved = Processes.Resolve(subject);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);

            var operations = PlatformAuthority.Dsc1OperationsForSubject(
                PlatformIdentity(resolved.Value!));
            return CancelAndReleaseDsc1Operations(operations);
        }
    }

    private KernelResult AdvancePlatformDsc1ForMapping(
        PlatformRegionMapping mapping)
    {
        lock (_dsc1PayloadGate)
        {
            return CancelAndReleaseDsc1Operations(
                PlatformAuthority.Dsc1OperationsForMapping(mapping));
        }
    }

    private KernelResult CascadePlatformDsc1CapabilityRevocation(
        CapabilityId capabilityId)
    {
        lock (_dsc1PayloadGate)
        {
            return CancelAndReleaseDsc1Operations(
                PlatformAuthority.Dsc1OperationsForCapability(capabilityId));
        }
    }

    private KernelResult CancelAndReleaseDsc1Operations(
        IEnumerable<PlatformDsc1CopySubmission> operations)
    {
        foreach (var submission in operations)
        {
            var owner = submission.DomainBinding.Subject.Process;
            var resolved = Processes.Resolve(owner);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);

            var payload = ResolveDsc1Payload(owner, submission);
            if (!payload.IsSuccess) return KernelResult.Fail(payload.Error, payload.Message!);

            var terminal = PlatformAuthority.CancelDsc1ModelCopy(
                submission,
                PlatformIdentity(resolved.Value!));
            if (!terminal.IsSuccess)
                return KernelResult.Fail(terminal.Error, terminal.Message!);

            var finalized = FinalizeDsc1Payload(
                payload.Value!,
                terminal.Value!,
                discardCompletedOutput: true);
            if (!finalized.IsSuccess)
                return KernelResult.Fail(finalized.Error, finalized.Message!);
        }

        return KernelResult.Ok();
    }
}
