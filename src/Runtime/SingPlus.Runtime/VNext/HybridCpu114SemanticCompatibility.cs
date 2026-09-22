using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal static class HybridCpu114SemanticCompatibility
{
    internal const string PackageId = "HybridCPU.ExternalRuntime.Contracts";
    internal const string PackageVersion = "1.14.0";
    internal const string SchemaIdentity = "ExternalOperationContract/1.4.0";

    internal static KernelResult<ExecutionGuaranteesV1> Map(Hc.ExternalOperationSemanticRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContractVersion != Hc.ExternalOperationContract.Version ||
            !Enum.IsDefined(request.EffectClass) || !Enum.IsDefined(request.VisibilityRequirement) ||
            !Enum.IsDefined(request.CancellationMode) || !Enum.IsDefined(request.ReplayEffectClass))
            return KernelResult<ExecutionGuaranteesV1>.Fail(KernelError.InvalidMessage,
                "HybridCPU 1.14.0 request version or semantic class is unknown.");

        var cancellation = request.CancellationMode == Hc.ExternalCancellationMode.ExactAcknowledgement
            ? Supported(CancellationClassV1.ExactAcknowledgement, GuaranteeEvidenceSourceV1.ContractSemantics)
            : Supported(CancellationClassV1.None, GuaranteeEvidenceSourceV1.ContractSemantics);
        var publication = request.VisibilityRequirement == Hc.ExternalVisibilityRequirement.StagedOutput
            ? Supported(PublicationEnforcementClassV1.StagedWithholdingUntilDecision,
                GuaranteeEvidenceSourceV1.PublicationEvidenceGate)
            : Supported(PublicationEnforcementClassV1.VisibilityFence,
                GuaranteeEvidenceSourceV1.ContractSemantics);

        try
        {
            return KernelResult<ExecutionGuaranteesV1>.Ok(new ExecutionGuaranteesV1(
                ExecutionGuaranteesV1.CurrentVersion,
                new(PackageId, PackageVersion, SchemaIdentity),
                "external-operation/1.4.0",
                Supported(ExecutionAdmissionClassV1.IndependentRuntimeLegalityAndProviderAdmission,
                    GuaranteeEvidenceSourceV1.ContractSemantics),
                Unsupported(MeasurementClassV1.None),
                Unsupported(ResourceAssuranceV1.AccountingOnly),
                cancellation,
                Unsupported(IsolationClassV1.None),
                Unsupported(ContentionClassV1.None),
                publication,
                Unsupported(ReplayClassV1.None),
                Unsupported(DeterminismClassV1.None),
                Unsupported(ContainmentClassV1.None),
                Supported(FailureHandlingClassV1.FailClosedExactGeneration,
                    GuaranteeEvidenceSourceV1.ContractSemantics),
                Unsupported(LocalityClassV1.Any),
                Supported(RetireEvidenceClassV1.DistinctCompletionVisibilityPublicationRelease,
                    GuaranteeEvidenceSourceV1.LifecycleReceipt)).Canonicalize());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return KernelResult<ExecutionGuaranteesV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
    }

    internal static string GenerationDigest(Hc.ExternalGenerationSet generations)
    {
        ArgumentNullException.ThrowIfNull(generations);
        if (generations.ContractVersion != Hc.ExternalOperationContract.Version)
            throw new NotSupportedException("HybridCPU external generation schema is unsupported.");
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(generations)));
    }

    private static ExecutionGuaranteeDimensionV1<T> Supported<T>(T value, GuaranteeEvidenceSourceV1 source)
        where T : struct, Enum => new(1, SemanticGuaranteeSupportV1.Supported, value,
            SemanticClaimLevelV1.StaticAdmission, source);

    private static ExecutionGuaranteeDimensionV1<T> Unsupported<T>(T value)
        where T : struct, Enum => new(1, SemanticGuaranteeSupportV1.Unsupported, value,
            SemanticClaimLevelV1.ModelOnly, GuaranteeEvidenceSourceV1.None);
}

internal readonly record struct SemanticExecutionBindingDigestV1(string Value);

