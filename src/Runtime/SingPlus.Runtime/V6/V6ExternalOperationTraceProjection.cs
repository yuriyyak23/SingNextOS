using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>
/// Non-authoritative projection of already-committed external-operation owner transitions.
/// Budget settlement and authority release are deliberately not inferred from a lifecycle snapshot.
/// </summary>
internal static class V6ExternalOperationTraceProjection
{
    private const string Source = "singnext.external-operation";

    internal static IReadOnlyList<SemanticTraceEventV1> ProjectPublishedPrefix(
        ExternalOperationSnapshot snapshot,
        string generationVectorDigest)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateDigest(generationVectorDigest);
        if (snapshot.Operation.OperationId.Value == 0 || snapshot.Operation.Generation.Value == 0)
            throw new InvalidOperationException("External-operation snapshot identity is incomplete.");
        if (snapshot.Transitions.Count == 0)
            throw new InvalidOperationException("External-operation owner history lacks its Prepared origin.");
        if (!Enum.IsDefined(snapshot.State) || !Enum.IsDefined(snapshot.Disposition) ||
            !Enum.IsDefined(snapshot.VisibilityRequirement) || !Enum.IsDefined(snapshot.PublicationPolicy) ||
            !Enum.IsDefined(snapshot.EffectBoundary) || !Enum.IsDefined(snapshot.EffectPolicy.EffectClass) ||
            !Enum.IsDefined(snapshot.EffectPolicy.ReplayProtection))
            throw new NotSupportedException("External-operation snapshot contains unknown mandatory owner semantics.");
        var correlation = $"external:{snapshot.Operation.OperationId.Value}:{snapshot.Operation.Generation.Value}";
        var result = new List<SemanticTraceEventV1>();
        ulong semanticSequence = 0;
        ulong ownerSequence = 0;
        ExternalOperationState? priorOwnerState = null;
        var submitted = false;
        var quarantined = false;
        var published = false;
        var settled = false;
        var expectedDisposition = ExternalOperationDisposition.Active;
        var expectedBoundary = ExternalEffectBoundaryState.NotCrossed;
        var publicationAmbiguous = false;
        var cancellationRequested = false;
        var resourceBound = false;
        var resourceCancelled = false;

