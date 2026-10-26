using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class CxlSecurityAndMultiHostTests
{
    private static readonly CxlSecurityProperties SecureProperties =
        CxlSecurityProperties.LinkEncryptionAvailable | CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated;

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, false)]
    [InlineData(true, 0, true)]
    [InlineData(false, 1, false)]
    [InlineData(false, 1, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 1, true)]
    [InlineData(false, 2, false)]
    [InlineData(false, 2, true)]
    [InlineData(true, 2, false)]
    [InlineData(true, 2, true)]
    [InlineData(false, 3, false)]
    [InlineData(false, 3, true)]
    [InlineData(true, 3, false)]
    [InlineData(true, 3, true)]
    public void ActualSecurityReadinessRequiresOriginalParentAcrossQueries(bool foreign, int lossStage, bool required)
    {
        SecurityDiscoveryHook? discovery = null;
        SecurityScenario s = CreateSecurityScenario(SecureProperties, model => discovery = new(model));
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object original = routes[s.Fabric.BindingId]!;
        object gate = typeof(CxlAuthorityBridge).GetField("_gate", flags)!.GetValue(s.Bridge)!;
        bool lost = false;
        void Lose()
        {
            Assert.False(Monitor.IsEntered(gate));
            lock (gate)
            {
                if (foreign) routes[s.Fabric.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                else routes.Remove(s.Fabric.BindingId);
            }
            lost = true;
        }
        int queries = 0;
        CxlSecurityAuthority authority = new CxlSecurityAuthority(s.Bridge, new OverriddenSecurityProvider(s.SecurityProvider, (endpoint, generation) =>
        {
            queries++;
            PlatformAuthorityResult<CxlSecurityStateSnapshot> state = s.SecurityProvider.QuerySecurityState(endpoint, generation);
            if (lossStage == 2) Lose();
            if (lossStage == 3) discovery!.BeforeQuery = count => { if (count == 2) Lose(); };
            return state;
        }));
        if (lossStage == 0) Lose();
        if (lossStage == 1) discovery!.BeforeQuery = count => { if (count == 2) Lose(); };
        KernelResult<CxlSecureComputeReadiness> result = authority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), required));
        Assert.True(lost);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(lossStage < 2 ? 0 : 1, queries);
        Assert.True(s.Kernel.Regions.ValidateUse(s.Use.Handle, s.Owner).IsSuccess);
        Assert.Equal(s.Fabric, s.Model.Query(s.Fabric.BindingId).Value!);
        Assert.False(s.Kernel.RevokePlatformDevice(s.Subject.Process, s.Lease).IsSuccess);
        lock (gate) routes[s.Fabric.BindingId] = original;
        discovery!.BeforeQuery = null;
        Assert.True(s.Bridge.ReleaseFabric(s.Fabric).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Subject.Process, s.Lease).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentSecurityReplyCannotPublishReadinessThroughForeignParent(bool foreign)
    {
        SecurityScenario s = CreateSecurityScenario(SecureProperties);
        const global::System.Reflection.BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge).GetField("_trackedFabric", flags)!.GetValue(s.Bridge)!;
        object original = routes[s.Fabric.BindingId]!;
        object gate = typeof(CxlAuthorityBridge).GetField("_gate", flags)!.GetValue(s.Bridge)!;
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CxlSecurityAuthority authority = new CxlSecurityAuthority(s.Bridge, new OverriddenSecurityProvider(s.SecurityProvider, (endpoint, generation) =>
        {
            Assert.False(Monitor.IsEntered(gate));
            PlatformAuthorityResult<CxlSecurityStateSnapshot> reply = s.SecurityProvider.QuerySecurityState(endpoint, generation);
            entered.SetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
            return reply;
        }));
        Task<KernelResult<CxlSecureComputeReadiness>> querying = Task.Run(() => authority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), true)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            lock (gate)
            {
                if (foreign) routes[s.Fabric.BindingId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                else routes.Remove(s.Fabric.BindingId);
            }
        }
        finally { resume.Set(); }
        KernelResult<CxlSecureComputeReadiness> result = await querying.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.False(s.Kernel.TerminateProcess(s.Subject.Process).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Subject.Process).Value!.LocalReclaimCompleted);
    }

    private sealed class SecurityDiscoveryHook(ICxlDiscoveryProvider inner) : ICxlDiscoveryProvider
    {
        private Action<int>? _before;
        private int _queries;
        internal Action<int>? BeforeQuery { set { _before = value; _queries = 0; } }
        public PlatformAuthorityResult<CxlEndpointSnapshot> QueryEndpoint(CxlEndpointId id)
        {
            _before?.Invoke(++_queries);
            return inner.QueryEndpoint(id);
        }
    }

    [Fact]
    public void AuthenticatedEvidenceCannotReplaceRegionAuthority()
    {
        SecurityScenario s = CreateSecurityScenario(SecureProperties);
        RegionUseHandle forged = s.Use.Handle with { UseId = new RegionUseId(9999) };
        KernelResult<CxlSecureComputeReadiness> result = s.SecurityAuthority.Evaluate(s.Owner, forged, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), true));
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.RegionUseNotFound, result.Error);
    }

    [Fact]
    public void ValidAuthorityStillRejectsMissingRequiredIdeEvidence()
    {
        SecurityScenario s = CreateSecurityScenario(CxlSecurityProperties.DeviceAuthenticated);
        KernelResult<CxlSecureComputeReadiness> result = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), true));
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, result.Error);
    }

    [Fact]
    public void SecuritySessionResetInvalidatesCapturedEvidenceGenerationOnly()
    {
        SecurityScenario s = CreateSecurityScenario(SecureProperties);
        Assert.True(s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), true)).IsSuccess);
        Assert.True(s.SecurityProvider.ResetSecuritySession(s.Endpoint.EndpointId).IsSuccess);
        KernelResult<CxlSecureComputeReadiness> stale = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), true));
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.True(s.Kernel.Regions.ValidateUse(s.Use.Handle, s.Owner).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentSecurityResetAndQueryReturnOneCoherentGenerationTuple()
    {
        for (int iteration = 0; iteration < 64; iteration++)
        {
            CxlSecurityModelProvider provider = new CxlSecurityModelProvider();
            CxlEndpointId endpoint = new CxlEndpointId($"security-race-{iteration}");
            CxlDeviceGeneration device = new CxlDeviceGeneration(1);
            Assert.True(provider.Register(endpoint, device, SecureProperties).IsSuccess);
            using Barrier barrier = new Barrier(2);
            Task<PlatformAuthorityResult<CxlSecurityStateSnapshot>> query = Task.Run(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                return provider.QuerySecurityState(endpoint, device);
            });
            Task<PlatformAuthorityResult> reset = Task.Run(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                return provider.ResetSecuritySession(endpoint);
            });
            await Task.WhenAll(query, reset).WaitAsync(TimeSpan.FromSeconds(10));
            PlatformAuthorityResult resetResult = await reset;
            PlatformAuthorityResult<CxlSecurityStateSnapshot> queryResult = await query;
            Assert.True(resetResult.IsSuccess);
            CxlSecurityStateSnapshot state = queryResult.Value!;
            Assert.True(queryResult.IsSuccess);
            Assert.Equal(state.EvidenceGeneration.Value, state.ProviderTrustGeneration);
            Assert.Equal(state.EvidenceGeneration.Value, state.ResetGeneration);
            if (state.EvidenceGeneration.Value == 1)
                Assert.Equal(CxlSecurityHealth.Ready, state.Health);
            else
            {
                Assert.Equal(2UL, state.EvidenceGeneration.Value);
                Assert.Equal(CxlSecurityHealth.Failed, state.Health);
                Assert.Equal(CxlSecurityProperties.None,
                    state.Properties & (CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated));
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeviceRebindDuringSecurityQueryCannotReturnStaleReadiness(bool required)
    {
        SecurityScenario s = CreateSecurityScenario(SecureProperties);
        CxlSecurityAuthority authority = new CxlSecurityAuthority(s.Bridge,
            new RebindingSecurityProvider(s.SecurityProvider, s.Model, s.Endpoint.EndpointId));

        KernelResult<CxlSecureComputeReadiness> result = authority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease,
            s.Endpoint, s.Fabric, new(SecureProperties, new(1), required));

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.True(s.Kernel.Regions.ValidateUse(s.Use.Handle, s.Owner).IsSuccess);
    }

    [Fact]
    public void SecuritySnapshotForAnotherEndpointCannotSatisfyPolicy()
    {
        SecurityScenario s = CreateSecurityScenario(SecureProperties);
        CxlSecurityAuthority authority = new CxlSecurityAuthority(s.Bridge,
            new OverriddenSecurityProvider(s.SecurityProvider, (endpoint, generation) =>
            {
                CxlSecurityStateSnapshot current = s.SecurityProvider.QuerySecurityState(endpoint, generation).Value!;
                return PlatformAuthorityResult<CxlSecurityStateSnapshot>.Ok(
                    current with { EndpointId = new("other-endpoint") });
            }));

        KernelResult<CxlSecureComputeReadiness> result = authority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease,
            s.Endpoint, s.Fabric, new(SecureProperties, new(1), true));

        Assert.Equal(KernelError.StaleGeneration, result.Error);
    }

    [Fact]
    public void ThrowingSecurityQueryFailsClosedWithoutReadiness()
    {
        SecurityScenario s = CreateSecurityScenario(SecureProperties);
        CxlSecurityAuthority authority = new CxlSecurityAuthority(s.Bridge,
            new OverriddenSecurityProvider(s.SecurityProvider, (_, _) =>
                throw new InvalidOperationException("model query fault")));

        KernelResult<CxlSecureComputeReadiness> result = authority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease,
            s.Endpoint, s.Fabric, new(SecureProperties, new(1), true));

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
    }

    [Fact]
    public void UnavailableSecurityDoesNotBreakNonSecureStagedEligibility()
    {
        SecurityScenario s = CreateSecurityScenario(null);
        KernelResult<CxlSecureComputeReadiness> result = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(CxlSecurityProperties.None, new(1), false));
        Assert.True(result.IsSuccess, result.Message);
        Assert.False(result.Value!.Ready);
        Assert.Null(result.Value.SecurityState);
    }

    [Fact]
    public void ModelBackendCannotAdvertiseHardwareAttestedSecurity()
    {
        SecurityScenario s = CreateSecurityScenario(SecureProperties);
        KernelResult<CxlSecureComputeReadiness> result = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease, s.Endpoint, s.Fabric,
            new(SecureProperties, new(1), RequiredForOperation: true, RequireHardwareAttestation: true));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.Equal(CxlSecurityEvidenceAssurance.ModelOnly,
            s.SecurityProvider.QuerySecurityState(s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!.Assurance);
    }

    [Fact]
    public void ProviderNeutralTrustPredicateBindsFirmwareDeviceAssignmentAndResetGenerations()
    {
        SecurityScenario s = CreateSecurityScenario(SecureProperties);
        CxlSecurityStateSnapshot state = s.SecurityProvider.QuerySecurityState(
            s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!;
        TrustObligationV1 obligation = new TrustObligationV1(1, s.Lease.Device.ResourceId,
            state.FirmwareMeasurementDigest, DeviceTrustAssuranceV1.ModelOnly,
            state.ProviderTrustGeneration, state.DeviceGeneration.Value,
            s.Lease.Generation.Value, state.ResetGeneration, 31);

        KernelResult<CxlSecureComputeReadiness> result = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease,
            s.Endpoint, s.Fabric, new(SecureProperties, state.EvidenceGeneration,
                RequiredForOperation: true, TrustObligation: obligation, CurrentPolicyGeneration: 31));

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Value!.Ready);
        Assert.False(CxlDeviceTrustEvidenceAdapter.Project(state, s.Lease, 31).Value!.GrantsAuthority);
    }

    [Fact]
    public void UpdatedCxlEnvelopeCannotMakePreResetTrustObligationFreshAgain()
    {
        SecurityScenario s = CreateSecurityScenario(SecureProperties);
        CxlSecurityStateSnapshot before = s.SecurityProvider.QuerySecurityState(
            s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!;
        TrustObligationV1 obligation = new TrustObligationV1(1, s.Lease.Device.ResourceId,
            before.FirmwareMeasurementDigest, DeviceTrustAssuranceV1.ModelOnly,
            before.ProviderTrustGeneration, before.DeviceGeneration.Value,
            s.Lease.Generation.Value, before.ResetGeneration, 31);
        Assert.True(s.SecurityProvider.ResetSecuritySession(s.Endpoint.EndpointId).IsSuccess);
        CxlSecurityStateSnapshot after = s.SecurityProvider.QuerySecurityState(
            s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!;

        KernelResult<CxlSecureComputeReadiness> replay = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease,
            s.Endpoint, s.Fabric, new(SecureProperties, after.EvidenceGeneration,
                RequiredForOperation: true, TrustObligation: obligation, CurrentPolicyGeneration: 31));

        Assert.False(replay.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, replay.Error);
    }

    [Fact]
    public void TrustPredicateRejectsWrongSecureAssignmentAndCurrentPolicyGeneration()
    {
        SecurityScenario s = CreateSecurityScenario(SecureProperties);
        CxlSecurityStateSnapshot state = s.SecurityProvider.QuerySecurityState(
            s.Endpoint.EndpointId, s.Endpoint.DeviceGeneration).Value!;
        TrustObligationV1 exact = new TrustObligationV1(1, s.Lease.Device.ResourceId,
            state.FirmwareMeasurementDigest, DeviceTrustAssuranceV1.ModelOnly,
            state.ProviderTrustGeneration, state.DeviceGeneration.Value,
            s.Lease.Generation.Value, state.ResetGeneration, 31);

        KernelResult<CxlSecureComputeReadiness> wrongAssignment = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease,
            s.Endpoint, s.Fabric, new(SecureProperties, state.EvidenceGeneration,
                RequiredForOperation: true,
                TrustObligation: exact with { AssignmentGeneration = exact.AssignmentGeneration + 1 },
                CurrentPolicyGeneration: 31));
        KernelResult<CxlSecureComputeReadiness> stalePolicy = s.SecurityAuthority.Evaluate(s.Owner, s.Use.Handle, s.Subject, s.Lease,
            s.Endpoint, s.Fabric, new(SecureProperties, state.EvidenceGeneration,
                RequiredForOperation: true, TrustObligation: exact, CurrentPolicyGeneration: 32));

        Assert.Equal(KernelError.StaleGeneration, wrongAssignment.Error);
        Assert.Equal(KernelError.StaleGeneration, stalePolicy.Error);
    }

    [Fact]
    public void LocalFenceCannotReclaimOrEnableSecondWriterWithoutRemoteClosure()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle ownerHandle) = TestFixtures.Create(kernel, 1001, 1002);
        RegionOwner owner = new RegionOwner(new DomainId(1002), ownerHandle.Generation);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(ownerHandle, 32).Value!;
        CxlMultiHostGate gate = new CxlMultiHostGate(kernel);
        MultiHostMemoryBinding first = gate.Bind(buffer.Handle, owner, new("host-a"), MultiHostAccessMode.Writable, false).Value!;
        Assert.Equal(KernelError.RegionUseConflict,
            gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).Error);
        Assert.True(gate.Fence(first.Binding).IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict,
            gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).Error);
        Assert.Equal(KernelError.PlatformUnsupported, gate.BeginReclaim(first.Binding).Error);
        Assert.Equal(KernelError.RegionUseConflict,
            gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).Error);
        Assert.Equal(KernelError.PlatformUnsupported, gate.CompleteReclaim(first.Binding).Error);
        Assert.Equal(KernelError.RegionUseConflict, gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).Error);
    }

    [Fact]
    public void DistributedWritableFlagCannotEnableUnimplementedProtocol()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle handle) = TestFixtures.Create(kernel, 1011, 1012);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        CxlMultiHostGate gate = new CxlMultiHostGate(kernel);
        KernelResult<MultiHostMemoryBinding> result = gate.Bind(buffer.Handle, new(new(1012), handle.Generation), new("host-a"),
            MultiHostAccessMode.Writable, false, distributedWritableAuthorityAvailable: true);
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
    }

    [Fact]
    public void ReadOnlySharingRequiresExplicitProviderSupport()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle handle) = TestFixtures.Create(kernel, 1021, 1022);
        RegionOwner owner = new RegionOwner(new(1022), handle.Generation);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        CxlMultiHostGate gate = new CxlMultiHostGate(kernel);
        Assert.Equal(KernelError.PlatformUnsupported,
            gate.Bind(buffer.Handle, owner, new("host-a"), MultiHostAccessMode.ReadOnly, false).Error);
        Assert.True(gate.Bind(buffer.Handle, owner, new("host-a"), MultiHostAccessMode.ReadOnly, true).IsSuccess);
        Assert.True(gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.ReadOnly, true).IsSuccess);
    }

    [Fact]
    public void HostDisappearanceRetainsOriginalUseAndCannotProveRemoteIsolation()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle handle) = TestFixtures.Create(kernel, 1025, 1026);
        RegionOwner owner = new RegionOwner(new(1026), handle.Generation);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        CxlMultiHostGate gate = new CxlMultiHostGate(kernel);
        MultiHostMemoryBinding first = gate.Bind(buffer.Handle, owner, new("host-a"), MultiHostAccessMode.Writable, false).Value!;

        KernelResult<MultiHostMemoryBinding> disappeared = gate.RecordHostDisappearance(first.Binding);

        Assert.True(disappeared.IsSuccess, disappeared.Message);
        Assert.Equal(MultiHostBindingState.Fenced, disappeared.Value!.State);
        Assert.Equal(KernelError.RegionUseConflict,
            gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).Error);
        Assert.Equal(KernelError.PlatformUnsupported, gate.BeginReclaim(first.Binding).Error);
        Assert.Equal(KernelError.PlatformUnsupported, gate.CompleteReclaim(first.Binding).Error);
        Assert.Equal(KernelError.RegionUseConflict, gate.Bind(buffer.Handle, owner, new("host-b"), MultiHostAccessMode.Writable, false).Error);
    }

    [Fact]
    public void SecurityEvidenceSurfaceContainsNoAuthorityToken()
    {
        PropertyInfo[] properties = typeof(CxlSecurityStateSnapshot).GetProperties();
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(CapabilityId) ||
            property.PropertyType == typeof(RegionHandle) || property.Name.Contains("Authority", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(IPlatformAuthorityProvider), typeof(CxlSecurityModelProvider).GetInterfaces());
    }

    private static SecurityScenario CreateSecurityScenario(CxlSecurityProperties? properties,
        Func<CxlType3ModelProvider, ICxlDiscoveryProvider>? discoveryFactory = null)
    {
        AuthorityProvider platform = new AuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(platform);
        (SingProcess _, ProcessHandle handle) = TestFixtures.Create(kernel, 1031, 1032);
        SingProcess process = kernel.Processes.Resolve(handle).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, handle.Generation);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(process.DomainId, handle);
        PlatformDomainBinding domain = kernel.BindPlatformAuthorityDomain(handle).Value!;
        CapabilityDescriptorV1 capability = kernel.MintCapability(process.DomainId, handle, ResourceKind.Device, "device:secure-cxl",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        PlatformDeviceLease lease = kernel.BindPlatformDevice(handle, domain, capability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        CxlType3ModelProvider model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-cxl"), lease.Device, 1024).IsSuccess);
        CxlEndpointSnapshot endpoint = model.QueryEndpoint(new("secure-cxl")).Value!;
        CxlSecurityModelProvider security = new CxlSecurityModelProvider();
        if (properties is not null) Assert.True(security.Register(endpoint.EndpointId, endpoint.DeviceGeneration, properties.Value).IsSuccess);
        CxlAuthorityBridge bridge = new CxlAuthorityBridge(kernel, discoveryFactory?.Invoke(model) ?? model, model, model, model,
            new CoherentStub(), security);
        OwnedBuffer<byte> control = kernel.AllocateBuffer<byte>(handle, 16).Value!;
        RegionUseDescriptor use = kernel.AcquireRegionUse(handle, control.Handle, RegionUseMode.DevicePrivate, new(0, 16)).Value!;
        CxlFabricBinding fabric = bridge.BindFabric(owner, use.Handle, subject, lease,
            new(endpoint.EndpointId, endpoint.DeviceGeneration, 16, CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        return new(kernel, owner, subject, lease, endpoint, fabric, use, security, model,
            bridge, new(bridge, security));
    }

    private sealed record SecurityScenario(RuntimeKernel Kernel, RegionOwner Owner, PlatformDomainIdentity Subject,
        PlatformDeviceLease Lease, CxlEndpointSnapshot Endpoint, CxlFabricBinding Fabric, RegionUseDescriptor Use,
        CxlSecurityModelProvider SecurityProvider, CxlType3ModelProvider Model,
        CxlAuthorityBridge Bridge, CxlSecurityAuthority SecurityAuthority);

    private sealed class RebindingSecurityProvider(
        CxlSecurityModelProvider security, CxlType3ModelProvider model, CxlEndpointId endpointId)
        : ICxlSecurityStateProvider
    {
        public PlatformAuthorityResult<CxlSecurityStateSnapshot> QuerySecurityState(
            CxlEndpointId endpoint, CxlDeviceGeneration generation)
        {
            PlatformAuthorityResult<CxlSecurityStateSnapshot> captured = security.QuerySecurityState(endpoint, generation);
            if (!model.Rebind(endpointId).IsSuccess)
                throw new InvalidOperationException("The model rebind fault injection failed.");
            return captured;
        }

        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(
            CxlEndpointId endpoint, CxlDeviceGeneration generation) =>
            security.QuerySecurityEvidence(endpoint, generation);
    }

    private sealed class OverriddenSecurityProvider(
        CxlSecurityModelProvider security,
        Func<CxlEndpointId, CxlDeviceGeneration,
            PlatformAuthorityResult<CxlSecurityStateSnapshot>> query) : ICxlSecurityStateProvider
    {
        public PlatformAuthorityResult<CxlSecurityStateSnapshot> QuerySecurityState(
            CxlEndpointId endpoint, CxlDeviceGeneration generation) => query(endpoint, generation);

        public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(
            CxlEndpointId endpoint, CxlDeviceGeneration generation) =>
            security.QuerySecurityEvidence(endpoint, generation);
    }

    private sealed class CoherentStub : ICxlCoherentAccessProvider
    {
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding f, RegionUseDescriptor u) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId id) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding b) => PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
        private static PlatformAuthorityResult<T> Fail<T>() => PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }
    private sealed class AuthorityProvider : IPlatformAuthorityProvider, IPlatformDeviceLeaseProvider, IPlatformFeatureProvider
    {
        public PlatformProviderDescriptor Descriptor { get; } = new(new("security-test"), 1, PlatformAuthorityFeatures.NeutralDomainBinding);
        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission)
        });
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity s) => PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(1), new(1), s));
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease l) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease d, PlatformDeviceIdentity i, PlatformDeviceRights r) => PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(1), new(1), d, i, r));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease l) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease d, PlatformRegionIdentity r, PlatformMemoryAccess a) => PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(PlatformAuthorityStatus.Unsupported, "unused");
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease m, PlatformRegionRevocationPolicy p) => PlatformAuthorityResult.Ok();
    }
}
