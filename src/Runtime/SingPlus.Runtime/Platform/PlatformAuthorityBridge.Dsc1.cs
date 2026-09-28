using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformDsc1SubmissionId(ulong Value);
public readonly record struct PlatformDsc1SubmissionGeneration(ulong Value);

public readonly record struct PlatformDsc1RegionRange(
    PlatformRegionMapping Mapping,
    long Offset,
    long Length);

/// <summary>
/// Local continuation identity for one bounded DSC1 Copy model operation. Provider
/// operation identities remain bridge-private and this value is not a capability.
/// </summary>
public readonly record struct PlatformDsc1CopySubmission(
    PlatformDsc1SubmissionId SubmissionId,
    PlatformDsc1SubmissionGeneration Generation,
    PlatformDomainBinding DomainBinding,
    PlatformDsc1RegionRange Source,
    PlatformDsc1RegionRange Destination,
    PlatformDsc1CopyProfile Profile,
    PlatformFeatureDescriptor Feature);

public enum PlatformDsc1CopyOutcome : byte
{
    Completed = 0,
    Cancelled,
}

public readonly record struct PlatformDsc1CopyReceipt(
    PlatformDsc1CopySubmission Submission,
    PlatformDsc1CopyOutcome Outcome,
    long ByteLength,
    bool OutputPublished);

internal readonly record struct PlatformDsc1TerminalObservation(
    PlatformDsc1CopySubmission Submission,
    PlatformDsc1CopyOutcome Outcome);

public sealed partial class PlatformAuthorityBridge
{
    private enum Dsc1OperationState
    {
        Submitting = 0,
        Active,
        ClosedCompleted,
        ClosedCancelled,
        Faulted,
    }

    private sealed class Dsc1OperationRecord(
        PlatformDsc1CopySubmission submission,
        CapabilityId computeCapabilityId)
    {
        public PlatformDsc1CopySubmission Submission { get; } = submission;
        public PlatformProviderDsc1Submission? AcceptedProviderSubmission { get; private set; }
        public PlatformProviderDsc1Submission ProviderSubmission =>
            AcceptedProviderSubmission ?? throw new InvalidOperationException(
                "The DSC1 provider submission has not been accepted.");
        public CapabilityId ComputeCapabilityId { get; } = computeCapabilityId;
        public Dsc1OperationState State { get; set; }
        public bool CancellationRequested { get; set; }
        public bool ProviderCallInFlight { get; set; }
        public bool LocalReservationsReleased { get; set; }

        public void Accept(PlatformProviderDsc1Submission providerSubmission)
        {
            AcceptedProviderSubmission = providerSubmission;
            State = Dsc1OperationState.Active;
        }
    }

    private readonly object _dsc1Gate = new();
    private readonly Dictionary<PlatformDsc1SubmissionId, Dsc1OperationRecord>
        _dsc1Operations = [];
    private ulong _nextDsc1SubmissionId = 1;

