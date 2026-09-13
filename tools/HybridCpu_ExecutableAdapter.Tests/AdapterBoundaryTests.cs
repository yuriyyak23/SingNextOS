using System.Reflection;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.Core.Authority;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class AdapterBoundaryTests
{
    [Fact]
    public void OperationalChildAdapterPublishesOnlyQualifiedFamilies()
    {
        var adapter = new HybridCpuExecutableChildAdapter();
        var features = adapter.QueryNeutralFeatures();
        Assert.Equal(NeutralRuntimeFeatureAvailability.Executable,
            features.Resolve(NeutralRuntimeFeatureFamily.ChildExecutableArtifact).Availability);
        Assert.Equal(NeutralRuntimeFeatureAvailability.Executable,
            features.Resolve(NeutralRuntimeFeatureFamily.BoundedVirtualIo).Availability);
        Assert.Equal(NeutralRuntimeFeatureAvailability.Unavailable,
            features.Resolve(NeutralRuntimeFeatureFamily.ChildTrapDelivery).Availability);
        Assert.IsNotAssignableFrom<INeutralVirtualTrapProvider>(adapter);
        Assert.False(typeof(INeutralDomainRuntime).IsAssignableFrom(adapter.GetType()));
    }

    [Fact]
    public void NoProviderCompositionCannotBecomeAnExecutableFallback()
    {
        var admissionOnly = new NeutralRuntimeFeatureManifest(
        [
            new(NeutralRuntimeFeatureFamily.ChildDomainLifecycle, 1, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        ]);

        Assert.DoesNotContain(admissionOnly.Features,
            feature => feature.Availability == NeutralRuntimeFeatureAvailability.Executable);
        Assert.False(typeof(INeutralDomainRuntime).IsAssignableFrom(typeof(HybridCpuExecutableChildAdapter)));
    }

    [Fact]
    public void AssemblyReferencesContractsButNotModelAuthorityOrHybridCpuInternals()
    {
        var references = typeof(HybridCpuExecutableAdapterProject).Assembly.GetReferencedAssemblies();
        Assert.Contains(references, x => x.Name == "HybridCPU_NeutralRuntime.Contracts");
        Assert.DoesNotContain(references, x => x.Name?.Contains("Model", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(references, x => x.Name == "HybridCPU_NeutralRuntime.AuthorityCore");
        Assert.DoesNotContain(references, x => x.Name?.Contains("ISE", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(typeof(HybridCpuExecutableAdapterProject).Assembly.GetExportedTypes(),
            type => type.Name.Contains("Scaffold", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LifecycleFixtureIncludesEveryFailClosedStateAndRejectsOptimisticTransitions()
    {
        Assert.Equal(
            ["Opening", "Active", "Draining", "Closed", "Revoked", "Faulted", "Quarantined"],
            Enum.GetNames<AdapterResourceLifecycle>());

        var fixture = new LifecycleSemanticsFixture();
        Assert.False(fixture.TryTransition(AdapterResourceLifecycle.Closed));
        Assert.True(fixture.TryTransition(AdapterResourceLifecycle.Active));
        Assert.True(fixture.TryTransition(AdapterResourceLifecycle.Draining));
        Assert.True(fixture.TryTransition(AdapterResourceLifecycle.Closed));
        Assert.False(fixture.TryTransition(AdapterResourceLifecycle.Active));
    }

    [Fact]
    public void AuthorityReservationsAreUniqueAndLateGenerationCannotCrossApply()
    {
        var operations = new OperationReservationRegistry();
        var first = operations.Reserve();
        var second = operations.Reserve();
        Assert.NotEqual(first.Handle, second.Handle);
        Assert.Equal(NeutralLateResultDisposition.RejectedDifferentGeneration,
            operations.Reconcile(first with { Generation = new(first.Generation.Value + 1) }, true));
        Assert.Equal(NeutralLateResultDisposition.Applied, operations.Reconcile(first, true));
    }

    [Fact]
    public void UnknownBackendOutcomeQuarantinesWithoutReleasingReservation()
    {
        var operations = new OperationReservationRegistry();
        var operation = operations.Reserve();
        Assert.Equal(NeutralLateResultDisposition.QuarantinedUnknownOutcome,
            operations.Reconcile(operation, false));
        Assert.True(operations.IsReserved(operation));
        Assert.Equal(NeutralLateResultDisposition.Applied, operations.Reconcile(operation, true));
        Assert.False(operations.IsReserved(operation));
    }

    [Fact]
    public void PublicSurfaceContainsNoRawHardwareIdentityOrDmaExecutionOperation()
    {
        string[] forbidden = ["DomainTag", "AddressSpaceTag", "Vmx", "Vmcs", "Apic", "Msi", "Gsi", "Iommu", "Physical", "BusAddress", "SubmitDma", "CompleteDma", "Descriptor", "Queue"];
        var names = typeof(HybridCpuExecutableAdapterProject).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Select(member => member.Name);
        foreach (var fragment in forbidden)
            Assert.DoesNotContain(names, name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class LifecycleSemanticsFixture
    {
        public AdapterResourceLifecycle Lifecycle { get; private set; } = AdapterResourceLifecycle.Opening;

        public bool TryTransition(AdapterResourceLifecycle next)
        {
            bool allowed = (Lifecycle, next) switch
            {
                (AdapterResourceLifecycle.Opening, AdapterResourceLifecycle.Active or AdapterResourceLifecycle.Revoked or AdapterResourceLifecycle.Faulted or AdapterResourceLifecycle.Quarantined) => true,
                (AdapterResourceLifecycle.Active, AdapterResourceLifecycle.Draining or AdapterResourceLifecycle.Revoked or AdapterResourceLifecycle.Faulted or AdapterResourceLifecycle.Quarantined) => true,
                (AdapterResourceLifecycle.Draining, AdapterResourceLifecycle.Closed or AdapterResourceLifecycle.Revoked or AdapterResourceLifecycle.Faulted or AdapterResourceLifecycle.Quarantined) => true,
                _ => false,
            };
            if (allowed) Lifecycle = next;
            return allowed;
        }
    }
}
