using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum SemanticGateDecisionStatusV1 : byte
{
    Allowed = 1,
    Denied = 2,
    Stale = 3,
}

internal readonly record struct ProviderAdmissionDecisionV1(
    ushort Version,
    SemanticExecutionBindingDigestV1 BindingDigest,
    string ProviderIdentity,
    string ProviderGenerationDigest,
    Hc.ExternalRequestCorrelation Correlation,
    SemanticGateDecisionStatusV1 Status,
    string EvidenceIdentity)
{
    internal const ushort CurrentVersion = 1;
    internal bool AuthorizesSingNextWork => false;
}

internal readonly record struct RuntimeLegalityDecisionV1(
    ushort Version,
    SemanticExecutionBindingDigestV1 BindingDigest,
    string RuntimeIdentity,
    ulong RuntimeGeneration,
    SemanticGateDecisionStatusV1 Status,
    string EvidenceIdentity)
{
    internal const ushort CurrentVersion = 1;
    internal bool AuthorizesSingNextWork => false;
}

internal interface ISemanticProviderAdmissionService
{
    KernelResult<ProviderAdmissionDecisionV1> Revalidate(SemanticExecutionBindingV1 binding);
}

/// <summary>Independent runtime-owned legality service. Compiler metadata is not an input.</summary>
internal interface IRuntimeLegalityService
{
    KernelResult<RuntimeLegalityDecisionV1> Evaluate(SemanticExecutionBindingV1 binding);
}

internal enum SemanticRefinementProofStatusV1 : byte
{
    Accepted = 1,
    Rejected = 2,
}

internal readonly record struct SemanticRefinementProofDigestV1(string Value);

internal sealed record SemanticRefinementProofV1(
    ushort Version,
    SemanticExecutionBindingDigestV1 BindingDigest,
    SemanticRefinementProofStatusV1 Status,
    IReadOnlyList<SemanticRefinementDecisionV1> Decisions,
    SemanticRefinementProofDigestV1 Digest)
{
    internal const ushort CurrentVersion = 1;
    internal bool AuthorizesExecution => false;
    internal bool AuthorizesEffect => false;
}

