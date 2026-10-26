using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;

namespace SingPlus.Tests.Platform;

public sealed class PlatformMemoryVisibilityTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public void HostPublishesExplicitMemoryVisibilityAsModelOnly()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformFeatureManifest manifest = provider.QueryFeatures();

        Assert.True(manifest.Supports(
            PlatformFeatureFamily.ExplicitMemoryVisibility,
            1,
            PlatformFeatureAvailability.ModelOnly));
        Assert.False(manifest.Supports(
            PlatformFeatureFamily.ExplicitMemoryVisibility,
            1,
            PlatformFeatureAvailability.RuntimeAdmission));
        Assert.False(manifest.Supports(
            PlatformFeatureFamily.ExplicitMemoryVisibility,
            1,
            PlatformFeatureAvailability.Executable));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void HostModelsCoherentFenceMaintenanceAndUnsupportedOutcomes()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformOperationIdentity operation = Stage(provider, Bind(provider, 10, 1));

        PlatformMemoryVisibilityResult coherent = Ensure(
            provider,
            operation,
            PlatformMemoryConsumerClass.CpuExecution,
            PlatformMemoryVisibilityRequirement.CoherentAccess);
        PlatformMemoryVisibilityResult fenced = Ensure(
            provider,
            operation,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);
        PlatformMemoryVisibilityResult maintained = Ensure(
            provider,
            operation,
            PlatformMemoryConsumerClass.IoDevice,
            PlatformMemoryVisibilityRequirement.CacheMaintenance);
        PlatformMemoryVisibilityResult unsupported = Ensure(
            provider,
            operation,
            PlatformMemoryConsumerClass.Accelerator,
            PlatformMemoryVisibilityRequirement.CacheMaintenance);

        Assert.Equal(PlatformMemoryVisibilityOutcome.Coherent, coherent.Outcome);
        Assert.True(coherent.IsSatisfied);
        Assert.Equal(PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied, fenced.Outcome);
        Assert.True(fenced.IsSatisfied);
        Assert.Equal(PlatformMemoryVisibilityOutcome.CacheMaintenanceSatisfied, maintained.Outcome);
        Assert.True(maintained.IsSatisfied);
        Assert.Equal(PlatformMemoryVisibilityOutcome.Unsupported, unsupported.Outcome);
        Assert.False(unsupported.IsSatisfied);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void RequirementsUseExactSemanticOutcomesRatherThanPrivilegeOrdering()
    {
        Assert.True(PlatformMemoryVisibilityContract.IsSatisfied(
            PlatformMemoryVisibilityRequirement.CoherentAccess,
            PlatformMemoryVisibilityOutcome.Coherent));
        Assert.True(PlatformMemoryVisibilityContract.IsSatisfied(
            PlatformMemoryVisibilityRequirement.PublicationFence,
            PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));
        Assert.True(PlatformMemoryVisibilityContract.IsSatisfied(
            PlatformMemoryVisibilityRequirement.CacheMaintenance,
            PlatformMemoryVisibilityOutcome.CacheMaintenanceSatisfied));

        Assert.False(PlatformMemoryVisibilityContract.IsSatisfied(
            PlatformMemoryVisibilityRequirement.PublicationFence,
            PlatformMemoryVisibilityOutcome.Coherent));
        Assert.False(PlatformMemoryVisibilityContract.IsSatisfied(
            PlatformMemoryVisibilityRequirement.CacheMaintenance,
            PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));
        Assert.False(PlatformMemoryVisibilityContract.IsSatisfied(
            PlatformMemoryVisibilityRequirement.CoherentAccess,
            PlatformMemoryVisibilityOutcome.Unsupported));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void MalformedConsumerAndRequirementFailClosed()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformOperationIdentity operation = Stage(provider, Bind(provider, 10, 1));

        PlatformAuthorityResult<PlatformMemoryVisibilityResult> badConsumer = provider.EnsureMemoryVisibility(
            new PlatformMemoryVisibilityRequest(
                operation,
                (PlatformMemoryConsumerClass)999,
                PlatformMemoryVisibilityRequirement.CoherentAccess));
        PlatformAuthorityResult<PlatformMemoryVisibilityResult> badRequirement = provider.EnsureMemoryVisibility(
            new PlatformMemoryVisibilityRequest(
                operation,
                PlatformMemoryConsumerClass.CpuExecution,
                (PlatformMemoryVisibilityRequirement)999));

        Assert.False(badConsumer.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Faulted, badConsumer.Status);
        Assert.False(badRequirement.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Faulted, badRequirement.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void StaleOperationGenerationCannotProduceVisibilityEvidence()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformOperationIdentity operation = Stage(provider, Bind(provider, 10, 1));
        PlatformOperationIdentity stale = operation with
        {
            Generation = new PlatformOperationGeneration(operation.Generation.Value + 1)
        };

        PlatformAuthorityResult<PlatformMemoryVisibilityResult> result = provider.EnsureMemoryVisibility(
            new PlatformMemoryVisibilityRequest(
                stale,
                PlatformMemoryConsumerClass.CpuExecution,
                PlatformMemoryVisibilityRequirement.CoherentAccess));

        Assert.False(result.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Stale, result.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void WrongDomainOperationCannotProduceVisibilityEvidence()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformProviderDomainLease left = Bind(provider, 10, 1);
        PlatformProviderDomainLease right = Bind(provider, 20, 2);
        PlatformOperationIdentity operation = Stage(provider, left);
        PlatformOperationIdentity wrongDomain = operation with { DomainLease = right };

        PlatformAuthorityResult<PlatformMemoryVisibilityResult> result = provider.EnsureMemoryVisibility(
            new PlatformMemoryVisibilityRequest(
                wrongDomain,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryVisibilityRequirement.PublicationFence));

        Assert.False(result.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.WrongDomain, result.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void RevokedDomainCannotProduceVisibilityEvidence()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformProviderDomainLease lease = Bind(provider, 10, 1);
        PlatformOperationIdentity operation = Stage(provider, lease);

        PlatformAuthorityResult revoke = provider.RevokeDomain(lease);
        PlatformAuthorityResult<PlatformMemoryVisibilityResult> result = provider.EnsureMemoryVisibility(
            new PlatformMemoryVisibilityRequest(
                operation,
                PlatformMemoryConsumerClass.CpuExecution,
                PlatformMemoryVisibilityRequirement.CoherentAccess));

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.False(result.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Revoked, result.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ClosedOperationCannotProduceNewVisibilityEvidence()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformOperationIdentity operation = Stage(provider, Bind(provider, 10, 1));

        Assert.True(provider.AdvanceOperation(
            operation,
            PlatformCompletionState.Pending).IsSuccess);
        Assert.True(provider.AdvanceOperation(
            operation,
            PlatformCompletionState.Completed).IsSuccess);
        Assert.True(provider.AdvanceOperation(
            operation,
            PlatformCompletionState.Closed).IsSuccess);

        PlatformAuthorityResult<PlatformMemoryVisibilityResult> result = provider.EnsureMemoryVisibility(
            new PlatformMemoryVisibilityRequest(
                operation,
                PlatformMemoryConsumerClass.CpuExecution,
                PlatformMemoryVisibilityRequirement.CoherentAccess));

        Assert.False(result.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Denied, result.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void VisibilityEvidenceDoesNotAdvanceCompletionLifecycle()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformOperationIdentity operation = Stage(provider, Bind(provider, 10, 1));

        PlatformCompletionReceipt before = provider.ObserveCompletion(operation).Value!;
        PlatformAuthorityResult<PlatformMemoryVisibilityResult> visibility = provider.EnsureMemoryVisibility(
            new PlatformMemoryVisibilityRequest(
                operation,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryVisibilityRequirement.PublicationFence));
        PlatformCompletionReceipt after = provider.ObserveCompletion(operation).Value!;

        Assert.Equal(PlatformCompletionState.Staged, before.State);
        Assert.True(visibility.IsSuccess, visibility.Message);
        Assert.Equal(PlatformCompletionState.Staged, after.State);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void VisibilityResultCarriesNoLocalOrMappingAuthority()
    {
        Type[] propertyTypes = typeof(PlatformMemoryVisibilityResult)
            .GetProperties()
            .Select(static property => property.PropertyType)
            .ToArray();

        Assert.DoesNotContain(typeof(CapabilityId), propertyTypes);
        Assert.DoesNotContain(typeof(DomainId), propertyTypes);
        Assert.DoesNotContain(typeof(RegionHandle), propertyTypes);
        Assert.DoesNotContain(typeof(PlatformProviderDomainLease), propertyTypes);
        Assert.DoesNotContain(typeof(PlatformProviderRegionMappingId), propertyTypes);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void VisibilityProviderDoesNotExposeFlushCachesPrimitive()
    {
        MethodInfo[] methods = typeof(IPlatformMemoryVisibilityProvider).GetMethods();

        Assert.Single(methods);
        Assert.Equal(nameof(IPlatformMemoryVisibilityProvider.EnsureMemoryVisibility), methods[0].Name);
        Assert.DoesNotContain(
            methods,
            static method => method.Name.Contains("Flush", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            methods,
            static method => method.Name.Contains("Invalidate", StringComparison.OrdinalIgnoreCase));
    }

    private static PlatformProviderDomainLease Bind(
        HostPlatformAuthorityProvider provider,
        ulong domainId,
        ulong processGeneration)
    {
        PlatformAuthorityResult<PlatformProviderDomainLease> result = provider.BindDomain(
            new PlatformDomainIdentity(
                new DomainId(domainId),
                new ProcessHandle(new ProcessId(domainId), processGeneration)));

        Assert.True(result.IsSuccess, result.Message);
        return result.Value!;
    }

    private static PlatformOperationIdentity Stage(
        HostPlatformAuthorityProvider provider,
        PlatformProviderDomainLease lease)
    {
        PlatformAuthorityResult<PlatformOperationIdentity> result = provider.StageOperation(lease);

        Assert.True(result.IsSuccess, result.Message);
        return result.Value!;
    }

    private static PlatformMemoryVisibilityResult Ensure(
        HostPlatformAuthorityProvider provider,
        PlatformOperationIdentity operation,
        PlatformMemoryConsumerClass consumer,
        PlatformMemoryVisibilityRequirement requirement)
    {
        PlatformAuthorityResult<PlatformMemoryVisibilityResult> result = provider.EnsureMemoryVisibility(
            new PlatformMemoryVisibilityRequest(operation, consumer, requirement));

        Assert.True(result.IsSuccess, result.Message);
        return result.Value!;
    }
}
