using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformBorrowReadGrantTests
{
    [Fact]
    public void GrantRequiresLiveExactBorrowAndRejectsLocalStalenessBeforeProvider()
    {
        var provider = new BorrowGrantProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 801, 810);
        var (_, borrower) = TestFixtures.Create(kernel, 802, 820);
        var (_, otherOwner) = TestFixtures.Create(kernel, 803, 830);
        var buffer = kernel.AllocateBuffer<byte>(owner, 256).Value!;
        var ownerBinding = kernel.BindPlatformDomain(owner).Value!;

        var noBorrow = kernel.CreatePlatformBorrowReadGrant(
            owner,
            borrower,
            ownerBinding,
            new BorrowLeaseHandle(buffer.Handle, new BorrowLeaseGeneration(1)),
            0,
            64);
        Assert.False(noBorrow.IsSuccess);
        Assert.Equal(0, provider.MapCalls);

        var lease = CreateCpuBorrow(kernel, owner, borrower, buffer);

        var staleRegion = lease.Handle with
        {
            Region = lease.Handle.Region with
            {
                Generation = new RegionGeneration(lease.Handle.Region.Generation.Value + 1),
            },
        };
        Assert.Equal(KernelError.StaleGeneration,
            kernel.CreatePlatformBorrowReadGrant(
                owner, borrower, ownerBinding, staleRegion, 0, 64).Error);
        Assert.Equal(0, provider.MapCalls);

        var staleBorrow = lease.Handle with
        {
            Generation = new BorrowLeaseGeneration(lease.Handle.Generation.Value + 1),
        };
        Assert.Equal(KernelError.StaleGeneration,
            kernel.CreatePlatformBorrowReadGrant(
                owner, borrower, ownerBinding, staleBorrow, 0, 64).Error);
        Assert.Equal(0, provider.MapCalls);

        var staleOwner = owner with { Generation = owner.Generation + 1 };
        Assert.Equal(KernelError.StaleHandle,
            kernel.CreatePlatformBorrowReadGrant(
                staleOwner, borrower, ownerBinding, lease.Handle, 0, 64).Error);
        Assert.Equal(0, provider.MapCalls);

        var staleBorrower = borrower with { Generation = borrower.Generation + 1 };
        Assert.Equal(KernelError.StaleHandle,
            kernel.CreatePlatformBorrowReadGrant(
                owner, staleBorrower, ownerBinding, lease.Handle, 0, 64).Error);
        Assert.Equal(0, provider.MapCalls);

        Assert.Equal(KernelError.WrongRegionOwner,
            kernel.CreatePlatformBorrowReadGrant(
                otherOwner, borrower, ownerBinding, lease.Handle, 0, 64).Error);
        Assert.Equal(0, provider.MapCalls);

        var staleBinding = ownerBinding with
        {
            Generation = new PlatformDomainBindingGeneration(ownerBinding.Generation.Value + 1),
        };
        Assert.Equal(KernelError.StaleGeneration,
            kernel.CreatePlatformBorrowReadGrant(
                owner, borrower, staleBinding, lease.Handle, 0, 64).Error);
        Assert.Equal(0, provider.MapCalls);

        var borrowerBinding = kernel.BindPlatformDomain(borrower).Value!;
        Assert.Equal(KernelError.WrongPlatformDomain,
            kernel.CreatePlatformBorrowReadGrant(
                owner, borrower, borrowerBinding, lease.Handle, 0, 64).Error);
        Assert.Equal(0, provider.MapCalls);

        Assert.True(kernel.ReturnBorrow(borrower, lease.Handle).IsSuccess);
    }

    [Fact]
    public void GrantHasSeparateReadOnlyIdentityExactRangeAndOwnerBoundDomain()
    {
        var scenario = CreateScenario();

        var grant = scenario.Kernel.CreatePlatformBorrowReadGrant(
            scenario.Owner,
            scenario.Borrower,
            scenario.Binding,
            scenario.Lease.Handle,
            32,
            96);

        Assert.True(grant.IsSuccess, grant.Message);
        Assert.Equal(PlatformMemoryAccess.Read, grant.Value!.Access);
        Assert.Equal(32, grant.Value.Offset);
        Assert.Equal(96, grant.Value.Length);
        Assert.Equal(scenario.OwnerIdentity, grant.Value.DomainBinding.Subject);
        Assert.Equal(PlatformMemoryAccess.Read, scenario.Provider.LastSlice!.Value.Access);
        Assert.Equal(32, scenario.Provider.LastSlice.Value.Offset);
        Assert.Equal(96, scenario.Provider.LastSlice.Value.Length);
        Assert.Equal(scenario.Lease.Handle.Region, scenario.Provider.LastSlice.Value.Region.Handle);
        Assert.Equal(
            new RegionOwner(scenario.OwnerIdentity.DomainId, scenario.OwnerIdentity.ProcessGeneration),
            scenario.Provider.LastSlice.Value.Region.Owner);

        Assert.NotEqual(typeof(PlatformBorrowReadGrantId), typeof(BorrowLeaseGeneration));
        Assert.NotEqual(typeof(PlatformBorrowReadGrantId), typeof(RegionId));
        Assert.NotEqual(typeof(PlatformBorrowReadGrantId), typeof(PlatformRegionMappingId));
        Assert.NotEqual(typeof(PlatformBorrowReadGrantId), typeof(PlatformProviderRegionMappingId));
        Assert.NotEqual(typeof(PlatformBorrowReadGrantGeneration), typeof(PlatformProviderLeaseGeneration));

        var evidence = scenario.Kernel.PreparePlatformBorrowReadGrantForExternalReader(
            scenario.Owner,
            grant.Value);
        Assert.True(evidence.IsSuccess, evidence.Message);
        Assert.True(evidence.Value!.IsSatisfied);
        Assert.Equal(PlatformMemoryConsumerClass.ExternalExecutionDomain, evidence.Value.Consumer);
        Assert.Equal(PlatformMemoryVisibilityRequirement.PublicationFence, evidence.Value.Requirement);

        scenario.Provider.CompletionState = PlatformCompletionState.Closed;
        Assert.True(scenario.Kernel.RequestPlatformBorrowCompletion(
            scenario.Owner,
            scenario.Borrower,
            scenario.Lease.Handle,
            grant.Value).IsSuccess);
    }

    [Theory]
    [InlineData(MapFaultMode.Denied, KernelError.PlatformDenied)]
    [InlineData(MapFaultMode.Revoked, KernelError.PlatformBindingRevoked)]
    [InlineData(MapFaultMode.Faulted, KernelError.PlatformFaulted)]
    [InlineData(MapFaultMode.MalformedAccess, KernelError.PlatformFaulted)]
    public void DeniedRevokedFaultedOrMalformedGrantAdmissionFailsClosed(
        MapFaultMode mode,
        KernelError expected)
    {
        var scenario = CreateScenario();
        scenario.Provider.MapFault = mode;

        var grant = scenario.Kernel.CreatePlatformBorrowReadGrant(
            scenario.Owner,
            scenario.Borrower,
            scenario.Binding,
            scenario.Lease.Handle,
            0,
            64);

        Assert.False(grant.IsSuccess);
        Assert.Equal(expected, grant.Error);
        Assert.True(scenario.Lease.IsValid);
        Assert.True(scenario.Kernel.ReturnBorrow(
            scenario.Borrower,
            scenario.Lease.Handle).IsSuccess);
    }

    [Theory]
    [InlineData(VisibilityFaultMode.Denied, KernelError.PlatformDenied)]
    [InlineData(VisibilityFaultMode.Revoked, KernelError.PlatformBindingRevoked)]
    [InlineData(VisibilityFaultMode.Faulted, KernelError.PlatformFaulted)]
    [InlineData(VisibilityFaultMode.Malformed, KernelError.PlatformFaulted)]
    public void DeniedRevokedFaultedOrMalformedPublicationEvidenceFailsClosed(
        VisibilityFaultMode mode,
        KernelError expected)
    {
        var scenario = CreateScenario();
        var grant = CreateGrant(scenario);
        scenario.Provider.VisibilityFault = mode;

        var evidence = scenario.Kernel.PreparePlatformBorrowReadGrantForExternalReader(
            scenario.Owner,
            grant);

        Assert.False(evidence.IsSuccess);
        Assert.Equal(expected, evidence.Error);
        Assert.True(scenario.Lease.IsValid);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.ReturnBorrow(
                scenario.Borrower,
                scenario.Lease.Handle).Error);
    }

    [Fact]
    public void ExactRangeIsValidatedAndWritableExternalMappingCannotReuseBorrowLifetime()
    {
        var scenario = CreateScenario();
        var mapCalls = scenario.Provider.MapCalls;

        var invalid = scenario.Kernel.CreatePlatformBorrowReadGrant(
            scenario.Owner,
            scenario.Borrower,
            scenario.Binding,
            scenario.Lease.Handle,
            240,
            17);
        Assert.Equal(KernelError.PlatformDenied, invalid.Error);
        Assert.Equal(mapCalls, scenario.Provider.MapCalls);

        var grant = CreateGrant(scenario);
        var capability = MintRegionCapability(
            scenario.Kernel,
            scenario.Owner,
            scenario.Buffer.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);
        var beforeWritableAttempt = scenario.Provider.MapCalls;

        var writable = scenario.Kernel.MapPlatformOwnedRegionSlice(
            scenario.Owner,
            scenario.Binding,
            capability,
            scenario.Buffer.Handle,
            0,
            64,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);

        Assert.False(writable.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, writable.Error);
        Assert.Equal(beforeWritableAttempt, scenario.Provider.MapCalls);

        scenario.Provider.CompletionState = PlatformCompletionState.Closed;
        Assert.True(scenario.Kernel.RequestPlatformBorrowCompletion(
            scenario.Owner,
            scenario.Borrower,
            scenario.Lease.Handle,
            grant).IsSuccess);
    }

    [Fact]
    public void ActiveGrantBlocksMoveReleaseBorrowCompletionAndOwnerRevoke()
    {
        var scenario = CreateScenario();
        var (_, target) = TestFixtures.Create(scenario.Kernel, 804, 840);
        var grant = CreateGrant(scenario);

        Assert.False(scenario.Kernel.TransferRegion(
            scenario.Owner,
            target,
            scenario.Buffer).IsSuccess);
        Assert.False(scenario.Kernel.ReleaseRegion(
            scenario.Owner,
            scenario.Buffer).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.ReturnBorrow(
                scenario.Borrower,
                scenario.Lease.Handle).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.RevokeBorrow(
                scenario.Owner,
                scenario.Lease.Handle).Error);
        Assert.True(scenario.Lease.IsValid);

        scenario.Provider.CompletionState = PlatformCompletionState.Closed;
        Assert.True(scenario.Kernel.RequestPlatformBorrowCompletion(
            scenario.Owner,
            scenario.Borrower,
            scenario.Lease.Handle,
            grant).IsSuccess);
    }

    [Fact]
    public void CompletionRequestDrainsGrantBlocksNewExternalEffectsAndKeepsBorrowLive()
    {
        var scenario = CreateScenario();
        var grant = CreateGrant(scenario);
        Assert.True(scenario.Kernel.PreparePlatformBorrowReadGrantForExternalReader(
            scenario.Owner,
            grant).IsSuccess);

        scenario.Provider.CompletionState = PlatformCompletionState.Draining;
        var requested = scenario.Kernel.RequestPlatformBorrowCompletion(
            scenario.Owner,
            scenario.Borrower,
            scenario.Lease.Handle,
            grant);

        Assert.Equal(KernelError.PlatformBindingDraining, requested.Error);
        Assert.True(scenario.Lease.IsValid);
        Assert.Equal(RegionState.Loaned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);

        var lifecycle = scenario.Kernel.QueryPlatformBorrowReadGrantLifecycle(
            scenario.Owner,
            grant);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(PlatformExternalClosureState.Draining, lifecycle.Value!.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);

        var visibilityCalls = scenario.Provider.VisibilityCalls;
        var lateEvidence = scenario.Kernel.PreparePlatformBorrowReadGrantForExternalReader(
            scenario.Owner,
            grant);
        Assert.Equal(KernelError.PlatformBindingDraining, lateEvidence.Error);
        Assert.Equal(visibilityCalls, scenario.Provider.VisibilityCalls);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.ReturnBorrow(
                scenario.Borrower,
                scenario.Lease.Handle).Error);

        scenario.Provider.CompletionState = PlatformCompletionState.Closed;
        Assert.True(scenario.Kernel.RequestPlatformBorrowCompletion(
            scenario.Owner,
            scenario.Borrower,
            scenario.Lease.Handle,
            grant).IsSuccess);
    }

    [Theory]
    [InlineData(PlatformCompletionState.Staged)]
    [InlineData(PlatformCompletionState.Pending)]
    [InlineData(PlatformCompletionState.Draining)]
    [InlineData(PlatformCompletionState.Completed)]
    [InlineData(PlatformCompletionState.Cancelled)]
    public void NonTerminalCompletionNeverCompletesBorrow(PlatformCompletionState state)
    {
        var scenario = CreateScenario();
        var grant = CreateGrant(scenario);
        scenario.Provider.CompletionState = state;

        var result = scenario.Kernel.RequestPlatformBorrowCompletion(
            scenario.Owner,
            scenario.Borrower,
            scenario.Lease.Handle,
            grant);

        Assert.Equal(KernelError.PlatformBindingDraining, result.Error);
        Assert.True(scenario.Lease.IsValid);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.ReturnBorrow(
                scenario.Borrower,
                scenario.Lease.Handle).Error);
    }

    [Theory]
    [InlineData(ReceiptFaultMode.StaleGeneration, KernelError.StaleGeneration)]
    [InlineData(ReceiptFaultMode.WrongDomain, KernelError.WrongPlatformDomain)]
    [InlineData(ReceiptFaultMode.WrongOperation, KernelError.PlatformDenied)]
    [InlineData(ReceiptFaultMode.StaleMappingTicket, KernelError.PlatformFaulted)]
    [InlineData(ReceiptFaultMode.WrongMappingTicket, KernelError.PlatformFaulted)]
    [InlineData(ReceiptFaultMode.WrongTicketDomain, KernelError.PlatformFaulted)]
    [InlineData(ReceiptFaultMode.MalformedTicket, KernelError.PlatformFaulted)]
    public void StaleWrongDomainWrongOperationAndMalformedClosureEvidenceFailClosed(
        ReceiptFaultMode mode,
        KernelError expected)
    {
        var scenario = CreateScenario();
        var grant = CreateGrant(scenario);
        scenario.Provider.ReceiptFault = mode;
        scenario.Provider.CompletionState = PlatformCompletionState.Closed;

        var result = scenario.Kernel.RequestPlatformBorrowCompletion(
            scenario.Owner,
            scenario.Borrower,
            scenario.Lease.Handle,
            grant);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error);
        Assert.True(scenario.Lease.IsValid);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.ReturnBorrow(
                scenario.Borrower,
                scenario.Lease.Handle).Error);
    }

    [Fact]
    public void FaultedCompletionNeverAllowsBorrowCompletionOrReclaim()
    {
        var scenario = CreateScenario();
        var grant = CreateGrant(scenario);
        scenario.Provider.CompletionState = PlatformCompletionState.Faulted;

        var result = scenario.Kernel.RequestPlatformBorrowCompletion(
            scenario.Owner,
            scenario.Borrower,
            scenario.Lease.Handle,
            grant);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.True(scenario.Lease.IsValid);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.ReturnBorrow(
                scenario.Borrower,
                scenario.Lease.Handle).Error);
    }

    [Fact]
    public void ClosedStillRequiresExactLocalRevalidationBeforeBorrowCompletion()
    {
        var scenario = CreateScenario();
        var grant = scenario.Kernel.CreatePlatformBorrowReadGrant(
            scenario.Owner,
            scenario.Borrower,
            scenario.Binding,
            scenario.Lease.Handle,
            16,
            64).Value!;

        scenario.Provider.CompletionState = PlatformCompletionState.Draining;
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.RequestPlatformBorrowCompletion(
                scenario.Owner,
                scenario.Borrower,
                scenario.Lease.Handle,
                grant).Error);

        scenario.Provider.CompletionState = PlatformCompletionState.Closed;

        var staleOwner = scenario.Owner with { Generation = scenario.Owner.Generation + 1 };
        Assert.Equal(KernelError.StaleHandle,
            scenario.Kernel.RequestPlatformBorrowCompletion(
                staleOwner,
                scenario.Borrower,
                scenario.Lease.Handle,
                grant).Error);
        Assert.True(scenario.Lease.IsValid);

        var staleBorrower = scenario.Borrower with
        {
            Generation = scenario.Borrower.Generation + 1,
        };
        Assert.Equal(KernelError.StaleHandle,
            scenario.Kernel.RequestPlatformBorrowCompletion(
                scenario.Owner,
                staleBorrower,
                scenario.Lease.Handle,
                grant).Error);
        Assert.True(scenario.Lease.IsValid);

        var staleGrant = grant with
        {
            Generation = new PlatformBorrowReadGrantGeneration(
                grant.Generation.Value + 1),
        };
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.RequestPlatformBorrowCompletion(
                scenario.Owner,
                scenario.Borrower,
                scenario.Lease.Handle,
                staleGrant).Error);
        Assert.True(scenario.Lease.IsValid);

        var staleBindingGrant = grant with
        {
            DomainBinding = grant.DomainBinding with
            {
                Generation = new PlatformDomainBindingGeneration(
                    grant.DomainBinding.Generation.Value + 1),
            },
        };
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.RequestPlatformBorrowCompletion(
                scenario.Owner,
                scenario.Borrower,
                scenario.Lease.Handle,
                staleBindingGrant).Error);
        Assert.True(scenario.Lease.IsValid);

        var forgedRangeGrant = grant with { Length = grant.Length + 1 };
        Assert.Equal(KernelError.PlatformDenied,
            scenario.Kernel.RequestPlatformBorrowCompletion(
                scenario.Owner,
                scenario.Borrower,
                scenario.Lease.Handle,
                forgedRangeGrant).Error);
        Assert.True(scenario.Lease.IsValid);

        var completed = scenario.Kernel.RequestPlatformBorrowCompletion(
            scenario.Owner,
            scenario.Borrower,
            scenario.Lease.Handle,
            grant);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.False(scenario.Lease.IsValid);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);

        var oldEvidence = scenario.Kernel.PreparePlatformBorrowReadGrantForExternalReader(
            scenario.Owner,
            grant);
        Assert.Equal(KernelError.PlatformBindingNotFound, oldEvidence.Error);
        Assert.Equal(KernelError.PlatformBindingNotFound,
            scenario.Kernel.QueryPlatformBorrowReadGrantLifecycle(
                scenario.Owner,
                grant).Error);
    }

    [Fact]
    public void PublicGrantSurfaceContainsNoProviderHybridCpuOrHardwareAuthorityIdentifiers()
    {
        var forbidden = new[]
        {
            "PlatformProviderRegionMapping",
            "PlatformProviderDomainLease",
            "PlatformOperation",
            "NeutralOwnedRegionMapping",
            "HybridCPU",
            "Physical",
            "Pte",
            "PageTable",
            "CacheLine",
            "Dma",
            "Iommu",
            "Vmcs",
            "Vmx",
            "Lane",
            "Opcode",
        };
        var surfaceTypes = new[]
        {
            typeof(PlatformBorrowReadGrantId),
            typeof(PlatformBorrowReadGrantGeneration),
            typeof(PlatformBorrowReadGrant),
            typeof(PlatformBorrowReadGrantEvidence),
            typeof(PlatformBorrowReadGrantLifecycle),
        };

        foreach (var type in surfaceTypes)
        foreach (var member in type.GetMembers(
                     BindingFlags.Public |
                     BindingFlags.Instance |
                     BindingFlags.Static))
        {
            var signature = member.ToString() ?? member.Name;
            foreach (var name in forbidden)
                Assert.DoesNotContain(name, signature, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static PlatformBorrowReadGrant CreateGrant(BorrowScenario scenario)
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

    private static BorrowScenario CreateScenario()
    {
        var provider = new BorrowGrantProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 811, 910);
        var (_, borrower) = TestFixtures.Create(kernel, 812, 920);
        var buffer = kernel.AllocateBuffer<byte>(owner, 256).Value!;
        var lease = CreateCpuBorrow(kernel, owner, borrower, buffer);
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var ownerIdentity = new PlatformDomainIdentity(new DomainId(910), owner);
        return new BorrowScenario(
            kernel,
            provider,
            owner,
            borrower,
            buffer,
            lease,
            binding,
            ownerIdentity);
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

    private static CapabilityId MintRegionCapability(
        RuntimeKernel kernel,
        ProcessHandle subject,
        RegionHandle region,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var capability = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.RegionId),
            rights);
        Assert.True(capability.IsSuccess, capability.Message);
        return capability.Value!.CapabilityId;
    }

    private static RequestPayloadDescriptorV1 OwnershipRequest(string parameterName) =>
        new(
            RequestPayloadKind.Ownership,
            parameterName,
            "SingPlus.Sip.OwnedBuffer",
            ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);

    private static ProtocolDefinitionV1 BorrowProtocol() => new(
        "SingPlus.Tests.Platform.IBorrowGrantProtocol",
        "borrow-grant-contract-digest",
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

    public enum MapFaultMode
    {
        None = 0,
        Denied,
        Revoked,
        Faulted,
        MalformedAccess,
    }

    public enum VisibilityFaultMode
    {
        None = 0,
        Denied,
        Revoked,
        Faulted,
        Malformed,
    }

    public enum ReceiptFaultMode
    {
        None = 0,
        StaleGeneration,
        WrongDomain,
        WrongOperation,
        StaleMappingTicket,
        WrongMappingTicket,
        WrongTicketDomain,
        MalformedTicket,
    }

    private sealed record BorrowScenario(
        RuntimeKernel Kernel,
        BorrowGrantProvider Provider,
        ProcessHandle Owner,
        ProcessHandle Borrower,
        OwnedBuffer<byte> Buffer,
        BorrowLease<byte> Lease,
        PlatformDomainBinding Binding,
        PlatformDomainIdentity OwnerIdentity);

    private sealed class BorrowGrantProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionVisibilityProvider,
        IPlatformRegionRevocationProvider
    {
        private readonly Dictionary<PlatformProviderDomainLeaseId, PlatformProviderDomainLease>
            _domains = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, PlatformProviderOwnedRegionMapping>
            _mappings = [];
        private PlatformOperationIdentity? _operation;
        private ulong _nextDomain = 1;
        private ulong _nextMapping = 1;
        private ulong _nextOperation = 1;

        public int MapCalls { get; private set; }
        public int VisibilityCalls { get; private set; }
        public PlatformRegionSlice? LastSlice { get; private set; }
        public MapFaultMode MapFault { get; set; }
        public VisibilityFaultMode VisibilityFault { get; set; }
        public ReceiptFaultMode ReceiptFault { get; set; }
        public PlatformCompletionState CompletionState { get; set; } =
            PlatformCompletionState.Closed;

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("borrow-read-grant-test"),
            4,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
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

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) =>
            _domains.ContainsKey(lease.LeaseId)
                ? PlatformAuthorityResult.Ok()
                : PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Stale,
                    "Unknown domain lease.");

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
            MapCalls++;
            LastSlice = slice;

            if (!_domains.TryGetValue(domainLease.LeaseId, out var active) ||
                active != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Wrong domain lease.");
            }

            var expectedOwner = new RegionOwner(
                domainLease.Subject.DomainId,
                domainLease.Subject.ProcessGeneration);
            if (slice.Region.Owner != expectedOwner)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "The exact region is owned by a different Sing subject.");
            }

            switch (MapFault)
            {
                case MapFaultMode.Denied:
                    return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                        PlatformAuthorityStatus.Denied,
                        "Mapping denied.");
                case MapFaultMode.Revoked:
                    return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                        PlatformAuthorityStatus.Revoked,
                        "Domain revoked.");
                case MapFaultMode.Faulted:
                    return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                        PlatformAuthorityStatus.Faulted,
                        "Mapping faulted.");
            }

            var validation = PlatformOwnedRegionMappingContract.ValidateSlice(slice);
            if (!validation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    validation.Status,
                    validation.Message!);
            }

            var leaseAccess = MapFault == MapFaultMode.MalformedAccess
                ? PlatformMemoryAccess.Read | PlatformMemoryAccess.Write
                : slice.Access;
            var lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                slice.Region,
                leaseAccess);
            var mapped = new PlatformProviderOwnedRegionMapping(lease, slice);
            _mappings.Add(lease.MappingId, mapped);
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapped);
        }

        public PlatformAuthorityResult<PlatformRegionVisibilityResult>
            PrepareRegionMappingForConsumer(PlatformRegionVisibilityRequest request)
        {
            VisibilityCalls++;
            if (!_mappings.TryGetValue(request.Mapping.MappingId, out var mapped) ||
                mapped.Lease != request.Mapping ||
                mapped.Slice != request.Slice)
            {
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Wrong mapping visibility request.");
            }

            switch (VisibilityFault)
            {
                case VisibilityFaultMode.Denied:
                    return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                        PlatformAuthorityStatus.Denied,
                        "Visibility denied.");
                case VisibilityFaultMode.Revoked:
                    return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                        PlatformAuthorityStatus.Revoked,
                        "Mapping revoked.");
                case VisibilityFaultMode.Faulted:
                    return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                        PlatformAuthorityStatus.Faulted,
                        "Visibility faulted.");
            }

            var outcome = VisibilityFault == VisibilityFaultMode.Malformed
                ? (PlatformMemoryVisibilityOutcome)999
                : PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied;
            return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(
                new PlatformRegionVisibilityResult(
                    request.Mapping.MappingId,
                    request.Mapping.Generation,
                    request.Slice,
                    request.Consumer,
                    request.Requirement,
                    outcome));
        }

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformRegionRevocationTicket>
            BeginRegionMappingRevocation(
                PlatformProviderRegionMappingLease mapping,
                PlatformRegionRevocationPolicy policy)
        {
            if (!_mappings.TryGetValue(mapping.MappingId, out var mapped) ||
                mapped.Lease != mapping)
            {
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Wrong mapping.");
            }

            var operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++),
                new PlatformOperationGeneration(1),
                mapping.DomainLease);
            _operation = operation;

            var ticketMappingId = ReceiptFault == ReceiptFaultMode.WrongMappingTicket
                ? new PlatformProviderRegionMappingId(mapping.MappingId.Value + 100)
                : ReceiptFault == ReceiptFaultMode.MalformedTicket
                    ? new PlatformProviderRegionMappingId(0)
                    : mapping.MappingId;
            var ticketGeneration = ReceiptFault == ReceiptFaultMode.StaleMappingTicket
                ? new PlatformProviderLeaseGeneration(mapping.Generation.Value + 1)
                : mapping.Generation;
            var ticketDomain = ReceiptFault == ReceiptFaultMode.WrongTicketDomain
                ? new PlatformProviderDomainLease(
                    new PlatformProviderDomainLeaseId(mapping.DomainLease.LeaseId.Value + 100),
                    mapping.DomainLease.Generation,
                    mapping.DomainLease.Subject)
                : mapping.DomainLease;
            var ticketOperation = operation with { DomainLease = ticketDomain };

            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(
                    ticketMappingId,
                    ticketGeneration,
                    ticketOperation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            if (_operation != operation)
            {
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Wrong operation.");
            }

            var operationId = ReceiptFault == ReceiptFaultMode.WrongOperation
                ? new PlatformOperationId(operation.OperationId.Value + 100)
                : operation.OperationId;
            var generation = ReceiptFault == ReceiptFaultMode.StaleGeneration
                ? new PlatformOperationGeneration(operation.Generation.Value + 1)
                : operation.Generation;
            var domain = ReceiptFault == ReceiptFaultMode.WrongDomain
                ? new PlatformProviderDomainLease(
                    new PlatformProviderDomainLeaseId(operation.DomainLease.LeaseId.Value + 100),
                    operation.DomainLease.Generation,
                    operation.DomainLease.Subject)
                : operation.DomainLease;

            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(
                new PlatformCompletionReceipt(
                    operationId,
                    generation,
                    domain,
                    CompletionState));
        }
    }
}
