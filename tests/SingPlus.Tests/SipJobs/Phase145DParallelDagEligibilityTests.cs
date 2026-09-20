using System.Collections.Immutable;
using System.Reflection;
using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase145DParallelDagEligibilityTests
{
    [Fact]
    public void SupportedTopologyMatrixIsAdmittedOnlyUpToRecordedProcessorCount()
    {
        var available = Environment.ProcessorCount;
        foreach (var branches in new[] { 2, 4, 8 })
        foreach (var workers in new[] { 1, 2, 4, 8, 16, 32 })
        {
            var result = SipJobParallelDagEligibilityVerifier.Verify(Descriptor(branches, workers, available));
            Assert.Equal(workers <= available, result.IsSuccess);
        }
        Assert.False(SipJobFeatureGates.IsEnabled("FG-DAG-PARALLEL"));
    }

    [Fact]
    public void WorkerItemSurfaceCarriesNoAmbientAuthorityOrMutablePayload()
    {
        var properties = typeof(SipJobWorkerItemDescriptor)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(static property => property.Name != "EqualityContract")
            .ToArray();
        Assert.All(properties, property => Assert.Contains(property.PropertyType, new[] { typeof(uint), typeof(string) }));
        Assert.DoesNotContain(properties, property =>
            property.Name.Contains("Capability", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Authority", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Delegate", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Implementation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownVersionGateTopologyAndDuplicateCorrelationFailClosed()
    {
        var valid = Descriptor(2, 1, Math.Max(1, Environment.ProcessorCount));
        Assert.Equal(SipJobParallelDagEligibilityError.UnknownVersion,
            SipJobParallelDagEligibilityVerifier.Verify(valid with { Version = 2 }).Error);
        Assert.Equal(SipJobParallelDagEligibilityError.UnsupportedGate,
            SipJobParallelDagEligibilityVerifier.Verify(valid with { DeclaredGateSet = ["FG-DAG-PARALLEL"] }).Error);
        Assert.Equal(SipJobParallelDagEligibilityError.UnsupportedTopology,
            SipJobParallelDagEligibilityVerifier.Verify(valid with { BranchCount = 3 }).Error);
        Assert.Equal(SipJobParallelDagEligibilityError.MalformedWorkItem,
            SipJobParallelDagEligibilityVerifier.Verify(valid with
            {
                WorkItems = [valid.WorkItems[0], valid.WorkItems[1] with { InvocationCorrelationId = valid.WorkItems[0].InvocationCorrelationId }]
            }).Error);
    }

    [Fact]
    public void CallerCannotInflateRecordedProcessorTopology()
    {
        var actual = Environment.ProcessorCount;
        var inflated = Descriptor(2, 1, checked(actual + 1));

        Assert.Equal(
            SipJobParallelDagEligibilityError.UnsupportedTopology,
            SipJobParallelDagEligibilityVerifier.Verify(inflated).Error);
    }

    [Theory]
    [InlineData("schema:")]
    [InlineData("schema:closed-v1 ")]
    [InlineData("object")]
    [InlineData("region:r")]
    public void WorkerClosedStateRequiresCanonicalNonemptySchemaIdentity(string schemaId)
    {
        var valid = Descriptor(2, 1, Environment.ProcessorCount);
        var malformed = valid with
        {
            WorkItems = valid.WorkItems.SetItem(0, valid.WorkItems[0] with { ClosedStateSchemaId = schemaId })
        };

        Assert.Equal(SipJobParallelDagEligibilityError.MalformedWorkItem,
            SipJobParallelDagEligibilityVerifier.Verify(malformed).Error);
    }

    [Fact]
    public void DefaultCollectionsAndNullWorkerItemsFailClosedBeforeTopologyAnalysis()
    {
        var valid = Descriptor(2, 1, Environment.ProcessorCount);

        Assert.Equal(SipJobParallelDagEligibilityError.MalformedWorkItem,
            SipJobParallelDagEligibilityVerifier.Verify(valid with { WorkItems = default }).Error);
        Assert.Equal(SipJobParallelDagEligibilityError.MalformedWorkItem,
            SipJobParallelDagEligibilityVerifier.Verify(valid with
            {
                WorkItems = [null!, valid.WorkItems[1]]
            }).Error);
        Assert.Equal(SipJobParallelDagEligibilityError.UnsupportedGate,
            SipJobParallelDagEligibilityVerifier.Verify(valid with { DeclaredGateSet = default }).Error);
    }

    private static SipJobParallelDagEligibilityDescriptor Descriptor(int branches, int workers, int available) => new(
        1, branches, workers, available,
        Enumerable.Range(0, branches).Select(index =>
            new SipJobWorkerItemDescriptor(1, $"stage-{index:D2}", $"corr-{index:D2}", "schema:closed-v1")).ToImmutableArray(),
        SipJobParallelDagEligibilityVerifier.ExactGates);
}
