using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Tests.Platform;

public sealed class VNextPhase16ProviderResourceContractTests
{
    [Fact]
    public void ExactVersionedLifecycleValidatesWithoutTransportingLocalAuthority()
    {
        var request = Request();
        var reservation = Reservation(request);
        var binding = Binding(reservation);
        var evidence = new PlatformResourceUsageEvidence(
            PlatformResourceContract.ContractVersion, reservation, binding.Operation,
            PlatformResourceReconciliationState.ExactUsage, 600, 1);

        Assert.True(PlatformResourceContract.ValidateRequest(request).IsSuccess);
        Assert.True(PlatformResourceContract.ValidateReservation(request, reservation).IsSuccess);
        Assert.True(PlatformResourceContract.ValidateSubmissionBinding(reservation, binding).IsSuccess);
        Assert.True(PlatformResourceContract.ValidateUsageEvidence(binding, evidence).IsSuccess);

        var publicMembers = typeof(PlatformResourceAdmissionRequest).Assembly.GetExportedTypes()
            .Where(type => type.Name.StartsWith("PlatformResource", StringComparison.Ordinal))
            .SelectMany(type => type.GetProperties())
            .Select(property => property.PropertyType)
            .ToArray();
        Assert.DoesNotContain(typeof(CapabilityId), publicMembers);
        Assert.DoesNotContain(typeof(BudgetReservationHandle), publicMembers);
        Assert.DoesNotContain(typeof(RegionHandle), publicMembers);
    }

    [Fact]
    public void UnknownMalformedStaleAndCrossOperationFormsFailClosed()
    {
        var request = Request();
        var reservation = Reservation(request);
        var binding = Binding(reservation);

        Assert.Equal(PlatformAuthorityStatus.Faulted,
            PlatformResourceContract.ValidateRequest(request with { ContractVersion = 2 }).Status);
        Assert.Equal(PlatformAuthorityStatus.Faulted,
            PlatformResourceContract.ValidateRequest(request with
            {
                Envelope = request.Envelope with { Amount = 0 }
            }).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            PlatformResourceContract.ValidateReservation(request,
                reservation with
                {
                    Correlation = reservation.Correlation with
                    {
                        Generation = new PlatformResourceCorrelationGeneration(2)
                    }
                }).Status);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            PlatformResourceContract.ValidateSubmissionBinding(reservation,
                binding with
                {
                    Operation = binding.Operation with
                    {
                        DomainLease = binding.Operation.DomainLease with
                        {
                            Generation = new PlatformProviderLeaseGeneration(2)
                        }
                    }
                }).Status);

        var replay = new PlatformResourceUsageEvidence(
            PlatformResourceContract.ContractVersion, reservation,
            binding.Operation with { Generation = new PlatformOperationGeneration(2) },
            PlatformResourceReconciliationState.ExactUsage, 1, 1);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            PlatformResourceContract.ValidateUsageEvidence(binding, replay).Status);
    }

    [Theory]
    [InlineData(PlatformResourceReconciliationState.Pending, 0UL, true, false)]
    [InlineData(PlatformResourceReconciliationState.Pending, 1UL, false, false)]
    [InlineData(PlatformResourceReconciliationState.ExactUsage, 600UL, true, true)]
    [InlineData(PlatformResourceReconciliationState.ExactUsage, 1001UL, false, true)]
    [InlineData(PlatformResourceReconciliationState.ContainedWithoutConsumption, 0UL, true, true)]
    [InlineData(PlatformResourceReconciliationState.ContainedWithoutConsumption, 1UL, false, true)]
    [InlineData(PlatformResourceReconciliationState.ConservativeWorstCase, 1000UL, true, true)]
    [InlineData(PlatformResourceReconciliationState.ConservativeWorstCase, 999UL, false, true)]
    public void ReconciliationAmountsAreCanonicalAndPendingNeverProvesClosure(
        PlatformResourceReconciliationState state, ulong amount, bool valid, bool terminal)
    {
        var reservation = Reservation(Request());
        var binding = Binding(reservation);
        var evidence = new PlatformResourceUsageEvidence(
            PlatformResourceContract.ContractVersion, reservation, binding.Operation, state, amount, 1);

        Assert.Equal(valid, PlatformResourceContract.ValidateUsageEvidence(binding, evidence).IsSuccess);
        Assert.Equal(terminal, evidence.IsTerminal);
    }

    [Fact]
    public void ContractContainsNoHardwarePrivatePlacementVocabulary()
    {
        var names = typeof(PlatformResourceContract).Assembly.GetExportedTypes()
            .Where(type => type.Name.StartsWith("PlatformResource", StringComparison.Ordinal))
            .SelectMany(type => type.GetProperties().Select(property => property.Name)
                .Append(type.Name))
            .ToArray();

        foreach (var forbidden in new[]
                 {
                     "Lane", "Opcode", "SlotIndex", "Dsc", "L7", "Vmcs", "Iommu", "Cxl",
                     "QueueId", "Topology", "PhysicalAddress"
                 })
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    private static PlatformResourceAdmissionRequest Request()
    {
        var subject = new PlatformDomainIdentity(new DomainId(7), new ProcessHandle(new ProcessId(11), 3));
        var domain = new PlatformProviderDomainLease(new PlatformProviderDomainLeaseId(13),
            new PlatformProviderLeaseGeneration(1), subject);
        var envelope = new ResourceEnvelopeV1(ResourceEnvelopeV1.CurrentVersion,
            ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds,
            1000, 0, "compute.default");
        return new PlatformResourceAdmissionRequest(PlatformResourceContract.ContractVersion,
            new PlatformResourceCorrelation(new PlatformResourceCorrelationId(17),
                new PlatformResourceCorrelationGeneration(1)), domain, envelope);
    }

    private static PlatformResourceReservation Reservation(PlatformResourceAdmissionRequest request) =>
        new(PlatformResourceContract.ContractVersion, new PlatformResourceReservationId(19),
            new PlatformResourceReservationGeneration(1), request.Correlation, request.DomainLease, request.Envelope);

    private static PlatformResourceSubmissionBinding Binding(PlatformResourceReservation reservation) =>
        new(PlatformResourceContract.ContractVersion, reservation,
            new PlatformOperationIdentity(new PlatformOperationId(23), new PlatformOperationGeneration(1),
                reservation.DomainLease));
}
