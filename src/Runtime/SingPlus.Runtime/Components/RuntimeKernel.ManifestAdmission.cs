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

    internal KernelResult ValidateReplacementAdmission(ComponentAdmissionPlan plan, ProcessHandle previous)
    {
        KernelResult imports = ValidateComponentContractImports(plan);
        if (!imports.IsSuccess) return imports;
        ComponentAdmissionEvaluation evaluation = EvaluateComponentAdmissionCore(plan, previous);
        if (evaluation.Error is { } error) return KernelResult.Fail(error, evaluation.Message!);
        KernelResult process = Processes.ValidateCreation(plan.Manifest.Process);
        if (!process.IsSuccess) return process;
        foreach (ComponentCapabilityGrant grant in plan.Grants)
        {
            // The subject domain may be materialized by CreateProcess. This is a request,
            // not issuer authority; MintCapability still performs its live owner check.
            if (grant.IssuerDomain == plan.Manifest.Process.DomainId) continue;
            KernelResult issuer = ValidateCapabilityIssuerPresence(grant.IssuerDomain);
            if (!issuer.IsSuccess) return issuer;
        }
        return KernelResult.Ok();
    }

    private static KernelResult ValidateComponentContractImports(ComponentAdmissionPlan plan)
    {
        if (plan.ManifestV2 is { } v2)
        {
            CapabilityRequirementV1[] declared = v2.StaticCapabilityImports.ToArray();
            CapabilityRequirementV1[] supplied = plan.Grants.Select(static grant => grant.Requirement)
                .OrderBy(static item => item.ResourceKind).ThenBy(static item => item.ResourceId, StringComparer.Ordinal).ThenBy(static item => item.Rights).ToArray();
            if (!declared.SequenceEqual(supplied))
                return KernelResult.Fail(KernelError.InvalidManifest, "Runtime grants must exactly match the V2 static capability imports.");
        }
        return KernelResult.Ok();
    }

    private ComponentAdmissionEvaluation EvaluateComponentAdmissionCore(ComponentAdmissionPlan plan, ProcessHandle? replacementOf = null)
    {
        ServiceManifestV1 manifest = plan.Manifest;
        List<ManifestRequirementDecisionV1> decisions = new List<ManifestRequirementDecisionV1>();

        ComponentAdmissionEvaluation Denied(KernelError error, string message, ManifestRequirementKind kind = ManifestRequirementKind.Compatibility)
        {
            decisions.Add(new(kind, message, ManifestRequirementCriticality.Mandatory, ManifestRequirementDisposition.Denied, message));
            return new(new(manifest.NormalizedDigest, ManifestAdmissionDisposition.Denied, decisions.ToArray()), error, message);
        }

        if (!manifest.MatchesImage(plan.Image.Span))
            return Denied(KernelError.ComponentDigestMismatch, "Component image digest does not match its manifest.");
        if (replacementOf is { } previous)
        {
            if (!_components.TryGetValue(manifest.Identity, out ComponentAdmissionRecord? old) || old.Process != previous ||
                previous.Generation == ulong.MaxValue || manifest.Process.ProcessId != previous.ProcessId ||
                manifest.Process.Generation != previous.Generation + 1)
                return Denied(KernelError.StaleGeneration, "Replacement preflight requires the exact tracked old process and next generation.");
            if (old.State != ComponentLifecycleState.Reclaimable)
                return Denied(KernelError.ReplacementBlocked, "Replacement preflight requires exact old component closure.");
        }
        else if (_components.ContainsKey(manifest.Identity))
            return Denied(KernelError.DuplicateIdentity, $"Component '{manifest.Identity.Name}' is already tracked.");
        if (!RegistrationsMatchManifest(manifest, plan.ProvidedServices))
            return Denied(KernelError.InvalidManifest, "Provided service registrations do not exactly match the component manifest.");

        ServiceCompatibilityConstraintsV1 compatibility = manifest.Compatibility;
        bool compatible = RuntimeManifestContractVersion >= compatibility.MinimumRuntimeContractVersion &&
            (compatibility.MaximumRuntimeContractVersion is null || RuntimeManifestContractVersion <= compatibility.MaximumRuntimeContractVersion.Value);
        decisions.Add(new(ManifestRequirementKind.Compatibility, $"runtime-contract:{RuntimeManifestContractVersion}", ManifestRequirementCriticality.Mandatory,
            compatible ? ManifestRequirementDisposition.Granted : ManifestRequirementDisposition.Denied,
            compatible ? "Runtime contract is within the requested range." : "Runtime contract is outside the requested range."));
        if (!compatible)
            return new(new(manifest.NormalizedDigest, ManifestAdmissionDisposition.Denied, decisions.ToArray()), KernelError.PlatformUnsupported, "Runtime compatibility constraint is not satisfied.");

        PlatformFeatureManifest platformManifest = QueryPlatformFeatures();
        foreach (PlatformRequirementV1 requirement in manifest.PlatformRequirements)
        {
            ManifestRequirementCriticality criticality = requirement.Criticality;
            string identity = $"platform:{(int)requirement.Family}:v{requirement.MinimumContractVersion}:{requirement.Availability}";
            bool supported = false;
            string reason = "Feature family is unknown to this runtime.";
            if (Enum.IsDefined(requirement.Family))
            {
                PlatformFeatureDescriptor feature = platformManifest.Resolve((PlatformFeatureFamily)requirement.Family);
                supported = feature.ContractVersion >= requirement.MinimumContractVersion &&
                    feature.Availability == (PlatformFeatureAvailability)requirement.Availability;
                reason = supported
                    ? "Exact provider-neutral feature requirement is available."
                    : $"Available projection is v{feature.ContractVersion} {feature.Availability}.";
            }
            ManifestRequirementDisposition disposition = supported
                ? ManifestRequirementDisposition.Granted
                : criticality == ManifestRequirementCriticality.Optional
                    ? ManifestRequirementDisposition.Unsupported
                    : ManifestRequirementDisposition.Denied;
            decisions.Add(new(ManifestRequirementKind.PlatformFeature, identity, criticality, disposition, reason));
            if (!supported && criticality == ManifestRequirementCriticality.Mandatory)
                return new(new(manifest.NormalizedDigest, ManifestAdmissionDisposition.Denied, decisions.ToArray()), KernelError.PlatformUnsupported, reason);
        }

        KernelResult resourcePlan = ValidateResourcePlan(manifest.ResourceRequirements, plan.Grants);
        if (!resourcePlan.IsSuccess)
            return Denied(resourcePlan.Error, resourcePlan.Message!, ManifestRequirementKind.LocalCapability);
        foreach (ComponentResourceRequirementV1 requirement in manifest.ResourceRequirements)
        {
            ManifestRequirementKind kind = requirement.Kind == ComponentResourceRequirementKind.LocalCapability
                ? ManifestRequirementKind.LocalCapability
                : ManifestRequirementKind.PlatformAuthorityDomain;
            string identity = requirement.Kind == ComponentResourceRequirementKind.LocalCapability
                ? $"{requirement.ResourceKind}:{requirement.ResourceId}:{requirement.Rights}"
                : "platform-authority-domain";
            decisions.Add(new(kind, identity, ManifestRequirementCriticality.Mandatory, ManifestRequirementDisposition.Requested,
                "Declarative request validated; live authority is materialized only by the owning kernel/provider admission path."));
        }

        KernelResult driverPlan = ValidateDriverResourcePlan(manifest, plan.DriverResources);
        if (!driverPlan.IsSuccess)
            return Denied(driverPlan.Error, driverPlan.Message!, ManifestRequirementKind.LocalCapability);

        foreach (ServiceDependencyRequirementV1 dependency in manifest.Dependencies)
        {
            KernelResult<ServiceEndpointDescriptor[]> candidates = ResolveByContract(dependency.Contract);
            bool exact = candidates.IsSuccess && candidates.Value!.Length == 1;
            ManifestRequirementCriticality criticality = dependency.Kind == ServiceDependencyKind.Hard ? ManifestRequirementCriticality.Mandatory : ManifestRequirementCriticality.Optional;
            ManifestRequirementDisposition disposition = exact
                ? ManifestRequirementDisposition.Granted
                : dependency.Kind == ServiceDependencyKind.Hard
                    ? ManifestRequirementDisposition.Denied
                    : ManifestRequirementDisposition.Unsupported;
            decisions.Add(new(ManifestRequirementKind.Dependency, DependencyIdentity(dependency), criticality, disposition,
                exact ? "Exactly one accepting dependency endpoint is available." : "Dependency did not resolve to exactly one accepting endpoint."));
            if (!exact && dependency.Kind == ServiceDependencyKind.Hard)
                return new(new(manifest.NormalizedDigest, ManifestAdmissionDisposition.Denied, decisions.ToArray()), KernelError.ServiceNotFound, "Hard dependency is unavailable or ambiguous.");
        }

        foreach (ServiceBudgetRequestV1 budget in manifest.BudgetRequests)
            decisions.Add(new(ManifestRequirementKind.Budget, $"{budget.Dimension}:{budget.Limit}", ManifestRequirementCriticality.Mandatory,
                ManifestRequirementDisposition.Requested, "Budget request requires exact generation-bound allocation by the budget ledger during admission."));

        foreach (SipResourceRequirementV1 resource in manifest.ResourceUseRequirements)
            decisions.Add(new(ManifestRequirementKind.ResourceUse, resource.CanonicalIdentity,
                ManifestRequirementCriticality.Mandatory, ManifestRequirementDisposition.Requested,
                "Resource-use metadata is declarative only; generated sentry must resolve live capability and budget owners."));

        bool degraded = decisions.Any(static decision => decision.Criticality == ManifestRequirementCriticality.Optional && decision.Disposition != ManifestRequirementDisposition.Granted);
        return new(new(manifest.NormalizedDigest, degraded ? ManifestAdmissionDisposition.Degraded : ManifestAdmissionDisposition.Granted, decisions.ToArray()));
    }

    private static ManifestAdmissionResultV1 DegradeOptionalDependency(
        ManifestAdmissionResultV1 admission,
        ServiceDependencyRequirementV1 dependency,
        string reason)
    {
        string identity = DependencyIdentity(dependency);
        ManifestRequirementDecisionV1[] decisions = admission.Decisions.Select(decision =>
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
        ManifestRequirementDecisionV1[] decisions = admission.Decisions.Select(decision =>
            decision.Kind == kind && string.Equals(decision.Identity, identity, StringComparison.Ordinal)
                ? decision with { Disposition = ManifestRequirementDisposition.Granted, Reason = reason }
                : decision).ToArray();
        return admission with { Decisions = decisions };
    }

    private static string DependencyIdentity(ServiceDependencyRequirementV1 dependency) =>
        $"{dependency.Contract.Name}:{dependency.Contract.Version}:{dependency.Contract.Digest}";
}
