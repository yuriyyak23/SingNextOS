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
        var provider = new HostPlatformAuthorityProvider();
        var manifest = provider.QueryFeatures();

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
        var provider = new HostPlatformAuthorityProvider();
        var operation = Stage(provider, Bind(provider, 10, 1));

        var coherent = Ensure(
            provider,
            operation,
            PlatformMemoryConsumerClass.CpuExecution,
            PlatformMemoryVisibilityRequirement.CoherentAccess);
        var fenced = Ensure(
            provider,
            operation,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);
        var maintained = Ensure(
            provider,
            operation,
            PlatformMemoryConsumerClass.IoDevice,
            PlatformMemoryVisibilityRequirement.CacheMaintenance);
        var unsupported = Ensure(
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
        var provider = new HostPlatformAuthorityProvider();
        var operation = Stage(provider, Bind(provider, 10, 1));

        var badConsumer = provider.EnsureMemoryVisibility(
            new PlatformMemoryVisibilityRequest(
                operation,
                (PlatformMemoryConsumerClass)999,
                PlatformMemoryVisibilityRequirement.CoherentAccess));
        var badRequirement = provider.EnsureMemoryVisibility(
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
        var provider = new HostPlatformAuthorityProvider();
        var operation = Stage(provider, Bind(provider, 10, 1));
        var stale = operation with
        {
            Generation = new PlatformOperationGeneration(operation.Generation.Value + 1)
        };

        var result = provider.EnsureMemoryVisibility(
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
        var provider = new HostPlatformAuthorityProvider();
        var left = Bind(provider, 10, 1);
        var right = Bind(provider, 20, 2);
        var operation = Stage(provider, left);
        var wrongDomain = operation with { DomainLease = right };

        var result = provider.EnsureMemoryVisibility(
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
        var provider = new HostPlatformAuthorityProvider();
        var lease = Bind(provider, 10, 1);
        var operation = Stage(provider, lease);

        var revoke = provider.RevokeDomain(lease);
        var result = provider.EnsureMemoryVisibility(
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
        var provider = new HostPlatformAuthorityProvider();
        var operation = Stage(provider, Bind(provider, 10, 1));

        Assert.True(provider.AdvanceOperation(
            operation,
            PlatformCompletionState.Pending).IsSuccess);
        Assert.True(provider.AdvanceOperation(
            operation,
            PlatformCompletionState.Completed).IsSuccess);
        Assert.True(provider.AdvanceOperation(
            operation,
            PlatformCompletionState.Closed).IsSuccess);

        var result = provider.EnsureMemoryVisibility(
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
        var provider = new HostPlatformAuthorityProvider();
        var operation = Stage(provider, Bind(provider, 10, 1));

        var before = provider.ObserveCompletion(operation).Value!;
        var visibility = provider.EnsureMemoryVisibility(
            new PlatformMemoryVisibilityRequest(
                operation,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryVisibilityRequirement.PublicationFence));
        var after = provider.ObserveCompletion(operation).Value!;

        Assert.Equal(PlatformCompletionState.Staged, before.State);
        Assert.True(visibility.IsSuccess, visibility.Message);
        Assert.Equal(PlatformCompletionState.Staged, after.State);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void VisibilityResultCarriesNoLocalOrMappingAuthority()
    {
        var propertyTypes = typeof(PlatformMemoryVisibilityResult)
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
        var methods = typeof(IPlatformMemoryVisibilityProvider).GetMethods();

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
        var result = provider.BindDomain(
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
        var result = provider.StageOperation(lease);

        Assert.True(result.IsSuccess, result.Message);
        return result.Value!;
    }

    private static PlatformMemoryVisibilityResult Ensure(
        HostPlatformAuthorityProvider provider,
        PlatformOperationIdentity operation,
        PlatformMemoryConsumerClass consumer,
        PlatformMemoryVisibilityRequirement requirement)
    {
        var result = provider.EnsureMemoryVisibility(
            new PlatformMemoryVisibilityRequest(operation, consumer, requirement));

        Assert.True(result.IsSuccess, result.Message);
        return result.Value!;
    }
}