    internal KernelResult<PlatformDsc1CopySubmission> SubmitDsc1ModelCopy(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        CapabilityId computeCapabilityId,
        PlatformDsc1RegionRange source,
        PlatformDsc1RegionRange destination,
        Action<PlatformDsc1CopySubmission>? providerBoundaryReady = null)
    {
        PlatformDsc1CopyRequest request;
        PlatformDsc1CopySubmission localSubmission;
        Dsc1OperationRecord operationRecord;
        IPlatformDsc1ComputeProvider computeProvider;
        lock (_dsc1Gate)
        {
            // Repeat exact mapping-use admission at the bridge boundary. The
            // RuntimeKernel outer gate makes this check and operation-record
            // publication one atomic local policy transition.
            var mappingUse = ValidateDsc1MappingUseAdmissionLocked(
                binding,
                expectedSubject,
                source,
                destination);
            if (!mappingUse.IsSuccess)
            {
                return KernelResult<PlatformDsc1CopySubmission>.Fail(
                    mappingUse.Error,
                    mappingUse.Message!);
            }

            var feature = _featureManifest.Resolve(
                PlatformFeatureFamily.Dsc1BulkCompute);
            if (!PlatformDsc1ComputeContract.SupportsModelOnly(feature))
            {
                return KernelResult<PlatformDsc1CopySubmission>.Fail(
                    KernelError.PlatformUnsupported,
                    $"The platform provider does not expose DSC1 Copy contract v{PlatformDsc1ComputeContract.ContractVersion} as ModelOnly.");
            }

            if (_provider is not IPlatformDsc1ComputeProvider resolvedProvider)
            {
                return KernelResult<PlatformDsc1CopySubmission>.Fail(
                    KernelError.PlatformUnsupported,
                    "The platform provider does not implement the DSC1 Copy model contract.");
            }
            computeProvider = resolvedProvider;

            if (_nextDsc1SubmissionId == 0)
            {
                return KernelResult<PlatformDsc1CopySubmission>.Fail(
                    KernelError.CapacityExhausted,
                    "DSC1 local submission identity space is exhausted.");
            }

            var domainRecord = _domains[binding.BindingId];
            var sourceRecord = _mappings[source.Mapping.MappingId];
            var destinationRecord = _mappings[destination.Mapping.MappingId];
            request = new PlatformDsc1CopyRequest(
                domainRecord.ProviderLease,
                new PlatformProviderDsc1RegionRange(
                    sourceRecord.ProviderLease,
                    source.Offset,
                    source.Length),
                new PlatformProviderDsc1RegionRange(
                    destinationRecord.ProviderLease,
                    destination.Offset,
                    destination.Length),
                PlatformDsc1CopyProfile.UInt8AllOrNone);

            var requestValidation = PlatformDsc1ComputeContract.ValidateRequest(request);
            if (!requestValidation.IsSuccess)
            {
                return KernelResult<PlatformDsc1CopySubmission>.Fail(
                    KernelError.PlatformFaulted,
                    requestValidation.Message ??
                    "The bridge constructed an invalid DSC1 Copy request.");
            }

            localSubmission = new PlatformDsc1CopySubmission(
                new PlatformDsc1SubmissionId(_nextDsc1SubmissionId++),
                new PlatformDsc1SubmissionGeneration(1),
                binding,
                source,
                destination,
                PlatformDsc1CopyProfile.UInt8AllOrNone,
                feature);
            try
            {
                _dsc1Operations.EnsureCapacity(_dsc1Operations.Count + 1);
            }
            catch (OutOfMemoryException)
            {
                return KernelResult<PlatformDsc1CopySubmission>.Fail(
                    KernelError.CapacityExhausted,
                    "DSC1 local operation tracking capacity is exhausted.");
            }

            operationRecord = new Dsc1OperationRecord(
                localSubmission,
                computeCapabilityId);
            operationRecord.ProviderCallInFlight = true;
            _dsc1Operations.Add(localSubmission.SubmissionId, operationRecord);
        }

        providerBoundaryReady?.Invoke(localSubmission);

        PlatformAuthorityResult<PlatformProviderDsc1Submission> providerResult;
        try
        {
            providerResult = computeProvider.SubmitDsc1Copy(request);
        }
        catch (Exception exception)
        {
            lock (_dsc1Gate)
            {
                if (_dsc1Operations.Remove(localSubmission.SubmissionId) &&
                    _domains.TryGetValue(binding.BindingId, out var domain))
                    QuarantineDomain(domain);
            }
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                KernelError.PlatformFaulted,
                $"The DSC1 provider threw during submission; the domain is quarantined: {exception.Message}");
        }

        if (!providerResult.IsSuccess)
        {
            lock (_dsc1Gate)
            {
                _dsc1Operations.Remove(localSubmission.SubmissionId);
                if (RequiresDomainQuarantine(providerResult.Status) &&
                    _domains.TryGetValue(binding.BindingId, out var domain))
                    QuarantineDomain(domain);
            }
            return FromProviderFailure<PlatformDsc1CopySubmission>(
                providerResult.Status,
                providerResult.Message);
        }