        foreach (var transition in snapshot.Transitions)
        {
            if (!Enum.IsDefined(transition.From) || !Enum.IsDefined(transition.To))
                throw new NotSupportedException("External-operation transition contains unknown owner states.");
            if (!HasOwnerStateEdge(transition))
                throw new InvalidOperationException("External-operation event contradicts its owner state edge.");
            if (ownerSequence == ulong.MaxValue || transition.Sequence != ownerSequence + 1)
                throw new InvalidOperationException("External-operation transition sequence is not contiguous.");
            if (ownerSequence == 0 && (transition.Event != "Prepared" ||
                transition.From != ExternalOperationState.Prepared || transition.To != ExternalOperationState.Prepared))
                throw new InvalidOperationException("External-operation transition chain lacks its Prepared origin.");
            if (ownerSequence != 0 && transition.Event == "Prepared")
                throw new InvalidOperationException("External-operation owner history repeats its Prepared origin.");
            if (priorOwnerState.HasValue && transition.From != priorOwnerState.Value)
                throw new InvalidOperationException("External-operation transition states are not contiguous.");
            if (!HasOwnerWriterGuard(transition, expectedDisposition, snapshot.PublicationPolicy, publicationAmbiguous))
                throw new InvalidOperationException("External-operation event contradicts its existing owner writer guard.");
            ownerSequence = transition.Sequence;
            priorOwnerState = transition.To;
            // Reconstruct observation facts from the existing owner writers, never
            // permission or a second lifecycle ledger. Reject contradictory snapshots.
            switch (transition.Event)
            {
                case "ResourceLeaseBound":
                    if (resourceBound) throw new InvalidOperationException("Owner history repeats its resource lease binding.");
                    resourceBound = true;
                    break;
                case "ResourceCancelledBeforeSubmit":
                    if (!resourceBound || resourceCancelled || submitted)
                        throw new InvalidOperationException("Resource cancellation history lacks its exact unsubmitted binding.");
                    resourceCancelled = true;
                    break;
                case "ResourceQuarantined" or "ResourceSettlementQuarantined":
                    if (!resourceBound || resourceCancelled || settled)
                        throw new InvalidOperationException("Resource quarantine contradicts its bound nonterminal owner history.");
                    break;
                case "ResourceSettled":
                    if (!resourceBound || resourceCancelled || !submitted || settled)
                        throw new InvalidOperationException("Resource settlement lacks its exact submitted nonterminal association.");
                    break;
                case "Submitted":
                    expectedBoundary = snapshot.PublicationPolicy == ExternalPublicationPolicy.Staged
                        ? ExternalEffectBoundaryState.StagedPending
                        : snapshot.EffectPolicy.EffectClass == ExternalEffectClass.IrreversibleBarrier
                            ? ExternalEffectBoundaryState.Irreversible : ExternalEffectBoundaryState.ExternallyVisible;
                    break;
                case "DeviceComplete": expectedDisposition = ExternalOperationDisposition.Completed; break;
                case "DeviceComplete:Cancelled" or "CancelledBeforeSubmit" or "TeardownCancelledBeforeSubmit":
                    expectedDisposition = ExternalOperationDisposition.Cancelled; break;
                case "DeviceComplete:Faulted" or "VisibilityFailed" or "DirectWriteCannotBeUndone":
                    expectedDisposition = ExternalOperationDisposition.Faulted; break;
                case "Published" or "DirectPublicationObserved":
                    expectedDisposition = ExternalOperationDisposition.Published;
                    if (expectedBoundary == ExternalEffectBoundaryState.StagedPending)
                        expectedBoundary = ExternalEffectBoundaryState.ExternallyVisible;
                    break;
                case "ProviderCancellationRequested" or "DrainRequired" or "TeardownDrainRequired":
                    expectedDisposition = ExternalOperationDisposition.CancellationPending; break;
                case "StagedResultDiscarded" or "PublicationRevalidationFailed" or "TeardownResultDiscarded":
                    expectedDisposition = snapshot.PublicationPolicy == ExternalPublicationPolicy.Staged
                        ? ExternalOperationDisposition.Discarded : ExternalOperationDisposition.Faulted;
                    break;
                case "ProviderLost":
                    expectedDisposition = snapshot.PublicationPolicy == ExternalPublicationPolicy.Staged
                        ? ExternalOperationDisposition.ProviderLost : ExternalOperationDisposition.Faulted;
                    break;
                case "PublicationEffectAmbiguous":
                    publicationAmbiguous = true;
                    expectedDisposition = ExternalOperationDisposition.Faulted;
                    expectedBoundary = ExternalEffectBoundaryState.PossiblyExternallyVisible;
                    break;
                case "PublicationEffectClosedWithoutPublication":
                    publicationAmbiguous = false;
                    expectedDisposition = ExternalOperationDisposition.Discarded;
                    expectedBoundary = ExternalEffectBoundaryState.StagedPending;
                    break;
            }
            if (!submitted && transition.Event is "Prepared" or "Admitted" or "ResourceLeaseBound")
                continue;

            switch (transition.Event)
            {
                case "Submitted":
                    if (submitted) throw Unsupported(transition);
                    submitted = true;
                    Add(SemanticTraceEventKindV1.Submit, transition, "submit");
                    Add(SemanticTraceEventKindV1.EffectPossible, transition, "effect-possible");
                    break;
                case "DeviceComplete" when submitted:
                    Add(SemanticTraceEventKindV1.RetireOrComplete, transition, "complete");
                    break;
                case "DirectWriteCannotBeUndone" when submitted:
                    quarantined = true;
                    Add(SemanticTraceEventKindV1.Quarantined, transition, "irreversible-write-cannot-be-undone");
                    break;
                case "DeviceComplete:Cancelled" or "DeviceComplete:Faulted" when submitted:
                    // Terminal response is completion observation, never closure.
                    Add(SemanticTraceEventKindV1.RetireOrComplete, transition, "complete");
                    if (!quarantined)
                    {
                        quarantined = true;
                        Add(SemanticTraceEventKindV1.Quarantined, transition, "quarantined");
                    }
                    break;
                case "Visible" when submitted && !quarantined:
                    Add(SemanticTraceEventKindV1.Visible, transition, "visible");
                    break;
                case "Published" or "DirectPublicationObserved" when submitted && !quarantined:
                    published = true;
                    Add(SemanticTraceEventKindV1.Published, transition, "published");
                    break;
                case "ProviderCancellationRequested" or "DrainRequired" or "TeardownDrainRequired" when submitted && !quarantined:
                    if (cancellationRequested) break;
                    cancellationRequested = true;
                    Add(SemanticTraceEventKindV1.CancellationRequested, transition,
                        "cancellation-requested-with-effect-still-possible");
                    break;
                case "VisibilityFailed" or "PublicationRevalidationFailed" or
                    "PublicationEffectAmbiguous" or "StagedResultDiscarded" or "TeardownResultDiscarded" or "ProviderLost" when submitted && !quarantined:
                    quarantined = true;
                    Add(SemanticTraceEventKindV1.Quarantined, transition, "quarantined");
                    break;
                case "PublicationEffectClosedWithoutPublication" when quarantined && !published:
                    Add(SemanticTraceEventKindV1.EffectClosedWithoutPublication, transition,
                        "effect-closed-without-publication");
                    break;
                case "ResourceQuarantined" or "ResourceSettlementQuarantined" when submitted:
                    Add(SemanticTraceEventKindV1.ResourceAccountingQuarantined, transition,
                        "resource-accounting-quarantined");
                    break;
                case "ResourceQuarantined" when !submitted:
                    Add(SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit, transition,
                        "resource-accounting-quarantined-before-submit");
                    break;
                case "ResourceSettled" when submitted && !settled:
                    settled = true;
                    Add(SemanticTraceEventKindV1.Settled, transition, "settled");
                    break;
                case "Released" when !submitted && transition.From is ExternalOperationState.Prepared or ExternalOperationState.Admitted:
                    if (expectedDisposition != ExternalOperationDisposition.Cancelled ||
                        expectedBoundary != ExternalEffectBoundaryState.NotCrossed)
                        throw new InvalidOperationException("Pre-submit release lacks owner-confirmed cancellation before the effect boundary.");
                    Add(SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit, transition,
                        "local-authority-released-before-submit");
                    break;
                case "CancelledBeforeSubmit" or "TeardownCancelledBeforeSubmit" when !submitted:
                    Add(SemanticTraceEventKindV1.CancelledBeforeSubmit, transition,
                        "cancelled-before-submit");
                    break;
                case "Released" when submitted && !resourceBound:
                    Add(SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding, transition,
                        "local-authority-released-without-resource-binding");
                    break;
                case "Released":
                    if (!settled)
                        throw new InvalidOperationException(
                            "Release cannot be projected from an external-operation snapshot without ordered budget-settlement evidence.");
                    Add(SemanticTraceEventKindV1.Released, transition, "released");
                    break;
                default:
                    if (submitted) throw Unsupported(transition);
                    // Pre-submit cancellation has not crossed the semantic effect boundary.
                    if (transition.Event != "ResourceCancelledBeforeSubmit")
                        throw Unsupported(transition);
                    break;
            }
        }

