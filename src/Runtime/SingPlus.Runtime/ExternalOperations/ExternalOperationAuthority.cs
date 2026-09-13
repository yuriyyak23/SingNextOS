using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed class ExternalOperationAuthority
{
    private sealed class Record
    {
        public required OperationPreparation Preparation { get; init; }
        public required ExternalOperationState State { get; set; }
        public required ExternalOperationDisposition Disposition { get; set; }
        public OperationAdmissionSnapshot? Admission { get; set; }
        public OperationBinding? Binding { get; set; }
        public ExternalEffectBoundaryState EffectBoundary { get; set; }
        public List<ExternalOperationTransition> Transitions { get; } = [];
        public ulong NextTransitionSequence { get; set; } = 1;
    }

    private readonly object _gate = new();
    private readonly RegionAuthority _regions;
    private readonly Dictionary<ExternalOperationId, Record> _operations = [];
    private ulong _nextOperationId = 1;
    private ulong _nextBindingId = 1;

    internal ExternalOperationAuthority(RegionAuthority regions) => _regions = regions;

    public KernelResult<OperationPreparation> Prepare(
        RegionOwner principal,
        IReadOnlyList<OperationRegionUseRequest> regionUses,
        ExternalVisibilityRequirement visibilityRequirement,
        ExternalPublicationPolicy publicationPolicy,
        ExternalEffectPolicy? effectPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(regionUses);
        lock (_gate)
        {
            if (principal.DomainId.Value == 0 || principal.ProcessGeneration == 0)
                return KernelResult<OperationPreparation>.Fail(KernelError.WrongRegionOwner, "External operation principal is invalid.");
            if (regionUses.Count == 0)
                return KernelResult<OperationPreparation>.Fail(KernelError.InvalidRegionState, "External operations require at least one region-use request.");
            if (!Enum.IsDefined(visibilityRequirement) || !Enum.IsDefined(publicationPolicy))
                return KernelResult<OperationPreparation>.Fail(KernelError.InvalidTransition, "External operation visibility or publication policy is invalid.");
            var selectedEffect = effectPolicy ?? new ExternalEffectPolicy(
                ExternalEffectClass.StagedReversibleUntilPublish,
                ExternalReplayProtection.None,
                false);
            var effectValidation = ValidateEffectPolicy(publicationPolicy, selectedEffect);
            if (!effectValidation.IsSuccess)
                return KernelResult<OperationPreparation>.Fail(effectValidation.Error, effectValidation.Message!);
            if (_nextOperationId == 0)
                return KernelResult<OperationPreparation>.Fail(KernelError.CapacityExhausted, "External operation identity space is exhausted.");

            var requests = Array.AsReadOnly(regionUses.ToArray());
            var handle = new ExternalOperationHandle(new ExternalOperationId(_nextOperationId++), new OperationGeneration(1));
            var preparation = new OperationPreparation(handle, principal, requests, visibilityRequirement, publicationPolicy, selectedEffect);
            var record = new Record
            {
                Preparation = preparation,
                State = ExternalOperationState.Prepared,
                Disposition = ExternalOperationDisposition.Active
            };
            AddTransition(record, ExternalOperationState.Prepared, "Prepared");
            _operations.Add(handle.OperationId, record);
            return KernelResult<OperationPreparation>.Ok(preparation);
        }
    }

    public KernelResult<OperationAdmissionSnapshot> Admit(
        ExternalOperationHandle operation,
        OperationDependencySnapshot dependencies,
        ExternalServiceIdentity serviceIdentity = default,
        ExternalCancellationSupport cancellationSupport = ExternalCancellationSupport.BeforeSubmissionOnly)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess)
                return KernelResult<OperationAdmissionSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State != ExternalOperationState.Prepared || record.Disposition != ExternalOperationDisposition.Active)
                return InvalidTransition<OperationAdmissionSnapshot>(record, "Only an active Prepared operation can be admitted.");

            var acquired = new List<RegionUseDescriptor>();
            foreach (var request in record.Preparation.RegionUses)
            {
                var use = _regions.AcquireUse(request.Region, record.Preparation.Principal, request.Mode, request.Range);
                if (!use.IsSuccess)
                {
                    foreach (var prior in acquired)
                        _ = _regions.ReleaseUse(prior.Handle, record.Preparation.Principal);
                    return KernelResult<OperationAdmissionSnapshot>.Fail(use.Error, use.Message!);
                }
                acquired.Add(use.Value!);
            }

            var admission = new OperationAdmissionSnapshot(
                operation,
                record.Preparation.Principal,
                Array.AsReadOnly(acquired.ToArray()),
                dependencies,
                serviceIdentity,
                cancellationSupport,
                record.Preparation.EffectPolicy.EffectClass,
                record.Preparation.PublicationPolicy);
            record.Admission = admission;
            Move(record, ExternalOperationState.Admitted, "Admitted");
            return KernelResult<OperationAdmissionSnapshot>.Ok(admission);
        }
    }

    public KernelResult<OperationBinding> RecordSubmission(
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<OperationBinding>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State != ExternalOperationState.Admitted || record.Disposition != ExternalOperationDisposition.Active || record.Admission is null)
                return InvalidTransition<OperationBinding>(record, "Submission requires an active Admitted operation.");
            if (record.Admission.Dependencies != currentDependencies)
                return KernelResult<OperationBinding>.Fail(KernelError.StaleGeneration, "Operation dependency generation changed before provider submission.");
            var uses = ValidateUses(record);
            if (!uses.IsSuccess) return KernelResult<OperationBinding>.Fail(uses.Error, uses.Message!);
            if (_nextBindingId == 0)
                return KernelResult<OperationBinding>.Fail(KernelError.CapacityExhausted, "External operation binding identity space is exhausted.");

            var binding = new OperationBinding(operation, new OperationBindingId(_nextBindingId++), 1);
            record.Binding = binding;
            record.EffectBoundary = record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged
                ? ExternalEffectBoundaryState.StagedPending
                : record.Preparation.EffectPolicy.EffectClass == ExternalEffectClass.IrreversibleBarrier
                    ? ExternalEffectBoundaryState.Irreversible
                    : ExternalEffectBoundaryState.ExternallyVisible;
            Move(record, ExternalOperationState.Submitted, "Submitted");
            return KernelResult<OperationBinding>.Ok(binding);
        }
    }

    public KernelResult<ExternalOperationSnapshot> RecordCompletion(OperationCompletion completion)
    {
        lock (_gate)
        {
            var resolved = Resolve(completion.Binding.Operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State != ExternalOperationState.Submitted || record.Binding != completion.Binding)
                return InvalidTransition<ExternalOperationSnapshot>(record, "Completion does not match the exact active Submitted binding.");
            if (!Enum.IsDefined(completion.Disposition))
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformFaulted, "Provider completion disposition is invalid.");

            record.Disposition = completion.Disposition switch
            {
                ExternalOperationCompletionDisposition.Completed => ExternalOperationDisposition.Completed,
                ExternalOperationCompletionDisposition.Cancelled => ExternalOperationDisposition.Cancelled,
                _ => ExternalOperationDisposition.Faulted
            };
            Move(record, ExternalOperationState.DeviceComplete, completion.Disposition == ExternalOperationCompletionDisposition.Completed ? "DeviceComplete" : $"DeviceComplete:{completion.Disposition}");
            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    public KernelResult<ExternalOperationSnapshot> RecordVisibility(OperationVisibilityEvidence evidence)
    {
        lock (_gate)
        {
            var resolved = Resolve(evidence.Binding.Operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State != ExternalOperationState.DeviceComplete ||
                record.Disposition != ExternalOperationDisposition.Completed ||
                record.Binding != evidence.Binding ||
                evidence.Requirement != record.Preparation.VisibilityRequirement)
                return InvalidTransition<ExternalOperationSnapshot>(record, "Visibility evidence does not match the exact completed operation and requirement.");
            if (!evidence.Satisfied)
            {
                record.Disposition = ExternalOperationDisposition.Faulted;
                AddTransition(record, record.State, "VisibilityFailed");
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformFaulted, "Required external-operation visibility was not satisfied; publication remains forbidden.");
            }

            Move(record, ExternalOperationState.Visible, "Visible");
            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    public KernelResult<ExternalOperationSnapshot> Publish(
        ExternalOperationHandle operation,
        OperationDependencySnapshot currentDependencies,
        PublicationPlan plan,
        Action publicationAction)
    {
        ArgumentNullException.ThrowIfNull(publicationAction);
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State != ExternalOperationState.Visible || record.Disposition != ExternalOperationDisposition.Completed || record.Admission is null)
                return InvalidTransition<ExternalOperationSnapshot>(record, "Publication requires a completed and Visible operation.");
            if (plan.Policy != record.Preparation.PublicationPolicy)
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.InvalidTransition, "Publication plan does not match the admitted policy.");
            if (record.Admission.Dependencies != currentDependencies)
            {
                FailPublicationRevalidation(record);
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.StaleGeneration, "Operation dependency generation changed before publication.");
            }
            var uses = ValidateUses(record);
            if (!uses.IsSuccess)
            {
                FailPublicationRevalidation(record);
                return KernelResult<ExternalOperationSnapshot>.Fail(uses.Error, uses.Message!);
            }

            try
            {
                publicationAction();
            }
            catch (Exception exception)
            {
                record.Disposition = record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged
                    ? ExternalOperationDisposition.Discarded
                    : ExternalOperationDisposition.Faulted;
                AddTransition(record, record.State, "PublicationFailed");
                return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformFaulted, $"Publication action failed closed: {exception.Message}");
            }

            record.Disposition = ExternalOperationDisposition.Published;
            if (record.EffectBoundary == ExternalEffectBoundaryState.StagedPending)
                record.EffectBoundary = ExternalEffectBoundaryState.ExternallyVisible;
            Move(record, ExternalOperationState.Published, "Published");
            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    public KernelResult<ExternalOperationSnapshot> Cancel(
        ExternalOperationHandle operation,
        bool providerCancellationSupported)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State == ExternalOperationState.Released)
                return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
            if (record.State == ExternalOperationState.Published)
                return InvalidTransition<ExternalOperationSnapshot>(record, "Published results cannot be cancelled or unpublished.");
            if (record.Disposition is ExternalOperationDisposition.Cancelled or ExternalOperationDisposition.Discarded or ExternalOperationDisposition.CancellationPending)
                return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));

            switch (record.State)
            {
                case ExternalOperationState.Prepared:
                case ExternalOperationState.Admitted:
                    record.Disposition = ExternalOperationDisposition.Cancelled;
                    AddTransition(record, record.State, "CancelledBeforeSubmit");
                    break;
                case ExternalOperationState.Submitted:
                    record.Disposition = ExternalOperationDisposition.CancellationPending;
                    AddTransition(record, record.State, providerCancellationSupported ? "ProviderCancellationRequested" : "DrainRequired");
                    break;
                case ExternalOperationState.DeviceComplete:
                case ExternalOperationState.Visible:
                    record.Disposition = record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged
                        ? ExternalOperationDisposition.Discarded
                        : ExternalOperationDisposition.Faulted;
                    AddTransition(record, record.State, record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged ? "StagedResultDiscarded" : "DirectWriteCannotBeUndone");
                    break;
                default:
                    return InvalidTransition<ExternalOperationSnapshot>(record, "Operation cannot be cancelled from its current state.");
            }

            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    public KernelResult<ExternalOperationSnapshot> RecordProviderLoss(ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State is not (ExternalOperationState.Submitted or ExternalOperationState.DeviceComplete or ExternalOperationState.Visible))
                return InvalidTransition<ExternalOperationSnapshot>(record, "Provider loss is relevant only after submission and before publication.");

            if (record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged)
            {
                record.Disposition = record.State == ExternalOperationState.Submitted
                    ? ExternalOperationDisposition.ProviderLost
                    : ExternalOperationDisposition.Discarded;
                foreach (var use in record.Admission?.RegionUses ?? [])
                    _ = _regions.InvalidateUse(use.Handle, record.Preparation.Principal);
            }
            else
            {
                record.Disposition = ExternalOperationDisposition.Faulted;
            }
            AddTransition(record, record.State, "ProviderLost");
            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    public KernelResult<ExternalOperationSnapshot> Release(
        ExternalOperationHandle operation,
        ReleasePlan plan)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            if (!resolved.IsSuccess) return KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State == ExternalOperationState.Released)
                return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
            if (!CanRelease(record, plan))
                return InvalidTransition<ExternalOperationSnapshot>(record, "Operation cannot release local authority until cancellation/publication and provider closure or loss are explicit.");

            KernelResult? firstFailure = null;
            foreach (var use in record.Admission?.RegionUses ?? [])
            {
                var release = _regions.ReleaseUse(use.Handle, record.Preparation.Principal);
                if (!release.IsSuccess) firstFailure ??= release;
            }
            if (firstFailure is { } failure)
                return KernelResult<ExternalOperationSnapshot>.Fail(failure.Error, failure.Message!);

            Move(record, ExternalOperationState.Released, "Released");
            return KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(record));
        }
    }

    public KernelResult<ExternalOperationSnapshot> Query(ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            var resolved = Resolve(operation);
            return resolved.IsSuccess
                ? KernelResult<ExternalOperationSnapshot>.Ok(Snapshot(resolved.Value!))
                : KernelResult<ExternalOperationSnapshot>.Fail(resolved.Error, resolved.Message!);
        }
    }

    internal KernelResult AdvanceForTeardown(RegionOwner principal)
    {
        lock (_gate)
        {
            foreach (var record in _operations.Values.Where(record => record.Preparation.Principal == principal && record.State != ExternalOperationState.Released))
            {
                if (record.State == ExternalOperationState.Submitted && record.Disposition != ExternalOperationDisposition.ProviderLost)
                {
                    record.Disposition = ExternalOperationDisposition.CancellationPending;
                    AddTransition(record, record.State, "TeardownDrainRequired");
                    return KernelResult.Fail(KernelError.PlatformBindingDraining, "Submitted external operation must reach exact completion or provider-loss containment before process reclaim.");
                }

                if (record.State is ExternalOperationState.DeviceComplete or ExternalOperationState.Visible && record.Disposition == ExternalOperationDisposition.Completed)
                {
                    record.Disposition = record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged
                        ? ExternalOperationDisposition.Discarded
                        : ExternalOperationDisposition.Faulted;
                    AddTransition(record, record.State, "TeardownResultDiscarded");
                }
                else if (record.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted)
                {
                    record.Disposition = ExternalOperationDisposition.Cancelled;
                    AddTransition(record, record.State, "TeardownCancelledBeforeSubmit");
                }

                var release = Release(
                    record.Preparation.Operation,
                    new ReleasePlan(
                        ProviderResourcesClosed: record.State != ExternalOperationState.Submitted,
                        ProviderUnavailable: record.Disposition == ExternalOperationDisposition.ProviderLost));
                if (!release.IsSuccess) return KernelResult.Fail(release.Error, release.Message!);
            }
            return KernelResult.Ok();
        }
    }

    private KernelResult ValidateUses(Record record)
    {
        if (record.Admission is null)
            return KernelResult.Fail(KernelError.InvalidTransition, "Operation has no admission snapshot.");
        foreach (var use in record.Admission.RegionUses)
        {
            var validation = _regions.ValidateUse(use.Handle, record.Preparation.Principal);
            if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        }
        return KernelResult.Ok();
    }

    private KernelResult<Record> Resolve(ExternalOperationHandle operation)
    {
        if (!_operations.TryGetValue(operation.OperationId, out var record))
            return KernelResult<Record>.Fail(KernelError.ExternalOperationNotFound, "External operation was not found.");
        if (record.Preparation.Operation.Generation != operation.Generation)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "External operation generation is stale.");
        if (record.Preparation.Operation != operation)
            return KernelResult<Record>.Fail(KernelError.InvalidMessage, "External operation handle is forged.");
        return KernelResult<Record>.Ok(record);
    }

    private static bool CanRelease(Record record, ReleasePlan plan)
    {
        if (record.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted)
            return record.Disposition == ExternalOperationDisposition.Cancelled;
        if (record.State == ExternalOperationState.Published)
            return record.Disposition == ExternalOperationDisposition.Published && (plan.ProviderResourcesClosed || plan.ProviderEffectContained);
        if (record.State is ExternalOperationState.DeviceComplete or ExternalOperationState.Visible)
            return (record.Disposition is ExternalOperationDisposition.Cancelled ||
                    record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged &&
                    record.Disposition is ExternalOperationDisposition.Discarded or ExternalOperationDisposition.Faulted) &&
                   (plan.ProviderResourcesClosed || plan.ProviderEffectContained);
        if (record.State == ExternalOperationState.Submitted)
            return record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged &&
                   record.Disposition == ExternalOperationDisposition.ProviderLost &&
                   (plan.ProviderResourcesClosed || plan.ProviderEffectContained);
        return false;
    }

    private static void FailPublicationRevalidation(Record record)
    {
        record.Disposition = record.Preparation.PublicationPolicy == ExternalPublicationPolicy.Staged
            ? ExternalOperationDisposition.Discarded
            : ExternalOperationDisposition.Faulted;
        AddTransition(record, record.State, "PublicationRevalidationFailed");
    }

    private static void Move(Record record, ExternalOperationState next, string eventName)
    {
        var prior = record.State;
        record.State = next;
        record.Transitions.Add(new ExternalOperationTransition(record.NextTransitionSequence++, prior, next, eventName));
    }

    private static void AddTransition(Record record, ExternalOperationState state, string eventName) =>
        record.Transitions.Add(new ExternalOperationTransition(record.NextTransitionSequence++, state, state, eventName));

    private static KernelResult ValidateEffectPolicy(ExternalPublicationPolicy publication, ExternalEffectPolicy effect)
    {
        if (!Enum.IsDefined(effect.EffectClass) || !Enum.IsDefined(effect.ReplayProtection))
            return KernelResult.Fail(KernelError.InvalidMessage, "External effect classification is invalid.");
        if (publication == ExternalPublicationPolicy.Staged)
            return effect.EffectClass == ExternalEffectClass.StagedReversibleUntilPublish && effect.ReplayProtection == ExternalReplayProtection.None
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.InvalidMessage, "Staged publication requires the reversible-until-publish effect class.");
        if (!effect.ReplayConsumerAcknowledged)
            return KernelResult.Fail(KernelError.PlatformDenied, "Direct output requires replay-consumer acknowledgement of an already-visible effect.");
        if (effect.EffectClass == ExternalEffectClass.SnapshotOrIdempotenceRequired && effect.ReplayProtection != ExternalReplayProtection.None)
            return KernelResult.Ok();
        if (effect.EffectClass == ExternalEffectClass.IrreversibleBarrier && effect.ReplayProtection == ExternalReplayProtection.None)
            return KernelResult.Ok();
        return KernelResult.Fail(KernelError.PlatformDenied, "Direct output requires snapshot/idempotence protection or an explicit irreversible barrier.");
    }

    private static ExternalOperationSnapshot Snapshot(Record record) => new(
        record.Preparation.Operation,
        record.Preparation.Principal,
        record.State,
        record.Disposition,
        record.Admission,
        record.Binding,
        record.Preparation.VisibilityRequirement,
        record.Preparation.PublicationPolicy,
        record.Preparation.EffectPolicy,
        record.EffectBoundary,
        Array.AsReadOnly(record.Transitions.ToArray()));

    private static KernelResult<T> InvalidTransition<T>(Record record, string message) =>
        KernelResult<T>.Fail(KernelError.InvalidTransition, $"{message} Current state is {record.State}/{record.Disposition}.");
}
