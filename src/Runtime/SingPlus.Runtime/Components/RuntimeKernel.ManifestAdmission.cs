using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private const uint RuntimeManifestContractVersion = 1;

    private sealed record ComponentAdmissionEvaluation(
        ManifestAdmissionResultV1 Result,
        KernelError? Error = null,
        string? Message = null);

    public ManifestAdmissionResultV1 EvaluateComponentAdmission(ComponentAdmissionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return EvaluateComponentAdmissionCore(plan).Result;
    }

    private ComponentAdmissionEvaluation EvaluateComponentAdmissionCore(ComponentAdmissionPlan plan)
    {
        var manifest = plan.Manifest;
        var decisions = new List<ManifestRequirementDecisionV1>();

        ComponentAdmissionEvaluation Denied(KernelError error, string message, ManifestRequirementKind kind = ManifestRequirementKind.Compatibility)
        {
            decisions.Add(new(kind, message, ManifestRequirementCriticality.Mandatory, ManifestRequirementDisposition.Denied, message));
            return new(new(manifest.NormalizedDigest, ManifestAdmissionDisposition.Denied, decisions.ToArray()), error, message);
        }

        if (!manifest.MatchesImage(plan.Image.Span))
            return Denied(KernelError.ComponentDigestMismatch, "Component image digest does not match its manifest.");
        if (_components.ContainsKey(manifest.Identity))
            return Denied(KernelError.DuplicateIdentity, $"Component '{manifest.Identity.Name}' is already tracked.");
        if (!RegistrationsMatchManifest(manifest, plan.ProvidedServices))
            return Denied(KernelError.InvalidManifest, "Provided service registrations do not exactly match the component manifest.");

        var compatibility = manifest.Compatibility;
        var compatible = RuntimeManifestContractVersion >= compatibility.MinimumRuntimeContractVersion &&
            (compatibility.MaximumRuntimeContractVersion is null || RuntimeManifestContractVersion <= compatibility.MaximumRuntimeContractVersion.Value);
        decisions.Add(new(ManifestRequirementKind.Compatibility, $"runtime-contract:{RuntimeManifestContractVersion}", ManifestRequirementCriticality.Mandatory,
            compatible ? ManifestRequirementDisposition.Granted : ManifestRequirementDisposition.Denied,
            compatible ? "Runtime contract is within the requested range." : "Runtime contract is outside the requested range."));
        if (!compatible)
            return new(new(manifest.NormalizedDigest, ManifestAdmissionDisposition.Denied, decisions.ToArray()), KernelError.PlatformUnsupported, "Runtime compatibility constraint is not satisfied.");

        var platformManifest = QueryPlatformFeatures();
        foreach (var requirement in manifest.PlatformRequirements)
        {
            var criticality = requirement.Criticality;
            var identity = $"platform:{(int)requirement.Family}:v{requirement.MinimumContractVersion}:{requirement.Availability}";
            var supported = false;
            var reason = "Feature family is unknown to this runtime.";
            if (Enum.IsDefined(requirement.Family))
            {
                var feature = platformManifest.Resolve((PlatformFeatureFamily)requirement.Family);
                supported = feature.ContractVersion >= requirement.MinimumContractVersion &&
                    feature.Availability == (PlatformFeatureAvailability)requirement.Availability;
                reason = supported
                    ? "Exact provider-neutral feature requirement is available."
                    : $"Available projection is v{feature.ContractVersion} {feature.Availability}.";
            }
            var disposition = supported
                ? ManifestRequirementDisposition.Granted
                : criticality == ManifestRequirementCriticality.Optional
                    ? ManifestRequirementDisposition.Unsupported
                    : ManifestRequirementDisposition.Denied;
            decisions.Add(new(ManifestRequirementKind.PlatformFeature, identity, criticality, disposition, reason));
            if (!supported && criticality == ManifestRequirementCriticality.Mandatory)
                return new(new(manifest.NormalizedDigest, ManifestAdmissionDisposition.Denied, decisions.ToArray()), KernelError.PlatformUnsupported, reason);
        }

        var resourcePlan = ValidateResourcePlan(manifest.ResourceRequirements, plan.Grants);
        if (!resourcePlan.IsSuccess)
            return Denied(resourcePlan.Error, resourcePlan.Message!, ManifestRequirementKind.LocalCapability);
        foreach (var requirement in manifest.ResourceRequirements)
        {
            var kind = requirement.Kind == ComponentResourceRequirementKind.LocalCapability
                ? ManifestRequirementKind.LocalCapability
                : ManifestRequirementKind.PlatformAuthorityDomain;
            var identity = requirement.Kind == ComponentResourceRequirementKind.LocalCapability
                ? $"{requirement.ResourceKind}:{requirement.ResourceId}:{requirement.Rights}"
                : "platform-authority-domain";
            decisions.Add(new(kind, identity, ManifestRequirementCriticality.Mandatory, ManifestRequirementDisposition.Requested,
                "Declarative request validated; live authority is materialized only by the owning kernel/provider admission path."));
        }

        var driverPlan = ValidateDriverResourcePlan(manifest, plan.DriverResources);
        if (!driverPlan.IsSuccess)
            return Denied(driverPlan.Error, driverPlan.Message!, ManifestRequirementKind.LocalCapability);

        foreach (var dependency in manifest.Dependencies)
        {
            var candidates = ResolveByContract(dependency.Contract);
            var exact = candidates.IsSuccess && candidates.Value!.Length == 1;
            var criticality = dependency.Kind == ServiceDependencyKind.Hard ? ManifestRequirementCriticality.Mandatory : ManifestRequirementCriticality.Optional;
            var disposition = exact
                ? ManifestRequirementDisposition.Granted
                : dependency.Kind == ServiceDependencyKind.Hard
                    ? ManifestRequirementDisposition.Denied
                    : ManifestRequirementDisposition.Unsupported;
            decisions.Add(new(ManifestRequirementKind.Dependency, DependencyIdentity(dependency), criticality, disposition,
                exact ? "Exactly one accepting dependency endpoint is available." : "Dependency did not resolve to exactly one accepting endpoint."));
            if (!exact && dependency.Kind == ServiceDependencyKind.Hard)
                return new(new(manifest.NormalizedDigest, ManifestAdmissionDisposition.Denied, decisions.ToArray()), KernelError.ServiceNotFound, "Hard dependency is unavailable or ambiguous.");
        }

        foreach (var budget in manifest.BudgetRequests)
            decisions.Add(new(ManifestRequirementKind.Budget, $"{budget.Dimension}:{budget.Limit}", ManifestRequirementCriticality.Mandatory,
                ManifestRequirementDisposition.Requested, "Budget request requires exact generation-bound allocation by the budget ledger during admission."));

        foreach (var resource in manifest.ResourceUseRequirements)
            decisions.Add(new(ManifestRequirementKind.ResourceUse, resource.CanonicalIdentity,
                ManifestRequirementCriticality.Mandatory, ManifestRequirementDisposition.Requested,
                "Resource-use metadata is declarative only; generated sentry must resolve live capability and budget owners."));

        var degraded = decisions.Any(static decision => decision.Criticality == ManifestRequirementCriticality.Optional && decision.Disposition != ManifestRequirementDisposition.Granted);
        return new(new(manifest.NormalizedDigest, degraded ? ManifestAdmissionDisposition.Degraded : ManifestAdmissionDisposition.Granted, decisions.ToArray()));
    }

    private static ManifestAdmissionResultV1 DegradeOptionalDependency(
        ManifestAdmissionResultV1 admission,
        ServiceDependencyRequirementV1 dependency,
        string reason)
    {
        var identity = DependencyIdentity(dependency);
        var decisions = admission.Decisions.Select(decision =>
            decision.Kind == ManifestRequirementKind.Dependency && string.Equals(decision.Identity, identity, StringComparison.Ordinal)
                ? decision with { Disposition = ManifestRequirementDisposition.Degraded, Reason = reason }
                : decision).ToArray();
        return admission with { Disposition = ManifestAdmissionDisposition.Degraded, Decisions = decisions };
    }

    private static ManifestAdmissionResultV1 MarkRequirementGranted(
        ManifestAdmissionResultV1 admission,
        ManifestRequirementKind kind,
        string identity,
        string reason)
    {
        var decisions = admission.Decisions.Select(decision =>
            decision.Kind == kind && string.Equals(decision.Identity, identity, StringComparison.Ordinal)
                ? decision with { Disposition = ManifestRequirementDisposition.Granted, Reason = reason }
                : decision).ToArray();
        return admission with { Decisions = decisions };
    }

    private static string DependencyIdentity(ServiceDependencyRequirementV1 dependency) =>
        $"{dependency.Contract.Name}:{dependency.Contract.Version}:{dependency.Contract.Digest}";
}