        var providerSubmission = providerResult.Value!;
        var submissionValidation = PlatformDsc1ComputeContract.ValidateSubmission(
            request,
            providerSubmission);
        if (!submissionValidation.IsSuccess)
        {
            try
            {
                _ = computeProvider.CancelDsc1(providerSubmission);
            }
            catch (Exception)
            {
                // Best effort only: a malformed returned identity cannot
                // identify the exact accepted effect for authoritative cleanup.
            }
            lock (_dsc1Gate)
            {
                _dsc1Operations.Remove(localSubmission.SubmissionId);
                if (_domains.TryGetValue(binding.BindingId, out var domain))
                    QuarantineDomain(domain);
            }
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                KernelError.PlatformFaulted,
                "The provider returned a malformed DSC1 submission; exact closure of the requested effect is not provable and the domain is quarantined.");
        }

        lock (_dsc1Gate)
        {
            if (!_dsc1Operations.TryGetValue(localSubmission.SubmissionId, out var current) ||
                !ReferenceEquals(current, operationRecord) ||
                current.State != Dsc1OperationState.Submitting ||
                !current.ProviderCallInFlight)
            {
                return KernelResult<PlatformDsc1CopySubmission>.Fail(
                    KernelError.PlatformFaulted,
                    "The DSC1 submission reservation changed while the provider call was in flight.");
            }
            operationRecord.ProviderCallInFlight = false;
            operationRecord.Accept(providerSubmission);
            return KernelResult<PlatformDsc1CopySubmission>.Ok(localSubmission);
        }
    }

    internal KernelResult<PlatformDsc1TerminalObservation> ObserveDsc1ModelCopy(
        PlatformDsc1CopySubmission submission,
        PlatformDomainIdentity expectedSubject,
        Action? providerBoundaryReady = null)
    {
        Dsc1OperationRecord record;
        PlatformProviderDsc1Submission providerSubmission;
        lock (_dsc1Gate)
        {
            var recordResult = ResolveDsc1Operation(submission, expectedSubject);
            if (!recordResult.IsSuccess)
            {
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                    recordResult.Error,
                    recordResult.Message!);
            }

            record = recordResult.Value!;
            if (record.State == Dsc1OperationState.Faulted)
            {
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                    KernelError.PlatformFaulted,
                    "The DSC1 operation is fault-pinned and cannot publish output.");
            }
            if (record.ProviderCallInFlight || record.State == Dsc1OperationState.Submitting)
            {
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                    KernelError.PlatformBindingDraining,
                    "The exact DSC1 operation already has a provider transition in flight.");
            }
            if (TryGetDsc1Terminal(record, out var terminal))
                return KernelResult<PlatformDsc1TerminalObservation>.Ok(terminal);
            record.ProviderCallInFlight = true;
            providerSubmission = record.ProviderSubmission;
        }

        providerBoundaryReady?.Invoke();

        PlatformAuthorityResult<PlatformProviderDsc1Completion> observed;
        try
        {
            observed = ((IPlatformDsc1ComputeProvider)_provider!)
                .ObserveDsc1Completion(providerSubmission);
        }
        catch (Exception exception)
        {
            lock (_dsc1Gate) PinDsc1Fault(record);
            return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                KernelError.PlatformFaulted,
                $"The DSC1 provider threw while observing completion; reservations remain pinned: {exception.Message}");
        }
        lock (_dsc1Gate)
        {
            var accepted = AcceptDsc1Completion(record, observed);
            // A successful terminal observer remains the unique release winner
            // until RuntimeKernel commits the exact local reservation release.
            // Otherwise another observer can replay the terminal state and remove
            // the record while this winner is reacquiring the outer memory gate.
            if (!accepted.IsSuccess)
                record.ProviderCallInFlight = false;
            return accepted;
        }
    }

    internal KernelResult<PlatformDsc1TerminalObservation> CancelDsc1ModelCopy(
        PlatformDsc1CopySubmission submission,
        PlatformDomainIdentity expectedSubject,
        Action? providerBoundaryReady = null)
    {
        Dsc1OperationRecord record;
        PlatformProviderDsc1Submission providerSubmission;
        bool observeOnly;
        lock (_dsc1Gate)
        {
            var recordResult = ResolveDsc1Operation(submission, expectedSubject);
            if (!recordResult.IsSuccess)
            {
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                    recordResult.Error,
                    recordResult.Message!);
            }

            record = recordResult.Value!;
            if (record.State == Dsc1OperationState.Faulted)
            {
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                    KernelError.PlatformFaulted,
                    "The DSC1 operation is fault-pinned and cannot prove cancellation closure.");
            }
            if (record.ProviderCallInFlight || record.State == Dsc1OperationState.Submitting)
            {
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                    KernelError.PlatformBindingDraining,
                    "The exact DSC1 operation already has a provider transition in flight.");
            }
            if (TryGetDsc1Terminal(record, out var terminal))
                return KernelResult<PlatformDsc1TerminalObservation>.Ok(terminal);
            observeOnly = record.CancellationRequested;
            record.ProviderCallInFlight = true;
            providerSubmission = record.ProviderSubmission;
        }

        providerBoundaryReady?.Invoke();

        PlatformAuthorityResult<PlatformProviderDsc1Completion> completion;
        try
        {
            var provider = (IPlatformDsc1ComputeProvider)_provider!;
            completion = observeOnly
                ? provider.ObserveDsc1Completion(providerSubmission)
                : provider.CancelDsc1(providerSubmission);
        }
        catch (Exception exception)
        {
            lock (_dsc1Gate) PinDsc1Fault(record);
            return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                KernelError.PlatformFaulted,
                $"The DSC1 provider threw during cancellation/drain; reservations remain pinned: {exception.Message}");
        }
        lock (_dsc1Gate)
        {
            if (!observeOnly && completion.IsSuccess)
                record.CancellationRequested = true;
            var accepted = AcceptDsc1Completion(record, completion);
            if (!accepted.IsSuccess)
                record.ProviderCallInFlight = false;
            return accepted;
        }
    }

    internal KernelResult ValidateDsc1LocalReservationRelease(
        PlatformDsc1CopySubmission submission,
        PlatformDomainIdentity expectedSubject,
        PlatformDsc1CopyOutcome outcome)
    {
        lock (_dsc1Gate)
        {
            var recordResult = ResolveDsc1Operation(submission, expectedSubject);
            if (!recordResult.IsSuccess)
                return KernelResult.Fail(
                    recordResult.Error,
                    recordResult.Message!);

            var record = recordResult.Value!;
            var expectedState = outcome switch
            {
                PlatformDsc1CopyOutcome.Completed => Dsc1OperationState.ClosedCompleted,
                PlatformDsc1CopyOutcome.Cancelled => Dsc1OperationState.ClosedCancelled,
                _ => Dsc1OperationState.Faulted,
            };
            if (record.State != expectedState)
            {
                return KernelResult.Fail(
                    KernelError.PlatformFaulted,
                    "Local DSC1 reservations cannot be released without the exact provider terminal outcome.");
            }

            return KernelResult.Ok();
        }
    }

    internal void CommitDsc1LocalReservationRelease(
        PlatformDsc1CopySubmission submission)
    {
        lock (_dsc1Gate)
        {
            if (!_dsc1Operations.TryGetValue(submission.SubmissionId, out var record) ||
                record.Submission != submission ||
                record.State is not
                    (Dsc1OperationState.ClosedCompleted or
                     Dsc1OperationState.ClosedCancelled) ||
                record.LocalReservationsReleased)
            {
                throw new InvalidOperationException(
                    "The prevalidated DSC1 local reservation release invariant was lost.");
            }

            record.LocalReservationsReleased = true;
            _dsc1Operations.Remove(submission.SubmissionId);
        }
    }

    internal bool HasActiveDsc1Operations(PlatformDomainBinding binding)
    {
        lock (_dsc1Gate)
        {
            return _dsc1Operations.Values.Any(record =>
                !record.LocalReservationsReleased &&
                record.Submission.DomainBinding.BindingId == binding.BindingId);
        }
    }

    internal PlatformDsc1CopySubmission[] Dsc1OperationsForMapping(
        PlatformRegionMapping mapping)
    {
        lock (_dsc1Gate)
        {
            return _dsc1Operations.Values
                .Where(record =>
                    !record.LocalReservationsReleased &&
                    (record.Submission.Source.Mapping.MappingId == mapping.MappingId ||
                     record.Submission.Destination.Mapping.MappingId == mapping.MappingId))
                .Select(static record => record.Submission)
                .OrderBy(static submission => submission.SubmissionId.Value)
                .ToArray();
        }
    }

    internal PlatformDsc1CopySubmission[] Dsc1OperationsForCapability(
        CapabilityId capabilityId)
    {
        lock (_dsc1Gate)
        {
            return _dsc1Operations.Values
                .Where(record =>
                    !record.LocalReservationsReleased &&
                    (record.ComputeCapabilityId == capabilityId ||
                     _mappings[record.Submission.Source.Mapping.MappingId]
                         .AuthorityCapabilityId == capabilityId ||
                     _mappings[record.Submission.Destination.Mapping.MappingId]
                         .AuthorityCapabilityId == capabilityId))
                .Select(static record => record.Submission)
                .OrderBy(static submission => submission.SubmissionId.Value)
                .ToArray();
        }
    }

    internal PlatformDsc1CopySubmission[] Dsc1OperationsForSubject(
        PlatformDomainIdentity subject)
    {
        lock (_dsc1Gate)
        {
            return _dsc1Operations.Values
                .Where(record =>
                    !record.LocalReservationsReleased &&
                    record.Submission.DomainBinding.Subject == subject)
                .Select(static record => record.Submission)
                .OrderBy(static submission => submission.SubmissionId.Value)
                .ToArray();
        }
    }

    private KernelResult<PlatformDsc1TerminalObservation> AcceptDsc1Completion(
        Dsc1OperationRecord record,
        PlatformAuthorityResult<PlatformProviderDsc1Completion> providerResult)
    {
        if (!providerResult.IsSuccess)
        {
            if (RequiresDomainQuarantine(providerResult.Status))
                PinDsc1Fault(record);

            return FromProviderFailure<PlatformDsc1TerminalObservation>(
                providerResult.Status,
                providerResult.Message);
        }

        var completion = providerResult.Value!;
        var completionValidation = PlatformDsc1ComputeContract.ValidateCompletion(
            record.ProviderSubmission,
            completion);
        if (!completionValidation.IsSuccess)
        {
            PinDsc1Fault(record);
            return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                KernelError.PlatformFaulted,
                completionValidation.Message ??
                "The provider returned malformed DSC1 completion evidence.");
        }

        switch (completion.Disposition)
        {
            case PlatformDsc1CompletionDisposition.Pending:
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                    KernelError.PlatformBindingDraining,
                    "The exact DSC1 operation has not reached provider closure.");

            case PlatformDsc1CompletionDisposition.Completed:
                record.State = Dsc1OperationState.ClosedCompleted;
                return KernelResult<PlatformDsc1TerminalObservation>.Ok(
                    new PlatformDsc1TerminalObservation(
                        record.Submission,
                        PlatformDsc1CopyOutcome.Completed));

            case PlatformDsc1CompletionDisposition.Cancelled:
                record.State = Dsc1OperationState.ClosedCancelled;
                return KernelResult<PlatformDsc1TerminalObservation>.Ok(
                    new PlatformDsc1TerminalObservation(
                        record.Submission,
                        PlatformDsc1CopyOutcome.Cancelled));

            case PlatformDsc1CompletionDisposition.Faulted:
            default:
                PinDsc1Fault(record);
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                    KernelError.PlatformFaulted,
                    "The provider reported the DSC1 operation faulted; owned-region reservations remain pinned.");
        }
    }

    private KernelResult<Dsc1OperationRecord> ResolveDsc1Operation(
        PlatformDsc1CopySubmission submission,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_dsc1Operations.TryGetValue(submission.SubmissionId, out var record))
        {
            return KernelResult<Dsc1OperationRecord>.Fail(
                KernelError.PlatformBindingNotFound,
                "The local DSC1 submission does not exist.");
        }

        if (record.Submission.Generation != submission.Generation)
        {
            return KernelResult<Dsc1OperationRecord>.Fail(
                KernelError.StaleGeneration,
                "The local DSC1 submission generation is stale.");
        }

        if (record.Submission != submission)
        {
            return KernelResult<Dsc1OperationRecord>.Fail(
                KernelError.PlatformDenied,
                "The local DSC1 submission identity or bounded request is forged.");
        }

        var domain = ValidateDomain(submission.DomainBinding, expectedSubject);
        if (!domain.IsSuccess)
        {
            return KernelResult<Dsc1OperationRecord>.Fail(
                domain.Error,
                domain.Message!);
        }

        if (record.LocalReservationsReleased)
        {
            return KernelResult<Dsc1OperationRecord>.Fail(
                KernelError.PlatformDenied,
                "The local DSC1 submission has already released its reservations and cannot be replayed.");
        }

        return KernelResult<Dsc1OperationRecord>.Ok(record);
    }

    private KernelResult ValidateDsc1LocalRange(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        PlatformDsc1RegionRange range,
        PlatformMemoryAccess requiredAccess,
        string role)
    {
        var mappingValidation = ValidateMapping(range.Mapping, expectedSubject);
        if (!mappingValidation.IsSuccess) return mappingValidation;

        if (range.Mapping.DomainBinding != binding)
        {
            return KernelResult.Fail(
                KernelError.WrongPlatformDomain,
                $"The DSC1 {role} mapping belongs to a different local platform-domain binding.");
        }

        if ((range.Mapping.Access & requiredAccess) != requiredAccess)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                $"The DSC1 {role} mapping lacks the required access.");
        }

        if (!_mappings.TryGetValue(range.Mapping.MappingId, out var record))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                $"The DSC1 {role} mapping does not exist.");
        }

        var allowedOffset = 0L;
        var allowedLength = record.ProviderLease.Region.ByteLength;
        if (_exactMappingSlices.TryGetValue(range.Mapping.MappingId, out var exactSlice))
        {
            allowedOffset = exactSlice.Offset;
            allowedLength = exactSlice.Length;
        }

        if (range.Offset < allowedOffset ||
            range.Length <= 0 ||
            range.Length > PlatformDsc1ComputeContract.MaximumByteLength ||
            range.Offset > allowedOffset + allowedLength - range.Length)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                $"The DSC1 {role} range is outside the exact active owned-region mapping or exceeds the v1 bound.");
        }

        return KernelResult.Ok();
    }

    private static bool TryGetDsc1Terminal(
        Dsc1OperationRecord record,
        out PlatformDsc1TerminalObservation terminal)
    {
        switch (record.State)
        {
            case Dsc1OperationState.ClosedCompleted:
                terminal = new PlatformDsc1TerminalObservation(
                    record.Submission,
                    PlatformDsc1CopyOutcome.Completed);
                return true;
            case Dsc1OperationState.ClosedCancelled:
                terminal = new PlatformDsc1TerminalObservation(
                    record.Submission,
                    PlatformDsc1CopyOutcome.Cancelled);
                return true;
            default:
                terminal = default;
                return false;
        }
    }

    private void PinDsc1Fault(Dsc1OperationRecord record)
    {
        record.State = Dsc1OperationState.Faulted;
        if (_domains.TryGetValue(
                record.Submission.DomainBinding.BindingId,
                out var domain))
        {
            QuarantineDomain(domain);
        }
    }
}
