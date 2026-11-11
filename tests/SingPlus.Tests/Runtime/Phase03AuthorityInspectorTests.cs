using System.Reflection;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class Phase03AuthorityInspectorTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void InspectionClockRevokeDeniesActualSnapshotAndExplanationPublication(int mode)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 330, 3030).Handle;
        ProcessHandle other = TestFixtures.Create(kernel, 331, 3031).Handle;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(other, 8).Value!;
        CapabilityId capability = kernel.MintCapability(new DomainId(3030), caller, ResourceKind.KernelService,
            CapabilityResourceIds.AuthorityInspector, CapabilityRights.Read).Value!.CapabilityId;
        InspectionClock clock = new InspectionClock();
        AuthorityInspector inspector = kernel.CreateAuthorityInspector(caller, capability, clock).Value!;
        clock.Callback = () =>
        {
            Task<KernelResult> worker = Task.Run(() => kernel.RevokeCapability(capability));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Inspection clock ran under permission/publication lock.");
            Assert.True(worker.Result.IsSuccess);
        };
        KernelError error = mode switch
        {
            0 => inspector.Capture().Error,
            1 => inspector.Capture(AuthorityInspectionConsistency.HistoricalReference).Error,
            2 => inspector.Capture(AuthorityInspectionConsistency.AuthorityLockedSnapshot).Error,
            3 => inspector.InspectOwner(buffer.Handle).Error,
            4 => inspector.InspectDependents(buffer.Handle).Error,
            5 => inspector.InspectCapabilityProvenance(capability).Error,
            6 => inspector.WhyMoveBlocked(buffer.Handle).Error,
            _ => inspector.WhyReclaimBlocked(buffer.Handle).Error,
        };
        Assert.Equal(KernelError.ProjectionDenied, error);
        Assert.Equal(KernelError.ProjectionDenied, inspector.Capture().Error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InspectionClockFailureOrSourceDriftDoesNotPublishLockedCut(bool throws)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 332, 3032).Handle;
        InspectionClock clock = new InspectionClock();
        AuthorityInspector inspector = kernel.CreateAuthorityInspector(caller, timeProvider: clock).Value!;
        clock.Callback = () =>
        {
            if (throws) throw new InvalidOperationException("inspection clock fault");
            Assert.True(kernel.AllocateBuffer<byte>(caller, 8).IsSuccess);
        };
        Assert.Equal(throws ? KernelError.PlatformFaulted : KernelError.SnapshotUnstable,
            inspector.Capture(AuthorityInspectionConsistency.AuthorityLockedSnapshot).Error);
        AuthorityInspectionSnapshot fresh = inspector.Capture(AuthorityInspectionConsistency.AuthorityLockedSnapshot).Value!;
        Assert.Equal(1UL, fresh.CaptureSequence);
        Assert.False(fresh.AuthorizesMutation);
    }

    [Fact]
    public void CapturePublishesFinalUniqueSequenceThenRefusesWithoutWrap()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 333, 3033).Handle;
        AuthorityInspector inspector = kernel.CreateAuthorityInspector(caller).Value!;
        FieldInfo sequence = typeof(AuthorityInspector).GetField("_captureSequence", BindingFlags.Instance | BindingFlags.NonPublic)!;
        sequence.SetValue(inspector, ulong.MaxValue - 1);
        Assert.Equal(ulong.MaxValue, inspector.Capture().Value!.CaptureSequence);
        Assert.Equal(KernelError.CapacityExhausted, inspector.Capture().Error);
        Assert.Equal(ulong.MaxValue, (ulong)sequence.GetValue(inspector)!);
    }

    private sealed class InspectionClock : TimeProvider
    {
        public Action? Callback;
        public override long GetTimestamp()
        {
            Interlocked.Exchange(ref Callback, null)?.Invoke();
            return 100;
        }
    }

    private static readonly byte[] ComponentImage = [3, 0, 3, 1];

    [Fact]
    public void SelfScopedInspectorCannotInspectUnrelatedRegion()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle caller) = TestFixtures.Create(kernel, 301, 3001);
        (SingProcess _, ProcessHandle other) = TestFixtures.Create(kernel, 302, 3002);
        OwnedBuffer<byte> otherRegion = kernel.AllocateBuffer<byte>(other, 16).Value!;
        AuthorityInspector inspector = kernel.CreateAuthorityInspector(caller).Value!;

        KernelResult<AuthorityInspectionSnapshot> denied = inspector.InspectOwner(otherRegion.Handle);

        Assert.Equal(KernelError.ProjectionDenied, denied.Error);
        AuthorityInspectionSnapshot self = inspector.Capture().Value!;
        Assert.Equal(AuthorityInspectionScope.Self, self.Scope);
        Assert.Single(self.Nodes, node => node.Kind == AuthorityNodeKind.Process);
        Assert.False(self.AuthorizesMutation);
    }

    [Fact]
    public void PrivilegedInspectorSeesAuthorizedCrossServiceGraphAndRevocationFailsClosed()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle caller) = TestFixtures.Create(kernel, 303, 3003);
        _ = TestFixtures.Create(kernel, 304, 3004);
        CapabilityId capability = kernel.MintCapability(new DomainId(3003), caller, ResourceKind.KernelService,
            CapabilityResourceIds.AuthorityInspector, CapabilityRights.Read).Value!.CapabilityId;
        AuthorityInspector inspector = kernel.CreateAuthorityInspector(caller, capability).Value!;

        KernelResult<AuthorityInspectionSnapshot> snapshot = inspector.Capture(AuthorityInspectionConsistency.AuthorityLockedSnapshot);

        Assert.True(snapshot.IsSuccess, snapshot.Message);
        Assert.Equal(AuthorityInspectionScope.System, snapshot.Value!.Scope);
        Assert.Equal(2, snapshot.Value.Nodes.Count(node => node.Kind == AuthorityNodeKind.Process));
        Assert.Equal(AuthorityInspectionConsistency.AuthorityLockedSnapshot, snapshot.Value.Consistency);
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(KernelError.ProjectionDenied, inspector.Capture().Error);
    }

    [Fact]
    public void CapabilityProvenanceIsExactAndCrossDomainParentIsRedactedForSelfScope()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle source) = TestFixtures.Create(kernel, 305, 3005);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 306, 3006);
        CapabilityId sourceCapability = kernel.MintCapability(new DomainId(3005), source, ResourceKind.File,
            "opaque-file", CapabilityRights.Read | CapabilityRights.Delegate).Value!.CapabilityId;
        CapabilityDescriptorV1 delegated = kernel.DelegateCapability(source, target, sourceCapability, CapabilityRights.Read).Value!;
        CapabilityId systemCapability = kernel.MintCapability(new DomainId(3005), source, ResourceKind.KernelService,
            CapabilityResourceIds.AuthorityInspector, CapabilityRights.Read).Value!.CapabilityId;

        AuthorityInspectionSnapshot system = kernel.CreateAuthorityInspector(source, systemCapability).Value!
            .InspectCapabilityProvenance(delegated.CapabilityId).Value!;
        AuthorityInspectionSnapshot self = kernel.CreateAuthorityInspector(target).Value!
            .InspectCapabilityProvenance(delegated.CapabilityId).Value!;

        Assert.Equal(2, system.Nodes.Count(node => node.Kind == AuthorityNodeKind.Capability));
        Assert.Contains(system.Edges, edge => edge.Kind == AuthorityEdgeKind.DelegatedTo && !edge.Redacted);
        Assert.Single(self.Nodes, node => node.Kind == AuthorityNodeKind.Capability);
        Assert.Contains(self.Nodes, node => node.Kind == AuthorityNodeKind.Redacted && node.Redacted);
        Assert.Contains(self.Edges, edge => edge.Kind == AuthorityEdgeKind.DelegatedTo && edge.Redacted);
    }

    [Fact]
    public void WhyMoveBlockedReportsExactRegionUseAndBackingPins()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 307, 3007);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 32).Value!;
        KernelResult<RegionUseDescriptor> use = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ExclusiveWrite, new(8, 8));
        Assert.True(use.IsSuccess, use.Message);
        KernelResult<RegionBackingLeaseDescriptor> backing = kernel.Regions.ReserveBacking(buffer.Handle, new(new(3007), owner.Generation));
        Assert.True(backing.IsSuccess, backing.Message);

        AuthorityInspectionExplanation explanation = kernel.CreateAuthorityInspector(owner).Value!.WhyMoveBlocked(buffer.Handle).Value!;

        Assert.Contains(explanation.Reasons, reason => reason.Kind == AuthorityBlockReasonKind.RegionUseActive && reason.Detail.Contains("ExclusiveWrite", StringComparison.Ordinal));
        Assert.Contains(explanation.Reasons, reason => reason.Kind == AuthorityBlockReasonKind.BackingLeaseActive);
        Assert.DoesNotContain(explanation.Reasons, reason => reason.BlockingNode is null);
        Assert.False(explanation.AuthorizesMutation);
    }

    [Fact]
    public void WhyMoveBlockedReportsExactBorrowLease()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 314, 3014);
        (SingProcess _, ProcessHandle borrower) = TestFixtures.Create(kernel, 315, 3015);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        KernelResult<BorrowLeaseHandle> lease = kernel.Regions.Loan(buffer.Handle, new(new(3014), owner.Generation), new(new(3015), borrower.Generation));
        Assert.True(lease.IsSuccess, lease.Message);

        AuthorityInspectionExplanation explanation = kernel.CreateAuthorityInspector(owner).Value!.WhyMoveBlocked(buffer.Handle).Value!;

        Assert.Contains(explanation.Reasons, reason => reason.Kind == AuthorityBlockReasonKind.BorrowActive);
        Assert.Contains(explanation.Snapshot.Nodes, node => node.Kind == AuthorityNodeKind.BorrowLease);
    }

    [Fact]
    public void WhyMoveBlockedReportsPlatformMappingWithoutProviderPrivateIdentity()
    {
        RuntimeKernel kernel = new RuntimeKernel(new InspectionProvider());
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 308, 3008);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 32).Value!;
        CapabilityId capability = kernel.MintCapability(new DomainId(3008), owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Read | CapabilityRights.Map).Value!.CapabilityId;
        Assert.True(kernel.MapPlatformOwnedRegion(owner, binding, capability, buffer.Handle, PlatformMemoryAccess.Read).IsSuccess);

        AuthorityInspectionExplanation explanation = kernel.CreateAuthorityInspector(owner).Value!.WhyMoveBlocked(buffer.Handle).Value!;

        Assert.Contains(explanation.Reasons, reason => reason.Kind == AuthorityBlockReasonKind.PlatformMappingActive);
        Assert.DoesNotContain("provider-secret", string.Join('|', explanation.Snapshot.Nodes.Select(node => node.Id.Value)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExternalOperationPinReportsClosureAndExactRegionUses()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 309, 3009);
        OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        OwnedBuffer<byte> output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        OperationPreparation prepared = kernel.PrepareExternalOperation(owner,
        [
            new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
            new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))
        ], ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        OperationDependencySnapshot dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(owner, prepared.Operation, dependencies).IsSuccess);
        Assert.True(kernel.RecordExternalOperationSubmission(owner, prepared.Operation, dependencies).IsSuccess);

        AuthorityInspectionExplanation explanation = kernel.CreateAuthorityInspector(owner).Value!.WhyExternalOperationPinned(prepared.Operation).Value!;

        Assert.Contains(explanation.Reasons, reason => reason.Kind == AuthorityBlockReasonKind.ExternalOperationActive);
        Assert.Equal(2, explanation.Reasons.Count(reason => reason.Kind == AuthorityBlockReasonKind.RegionUseActive));
        Assert.Contains(explanation.Snapshot.Edges, edge => edge.Kind == AuthorityEdgeKind.Pins);
    }

    [Fact]
    public void ProcessAndServiceDrainQueriesReportExactExternalOperationClosureDependency()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle serviceProcess) = TestFixtures.Create(kernel, 316, 3016);
        ServiceContractIdentity contract = new ServiceContractIdentity("PinnedService", "1", "pinned-service-digest");
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(serviceProcess, "pinned-service", contract, Protocol(contract)).Value!;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(serviceProcess, 8).Value!;
        OperationPreparation prepared = kernel.PrepareExternalOperation(serviceProcess,
            [new(buffer.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence,
            ExternalPublicationPolicy.Staged).Value!;
        OperationDependencySnapshot dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(serviceProcess, prepared.Operation, dependencies).IsSuccess);
        Assert.True(kernel.RecordExternalOperationSubmission(serviceProcess, prepared.Operation, dependencies).IsSuccess);
        ServiceInstanceHandle service = new ServiceInstanceHandle(descriptor.Service.Id, descriptor.Generation, serviceProcess, new(3016));
        AuthorityInspector inspector = kernel.CreateAuthorityInspector(serviceProcess).Value!;

        AuthorityInspectionExplanation processReason = inspector.WhyReclaimBlocked(serviceProcess).Value!;
        AuthorityInspectionExplanation serviceReason = inspector.WhyServiceDrainBlocked(service).Value!;

        Assert.Contains(processReason.Reasons, reason => reason.Kind == AuthorityBlockReasonKind.ProviderClosurePending);
        Assert.Contains(serviceReason.Reasons, reason => reason.Kind == AuthorityBlockReasonKind.ProviderClosurePending);
        Assert.Contains(serviceReason.Reasons, reason => reason.BlockingNode is not null);
    }

    [Fact]
    public void StaleRegionIsRepresentedSeparatelyAndCannotAuthorizeMutation()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle source) = TestFixtures.Create(kernel, 310, 3010);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 311, 3011);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(source, 8).Value!;
        RegionHandle old = buffer.Handle;
        Assert.True(kernel.TransferRegion(source, target, buffer).IsSuccess);
        CapabilityId inspectionCapability = kernel.MintCapability(new DomainId(3010), source, ResourceKind.KernelService,
            CapabilityResourceIds.AuthorityInspector, CapabilityRights.Read).Value!.CapabilityId;
        AuthorityInspector inspector = kernel.CreateAuthorityInspector(source, inspectionCapability).Value!;

        AuthorityInspectionSnapshot stale = inspector.InspectOwner(old).Value!;

        AuthorityInspectionNode node = Assert.Single(stale.Nodes);
        Assert.True(node.Stale);
        Assert.Equal(old.Generation.Value, node.Generation);
        Assert.False(stale.AuthorizesMutation);
        Assert.Equal(KernelError.StaleGeneration, kernel.Regions.Validate(old, new(new(3010), source.Generation)).Error);
    }

    [Fact]
    public void HistoricalSnapshotPreservesServiceReplacementRelationship()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle inspectorProcess) = TestFixtures.Create(kernel, 312, 3012);
        CapabilityId inspectionCapability = kernel.MintCapability(new DomainId(3012), inspectorProcess, ResourceKind.KernelService,
            CapabilityResourceIds.AuthorityInspector, CapabilityRights.Read).Value!.CapabilityId;
        (SingProcess _, ProcessHandle first) = TestFixtures.Create(kernel, 313, 3013, identity: "replaceable-service");
        ServiceContractIdentity contract = new ServiceContractIdentity("Replaceable", "1", "replaceable-digest");
        ProtocolDefinitionV1 protocol = Protocol(contract);
        ServiceEndpointDescriptor firstDescriptor = kernel.RegisterService(first, "replaceable", contract, protocol).Value!;
        Assert.True(kernel.TerminateProcess(first).IsSuccess);
        (SingProcess _, ProcessHandle second) = TestFixtures.Create(kernel, 313, 3013, generation: 2, identity: "replaceable-service");
        ServiceEndpointDescriptor secondDescriptor = kernel.RegisterService(second, "replaceable", contract, protocol).Value!;
        Assert.Equal(firstDescriptor.Service.Id, secondDescriptor.Service.Id);

        AuthorityInspectionSnapshot historical = kernel.CreateAuthorityInspector(inspectorProcess, inspectionCapability).Value!
            .Capture(AuthorityInspectionConsistency.HistoricalReference).Value!;

        Assert.Equal(2, historical.Nodes.Count(node => node.Kind == AuthorityNodeKind.ServiceInstance));
        Assert.Contains(historical.Nodes, node => node.Generation == firstDescriptor.Generation.Value && node.Stale);
        Assert.Contains(historical.Nodes, node => node.Generation == secondDescriptor.Generation.Value && node.Stale);
        Assert.Single(historical.Edges, edge => edge.Kind == AuthorityEdgeKind.ReplacedBy);
    }

    [Fact]
    public void InspectorProjectsAuthoritativeBudgetReservationsAndCheckpointPinsWithoutGrantingAuthority()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot component = kernel.AdmitComponent(InspectablePlan("inspect-ledgers", 317, 3017)).Value!;
        BudgetReservationSnapshot explicitReservation = kernel.ReserveBudget(component.Process,
            [new(ServiceBudgetDimension.IpcMessages, 1)], BudgetReservationLifetime.LocalResource).Value!;
        (SingProcess _, ProcessHandle admin) = TestFixtures.Create(kernel, 318, 3018);
        CapabilityId checkpointCapability = kernel.MintCapability(new DomainId(3018), admin, ResourceKind.KernelService,
            CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        OrdinaryCheckpointImage checkpoint = kernel.CreateOrdinaryCheckpoint(admin, checkpointCapability, component.Identity,
            new byte[] { 1, 2, 3 }).Value!;

        AuthorityInspectionSnapshot snapshot = kernel.CreateAuthorityInspector(component.Process).Value!.Capture().Value!;

        Assert.Equal(2, snapshot.Nodes.Count(node => node.Kind == AuthorityNodeKind.BudgetReservation));
        Assert.Single(snapshot.Nodes, node => node.Kind == AuthorityNodeKind.CheckpointPin);
        AuthorityInspectionNode checkpointNode = Assert.Single(snapshot.Nodes, node => node.Kind == AuthorityNodeKind.CheckpointPin);
        Assert.Contains(snapshot.Edges, edge => edge.Source == checkpointNode.Id && edge.Kind == AuthorityEdgeKind.Requires &&
            snapshot.Nodes.Any(node => node.Id == edge.Target && node.Kind == AuthorityNodeKind.BudgetReservation));
        Assert.False(snapshot.AuthorizesMutation);

        Assert.True(kernel.ReleaseBudget(component.Process, explicitReservation.Reservation).IsSuccess);
        Assert.True(kernel.DeleteOrdinaryCheckpoint(admin, checkpointCapability, checkpoint.Handle).IsSuccess);
    }

    [Fact]
    public void InspectorDtosContainNoReusableAuthorityOrProviderTopology()
    {
        Type[] dtoTypes =
        [
            typeof(AuthorityInspectionNode),
            typeof(AuthorityInspectionEdge),
            typeof(AuthorityInspectionSnapshot),
            typeof(AuthorityBlockReason),
            typeof(AuthorityInspectionExplanation)
        ];
        string[] forbidden =
        [
            nameof(CapabilityId), nameof(ProcessHandle), nameof(RegionHandle), nameof(ExternalOperationHandle),
            "ProviderLease", "PlatformDomainBinding", "Cxl", "Bdf", "Hdm", "Dpa", "FabricManager",
            "RecoveryToken", "Opcode", "ReplayCertificate"
        ];
        foreach (Type type in dtoTypes)
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                Assert.DoesNotContain(forbidden, value => property.PropertyType.ToString().Contains(value, StringComparison.OrdinalIgnoreCase));

        Type[] mutationParameters = typeof(RuntimeKernel).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name != nameof(RuntimeKernel.CreateAuthorityInspector))
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(typeof(AuthorityNodeId), mutationParameters);
        Assert.All(typeof(AuthorityInspector).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            method => Assert.DoesNotContain(new[] { "Mutate", "Revoke", "Release", "Transfer", "Publish", "Close" },
                verb => method.Name.Contains(verb, StringComparison.OrdinalIgnoreCase)));
    }

    private static ProtocolDefinitionV1 Protocol(ServiceContractIdentity contract) =>
        new(contract.Name, contract.Digest, "Idle", ["Done"], [new(1, "Invoke")], [new(1, "Idle", "Done")]);

    private static ComponentAdmissionPlan InspectablePlan(string name, ulong processId, ulong domainId)
    {
        SingProcessManifestV1 process = TestFixtures.Manifest(processId, domainId, 1, $"{name}-entry");
        ServiceManifestV1 manifest = new ServiceManifestV1(new(name), new("1"),
            Convert.ToHexString(SHA256.HashData(ComponentImage)).ToLowerInvariant(), process,
            budgetRequests:
            [
                new(ServiceBudgetDimension.IpcMessages, 4),
                new(ServiceBudgetDimension.CheckpointStorageBytes, 64),
            ],
            checkpointPolicy: new(ServiceCheckpointMode.PlannedReplacementOnly));
        return new(manifest, ComponentImage);
    }

    private sealed class InspectionProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider
    {
        private PlatformProviderDomainLease? _domain;

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("provider-secret"),
            PlatformDomainContract.ContractVersion,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new([
            new(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.OwnedRegionMapping, PlatformOwnedRegionMappingContract.ContractVersion, PlatformFeatureAvailability.Executable)
        ]);

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            _domain = new(new(1), new(1), subject);
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(_domain.Value);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            _domain = null;
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            _domain == domainLease
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(new(new(1), new(1), domainLease, region, access))
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(PlatformAuthorityStatus.WrongDomain, "Wrong domain.");

        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Ok();
    }
}
