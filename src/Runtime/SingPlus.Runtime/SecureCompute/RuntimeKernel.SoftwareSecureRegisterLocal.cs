using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>Historical OS publication result, never a reusable execution permission.</summary>
public sealed record SoftwareSecureRegisterLocalResult(ExternalOperationHandle Operation,
    bool ResourcesClosed, bool WasPublished, ulong? Value, ulong MachineAttempt, ulong RegisterLocalCommit);

public sealed partial class RuntimeKernel
{
    private sealed record SoftwareRegisterLocalPublication(PlatformSoftwareSecureAdmissionObservation Proof,
        ExternalPublicationDecisionV1 Decision, ulong Value);

    internal Action? BeforeSoftwareSecureRegisterLocalPublication { private get; set; }
    internal Action? AfterSoftwareSecureRegisterLocalStaging { private get; set; }

    public KernelResult<SoftwareSecureRegisterLocalResult> ExecuteSoftwareSecureRegisterLocal(ProcessHandle owner,
        VirtualDomainHandle domain, CapabilityId virtualCapability, CapabilityId executeCapability, ulong left, ulong right)
    {
        var result = ObserveSoftwareSecureAdmissionCore(owner, domain, virtualCapability, executeCapability, left, right, true);
        return result.IsSuccess ? ReadSoftwareSecureRegisterLocal(owner, result.Value!.Operation)
            : KernelResult<SoftwareSecureRegisterLocalResult>.Fail(result.Error, result.Message!);
    }

    public KernelResult<SoftwareSecureRegisterLocalResult> ResolveSoftwareSecureRegisterLocal(ProcessHandle owner,
        ExternalOperationHandle operation)
    {
        lock (_platformMemoryUseGate)
            if (!_softwareSecureRoutes.TryGetValue(operation, out var record) || record.Owner != owner ||
                !record.Request.ExecuteRegisterLocal || !HasOriginalSoftwareSecureRecord(record))
                return KernelResult<SoftwareSecureRegisterLocalResult>.Fail(KernelError.StaleGeneration, "Original arithmetic operation unavailable.");
        var resolved = ResolveSoftwareSecureAdmission(owner, operation);
        return resolved.IsSuccess ? ReadSoftwareSecureRegisterLocal(owner, operation)
            : KernelResult<SoftwareSecureRegisterLocalResult>.Fail(resolved.Error, resolved.Message!);
    }

    private KernelResult<SoftwareSecureRegisterLocalResult> ReadSoftwareSecureRegisterLocal(ProcessHandle owner,
        ExternalOperationHandle operation)
    {
        lock (_platformMemoryUseGate)
        {
            if (!_softwareSecureRoutes.TryGetValue(operation, out var record) || record.Owner != owner ||
                !record.Request.ExecuteRegisterLocal || !HasOriginalSoftwareSecureRecord(record))
                return KernelResult<SoftwareSecureRegisterLocalResult>.Fail(KernelError.StaleGeneration, "Original arithmetic result owner unavailable.");
            var published = record.PublishedResult;
            bool visible = record.Closed && published is not null && CommitSoftwareSecureSource(record, KernelResult.Ok).IsSuccess;
            return KernelResult<SoftwareSecureRegisterLocalResult>.Ok(new(operation, record.Closed, published is not null,
                visible ? published!.Value : null, visible ? published!.Proof.MachineAttempt : 0,
                visible ? published!.Proof.RegisterLocalCommit : 0));
        }
    }

    private void FinishSoftwareRegisterLocalOperation(SoftwareSecureRecord record,
        PlatformSoftwareSecureAdmissionObservation proof, bool allowPublication)
    {
        if (record.PublishedResult is not null) return;
        if (allowPublication && !record.PublicationAttempted && record.Binding is { } binding &&
            proof.RegisterLocalValue is { } value && proof.RegisterLocalCommit != 0 && proof.MachineAttempt != 0 &&
            value == unchecked(record.Request.Left + record.Request.Right))
        {
            record.PublicationAttempted = true;
            var completed = RecordExternalOperationCompletion(record.Owner, new(binding, ExternalOperationCompletionDisposition.Completed));
            var visible = completed.IsSuccess
                ? RecordExternalOperationVisibility(record.Owner, new(binding, ExternalVisibilityRequirement.None, true))
                : completed;
            if (visible.IsSuccess)
            {
                try
                {
                    BeforeSoftwareSecureRegisterLocalPublication?.Invoke();
                    ExternalOperations.PublishSoftwareRegisterLocal(record.Operation, proof,
                        visible.Value!.Admission!.Dependencies,
                        decision =>
                        {
                            lock (_platformMemoryUseGate)
                            {
                                if (!HasOriginalSoftwareSecureRecord(record) || record.Candidate is not null ||
                                    record.PublishedResult is not null || !ReferenceEquals(record.Observation, proof))
                                    throw new InvalidOperationException("Original arithmetic staging owner changed.");
                                record.Candidate = new(proof, decision, value);
                            }
                            AfterSoftwareSecureRegisterLocalStaging?.Invoke();
                        },
                        commit => CommitSoftwareRegisterLocalPublication(record, proof, commit, commitSink: true),
                        admit => CommitSoftwareRegisterLocalPublication(record, proof, admit, commitSink: false));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
                if (record.PublishedResult is null && record.Candidate is { } candidate)
                {
                    // The only action staged a private candidate. This exact sink owner can
                    // prove no OS publication occurred, independently of provider cleanup.
                    _ = ReconcileFailedExternalPublication(record.Owner, record.Operation, candidate.Decision, () =>
                    {
                        lock (_platformMemoryUseGate)
                            return HasOriginalSoftwareSecureRecord(record) && ReferenceEquals(record.Candidate, candidate) &&
                                record.PublishedResult is null
                                ? KernelResult.Ok() : KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original publication sink unavailable.");
                    });
                }
            }
        }
        if (record.PublishedResult is null)
        {
            if (record.Binding is { } pending)
                _ = RecordExternalOperationCompletion(record.Owner, new(pending, ExternalOperationCompletionDisposition.Cancelled));
            _ = CancelExternalOperation(record.Owner, record.Operation, false);
        }
    }

    private KernelResult<ExternalOperationSnapshot> CommitSoftwareRegisterLocalPublication(SoftwareSecureRecord record,
        PlatformSoftwareSecureAdmissionObservation proof, Func<KernelResult<ExternalOperationSnapshot>> commit, bool commitSink)
    {
        lock (_platformMemoryUseGate)
        {
            KernelResult<ExternalOperationSnapshot> outcome = default;
            KernelResult source = CommitSoftwareSecureSource(record, () =>
            {
                if (!ReferenceEquals(record.Observation, proof) || !proof.ResourcesClosed ||
                    commitSink && (record.Candidate is not { } candidate || !ReferenceEquals(candidate.Proof, proof) || record.PublishedResult is not null))
                    return KernelResult.Fail(KernelError.PlatformDenied, "Original arithmetic publication candidate changed.");
                outcome = commit();
                if (outcome.IsSuccess && commitSink)
                    record.PublishedResult = record.Candidate; // OS-visible sink changes only at final source/owner commit.
                return outcome.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(outcome.Error, outcome.Message!);
            });
            return source.IsSuccess ? outcome : KernelResult<ExternalOperationSnapshot>.Fail(source.Error, source.Message!);
        }
    }
}
