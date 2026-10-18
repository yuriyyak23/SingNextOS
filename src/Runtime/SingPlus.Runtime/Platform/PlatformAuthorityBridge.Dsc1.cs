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
        CapabilityId computeCapabilityId,
        DomainRecord originalDomain,
        IPlatformDsc1ComputeProvider? originalSource,
        PlatformDsc1CopyRequest originalRequest,
        MappingRecord originalSourceMapping,
        MappingRecord originalDestinationMapping)
    {
        public Dsc1OperationRecord? OriginalOwner { get; set; }
        public PlatformDsc1CopySubmission Submission { get; } = submission;
        public DomainRecord OriginalDomain { get; } = originalDomain;
        public IPlatformDsc1ComputeProvider? OriginalSource { get; } = originalSource;
        public IPlatformOriginalDsc1CopyBackingProvider? OriginalBackingSource { get; init; }
        public PlatformOriginalDsc1CopyBackingRequest? OriginalBackingRequest { get; init; }
        public PlatformOriginalDsc1CopyBackingObservation? OriginalBackingObservation { get; set; }
        public PlatformDsc1CopyRequest OriginalRequest { get; } = originalRequest;
        public MappingRecord OriginalSourceMapping { get; } = originalSourceMapping;
        public MappingRecord OriginalDestinationMapping { get; } = originalDestinationMapping;
        public bool PublicationAdmitted { get; set; }
        public PlatformProviderIncarnation LastCleanupSource { get; set; }
        public PlatformAuthorityResult<PlatformProviderDsc1Completion>? OriginalCompletionObservation { get; set; }
        public PlatformAuthorityResult<PlatformProviderDsc1Submission>? OriginalProviderObservation { get; set; }
        public PlatformProviderDsc1Submission? AcceptedProviderSubmission { get; private set; }
        public PlatformProviderDsc1Submission ProviderSubmission =>
            AcceptedProviderSubmission ?? throw new InvalidOperationException(
                "The DSC1 provider submission has not been accepted.");
        public CapabilityId ComputeCapabilityId { get; } = computeCapabilityId;
        public Dsc1OperationState State { get; set; }
        public bool CancellationRequested { get; set; }
        public bool ProviderCallInFlight { get; set; }
        public bool LocalReservationsReleased { get; set; }
        public bool ProviderNotInvoked { get; set; }

        public void Accept(PlatformProviderDsc1Submission providerSubmission)
        {
            AcceptedProviderSubmission = providerSubmission;
            if (State != Dsc1OperationState.Faulted) State = Dsc1OperationState.Active;
        }
    }

    private readonly object _dsc1Gate = new();
    private readonly Dictionary<PlatformDsc1SubmissionId, Dsc1OperationRecord>
        _dsc1Operations = [];
    private ulong _nextDsc1SubmissionId = 1;
    internal bool SupportsDsc1Backing => _provider is IPlatformOriginalDsc1CopyBackingProvider &&
        PlatformOriginalDsc1CopyBackingContract.Supports(_featureManifest.Resolve(PlatformFeatureFamily.Dsc1BulkCompute));

    internal KernelResult<PlatformDsc1CopySubmission> SubmitDsc1ModelCopy(
        PlatformDomainBinding binding,
        PlatformDomainIdentity expectedSubject,
        CapabilityId computeCapabilityId,
        PlatformDsc1RegionRange source,
        PlatformDsc1RegionRange destination,
        out bool retainPayload,
        Func<CapabilityId, CapabilityId, Func<KernelResult>, KernelResult> authorizeCommit,
        Action<PlatformDsc1CopySubmission>? providerBoundaryReady = null,
        ReadOnlyMemory<byte> backingInput = default,
        Memory<byte> backingOutput = default)
    {
        retainPayload = false;
        PlatformDsc1CopyRequest request;
        PlatformDsc1CopySubmission localSubmission;
        Dsc1OperationRecord operationRecord;
        IPlatformDsc1ComputeProvider? computeProvider;
        IPlatformOriginalDsc1CopyBackingProvider? backingProvider;
        lock (_secureDomainLifecycleGate)
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
            backingProvider = SupportsDsc1Backing ? (IPlatformOriginalDsc1CopyBackingProvider)_provider : null;
            if (backingProvider is null && !PlatformDsc1ComputeContract.SupportsModelOnly(feature))
            {
                return KernelResult<PlatformDsc1CopySubmission>.Fail(
                    KernelError.PlatformUnsupported,
                    $"The platform provider does not expose DSC1 Copy contract v{PlatformDsc1ComputeContract.ContractVersion} as ModelOnly.");
            }

            if (backingProvider is null && _provider is not IPlatformDsc1ComputeProvider)
            {
                return KernelResult<PlatformDsc1CopySubmission>.Fail(
                    KernelError.PlatformUnsupported,
                    "The platform provider does not implement the DSC1 Copy model contract.");
            }
            computeProvider = _provider as IPlatformDsc1ComputeProvider;
            if (backingProvider is not null && (backingInput.Length != source.Length || backingOutput.Length != destination.Length))
                return KernelResult<PlatformDsc1CopySubmission>.Fail(KernelError.PlatformDenied, "Original DSC1 backing byte custody is not exact.");

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
                domainRecord.Dsc1Operations.EnsureCapacity(domainRecord.Dsc1Operations.Count + 1);
            }
            catch (OutOfMemoryException)
            {
                return KernelResult<PlatformDsc1CopySubmission>.Fail(
                    KernelError.CapacityExhausted,
                    "DSC1 local operation tracking capacity is exhausted.");
            }

            operationRecord = new Dsc1OperationRecord(
                localSubmission,
                computeCapabilityId,
                domainRecord,
                computeProvider,
                request,
                sourceRecord,
                destinationRecord)
            {
                OriginalBackingSource = backingProvider,
                OriginalBackingRequest = backingProvider is null ? null : new(request, backingInput, backingOutput)
            };
            operationRecord.ProviderCallInFlight = true;
            operationRecord.OriginalOwner = operationRecord;
            _dsc1Operations.Add(localSubmission.SubmissionId, operationRecord);
            domainRecord.Dsc1Operations.Add(operationRecord);
        }

        PlatformProviderIncarnation admissionSource;
        try
        {
            providerBoundaryReady?.Invoke(localSubmission);
            admissionSource = CurrentProviderIncarnation();
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            RetainDsc1NoEffectReservation(operationRecord);
            retainPayload = true;
            return KernelResult<PlatformDsc1CopySubmission>.Fail(KernelError.PlatformFaulted,
                $"DSC1 preparation/source failed before provider invocation: {exception.GetType().Name}.");
        }
        KernelResult admission;
        try
        {
        admission = authorizeCommit(operationRecord.OriginalSourceMapping.AuthorityCapabilityId,
            operationRecord.OriginalDestinationMapping.AuthorityCapabilityId, () =>
            {
                lock (_secureDomainLifecycleGate)
                lock (_dsc1Gate)
                {
                    var original = ValidateDsc1OriginalOwnersLocked(operationRecord, admissionSource);
                    return original.IsSuccess ? ValidateDsc1MappingUseAdmissionLocked(
                        binding, expectedSubject, source, destination) : original;
                }
            });
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            RetainDsc1NoEffectReservation(operationRecord);
            retainPayload = true;
            return KernelResult<PlatformDsc1CopySubmission>.Fail(KernelError.PlatformFaulted,
                "DSC1 local admission failed before provider invocation; staging has no provider effect.");
        }
        if (!admission.IsSuccess)
        {
            RetainDsc1NoEffectReservation(operationRecord);
            retainPayload = true;
            return KernelResult<PlatformDsc1CopySubmission>.Fail(admission.Error, admission.Message!);
        }

        if (backingProvider is not null)
        {
            retainPayload = true; // Original local payload already retained before possible byte effect.
            return SubmitDsc1Backing(operationRecord, expectedSubject, authorizeCommit);
        }
        PlatformAuthorityResult<PlatformProviderDsc1Submission> providerResult;
        try
        {
            providerResult = computeProvider!.SubmitDsc1Copy(request);
        }
        catch (Exception exception)
        {
            lock (_secureDomainLifecycleGate)
            lock (_dsc1Gate)
            {
                operationRecord.ProviderCallInFlight = false;
                PinDsc1Fault(operationRecord);
            }
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                KernelError.PlatformFaulted,
                $"The DSC1 provider threw during submission; the domain is quarantined: {exception.Message}");
        }

        if (!providerResult.IsSuccess)
        {
            lock (_secureDomainLifecycleGate)
            lock (_dsc1Gate)
            {
                // A generic refusal describes neither exact original non-admission
                // nor closure. ModelOnly permits byte rollback, not lifecycle loss.
                operationRecord.OriginalProviderObservation = providerResult;
                operationRecord.ProviderCallInFlight = false;
                PinDsc1Fault(operationRecord);
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
            lock (_secureDomainLifecycleGate)
            lock (_dsc1Gate)
            {
                operationRecord.OriginalProviderObservation = providerResult;
                operationRecord.ProviderCallInFlight = false;
                PinDsc1Fault(operationRecord);
            }
            try
            {
                _ = computeProvider.CancelDsc1(providerSubmission);
            }
            catch (Exception)
            {
                // Best effort only: a malformed returned identity cannot
                // identify the exact accepted effect for authoritative cleanup.
            }
            return KernelResult<PlatformDsc1CopySubmission>.Fail(
                KernelError.PlatformFaulted,
                "The provider returned a malformed DSC1 submission; exact closure of the requested effect is not provable and the domain is quarantined.");
        }

        // Retain the validated original receipt before any further source callback.
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            operationRecord.OriginalProviderObservation = providerResult;
            operationRecord.Accept(providerSubmission);
            retainPayload = true;
        }
        PlatformProviderIncarnation publicationSource;
        try { publicationSource = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            ClearDsc1ReleaseWinner(operationRecord);
            return KernelResult<PlatformDsc1CopySubmission>.Fail(KernelError.PlatformFaulted,
                "Known DSC1 receipt retained; publication source unavailable.");
        }
        KernelResult publication;
        try
        {
        publication = authorizeCommit(operationRecord.OriginalSourceMapping.AuthorityCapabilityId,
            operationRecord.OriginalDestinationMapping.AuthorityCapabilityId, () =>
            {
                lock (_secureDomainLifecycleGate)
                lock (_dsc1Gate)
                {
                    var original = ValidateDsc1OriginalOwnersLocked(operationRecord, publicationSource);
                    if (!original.IsSuccess) return original;
                    var current = ValidateDsc1MappingUseAdmissionLocked(binding, expectedSubject, source, destination);
                    if (!current.IsSuccess) return current;
                    operationRecord.PublicationAdmitted = true;
                    return KernelResult.Ok();
                }
            });
        }
        finally { ClearDsc1ReleaseWinner(operationRecord); }
        return publication.IsSuccess ? KernelResult<PlatformDsc1CopySubmission>.Ok(localSubmission) :
            KernelResult<PlatformDsc1CopySubmission>.Fail(publication.Error, publication.Message!);
    }

    internal KernelResult<PlatformDsc1TerminalObservation> ObserveDsc1ModelCopy(
        PlatformDsc1CopySubmission submission,
        PlatformDomainIdentity expectedSubject,
        Action? providerBoundaryReady = null)
    {
        if (IsDsc1Backing(submission)) return ObserveDsc1Backing(submission, expectedSubject, providerBoundaryReady);
        Dsc1OperationRecord record;
        PlatformProviderDsc1Submission providerSubmission;
        lock (_secureDomainLifecycleGate)
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
            if (!record.AcceptedProviderSubmission.HasValue)
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(KernelError.PlatformFaulted,
                    "DSC1 cleanup requires the exact original accepted submission.");
            if (TryGetDsc1Terminal(record, out var terminal))
            {
                record.ProviderCallInFlight = true;
                return KernelResult<PlatformDsc1TerminalObservation>.Ok(terminal);
            }
            record.ProviderCallInFlight = true;
            providerSubmission = record.ProviderSubmission;
        }

        providerBoundaryReady?.Invoke();

        var source = ReadDsc1CleanupSource(record);
        if (!source.IsSuccess)
        {
            ClearDsc1ReleaseWinner(record);
            return KernelResult<PlatformDsc1TerminalObservation>.Fail(source.Error, source.Message!);
        }

        PlatformAuthorityResult<PlatformProviderDsc1Completion> observed;
        try
        {
            observed = record.OriginalSource!
                .ObserveDsc1Completion(providerSubmission);
        }
        catch (Exception exception)
        {
            lock (_secureDomainLifecycleGate)
            lock (_dsc1Gate) { record.ProviderCallInFlight = false; PinDsc1Fault(record); }
            return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                KernelError.PlatformFaulted,
                $"The DSC1 provider threw while observing completion; reservations remain pinned: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
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
        if (IsDsc1Backing(submission)) return ObserveDsc1Backing(submission, expectedSubject, providerBoundaryReady);
        Dsc1OperationRecord record;
        PlatformProviderDsc1Submission providerSubmission;
        bool observeOnly;
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            var recordResult = ResolveDsc1Operation(submission, expectedSubject, cleanup: true);
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
            if (!record.AcceptedProviderSubmission.HasValue)
                return KernelResult<PlatformDsc1TerminalObservation>.Fail(KernelError.PlatformFaulted,
                    "DSC1 cleanup requires the exact original accepted submission.");
            if (TryGetDsc1Terminal(record, out var terminal))
            {
                record.ProviderCallInFlight = true;
                return KernelResult<PlatformDsc1TerminalObservation>.Ok(terminal);
            }
            observeOnly = record.CancellationRequested;
            record.ProviderCallInFlight = true;
            providerSubmission = record.ProviderSubmission;
        }

        providerBoundaryReady?.Invoke();

        var source = ReadDsc1CleanupSource(record);
        if (!source.IsSuccess)
        {
            ClearDsc1ReleaseWinner(record);
            return KernelResult<PlatformDsc1TerminalObservation>.Fail(source.Error, source.Message!);
        }

        PlatformAuthorityResult<PlatformProviderDsc1Completion> completion;
        try
        {
            var provider = record.OriginalSource!;
            completion = observeOnly
                ? provider.ObserveDsc1Completion(providerSubmission)
                : provider.CancelDsc1(providerSubmission);
        }
        catch (Exception exception)
        {
            lock (_secureDomainLifecycleGate)
            lock (_dsc1Gate) { record.ProviderCallInFlight = false; PinDsc1Fault(record); }
            return KernelResult<PlatformDsc1TerminalObservation>.Fail(
                KernelError.PlatformFaulted,
                $"The DSC1 provider threw during cancellation/drain; reservations remain pinned: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
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
        PlatformDsc1CopyOutcome outcome,
        Action? providerBoundaryReady = null)
    {
        Dsc1OperationRecord record;
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            var recordResult = ResolveDsc1Operation(submission, expectedSubject, cleanup: true);
            if (!recordResult.IsSuccess)
                return KernelResult.Fail(
                    recordResult.Error,
                    recordResult.Message!);

            record = recordResult.Value!;
            var expectedState = outcome switch
            {
                PlatformDsc1CopyOutcome.Completed => Dsc1OperationState.ClosedCompleted,
                PlatformDsc1CopyOutcome.Cancelled => Dsc1OperationState.ClosedCancelled,
                _ => Dsc1OperationState.Faulted,
            };
            if (record.State != expectedState || !HasValidatedDsc1Closure(record))
            {
                return KernelResult.Fail(
                    KernelError.PlatformFaulted,
                    "Local DSC1 reservations cannot be released without the exact provider terminal outcome.");
            }

        }
        providerBoundaryReady?.Invoke();
        var source = ReadDsc1CleanupSource(record);
        if (!source.IsSuccess) ClearDsc1ReleaseWinner(record);
        return source;
    }

    internal KernelResult CommitDsc1LocalReservationRelease(
        PlatformDsc1CopySubmission submission,
        Func<bool,KernelResult> consume)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            var record = FindOriginalDsc1OperationLocked(submission.SubmissionId);
            if (record is null ||
                record.Submission != submission ||
                record.State is not
                    (Dsc1OperationState.ClosedCompleted or
                     Dsc1OperationState.ClosedCancelled) ||
                record.LocalReservationsReleased || !HasValidatedDsc1Closure(record))
            {
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "The exact DSC1 local reservation release owner changed.");
            }
            var current = ValidateDsc1OriginalOwnersLocked(record, record.LastCleanupSource);
            if (!current.IsSuccess)
            {
                record.ProviderCallInFlight = false;
                return current;
            }
            // Trusted local buffer/registry mutation, with the kernel memory gate
            // held. No provider/composition/notification callback enters this gate.
            var consumed=consume(record.PublicationAdmitted);
            if(!consumed.IsSuccess)return consumed;
            record.LocalReservationsReleased = true;
            _dsc1Operations.Remove(submission.SubmissionId);
            record.OriginalDomain.Dsc1Operations.Remove(record);
            return KernelResult.Ok();
        }
    }

    internal bool HasActiveDsc1Operations(PlatformDomainBinding binding)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            return OriginalDsc1OperationsLocked().Any(record =>
                !record.LocalReservationsReleased &&
                record.Submission.DomainBinding.BindingId == binding.BindingId);
        }
    }

    internal PlatformDsc1CopySubmission[] Dsc1OperationsForMapping(
        PlatformRegionMapping mapping)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            return OriginalDsc1OperationsLocked()
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
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            return OriginalDsc1OperationsLocked()
                .Where(record =>
                    !record.LocalReservationsReleased &&
                    (record.ComputeCapabilityId == capabilityId ||
                     record.OriginalSourceMapping
                         .AuthorityCapabilityId == capabilityId ||
                     record.OriginalDestinationMapping
                         .AuthorityCapabilityId == capabilityId))
                .Select(static record => record.Submission)
                .OrderBy(static submission => submission.SubmissionId.Value)
                .ToArray();
        }
    }

    internal PlatformDsc1CopySubmission[] Dsc1OperationsForSubject(
        PlatformDomainIdentity subject)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            return OriginalDsc1OperationsLocked()
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
        record.OriginalCompletionObservation = providerResult;
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
        PlatformDomainIdentity expectedSubject,
        bool cleanup = false)
    {
        var record = FindOriginalDsc1OperationLocked(submission.SubmissionId);
        if (record is null)
        {
            return KernelResult<Dsc1OperationRecord>.Fail(
                KernelError.PlatformBindingNotFound,
                "The local DSC1 submission does not exist.");
        }

        if (!ReferenceEquals(record.OriginalOwner, record))
            return KernelResult<Dsc1OperationRecord>.Fail(KernelError.PlatformFaulted,
                "Copied DSC1 history cannot replace the original operation owner.");
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

        if (_dsc1Operations.TryGetValue(submission.SubmissionId, out var routed) && !ReferenceEquals(record, routed))
            return KernelResult<Dsc1OperationRecord>.Fail(KernelError.PlatformFaulted,
                "Foreign DSC1 routing cannot replace the original operation owner.");
        if (!cleanup && record.State != Dsc1OperationState.Submitting &&
            record.AcceptedProviderSubmission.HasValue && !record.PublicationAdmitted)
            return KernelResult<Dsc1OperationRecord>.Fail(KernelError.PlatformFaulted,
                "DSC1 admission was not published; only exact lifetime cleanup is permitted.");
        var domain = cleanup ? ValidateDomainIdentity(submission.DomainBinding, expectedSubject) :
            ValidateDomain(submission.DomainBinding, expectedSubject);
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
        if (!HasValidatedDsc1Closure(record))
        {
            terminal = default;
            return false;
        }
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
        QuarantineDomain(record.OriginalDomain);
    }

    private static bool HasValidatedDsc1Closure(Dsc1OperationRecord record) =>
        ReferenceEquals(record.OriginalOwner, record) && (HasValidatedDsc1BackingClosure(record) || (record.AcceptedProviderSubmission is { } submission &&
        record.OriginalCompletionObservation is { IsSuccess: true } observation &&
        PlatformDsc1ComputeContract.ValidateCompletion(submission, observation.Value!).IsSuccess &&
        ((record.State == Dsc1OperationState.ClosedCompleted && observation.Value!.Disposition == PlatformDsc1CompletionDisposition.Completed) ||
         (record.State == Dsc1OperationState.ClosedCancelled && observation.Value!.Disposition == PlatformDsc1CompletionDisposition.Cancelled))));

    internal bool IsDsc1PublicationAdmitted(PlatformDsc1CopySubmission submission)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate) return FindOriginalDsc1OperationLocked(submission.SubmissionId) is { PublicationAdmitted: true } record &&
            record.Submission == submission;
    }

    internal void RefuseDsc1OutputPublication(PlatformDsc1CopySubmission submission)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            if (FindOriginalDsc1OperationLocked(submission.SubmissionId) is { } record && record.Submission == submission)
            {
                record.PublicationAdmitted = false;
                record.ProviderCallInFlight = false;
            }
        }
    }

    private IEnumerable<Dsc1OperationRecord> OriginalDsc1OperationsLocked() =>
        _activeSubjects.Values.Concat(_domains.Values)
            .Concat(_dsc1Operations.Values.Select(record => record.OriginalDomain))
            .Distinct<DomainRecord>(ReferenceEqualityComparer.Instance)
            .SelectMany(domain => domain.Dsc1Operations)
            .Distinct<Dsc1OperationRecord>(ReferenceEqualityComparer.Instance);

    private Dsc1OperationRecord? FindOriginalDsc1OperationLocked(PlatformDsc1SubmissionId id) =>
        OriginalDsc1OperationsLocked().FirstOrDefault(record => record.Submission.SubmissionId == id);

    private KernelResult ValidateDsc1OriginalOwnersLocked(Dsc1OperationRecord record, PlatformProviderIncarnation source)
    {
        if (!ReferenceEquals(record.OriginalOwner, record))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original DSC1 operation owner was copied.");
        var binding = record.Submission.DomainBinding;
        var root = ValidateRootCleanupSourceLocked(record.OriginalDomain, binding, binding.Subject,
            record.OriginalDomain.BackendEpoch, source);
        if (!root.IsSuccess) return root;
        if (!record.OriginalDomain.Dsc1Operations.Contains(record) || record.LocalReservationsReleased ||
            record.OriginalDomain.AuthorityState == DomainAuthorityState.Closed ||
            record.OriginalDomain.ParentRevokeInFlight || record.OriginalDomain.ParentRevokeMayHaveEffect ||
            (_dsc1Operations.TryGetValue(record.Submission.SubmissionId, out var routed) && !ReferenceEquals(record, routed)) ||
            !_mappings.TryGetValue(record.Submission.Source.Mapping.MappingId, out var currentSource) ||
            !ReferenceEquals(currentSource, record.OriginalSourceMapping) ||
            !_mappings.TryGetValue(record.Submission.Destination.Mapping.MappingId, out var currentDestination) ||
            !ReferenceEquals(currentDestination, record.OriginalDestinationMapping) ||
            currentSource.ProviderLease != record.OriginalRequest.Source.Mapping ||
            currentDestination.ProviderLease != record.OriginalRequest.Destination.Mapping ||
            currentSource.ProviderIncarnation != source || currentDestination.ProviderIncarnation != source ||
            currentSource.BackendEpoch != record.OriginalDomain.BackendEpoch ||
            currentDestination.BackendEpoch != record.OriginalDomain.BackendEpoch ||
            currentSource.LocalReservationReleased || currentDestination.LocalReservationReleased ||
            currentSource.ClosureState == PlatformExternalClosureState.Closed ||
            currentDestination.ClosureState == PlatformExternalClosureState.Closed)
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Original DSC1 owner/source dependency changed; lifetime remains pinned.");
        return KernelResult.Ok();
    }

    private void RetainDsc1NoEffectReservation(Dsc1OperationRecord record)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            // This path is reached only before the first provider invocation.
            // No-effect does not close the kernel's independent byte views.
            record.ProviderNotInvoked = true;
            record.State = Dsc1OperationState.ClosedCancelled;
            record.ProviderCallInFlight = false;
        }
    }

    internal KernelResult? TryCommitDsc1UninvokedRelease(
        PlatformDsc1CopySubmission submission, Func<KernelResult> consume)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            var record = FindOriginalDsc1OperationLocked(submission.SubmissionId);
            if (record is not { ProviderNotInvoked: true }) return null;
            if (record.Submission != submission || record.State != Dsc1OperationState.ClosedCancelled ||
                record.AcceptedProviderSubmission.HasValue || record.OriginalBackingObservation is not null)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original DSC1 non-invocation fact changed.");
            // Local rollback is justified by the retained pre-invocation fact,
            // independently of a live provider source or active permission.
            var owner = ValidateDsc1OriginalOwnersLocked(record, record.OriginalSourceMapping.ProviderIncarnation);
            if (!owner.IsSuccess) return owner;
            var released = consume();
            if (!released.IsSuccess) return released;
            record.LocalReservationsReleased = true;
            _dsc1Operations.Remove(submission.SubmissionId);
            record.OriginalDomain.Dsc1Operations.Remove(record);
            return KernelResult.Ok();
        }
    }

    private KernelResult ReadDsc1CleanupSource(Dsc1OperationRecord record)
    {
        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult.Fail(KernelError.PlatformFaulted, "Original DSC1 source unavailable; known lifetime retained."); }
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate)
        {
            var valid = ValidateDsc1OriginalOwnersLocked(record, source);
            if (valid.IsSuccess) record.LastCleanupSource = source;
            return valid;
        }
    }

    private void ClearDsc1ReleaseWinner(Dsc1OperationRecord record)
    {
        lock (_secureDomainLifecycleGate)
        lock (_dsc1Gate) record.ProviderCallInFlight = false;
    }
}