        if (priorOwnerState.HasValue && priorOwnerState.Value != snapshot.State)
            throw new InvalidOperationException("External-operation snapshot state differs from the final owner transition.");
        if (snapshot.Disposition != expectedDisposition || snapshot.EffectBoundary != expectedBoundary)
            throw new InvalidOperationException("External-operation snapshot disposition/effect boundary contradicts its owner history.");
        if (submitted != snapshot.Binding.HasValue ||
            snapshot.Binding is { } binding && (binding.Operation != snapshot.Operation ||
                binding.BindingId.Value == 0 || binding.Generation == 0))
            throw new InvalidOperationException("External-operation snapshot binding contradicts its submitted identity/history.");

        // Registration may inspect a pre-submit owner snapshot with no semantic events.
        if (result.Count != 0 && !SemanticTraceValidatorV1.Validate(result).IsValid)
            throw new NotSupportedException(
                "External-operation owner transitions do not form a valid semantic trace prefix.");
        return new ReadOnlyCollection<SemanticTraceEventV1>(result);

        void Add(SemanticTraceEventKindV1 kind, ExternalOperationTransition transition, string projection)
        {
            var evidence = EvidenceDigest(transition, projection);
            result.Add(new SemanticTraceEventV1(SemanticTraceEventV1.CurrentVersion, correlation,
                checked(++semanticSequence), kind, Source, generationVectorDigest, evidence).Validate());
        }
    }

    private static Exception Unsupported(ExternalOperationTransition transition) =>
        new NotSupportedException(
            $"External-operation event '{transition.Event}' at sequence {transition.Sequence} has no lossless v1 semantic projection.");

    // Only observable guards from current writers are checked. Missing lease,
    // provider or callback evidence is never inferred as permission or closure.
    private static bool HasOwnerWriterGuard(ExternalOperationTransition transition,
        ExternalOperationDisposition disposition, ExternalPublicationPolicy policy, bool ambiguous) => transition.Event switch
    {
        "Released" => !ambiguous && (transition.From switch
        {
            ExternalOperationState.Prepared or ExternalOperationState.Admitted => disposition == ExternalOperationDisposition.Cancelled,
            ExternalOperationState.Published => disposition == ExternalOperationDisposition.Published,
            ExternalOperationState.Submitted => policy == ExternalPublicationPolicy.Staged && disposition == ExternalOperationDisposition.ProviderLost,
            ExternalOperationState.DeviceComplete or ExternalOperationState.Visible =>
                disposition == ExternalOperationDisposition.Cancelled || policy == ExternalPublicationPolicy.Staged &&
                disposition is ExternalOperationDisposition.Discarded or ExternalOperationDisposition.Faulted or ExternalOperationDisposition.ProviderLost,
            _ => false,
        }),
        "Admitted" or "Submitted" => disposition == ExternalOperationDisposition.Active,
        "ResourceLeaseBound" => transition.From == ExternalOperationState.Admitted &&
            disposition == ExternalOperationDisposition.Active && !ambiguous,
        "ResourceCancelledBeforeSubmit" => transition.From is ExternalOperationState.Prepared or ExternalOperationState.Admitted or ExternalOperationState.Released &&
            disposition == ExternalOperationDisposition.Cancelled && !ambiguous,
        "ResourceSettled" or "ResourceSettlementQuarantined" => transition.From is
            ExternalOperationState.DeviceComplete or ExternalOperationState.Visible or ExternalOperationState.Published,
        "Published" => policy == ExternalPublicationPolicy.Staged &&
            disposition == ExternalOperationDisposition.Completed && !ambiguous,
        "DirectPublicationObserved" => policy == ExternalPublicationPolicy.DirectCoherent &&
            disposition == ExternalOperationDisposition.Completed && !ambiguous,
        "Visible" or "VisibilityFailed" or "PublicationRevalidationFailed" or
            "PublicationEffectAmbiguous" => disposition == ExternalOperationDisposition.Completed && !ambiguous,
        "CancelledBeforeSubmit" => transition.From is ExternalOperationState.Prepared or ExternalOperationState.Admitted &&
            disposition == ExternalOperationDisposition.Active && !ambiguous,
        "TeardownCancelledBeforeSubmit" => transition.From is ExternalOperationState.Prepared or ExternalOperationState.Admitted &&
            disposition is ExternalOperationDisposition.Active or ExternalOperationDisposition.Cancelled && !ambiguous,
        "ProviderCancellationRequested" or "DrainRequired" => transition.From == ExternalOperationState.Submitted &&
            disposition == ExternalOperationDisposition.Active && !ambiguous,
        "TeardownDrainRequired" => transition.From == ExternalOperationState.Submitted &&
            disposition != ExternalOperationDisposition.ProviderLost && !ambiguous,
        "TeardownResultDiscarded" => transition.From is ExternalOperationState.DeviceComplete or ExternalOperationState.Visible &&
            disposition == ExternalOperationDisposition.Completed && !ambiguous,
        "StagedResultDiscarded" => transition.From is ExternalOperationState.DeviceComplete or ExternalOperationState.Visible &&
            disposition == ExternalOperationDisposition.Completed && policy == ExternalPublicationPolicy.Staged && !ambiguous,
        "DirectWriteCannotBeUndone" => transition.From is ExternalOperationState.DeviceComplete or ExternalOperationState.Visible &&
            disposition == ExternalOperationDisposition.Completed && policy == ExternalPublicationPolicy.DirectCoherent && !ambiguous,
        "ProviderLost" => transition.From is ExternalOperationState.Submitted or ExternalOperationState.DeviceComplete or ExternalOperationState.Visible && !ambiguous,
        "PublicationEffectClosedWithoutPublication" => transition.From == ExternalOperationState.Visible &&
            disposition == ExternalOperationDisposition.Faulted && policy == ExternalPublicationPolicy.Staged && ambiguous,
        _ => true // Unsupported events remain rejected by the lossless projection switch.
    };

    // Checks observation against existing owner writers, never authorizes a transition.
    private static bool HasOwnerStateEdge(ExternalOperationTransition transition) => transition.Event switch
    {
        "Prepared" => transition.From == ExternalOperationState.Prepared && transition.To == ExternalOperationState.Prepared,
        "Admitted" => transition.From == ExternalOperationState.Prepared && transition.To == ExternalOperationState.Admitted,
        "Submitted" => transition.From == ExternalOperationState.Admitted && transition.To == ExternalOperationState.Submitted,
        "DeviceComplete" or "DeviceComplete:Cancelled" or "DeviceComplete:Faulted" =>
            transition.From == ExternalOperationState.Submitted && transition.To == ExternalOperationState.DeviceComplete,
        "Visible" => transition.From == ExternalOperationState.DeviceComplete && transition.To == ExternalOperationState.Visible,
        "Published" or "DirectPublicationObserved" =>
            transition.From == ExternalOperationState.Visible && transition.To == ExternalOperationState.Published,
        "Released" => transition.From != ExternalOperationState.Released && transition.To == ExternalOperationState.Released,
        "ResourceLeaseBound" or "ProviderCancellationRequested" or "DrainRequired" or "TeardownDrainRequired" or "TeardownResultDiscarded" or
        "VisibilityFailed" or "PublicationRevalidationFailed" or
        "PublicationEffectAmbiguous" or "StagedResultDiscarded" or "DirectWriteCannotBeUndone" or "ProviderLost" or
        "ResourceQuarantined" or "ResourceSettlementQuarantined" or "ResourceSettled" or
        "PublicationEffectClosedWithoutPublication" or "CancelledBeforeSubmit" or "TeardownCancelledBeforeSubmit" or "ResourceCancelledBeforeSubmit" =>
            transition.From == transition.To,
        _ => true // Unsupported event names remain rejected by the projection switch.
    };

    private static string EvidenceDigest(ExternalOperationTransition transition, string projection)
    {
        var payload = $"1|{transition.Sequence}|{(int)transition.From}|{(int)transition.To}|{transition.Event}|{projection}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static void ValidateDigest(string value)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Generation vector digest must be canonical SHA-256 hex.", nameof(value));
    }
}
