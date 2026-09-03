using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformBorrowReadGrantTeardownTests
{
    [Fact]
    public void BorrowerTeardownClosesGrantBeforeReturningCpuBorrow()
    {
        var scenario = CreateScenario();
        var grant = CreateGrant(scenario);

        var terminate = scenario.Kernel.TerminateProcess(scenario.Borrower);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.False(scenario.Lease.IsValid);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        scenario.Buffer.Span[0] = 7;
        Assert.Equal((byte)7, scenario.Buffer.Span[0]);
        Assert.Equal(0, scenario.Provider.ActiveMappingCount);
        Assert.Equal(
            KernelError.PlatformBindingNotFound,
            scenario.Kernel.PreparePlatformBorrowReadGrantForExternalReader(
                scenario.Owner,
                grant).Error);
    }

    [Fact]
    public void OwnerTeardownClosesGrantBeforeRevokingBorrowAndReclaimingRegion()
    {
        var scenario = CreateScenario();
        _ = CreateGrant(scenario);

        var terminate = scenario.Kernel.TerminateProcess(scenario.Owner);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.False(scenario.Lease.IsValid);
        Assert.False(scenario.Buffer.IsValid);
        Assert.Equal(RegionState.Released, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, scenario.Provider.ActiveMappingCount);
        Assert.Equal(KernelError.StaleHandle, scenario.Kernel.Processes.Resolve(scenario.Owner).Error);
    }

    [Fact]
    public void BorrowerTeardownRemainsDrainingUntilGrantClosureIsObserved()
    {
        var scenario = CreateScenario();
        _ = CreateGrant(scenario);
        scenario.Provider.CompletionState = PlatformCompletionState.Draining;

        var terminate = scenario.Kernel.TerminateProcess(scenario.Borrower);

        Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);
        Assert.True(scenario.Lease.IsValid);
        Assert.Equal(RegionState.Loaned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        var process = scenario.Kernel.Processes.Resolve(scenario.Borrower);
        Assert.True(process.IsSuccess, process.Message);
        Assert.Equal(ProcessState.Exiting, process.Value!.State);
        var pending = scenario.Kernel.QueryProcessTeardown(scenario.Borrower);
        Assert.True(pending.IsSuccess, pending.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformDraining, pending.Value!.Phase);
        Assert.Equal(1, pending.Value.PendingPlatformMappings);
        Assert.False(pending.Value.LocalReclaimCompleted);

        scenario.Provider.CompletionState = PlatformCompletionState.Closed;
        var completed = scenario.Kernel.ObserveProcessTeardown(scenario.Borrower);

        Assert.True(completed.IsSuccess, completed.Message);
        Assert.True(completed.Value!.LocalReclaimCompleted);
        Assert.False(scenario.Lease.IsValid);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, scenario.Provider.ActiveMappingCount);
    }

    private static PlatformBorrowReadGrant CreateGrant(Scenario scenario)
    {
        var grant = scenario.Kernel.CreatePlatformBorrowReadGrant(
            scenario.Owner,
            scenario.Borrower,
            scenario.Binding,
            scenario.Lease.Handle,
            0,
            64);
        Assert.True(grant.IsSuccess, grant.Message);
        return grant.Value!;
    }

    private static Scenario CreateScenario()
    {
        var provider = new TeardownProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 821, 921);
        var (_, borrower) = TestFixtures.Create(kernel, 822, 922);
        var buffer = kernel.AllocateBuffer<byte>(owner, 256).Value!;
        var lease = CreateCpuBorrow(kernel, owner, borrower, buffer);
        var binding = kernel.BindPlatformDomain(owner).Value!;
        return new Scenario(kernel, provider, owner, borrower, buffer, lease, binding);
    }

    private static BorrowLease<byte> CreateCpuBorrow(
        RuntimeKernel kernel,
        ProcessHandle owner,
        ProcessHandle borrower,
        OwnedBuffer<byte> buffer)
    {
        var channel = kernel.CreateChannel(owner, borrower, BorrowProtocol(), 4);
        Assert.True(channel.IsSuccess, channel.Message);
        Assert.True(kernel.Send(owner, borrower, channel.Value.Left, 1).IsSuccess);
        Assert.True(kernel.Receive(borrower, channel.Value.Right).IsSuccess);
        Assert.True(kernel.Send(owner, borrower, channel.Value.Left, 2, buffer).IsSuccess);
        var receive = kernel.Receive(borrower, channel.Value.Right);
        Assert.True(receive.IsSuccess, receive.Message);
        return Assert.IsType<BorrowLease<byte>>(receive.Value!.Payload);
    }

    private static RequestPayloadDescriptorV1 OwnershipRequest(string parameterName) =>
        new(
            RequestPayloadKind.Ownership,
            parameterName,
            "SingPlus.Sip.OwnedBuffer",
            ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);

    private static ProtocolDefinitionV1 BorrowProtocol() => new(
        "SingPlus.Tests.Platform.IBorrowGrantTeardownProtocol",
        "borrow-grant-teardown-contract-digest",
        "Idle",
        new[] { "Done" },
        new[]
        {
            new ProtocolMessageDescriptorV1(1, "Start"),
            new ProtocolMessageDescriptorV1(
                2,
                "Peek",
                borrows: new[] { "data" },
                requestPayload: OwnershipRequest("data")),
        },
        new[]
        {
            new ProtocolTransitionV1(1, "Idle", "Active"),
            new ProtocolTransitionV1(2, "Active", "Active"),
        });

    private sealed record Scenario(
        RuntimeKernel Kernel,
        TeardownProvider Provider,
        ProcessHandle Owner,
        ProcessHandle Borrower,
        OwnedBuffer<byte> Buffer,
        BorrowLease<byte> Lease,
        PlatformDomainBinding Binding);

    private sealed class TeardownProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionVisibilityProvider,
        IPlatformRegionRevocationProvider
    {
        private sealed class MappingRecord(
            PlatformProviderOwnedRegionMapping mapping)
        {
            public PlatformProviderOwnedRegionMapping Mapping { get; } = mapping;
            public bool Revoked { get; set; }
        }

        private readonly Dictionary<PlatformProviderDomainLeaseId, PlatformProviderDomainLease>
            _domains = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, MappingRecord>
            _mappings = [];
        private PlatformOperationIdentity? _operation;
        private PlatformProviderRegionMappingId? _revokingMapping;
        private ulong _nextDomain = 1;
        private ulong _nextMapping = 1;
        private ulong _nextOperation = 1;

        public PlatformCompletionState CompletionState { get; set; } =
            PlatformCompletionState.Closed;
        public int ActiveMappingCount => _mappings.Values.Count(static record => !record.Revoked);

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("borrow-read-grant-teardown-test"),
            4,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                1,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.OwnedRegionMapping,
                PlatformOwnedRegionMappingContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.ExplicitMemoryVisibility,
                PlatformRegionVisibilityContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(_nextDomain++),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _domains.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            if (_mappings.Values.Any(record =>
                    !record.Revoked && record.Mapping.Lease.DomainLease == lease))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Domain still has an active mapping.");
            }

            _domains.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access)
        {
            var exact = MapOwnedRegionSlice(
                domainLease,
                new PlatformRegionSlice(region, 0, region.ByteLength, access));
            return exact.IsSuccess
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(exact.Value!.Lease)
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                    exact.Status,
                    exact.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
            PlatformProviderDomainLease domainLease,
            PlatformRegionSlice slice)
        {
            if (!_domains.TryGetValue(domainLease.LeaseId, out var active) || active != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Wrong domain lease.");
            }

            var expectedOwner = new RegionOwner(
                domainLease.Subject.DomainId,
                domainLease.Subject.ProcessGeneration);
            if (slice.Region.Owner != expectedOwner || slice.Access != PlatformMemoryAccess.Read)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Teardown test accepts only owner-bound read mappings.");
            }

            var lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                slice.Region,
                slice.Access);
            var mapping = new PlatformProviderOwnedRegionMapping(lease, slice);
            _mappings.Add(lease.MappingId, new MappingRecord(mapping));
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapping);
        }

        public PlatformAuthorityResult<PlatformRegionVisibilityResult>
            PrepareRegionMappingForConsumer(PlatformRegionVisibilityRequest request) =>
            PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(
                new PlatformRegionVisibilityResult(
                    request.Mapping.MappingId,
                    request.Mapping.Generation,
                    request.Slice,
                    request.Consumer,
                    request.Requirement,
                    PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            if (_mappings.TryGetValue(mapping.MappingId, out var record))
                record.Revoked = true;
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformRegionRevocationTicket>
            BeginRegionMappingRevocation(
                PlatformProviderRegionMappingLease mapping,
                PlatformRegionRevocationPolicy policy)
        {
            if (!_mappings.TryGetValue(mapping.MappingId, out var record) ||
                record.Mapping.Lease != mapping)
            {
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Wrong mapping.");
            }

            _revokingMapping = mapping.MappingId;
            _operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++),
                new PlatformOperationGeneration(1),
                mapping.DomainLease);
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(
                    mapping.MappingId,
                    mapping.Generation,
                    _operation.Value));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            if (_operation != operation || _revokingMapping is null)
            {
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Wrong operation.");
            }

            if (CompletionState == PlatformCompletionState.Closed &&
                _mappings.TryGetValue(_revokingMapping.Value, out var record))
            {
                record.Revoked = true;
            }

            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(
                new PlatformCompletionReceipt(
                    operation.OperationId,
                    operation.Generation,
                    operation.DomainLease,
                    CompletionState));
        }
    }
}