internal static class SemanticExecutionRefinementV1
{
    internal static KernelResult<SemanticRefinementProofV1> Evaluate(
        SemanticExecutionBindingV1 binding,
        OperationObligationsV1 obligations,
        ExecutionGuaranteesV1 guarantees)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(obligations);
        ArgumentNullException.ThrowIfNull(guarantees);
        SemanticExecutionBindingV1 exactBinding;
        OperationObligationsV1 exactObligations;
        ExecutionGuaranteesV1 exactGuarantees;
        try
        {
            exactBinding = binding.Canonicalize();
            exactObligations = obligations.Canonicalize();
            exactGuarantees = guarantees.Canonicalize();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        { return KernelResult<SemanticRefinementProofV1>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (exactBinding.Operation != exactObligations.Operation ||
            exactBinding.ObligationsDigest != exactObligations.Digest ||
            exactBinding.GuaranteesDigest != exactGuarantees.Digest)
            return KernelResult<SemanticRefinementProofV1>.Fail(KernelError.StaleGeneration,
                "Refinement inputs do not match the exact semantic binding.");

        var required = exactObligations.SemanticRequirements;
        SemanticRefinementDecisionV1[] decisions =
        [
            Evaluate(required.Isolation, exactGuarantees.Isolation, SemanticPartialOrdersV1.IsolationRefines),
            Evaluate(required.Publication, exactGuarantees.VisibilityPublication, SemanticPartialOrdersV1.PublicationRefines),
            Evaluate(required.Replay, exactGuarantees.Replay, SemanticPartialOrdersV1.ReplayRefines),
            Evaluate(required.Determinism, exactGuarantees.Determinism, SemanticPartialOrdersV1.DeterminismRefines),
            Evaluate(required.Cancellation, exactGuarantees.PreemptionCancellation, SemanticPartialOrdersV1.CancellationRefines),
            Evaluate(required.Containment, exactGuarantees.Containment, SemanticPartialOrdersV1.ContainmentRefines),
            Evaluate(required.Locality, exactGuarantees.Locality, SemanticPartialOrdersV1.LocalityRefines),
            Evaluate(required.ResourceAssurance, exactGuarantees.ResourceEnforcement, SemanticPartialOrdersV1.ResourceAssuranceRefines),
        ];
        var accepted = decisions.All(decision => decision.IsAccepted);
        var status = accepted ? SemanticRefinementProofStatusV1.Accepted : SemanticRefinementProofStatusV1.Rejected;
        var digest = Digest(exactBinding.Digest, status, decisions);
        var proof = new SemanticRefinementProofV1(SemanticRefinementProofV1.CurrentVersion,
            exactBinding.Digest, status, new ReadOnlyCollection<SemanticRefinementDecisionV1>(decisions), digest);
        return accepted
            ? KernelResult<SemanticRefinementProofV1>.Ok(proof)
            : KernelResult<SemanticRefinementProofV1>.Fail(KernelError.PlatformDenied,
                "Execution guarantees do not refine all mandatory operation obligations.");
    }

    internal static bool ExactEquals(SemanticRefinementProofV1 left, SemanticRefinementProofV1 right) =>
        left.Version == right.Version && left.BindingDigest == right.BindingDigest &&
        left.Status == right.Status && left.Digest == right.Digest &&
        left.Decisions.SequenceEqual(right.Decisions);

    private static SemanticRefinementDecisionV1 Evaluate<T>(
        SemanticRequirementV1<T> requirement,
        ExecutionGuaranteeDimensionV1<T> guarantee,
        Func<T, T, bool> refines) where T : struct, Enum =>
        SemanticRefinementEvaluatorV1.Evaluate(requirement,
            new(SemanticGuaranteeV1<T>.CurrentVersion, guarantee.Support, guarantee.ProvidedClass), refines);

    private static SemanticRefinementProofDigestV1 Digest(
        SemanticExecutionBindingDigestV1 binding,
        SemanticRefinementProofStatusV1 status,
        IEnumerable<SemanticRefinementDecisionV1> decisions)
    {
        var payload = string.Join("|", binding.Value, (byte)status,
            string.Join(",", decisions.Select(decision => $"{decision.Version}:{(byte)decision.Status}")));
        return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
    }
}

public sealed partial class RuntimeKernel
{
    internal KernelResult<OperationBinding> SubmitSemanticResourceExternalAdmission(
        ResourceAdmissionCommit commit,
        OperationDependencySnapshot dependencies,
        SemanticExecutionBindingV1 binding,
        OperationObligationsV1 obligations,
        ExecutionGuaranteesV1 guarantees,
        SemanticRefinementProofV1 refinement,
        string currentProviderIdentity,
        Hc.ExternalGenerationSet currentProviderGenerations,
        ISemanticProviderAdmissionService providerAdmission,
        IRuntimeLegalityService runtimeLegality,
        Func<KernelResult> providerSubmit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(refinement);
        ArgumentNullException.ThrowIfNull(providerAdmission);
        ArgumentNullException.ThrowIfNull(runtimeLegality);
        ArgumentNullException.ThrowIfNull(providerSubmit);

        return SubmitResourceExternalAdmission(commit, dependencies, providerSubmit, () =>
        {
            var provider = providerAdmission.Revalidate(binding);
            if (!provider.IsSuccess) return KernelResult.Fail(provider.Error, provider.Message!);
            var providerDecision = provider.Value;
            if (providerDecision.Version != ProviderAdmissionDecisionV1.CurrentVersion ||
                providerDecision.Status != SemanticGateDecisionStatusV1.Allowed ||
                providerDecision.BindingDigest != binding.Digest ||
                providerDecision.ProviderIdentity != binding.ProviderIdentity ||
                providerDecision.ProviderGenerationDigest != binding.ProviderGenerationDigest ||
                providerDecision.Correlation != binding.ProviderRequestCorrelation ||
                string.IsNullOrWhiteSpace(providerDecision.EvidenceIdentity))
                return KernelResult.Fail(KernelError.PlatformDenied, "Exact provider admission gate denied or became stale.");

            var recomputed = SemanticExecutionRefinementV1.Evaluate(binding, obligations, guarantees);
            if (!recomputed.IsSuccess || !SemanticExecutionRefinementV1.ExactEquals(refinement, recomputed.Value!))
                return KernelResult.Fail(recomputed.IsSuccess ? KernelError.StaleGeneration : recomputed.Error,
                    recomputed.Message ?? "Refinement proof is stale or does not match the exact binding.");

            var singNext = RevalidateSemanticExecutionBindingV1(binding, obligations, guarantees,
                currentProviderIdentity, currentProviderGenerations);
            if (!singNext.IsSuccess) return singNext;
            var authorization = RevalidateResourceAdmissionCommit(commit);
            if (!authorization.IsSuccess) return authorization;

            var legality = runtimeLegality.Evaluate(binding);
            if (!legality.IsSuccess) return KernelResult.Fail(legality.Error, legality.Message!);
            var legalityDecision = legality.Value;
            if (legalityDecision.Version != RuntimeLegalityDecisionV1.CurrentVersion ||
                legalityDecision.Status != SemanticGateDecisionStatusV1.Allowed ||
                legalityDecision.BindingDigest != binding.Digest || legalityDecision.RuntimeGeneration == 0 ||
                string.IsNullOrWhiteSpace(legalityDecision.RuntimeIdentity) ||
                string.IsNullOrWhiteSpace(legalityDecision.EvidenceIdentity))
                return KernelResult.Fail(KernelError.PlatformDenied, "Independent runtime legality gate denied or became stale.");

            // A final owner read catches deterministic revoke/session/Region/lease races
            // triggered while the independent runtime service evaluated legality.
            singNext = RevalidateSemanticExecutionBindingV1(binding, obligations, guarantees,
                currentProviderIdentity, currentProviderGenerations);
            if (!singNext.IsSuccess) return singNext;
            return RevalidateResourceAdmissionCommit(commit);
        });
    }

