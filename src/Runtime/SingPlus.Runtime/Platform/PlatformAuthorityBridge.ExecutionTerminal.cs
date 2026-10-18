using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal bool HasOriginalExecutionTerminalProducer => _provider is IPlatformExecutionTerminalProvider;
    private KernelResult<PlatformVirtualTrapEvidence> ObserveOriginalExecutionTrap(PlatformChildBinding binding,
        IPlatformExecutionTerminalProvider provider, Func<Func<KernelResult>, KernelResult>? admissionConsumer,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer)
    {
        var read = ReadOriginalExecutionTerminal(binding, provider, false, admissionConsumer, sourceCommitConsumer, completionConsumer);
        if (!read.IsSuccess) return KernelResult<PlatformVirtualTrapEvidence>.Fail(read.Error, read.Message!);
        var kind = read.Value.Kind switch
        {
            PlatformExecutionTerminalKind.BudgetExhausted => PlatformVirtualTrapKind.ExecutionBudgetExhausted,
            PlatformExecutionTerminalKind.KernelRejected => PlatformVirtualTrapKind.KernelRejected,
            _ => PlatformVirtualTrapKind.ExecutionFailure,
        };
        var receipt = read.Value.Receipt;
        return KernelResult<PlatformVirtualTrapEvidence>.Ok(new(receipt.ChildLeaseId, receipt.ChildGeneration,
            receipt.ParentLeaseId, receipt.ParentGeneration, receipt.RetiredSequence, kind));
    }

    internal KernelResult<PlatformExecutionTerminalObservation> ReadCompletedExecution(PlatformChildBinding binding,
        Func<Func<KernelResult>, KernelResult> admissionConsumer, Func<Func<KernelResult>, KernelResult> sourceCommitConsumer,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult> completionConsumer) =>
        _provider is IPlatformExecutionTerminalProvider provider
            ? ReadOriginalExecutionTerminal(binding, provider, true, admissionConsumer, sourceCommitConsumer, completionConsumer)
            : KernelResult<PlatformExecutionTerminalObservation>.Fail(KernelError.PlatformUnsupported, "Original terminal producer unavailable.");
    private KernelResult<PlatformExecutionTerminalObservation> ReadOriginalExecutionTerminal(PlatformChildBinding binding,
        IPlatformExecutionTerminalProvider provider, bool completedOnly, Func<Func<KernelResult>, KernelResult>? admissionConsumer,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer,
        Func<Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer)
    {
        var resolved = ResolveChild(binding);
        if (!resolved.IsSuccess) return KernelResult<PlatformExecutionTerminalObservation>.Fail(resolved.Error, resolved.Message!);
        var child = resolved.Value!;
        ExecutableArtifactRecord artifact = null!;
        ExecutableArtifactSource owner = null!;
        long childRevision = 0, guestRevision = 0, parentRevision = 0;
        PlatformBackendEpoch epoch = default;
        Dictionary<ChildBindingRecord, long> lineage = [];
        bool pending = false;
        PlatformAuthorityResult<PlatformExecutionTerminalObservation>? admittedTerminal = null;
        KernelResult ValidateSource(PlatformProviderIncarnation incarnation) =>
            incarnation.Value != 0 && incarnation == owner.Root.ProviderIncarnation && incarnation == owner.Parent.ProviderIncarnation &&
            BackendEpoch == epoch && owner.Root.BackendEpoch == epoch && !_backendEpochExhausted &&
            TryGetCanonicalRootRecord(binding.ParentBinding.BindingId, out var root) && ReferenceEquals(root, owner.Root) &&
            root.Binding == binding.ParentBinding && root.ProviderLease == child.ProviderLease.ParentDomainLease
                ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "Original terminal root/source changed.");
        KernelResult Validate()
        {
            if (!GuestEffectLineageCurrent(lineage) || BackendEpoch != epoch || child.LifecycleRevisionExhausted || owner.Guest.LifecycleRevisionExhausted || owner.Parent.LifecycleRevisionExhausted ||
                child.LifecycleRevision != childRevision || owner.Guest.LifecycleRevision != guestRevision || owner.Parent.LifecycleRevision != parentRevision ||
                !ReferenceEquals(artifact.AdmissionSource, owner) || !artifact.Started || artifact.StartInFlight || artifact.ReleaseInvoked || artifact.Released ||
                !_executableArtifacts.TryGetValue(artifact.Binding.BindingId, out var route) || !ReferenceEquals(route, artifact) ||
                !_originalExecutableArtifactOwners.TryGetValue(artifact.Binding.BindingId, out var original) || !ReferenceEquals(original, artifact) ||
                artifact.StartInvocation is not { Invoked: true, NoEffectConsumed: false } start || !ReferenceEquals(start.OriginalOwner, start) ||
                start.PublishedChildRevision != child.LifecycleRevision || start.Ancestors.Any(pair =>
                    pair.Key.LifecycleRevisionExhausted || pair.Key.LifecycleRevision != pair.Value) ||
                !ReferenceEquals(start.Source, owner) || !PlatformVirtualTrapContract.ValidateObservationRequest(child.ProviderLease, child.State).IsSuccess)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original terminal publication owner/source changed.");
            return ValidateExecutableInputs(child, owner.Guest, owner.Parent);
        }
        KernelResult Begin()
        {
            lock (_secureDomainLifecycleGate)
            {
                if (pending) return KernelResult.Fail(KernelError.PlatformBindingActive, "Original terminal query is already pending.");
                var matches = _originalExecutableArtifactOwners.Values.Where(item => item.Started && !item.Released && ReferenceEquals(item.AdmissionSource?.Child, child)).ToArray();
                if (matches.Length != 1 || matches[0].AdmissionSource is not { } source)
                    return KernelResult.Fail(KernelError.PlatformUnsupported, "No original terminal executable producer fact.");
                artifact = matches[0]; owner = source; epoch = BackendEpoch;
                admittedTerminal = artifact.TerminalObservation;
                var captured = CaptureGuestEffectLineage(child);
                if (!captured.IsSuccess) return KernelResult.Fail(captured.Error, captured.Message!);
                lineage = captured.Value!;
                if (lineage.Keys.Any(record => record.PendingChildEffects != 0) || owner.Guest.PendingExecutableEffects != 0)
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Original terminal dependencies are pending.");
                childRevision = child.LifecycleRevision; guestRevision = owner.Guest.LifecycleRevision; parentRevision = owner.Parent.LifecycleRevision;
                var current = Validate();
                if (!current.IsSuccess) return current;
                KernelResult Commit()
                {
                    if (pending) return KernelResult.Fail(KernelError.PlatformBindingActive, "Original terminal query admission cannot overlap.");
                    foreach (var record in lineage.Keys) record.PendingChildEffects++;
                    owner.Guest.PendingExecutableEffects++; pending = true;
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        void End()
        {
            lock (_secureDomainLifecycleGate)
                if (pending) { foreach (var record in lineage.Keys) record.PendingChildEffects--; owner.Guest.PendingExecutableEffects--; pending = false; }
        }
        KernelResult begin;
        try { begin = admissionConsumer is null ? Begin() : admissionConsumer(Begin); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { End(); return KernelResult<PlatformExecutionTerminalObservation>.Fail(KernelError.PlatformFaulted, "Terminal admission failed: " + exception.GetType().Name); }
        if (!begin.IsSuccess || !pending)
        { End(); return KernelResult<PlatformExecutionTerminalObservation>.Fail(begin.IsSuccess ? KernelError.PlatformFaulted : begin.Error, begin.Message ?? "Terminal admission was not committed."); }
        try
        {
            var incarnation = CurrentProviderIncarnation(); // External callbacks outside commit locks.
            lock (_secureDomainLifecycleGate)
            {
                var source = ValidateSource(incarnation); var current = Validate();
                if (!source.IsSuccess || !current.IsSuccess)
                    return KernelResult<PlatformExecutionTerminalObservation>.Fail(KernelError.PlatformFaulted, "Typed terminal source changed before pure lookup.");
            }
            var raw = provider.ResolveExecutionTerminal(child.ProviderLease);
            lock (_secureDomainLifecycleGate) artifact.TerminalObservation ??= raw; // A failed pure read cannot replace the original Start outcome.
            incarnation = CurrentProviderIncarnation();
            if (!raw.IsSuccess) return FromProviderFailure<PlatformExecutionTerminalObservation>(raw.Status, raw.Message);
            PlatformVirtualTrapKind? kind = raw.Value.Kind switch
            {
                PlatformExecutionTerminalKind.BudgetExhausted => PlatformVirtualTrapKind.ExecutionBudgetExhausted,
                PlatformExecutionTerminalKind.KernelRejected => PlatformVirtualTrapKind.KernelRejected,
                PlatformExecutionTerminalKind.ExecutionFailure => PlatformVirtualTrapKind.ExecutionFailure,
                _ => null,
            };
            if (!completedOnly && raw.Value.Kind == PlatformExecutionTerminalKind.Completed)
                return KernelResult<PlatformExecutionTerminalObservation>.Fail(KernelError.PlatformUnsupported, "Exact completed execution has no terminal failure trap.");
            if (completedOnly && raw.Value.ProcessExitCode is null)
                return KernelResult<PlatformExecutionTerminalObservation>.Fail(KernelError.PlatformUnsupported, "Original completed program result is unavailable.");
            if ((!completedOnly && kind is null || completedOnly && raw.Value.Kind != PlatformExecutionTerminalKind.Completed) || artifact.ExecutionReceipt is not { } admittedReceipt || raw.Value.Receipt != admittedReceipt ||
                admittedTerminal is { IsSuccess: true } originalTerminal && raw.Value != originalTerminal.Value)
                return KernelResult<PlatformExecutionTerminalObservation>.Fail(KernelError.PlatformFaulted, "Typed terminal evidence is unknown or foreign.");
            var evidence = new PlatformVirtualTrapEvidence(child.ProviderLease.LeaseId, child.ProviderLease.Generation,
                child.ProviderLease.ParentDomainLease.LeaseId, child.ProviderLease.ParentDomainLease.Generation,
                admittedReceipt.RetiredSequence, kind ?? PlatformVirtualTrapKind.ExecutionFailure);
            bool committed = false;
            KernelResult Commit(Func<KernelResult> local)
            {
                lock (_secureDomainLifecycleGate)
                {
                    var source = ValidateSource(incarnation); var current = Validate();
                    if (!source.IsSuccess) return source;
                    if (!current.IsSuccess) return current;
                    if (!completedOnly && !PlatformVirtualTrapContract.ValidateEvidence(child.ProviderLease, evidence).IsSuccess)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Invalid original terminal trap evidence.");
                    var result = local(); committed = result.IsSuccess; return result;
                }
            }
            var publication = completionConsumer is null ? Commit(() => KernelResult.Ok()) : completionConsumer(Commit);
            return publication.IsSuccess && committed ? KernelResult<PlatformExecutionTerminalObservation>.Ok(raw.Value) :
                KernelResult<PlatformExecutionTerminalObservation>.Fail(publication.IsSuccess ? KernelError.PlatformFaulted : publication.Error,
                    publication.Message ?? "Typed terminal publication was not committed.");
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformExecutionTerminalObservation>.Fail(KernelError.PlatformFaulted, "Typed terminal pure lookup/source failed: " + exception.GetType().Name); }
        finally
        {
            End();
        }
    }
}

