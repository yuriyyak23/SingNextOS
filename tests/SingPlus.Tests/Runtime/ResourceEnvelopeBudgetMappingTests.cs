using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class ResourceEnvelopeBudgetMappingTests
{
    [Fact]
    public void ExactComputeTimeMappingIsCanonicalAndNonAuthoritative()
    {
        var mapped = ResourceEnvelopeBudgetMapping.Map([Compute(17)]);

        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.Equal([new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, 17)], mapped.Value);
        Assert.DoesNotContain(typeof(ResourceEnvelopeBudgetMapping).GetMethods(), method =>
            method.Name.Contains("Reserve", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Authorize", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyDuplicateMalformedAndUnknownInputsFailClosed()
    {
        Assert.Equal(KernelError.InvalidMessage, ResourceEnvelopeBudgetMapping.Map([]).Error);
        Assert.Equal(KernelError.InvalidMessage,
            ResourceEnvelopeBudgetMapping.Map([Compute(1), Compute(2)]).Error);
        Assert.Equal(KernelError.InvalidMessage,
            ResourceEnvelopeBudgetMapping.Map([Compute(1) with { Version = 2 }]).Error);
        Assert.Equal(KernelError.InvalidMessage,
            ResourceEnvelopeBudgetMapping.Map([Compute(1) with { ResourceClass = (ResourceClassV1)ushort.MaxValue }]).Error);
    }

    [Theory]
    [InlineData(ResourceClassV1.DmaThroughput)]
    [InlineData(ResourceClassV1.NetworkThroughput)]
    [InlineData(ResourceClassV1.FabricThroughput)]
    [InlineData(ResourceClassV1.DeviceMemoryOccupancy)]
    [InlineData(ResourceClassV1.QueueSlotOccupancy)]
    [InlineData(ResourceClassV1.InflightOperationOccupancy)]
    public void ProviderLocalClassesCannotBeLaunderedIntoSingNextBudgetAuthority(ResourceClassV1 resourceClass)
    {
        var envelope = resourceClass switch
        {
            ResourceClassV1.DmaThroughput or ResourceClassV1.NetworkThroughput or ResourceClassV1.FabricThroughput =>
                new ResourceEnvelopeV1(1, ResourceDimensionFamilyV1.Throughput, resourceClass,
                    ResourceUnitV1.BytesPerWindow, 10, 1_000, "provider:test"),
            ResourceClassV1.DeviceMemoryOccupancy => new(1, ResourceDimensionFamilyV1.Occupancy,
                resourceClass, ResourceUnitV1.Bytes, 10, 0, "provider:test"),
            ResourceClassV1.QueueSlotOccupancy => new(1, ResourceDimensionFamilyV1.Occupancy,
                resourceClass, ResourceUnitV1.Slots, 10, 0, "provider:test"),
            _ => new(1, ResourceDimensionFamilyV1.Occupancy,
                resourceClass, ResourceUnitV1.Operations, 10, 0, "provider:test"),
        };

        var result = ResourceEnvelopeBudgetMapping.Map([Compute(3), envelope]);

        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.Null(result.Value);
    }

    private static ResourceEnvelopeV1 Compute(ulong amount) => new(1,
        ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
        ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1");
}
