using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase13ResourceFamilyBoundaryTests
{
    [Fact]
    public void FutureFamilyGatesRemainIndependentlyOff()
    {
        string[] gates = ["FG-VNX-DMA-THROUGHPUT", "FG-VNX-NETWORK-THROUGHPUT",
            "FG-VNX-FABRIC-THROUGHPUT", "FG-VNX-DEVICE-OCCUPANCY"];
        Assert.All(gates, gate => Assert.False(VNextFeatureGates.IsEnabled(gate)));
    }

    [Fact]
    public void DimensionsCannotBeComparedOrConvertedAcrossFamilies()
    {
        var time = Envelope(ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
            ResourceUnitV1.Nanoseconds, 100, 0);
        var throughput = Envelope(ResourceDimensionFamilyV1.Throughput, ResourceClassV1.DmaThroughput,
            ResourceUnitV1.BytesPerWindow, 100, 1_000);
        var occupancy = Envelope(ResourceDimensionFamilyV1.Occupancy, ResourceClassV1.DeviceMemoryOccupancy,
            ResourceUnitV1.Bytes, 100, 0);

        Assert.False(ResourceEnvelopeV1.IsSubset(time, throughput));
        Assert.False(ResourceEnvelopeV1.IsSubset(throughput, occupancy));
        Assert.Throws<InvalidOperationException>(() => time.CheckedWindowQuantity());
        Assert.Throws<OverflowException>(() => (throughput with
            { Amount = ulong.MaxValue - 1, WindowNanoseconds = 2 }).CheckedWindowQuantity());
    }

    [Fact]
    public void ExistingBudgetVectorReservationIsAtomicAndCanonicallyOrdered()
    {
        var authority = new ResourceBudgetAuthority();
        BudgetAmount[] limits =
        [
            new(ServiceBudgetDimension.OwnedMemoryBytes, 10),
            new(ServiceBudgetDimension.ComputeTimeNanoseconds, 10),
        ];
        Assert.True(authority.ConfigureSystem(limits).IsSuccess);
        var service = authority.CreateChild(authority.SystemBudget, BudgetAccountLevel.Service,
            "svc", limits).Value!.Account;
        var processAccount = authority.CreateChild(service, BudgetAccountLevel.ProcessDomain,
            "proc", limits).Value!.Account;
        var process = new ProcessHandle(new(13), 1);
        Assert.True(authority.AttachProcess(process, processAccount).IsSuccess);

        var failed = authority.Reserve(process,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 5),
             new(ServiceBudgetDimension.OwnedMemoryBytes, 11)],
            BudgetReservationLifetime.LocalResource, AdmissionQosHint.None);
        Assert.Equal(KernelError.BudgetExceeded, failed.Error);
        Assert.All(authority.Query(processAccount).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));

        var reserved = authority.Reserve(process,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 3),
             new(ServiceBudgetDimension.ComputeTimeNanoseconds, 4)],
            BudgetReservationLifetime.LocalResource, AdmissionQosHint.None).Value!;
        Assert.Equal(reserved.Amounts.OrderBy(amount => amount.Dimension), reserved.Amounts);
    }

    [Fact]
    public void SemanticEnvelopeContainsNoProviderTopologyOrPrivateHandle()
    {
        string[] forbidden = ["Cxl", "Topology", "Physical", "QueueId", "Iommu", "Dpa", "Hdm", "ProviderHandle"];
        Assert.DoesNotContain(typeof(ResourceEnvelopeV1).GetProperties(), property =>
            forbidden.Any(fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(Enum.GetNames<ServiceBudgetDimension>(), name =>
            name.Contains("BytesPerWindow", StringComparison.OrdinalIgnoreCase));
    }

    private static ResourceEnvelopeV1 Envelope(ResourceDimensionFamilyV1 family,
        ResourceClassV1 resourceClass, ResourceUnitV1 unit, ulong amount, ulong window) =>
        new(1, family, resourceClass, unit, amount, window, "semantic:test");
}
