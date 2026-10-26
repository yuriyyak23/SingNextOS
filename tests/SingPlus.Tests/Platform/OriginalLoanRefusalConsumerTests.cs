using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class OriginalLoanRefusalConsumerTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    public interface IDeviceReplyProvider : ExactVirtualIoBackingConsumerTests.IBridgeBindProvider, IPlatformDomainRevocationNoEffectProvider { }
    public class DeviceReplySpy : DispatchProxy
    {
        public HybridCpuPlatformAuthorityProvider Inner = null!;
        public int Revokes, Queries;
        public bool LoseReply = true;
        public Action? AfterDeviceClose;
        public Action? AfterRootClose;
        public bool LoseRootReply;
        public int RootRevokes;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "RevokeDevice") Revokes++;
            if (method.Name == "RevokeDomain") RootRevokes++;
            if (method.Name == "ResolveDeviceRevocation") Queries++;
            object? result;
            try { result = method.Invoke(Inner, args); } catch (TargetInvocationException exception) { throw exception.InnerException!; }
            if (method.Name == "RevokeDomain" && result is PlatformAuthorityResult { IsSuccess: true } && LoseRootReply)
            { LoseRootReply = false; AfterRootClose?.Invoke(); throw new IOException("Original root reply lost after publication"); }
            if (method.Name == "RevokeDevice" && result is PlatformAuthorityResult { IsSuccess: true }) AfterDeviceClose?.Invoke();
            if (method.Name == "RevokeDevice" && result is PlatformAuthorityResult { IsSuccess: true } && LoseReply)
            { LoseReply = false; throw new IOException("Provider lifetime Close response lost after commit"); }
            return result;
        }
    }
    public interface ILegacyLoanRuntime : NativeTransferRawCustodyConsumerTests.ISourceRuntime, INeutralOriginalDeviceLoanResolutionProvider { }
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(false, 3)] [InlineData(false, 4)] [InlineData(true, 5)]
    [InlineData(false, 6)] [InlineData(false, 7)]
    [InlineData(false, 8)] [InlineData(false, 9)] [InlineData(false, 10)]
    [InlineData(false, 11)] [InlineData(false, 12)]
    [InlineData(false, 13)] [InlineData(false, 14)] [InlineData(false, 15)]
    [InlineData(false, 16)]
    [InlineData(false, 17)]
    [InlineData(false, 18)] [InlineData(false, 19)]
    [InlineData(false, 20)] [InlineData(false, 21)]
    public async Task GeneratedLoanRefusalReclaimsOnlyIoStageAndRetainsIndependentDeviceClosure(bool lostReply, int fault)
    {
        var native = DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime, ExactVirtualIoBackingConsumerTests.BackingSpy>();
        var spy = (ExactVirtualIoBackingConsumerTests.BackingSpy)(object)native; spy.Inner = new();
        NativeTransferRawCustodyConsumerTests.ISourceRuntime runtime = fault == 5
            ? DispatchProxy.Create<ILegacyLoanRuntime, VirtualIoPreDeviceAdmissionTests.LoanSpy>()
            : DispatchProxy.Create<VirtualIoPreDeviceAdmissionTests.ILoanRuntime, VirtualIoPreDeviceAdmissionTests.LoanSpy>();
        var loanSpy = (VirtualIoPreDeviceAdmissionTests.LoanSpy)(object)runtime;
        HybridCpuExecutableChildAdapter sdk = new(spy.Inner, native);
        HybridCpuPlatformAuthorityProvider provider = new(runtime, sdk);
        var bridgeProxy = DispatchProxy.Create<IDeviceReplyProvider, DeviceReplySpy>();
        DeviceReplySpy replySpy = (DeviceReplySpy)(object)bridgeProxy; replySpy.Inner = provider;
        bool replyLoss = fault is >= 8 and <= 10 or >= 13 and <= 16;
        RuntimeKernel kernel = new(replyLoss || fault is >= 17 and <= 21 ? bridgeProxy : provider);
        ProcessHandle owner = TestFixtures.Create(kernel, 3281, 32810).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 3282, 32820).Handle;
        CapabilityId admin = kernel.MintCapability(new(32820), service, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, owner, "loan-refusal", [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 32).Value!; foreign.Span.Fill(0x71);
        CapabilityId create = kernel.MintCapability(new(32810), owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        IDictionary roots = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", Hidden)!.GetValue(kernel.PlatformAuthority)!;
        object rootOwner = Assert.Single(roots.Values.Cast<object>());
        PlatformDomainBinding binding = (PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        CapabilityId deviceCap = kernel.MintCapability(new(32810), owner, ResourceKind.Device, "device:refused-loan", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!.CapabilityId;
        PlatformDeviceLease device = kernel.BindPlatformDevice(owner, binding, deviceCap, PlatformDeviceRights.Read | PlatformDeviceRights.Write).Value!;
        NeutralOriginalDeviceLoanRequest? original = null;
        loanSpy.BeforeAcquire = request =>
        {
            original = request;
            NeutralOriginalDeviceCloseObservation closed = loanSpy.Inner.CloseDevice(new NeutralOriginalDeviceCloseRequest(Guid.NewGuid(), loanSpy.Inner.MappingClosureIncarnation, request.DeviceAdmission.InvocationId, request.Device));
            Assert.Equal(NeutralDeviceCloseDecision.Closed, closed.Observation.Decision);
        };
        loanSpy.LoseAcquireReply = lostReply;
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "loan-refusal", new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(owner, descriptor, []).Value;
        RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        var reply = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, owner, session)).BindIoAsync(new(domain.Domain, device.LeaseId.Value, device.Generation.Value, VirtualIoAccess.Read | VirtualIoAccess.Write, 512));
        Assert.False(host.ProcessNext().IsSuccess); await Assert.ThrowsAnyAsync<Exception>(async () => await reply);
        Assert.NotNull(original); Assert.Equal(1, loanSpy.Acquires);
        Assert.Null(loanSpy.Inner.ResolveOriginalDeviceLoan(original));
        NeutralOriginalDeviceLoanNoEffect raw = loanSpy.Inner.ResolveOriginalDeviceLoanNoEffect(original)!;
        Assert.NotNull(raw); Assert.True(raw.NoEffect); Assert.True(raw.NoFurtherAdmission);
        Assert.Null(loanSpy.Inner.ResolveOriginalDeviceLoanNoEffect(original with { }));
        Assert.Null(new NeutralDomainRuntimeFacade().ResolveOriginalDeviceLoanNoEffect(original));
        Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        object dependencies = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies", Hidden)!.GetValue(loanSpy.Inner)!;
        IDictionary devices = (IDictionary)dependencies.GetType().GetField("Devices", Hidden)!.GetValue(dependencies)!;
        object deviceOwner = Assert.Single(devices.Values.Cast<object>());
        IList admissions = (IList)deviceOwner.GetType().GetProperty("OriginalDeviceLoans")!.GetValue(deviceOwner)!;
        object fact = Assert.Single(admissions.Cast<object>());
        Assert.Null(fact.GetType().GetProperty("Loan")!.GetValue(fact)); // Admission history does not mint a placeholder borrower.
        if (fault is > 0 and < 6)
        {
            PropertyInfo route = fact.GetType().GetProperty("NoEffect")!;
            if (fault == 1) loanSpy.LoseQuery = true;
            if (fault == 2) route.SetValue(fact, raw with { });
            if (fault == 3) route.SetValue(fact, null);
            if (fault == 4) admissions.Clear();
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(1, loanSpy.Acquires); Assert.Equal(0, spy.Calls.GetValueOrDefault("BindOriginalParentDevice"));
            Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x71, x));
            if (fault == 5) return; // Legacy resolver cannot reinterpret absent loan as no-effect.
            if (fault == 4) admissions.Add(fact);
            route.SetValue(fact, raw); loanSpy.LoseQuery = false;
        }
        PropertyInfo lifetimeRoute = deviceOwner.GetType().GetProperty("LifetimeClosure")!;
        NeutralOriginalDeviceLifetimeClosure lifetime = (NeutralOriginalDeviceLifetimeClosure)lifetimeRoute.GetValue(deviceOwner)!;
        Assert.Same(lifetime, loanSpy.Inner.ResolveOriginalDeviceLifetime(lifetime.Admission));
        NeutralDeviceBindResult copiedAdmission = (NeutralDeviceBindResult)typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(lifetime.Admission, null)!;
        Assert.Null(loanSpy.Inner.ResolveOriginalDeviceLifetime(copiedAdmission));
        Assert.Null(new NeutralDomainRuntimeFacade().ResolveOriginalDeviceLifetime(lifetime.Admission));
        if (fault is 6 or 7)
        {
            lifetimeRoute.SetValue(deviceOwner, fault == 6 ? lifetime with { } : null);
            Assert.Null(loanSpy.Inner.ResolveOriginalDeviceLifetime(lifetime.Admission));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            lifetimeRoute.SetValue(deviceOwner, lifetime);
        }
        if (fault is 11 or 12)
        {
            object? local = null; FieldInfo? route = null; object? retained = null;
            loanSpy.BeforeSource = () =>
            {
                if (retained is not null) return;
                IDictionary owners = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalDeviceLeases", Hidden)!.GetValue(provider)!;
                local = Assert.Single(owners.Values.Cast<object>()); route = local.GetType().GetField("LifetimeRaw")!;
                if (route.GetValue(local) is not NeutralOriginalDeviceLifetimeClosure produced) return;
                retained = produced;
                Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", Hidden)!.GetValue(provider)!));
                route.SetValue(local, fault == 11 ? produced with { } : null);
            };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); loanSpy.BeforeSource = null;
            Assert.NotNull(retained);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            route!.SetValue(local, retained);
        }
        if (replyLoss)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            if (fault is 9 or 10)
            {
                IDictionary owners = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalDeviceLeases", Hidden)!.GetValue(provider)!;
                object local = Assert.Single(owners.Values.Cast<object>());
                FieldInfo route = local.GetType().GetField("LifetimeRaw")!;
                NeutralOriginalDeviceLifetimeClosure retained = (NeutralOriginalDeviceLifetimeClosure)route.GetValue(local)!;
                route.SetValue(local, fault == 9 ? retained with { } : null);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x71, x));
                route.SetValue(local, retained);
            }
            if (fault is >= 13 and <= 16)
            {
                IDictionary owners = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalDeviceLeases", Hidden)!.GetValue(provider)!;
                object local = Assert.Single(owners.Values.Cast<object>());
                PropertyInfo admissionRoute = local.GetType().GetProperty("AdmissionSource")!;
                object admission = admissionRoute.GetValue(local)!;
                object parent = admission.GetType().GetProperty("Parent")!.GetValue(admission)!;
                PropertyInfo factoryRoute = parent.GetType().GetProperty("BindSource")!;
                object factory = factoryRoute.GetValue(parent)!;
                IList history = (IList)parent.GetType().GetProperty("OriginalDeviceAdmissions")!.GetValue(parent)!;
                if (fault == 13) admissionRoute.SetValue(local, typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(admission, null));
                if (fault == 14) factoryRoute.SetValue(parent, typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(factory, null));
                if (fault == 15) history.Clear();
                if (fault == 16) admissionRoute.SetValue(local, null);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
                admissionRoute.SetValue(local, admission); factoryRoute.SetValue(parent, factory);
                if (fault == 15) history.Add(admission);
            }
        }
        if (fault == 17)
        {
            replySpy.LoseReply = false;
            IDictionary originalRoutes = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalDeviceLeases", Hidden)!.GetValue(provider)!;
            IDictionary routes = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_deviceLeases", Hidden)!.GetValue(provider)!;
            object key = Assert.Single(originalRoutes.Keys.Cast<object>());
            DictionaryEntry entry = new(key, originalRoutes[key]);
            object admission = entry.Value!.GetType().GetProperty("AdmissionSource")!.GetValue(entry.Value)!;
            object parent = admission.GetType().GetProperty("Parent")!.GetValue(admission)!;
            IList history = (IList)parent.GetType().GetProperty("OriginalDeviceAdmissions")!.GetValue(parent)!;
            replySpy.AfterDeviceClose = () => { originalRoutes.Clear(); routes.Clear(); history.Clear(); };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); // Parent cannot interpret an empty corrupted inventory as zero issued children.
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            originalRoutes.Add(entry.Key, entry.Value); routes.Add(entry.Key, entry.Value); history.Add(admission);
            replySpy.AfterDeviceClose = null;
        }
        if (fault is 18 or 19)
        {
            replySpy.LoseReply = false; replySpy.LoseRootReply = true;
            IDictionary domains = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_domains", Hidden)!.GetValue(provider)!;
            object parent = Assert.Single(domains.Values.Cast<object>());
            IList history = (IList)parent.GetType().GetProperty("OriginalDeviceAdmissions")!.GetValue(parent)!;
            object admission = Assert.Single(history.Cast<object>());
            replySpy.AfterRootClose = () => { if (fault == 18) history.Clear(); else history.Add(admission); };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, replySpy.RootRevokes);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); // Published root terminal cannot erase incomplete original local inventory.
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            history.Clear(); history.Add(admission); replySpy.AfterRootClose = null;
        }
        if (fault is 20 or 21)
        {
            replySpy.LoseReply = false; replySpy.LoseRootReply = true;
            IDictionary children = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", Hidden)!.GetValue(provider)!;
            object child = Assert.Single(children.Values.Cast<object>());
            PropertyInfo route = child.GetType().GetProperty(fault == 20 ? "ClosurePublished" : "ClosureReceipt")!;
            object? retained = null;
            replySpy.AfterRootClose = () => { retained = route.GetValue(child); Assert.NotNull(retained); route.SetValue(child, fault == 20 ? false : null); };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(1, replySpy.RootRevokes);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            route.SetValue(child, retained); replySpy.AfterRootClose = null;
        }
        KernelResult terminal = kernel.TerminateProcess(owner);
        Assert.True(terminal.IsSuccess, terminal.Message + " Revoke=" + replySpy.Revokes + " Query=" + replySpy.Queries); // Admission-scoped lifetime closure, not another Close's reply.
        if (replyLoss) { Assert.Equal(1, replySpy.Revokes); Assert.True(replySpy.Queries > 0); }
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(0, spy.Calls.GetValueOrDefault("BindOriginalParentDevice"));
        Assert.Equal(0, spy.Calls.GetValueOrDefault("BindOriginalVirtualIo"));
        Assert.Equal(1, loanSpy.Acquires);
        Assert.Same(raw, loanSpy.Inner.ResolveOriginalDeviceLoanNoEffect(original));
        Assert.Null(loanSpy.Inner.AcquireOriginalDeviceLoan(original)); // Original refusal remains terminal; no new borrower.
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x71, x));
    }
}