/// <summary>Immutable exact correlation; never an authority or submit permit.</summary>
internal sealed record SemanticExecutionBindingV1(
    ushort Version,
    ExternalOperationHandle Operation,
    OperationObligationsDigestV1 ObligationsDigest,
    ExecutionGuaranteesDigestV1 GuaranteesDigest,
    ProcessHandle BudgetOwner,
    BudgetReservationHandle Lease,
    ResourceEnvelopeV1 ResourceEnvelope,
    string ProviderIdentity,
    Hc.ExternalRequestCorrelation ProviderRequestCorrelation,
    string ProviderGenerationDigest,
    Hc.ExternalGenerationSet ProviderGenerations,
    ProviderContractIdentityV1 ProviderContract,
    string MeasurementContractIdentity,
    ExternalVisibilityRequirement VisibilityRequirement,
    ExternalPublicationPolicy PublicationPolicy,
    SemanticExecutionBindingDigestV1 Digest)
{
    internal const ushort CurrentVersion = 1;
    internal bool AuthorizesExecution => false;
    internal bool AuthorizesEffect => false;
    internal bool AuthorizesPublication => false;
    internal bool IsProviderAdmission => false;

    internal SemanticExecutionBindingV1 Canonicalize()
    {
        if (Version != CurrentVersion || Operation.OperationId.Value == 0 || Operation.Generation.Value == 0 ||
            BudgetOwner.ProcessId.Value == 0 || BudgetOwner.Generation == 0 ||
            Lease.ReservationId.Value == 0 || Lease.Generation.Value == 0)
            throw new NotSupportedException("Semantic execution binding version or exact identity is invalid.");
        if (string.IsNullOrWhiteSpace(ObligationsDigest.Value) || string.IsNullOrWhiteSpace(GuaranteesDigest.Value) ||
            string.IsNullOrWhiteSpace(ProviderIdentity) || ProviderIdentity != ProviderIdentity.Trim() ||
            ProviderRequestCorrelation.Equals(default(Hc.ExternalRequestCorrelation)) ||
            string.IsNullOrWhiteSpace(ProviderGenerationDigest) || string.IsNullOrWhiteSpace(MeasurementContractIdentity))
            throw new ArgumentException("Semantic execution binding correlation is incomplete.");
        if (ProviderContract != new ProviderContractIdentityV1(
                HybridCpu114SemanticCompatibility.PackageId,
                HybridCpu114SemanticCompatibility.PackageVersion,
                HybridCpu114SemanticCompatibility.SchemaIdentity))
            throw new NotSupportedException("Provider contract tuple is not the qualified 1.14.0 mapping.");
        if (HybridCpu114SemanticCompatibility.GenerationDigest(ProviderGenerations) != ProviderGenerationDigest)
            throw new ArgumentException("Provider generation digest does not match the exact opaque generation set.");
        var envelope = ResourceEnvelope.Canonicalize();
        if (!Enum.IsDefined(VisibilityRequirement) || !Enum.IsDefined(PublicationPolicy))
            throw new ArgumentOutOfRangeException(nameof(PublicationPolicy));
        var canonical = this with { ResourceEnvelope = envelope, Digest = default };
        var expected = new SemanticExecutionBindingDigestV1(ComputeDigest(canonical));
        if (!string.IsNullOrEmpty(Digest.Value) && Digest != expected)
            throw new ArgumentException("Semantic execution binding digest mismatch.", nameof(Digest));
        return canonical with { Digest = expected };
    }

    private static string ComputeDigest(SemanticExecutionBindingV1 value)
    {
        var payload = string.Join("\n",
            value.Version, value.Operation.OperationId.Value, value.Operation.Generation.Value,
            value.ObligationsDigest.Value, value.GuaranteesDigest.Value,
            value.BudgetOwner.ProcessId.Value, value.BudgetOwner.Generation,
            value.Lease.ReservationId.Value, value.Lease.Generation.Value,
            value.ResourceEnvelope.Version, (int)value.ResourceEnvelope.Family,
            (int)value.ResourceEnvelope.ResourceClass, (int)value.ResourceEnvelope.Unit,
            value.ResourceEnvelope.Amount, value.ResourceEnvelope.WindowNanoseconds,
            value.ResourceEnvelope.SemanticScope, value.ProviderIdentity,
            value.ProviderRequestCorrelation,
            value.ProviderGenerationDigest, value.ProviderContract.PackageId,
            value.ProviderContract.PackageVersion, value.ProviderContract.SchemaIdentity,
            value.MeasurementContractIdentity, (int)value.VisibilityRequirement,
            (int)value.PublicationPolicy);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