    private KernelResult RevalidateResourceAdmissionCommit(ResourceAdmissionCommit commit)
    {
        var process = Processes.Resolve(commit.Principal);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        var accepts = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!accepts.IsSuccess) return accepts;
        var effect = CapabilityAuthority.Validate(commit.EffectCapability, process.Value!.DomainId,
            commit.Principal.Generation, CapabilityRights.Execute, commit.EffectResourceGeneration);
        if (!effect.IsSuccess) return KernelResult.Fail(effect.Error, effect.Message!);
        if (effect.Value!.ResourceKind != commit.EffectResourceKind ||
            !string.Equals(effect.Value.ResourceId, commit.EffectResourceId, StringComparison.Ordinal))
            return KernelResult.Fail(KernelError.WrongCapabilityResource, "Effect capability resource changed before submit.");
        var resource = CapabilityAuthority.ValidateResourceUse(commit.ResourceGrant,
            process.Value.DomainId, commit.Principal.Generation, commit.ResourceGeneration, commit.Envelope);
        if (!resource.IsSuccess) return KernelResult.Fail(resource.Error, resource.Message!);
        var lease = Budgets.Query(commit.Lease);
        if (!lease.IsSuccess || lease.Value!.Owner != commit.BudgetOwner || lease.Value.State != BudgetReservationState.Bound)
            return KernelResult.Fail(KernelError.StaleGeneration, "Bound budget lease changed before submit.");
        var operation = ExternalOperations.Query(commit.Operation);
        return operation.IsSuccess && operation.Value!.State == ExternalOperationState.Admitted
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.StaleGeneration, "External operation changed before submit.");
    }
}
