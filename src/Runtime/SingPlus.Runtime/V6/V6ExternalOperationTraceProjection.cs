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
        var correlation = $"external:{snapshot.Operation.OperationId.Value}:{snapshot.Operation.Generation.Value}";
        var result = new List<SemanticTraceEventV1>();
        ulong semanticSequence = 0;
        ulong ownerSequence = 0;
        ExternalOperationState? priorOwnerState = null;
        var submitted = false;
        var quarantined = false;
        var published = false;
        var settled = false;

        foreach (var transition in snapshot.Transitions)
        {
            if (ownerSequence == ulong.MaxValue || transition.Sequence != ownerSequence + 1)
                throw new InvalidOperationException("External-operation transition sequence is not contiguous.");
            if (ownerSequence == 0 && (transition.Event != "Prepared" ||
                transition.From != ExternalOperationState.Prepared || transition.To != ExternalOperationState.Prepared))
                throw new InvalidOperationException("External-operation transition chain lacks its Prepared origin.");
            if (priorOwnerState.HasValue && transition.From != priorOwnerState.Value)
                throw new InvalidOperationException("External-operation transition states are not contiguous.");
            ownerSequence = transition.Sequence;
            priorOwnerState = transition.To;
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
                case "DeviceComplete" when submitted && !quarantined:
                    Add(SemanticTraceEventKindV1.RetireOrComplete, transition, "complete");
                    break;
                case "Visible" when submitted && !quarantined:
                    Add(SemanticTraceEventKindV1.Visible, transition, "visible");
                    break;
                case "Published" when submitted && !quarantined:
                    published = true;
                    Add(SemanticTraceEventKindV1.Published, transition, "published");
                    break;
                case "ProviderCancellationRequested" or "DrainRequired" when submitted && !quarantined:
                    Add(SemanticTraceEventKindV1.CancellationRequested, transition,
                        "cancellation-requested-with-effect-still-possible");
                    break;
                case "VisibilityFailed" or "PublicationFailed" or "PublicationRevalidationFailed" or
                    "PublicationEffectAmbiguous" or "StagedResultDiscarded" or "ProviderLost" or "DeviceComplete:Cancelled" or
                    "DeviceComplete:Faulted" or "ResourceQuarantined" or
                    "ResourceSettlementQuarantined" when submitted && !quarantined:
                    quarantined = true;
                    Add(SemanticTraceEventKindV1.Quarantined, transition, "quarantined");
                    break;
                case "PublicationEffectClosedWithoutPublication" when quarantined && !published:
                    Add(SemanticTraceEventKindV1.EffectClosedWithoutPublication, transition,
                        "effect-closed-without-publication");
                    break;
                case "ResourceQuarantined" or "ResourceSettlementQuarantined" when quarantined:
                    // A second owner confirms the same already-projected quarantine state.
                    break;
                case "ResourceSettled" when submitted && !settled:
                    settled = true;
                    Add(SemanticTraceEventKindV1.Settled, transition, "settled");
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
                    if (transition.Event is not ("CancelledBeforeSubmit" or "ResourceCancelledBeforeSubmit"))
                        throw Unsupported(transition);
                    break;
            }
        }

        if (priorOwnerState.HasValue && priorOwnerState.Value != snapshot.State)
            throw new InvalidOperationException("External-operation snapshot state differs from the final owner transition.");

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
