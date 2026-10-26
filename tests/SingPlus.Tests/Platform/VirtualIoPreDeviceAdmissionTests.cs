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

public sealed class VirtualIoPreDeviceAdmissionTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    public interface ILoanRuntime : NativeTransferRawCustodyConsumerTests.ISourceRuntime, INeutralOriginalDeviceLoanResolutionProvider, INeutralOriginalDeviceLoanNoEffectProvider, INeutralOriginalDeviceLifetimeProvider { }
    public class LoanSpy : DispatchProxy
    {
        public NeutralDomainRuntimeFacade Inner = new();
        public bool LoseAcquireReply, LoseQuery, LoseRetireReply;
        public int Acquires;
        public Action? AfterAcquire;
        public Action? BeforeSource;
        public Action<NeutralOriginalDeviceLoanRequest>? BeforeAcquire;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_MappingClosureIncarnation") BeforeSource?.Invoke();
            if (method!.Name == "AcquireOriginalDeviceLoan") BeforeAcquire?.Invoke((NeutralOriginalDeviceLoanRequest)args![0]!);
            if (method!.Name is "ResolveOriginalDeviceLoan" or "ResolveOriginalDeviceLoanNoEffect" && LoseQuery) throw new IOException("Loan query reply lost");
            object? result;
            try { result = method.Invoke(Inner, args); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            if (method.Name == "AcquireOriginalDeviceLoan") { Acquires++; AfterAcquire?.Invoke(); if (LoseAcquireReply) throw new IOException("Acquired original loan reply lost"); }
            if (method.Name == "RetireOriginalDeviceLoan" && LoseRetireReply) { LoseRetireReply = false; throw new IOException("Retired loan reply lost"); }
            return result;
        }
    }
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)] [InlineData(true, 4)]
    [InlineData(true, 5)] [InlineData(true, 6)]
    public async Task SourceExceptionBeforeDeviceBindClosesExactLoanAndStagedCharges(bool acquired, int fault)
    {
        ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime native = DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime, ExactVirtualIoBackingConsumerTests.BackingSpy>();
        var spy = (ExactVirtualIoBackingConsumerTests.BackingSpy)(object)native; spy.Inner = new();
        ExactArtifactReleaseConsumerTests.IOriginalRootRuntime root = DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime, ExactArtifactReleaseConsumerTests.RootSpy>();
        var source = (ExactArtifactReleaseConsumerTests.RootSpy)(object)root; source.Inner = spy.Inner;
        HybridCpuExecutableChildAdapter sdk = new(root, native);
        NativeTransferRawCustodyConsumerTests.ISourceRuntime loanRuntime = fault == 4
            ? DispatchProxy.Create<NativeTransferRawCustodyConsumerTests.ISourceRuntime, LoanSpy>() : DispatchProxy.Create<ILoanRuntime, LoanSpy>();
        LoanSpy loanSpy = (LoanSpy)(object)loanRuntime;
        NeutralDomainRuntimeFacade model = loanSpy.Inner;
        RuntimeKernel kernel = new(new HybridCpuPlatformAuthorityProvider(loanRuntime, sdk));
        ProcessHandle owner = TestFixtures.Create(kernel, 3271, 32710).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 3272, 32720).Handle;
        CapabilityId admin = kernel.MintCapability(new(32720), service, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, owner, "io-pre-device", [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 32).Value!; foreign.Span.Fill(0x7F);
        CapabilityId create = kernel.MintCapability(new(32710), owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        IDictionary roots = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", Hidden)!.GetValue(kernel.PlatformAuthority)!;
        object rootOwner = Assert.Single(roots.Values.Cast<object>());
        PlatformDomainBinding binding = (PlatformDomainBinding)rootOwner.GetType().GetProperty("Binding")!.GetValue(rootOwner)!;
        CapabilityId deviceCap = kernel.MintCapability(new(32710), owner, ResourceKind.Device, "device:pre-device", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!.CapabilityId;
        PlatformDeviceLease device = kernel.BindPlatformDevice(owner, binding, deviceCap, PlatformDeviceRights.Read | PlatformDeviceRights.Write).Value!;
        int queries = 0;
        source.BeforeQuery = () => { if (++queries == (acquired ? 2 : 1)) source.ThrowSource = true; };
        if (fault is 1 or 3 or 4 or 5 or 6) { loanSpy.LoseAcquireReply = true; loanSpy.AfterAcquire = () => source.ThrowSource = true; }
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "io-pre-device", new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(owner, descriptor, []).Value;
        RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        var reply = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, owner, session)).BindIoAsync(new(domain.Domain, device.LeaseId.Value, device.Generation.Value, VirtualIoAccess.Read | VirtualIoAccess.Write, 512));
        Assert.False(host.ProcessNext().IsSuccess); await Assert.ThrowsAnyAsync<Exception>(async () => await reply);
        Assert.True(source.ThrowSource); Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0, spy.Calls.GetValueOrDefault("BindOriginalParentDevice"));
        object dependencies = typeof(NeutralDomainRuntimeFacade).GetField("_dependencies", Hidden)!.GetValue(model)!;
        IDictionary devices = (IDictionary)dependencies.GetType().GetField("Devices", Hidden)!.GetValue(dependencies)!;
        object deviceOwner = Assert.Single(devices.Values.Cast<object>());
        IList loans = (IList)deviceOwner.GetType().GetProperty("OriginalDeviceLoans")!.GetValue(deviceOwner)!;
        Assert.Equal(acquired ? 1 : 0, loans.Count);
        object? retainedLoanFact = null; NeutralOriginalDeviceLoan? retainedLoan = null;
        if (acquired)
        {
            object loanFact = loans[0]!;
            NeutralOriginalDeviceLoan original = (NeutralOriginalDeviceLoan)loanFact.GetType().GetProperty("Loan")!.GetValue(loanFact)!;
            Assert.Same(original, model.ResolveOriginalDeviceLoan(original.Request));
            Assert.Null(model.ResolveOriginalDeviceLoan(original.Request with { }));
            Assert.Null(new NeutralDomainRuntimeFacade().ResolveOriginalDeviceLoan(original.Request));
            Assert.True(model.ValidateOriginalDeviceLoan(original)); // Pure query did not retire the borrower.
            retainedLoanFact = loanFact; retainedLoan = original;
        }
        source.BeforeQuery = null; source.ThrowSource = false;
        loanSpy.AfterAcquire = null; loanSpy.LoseQuery = fault == 3; loanSpy.LoseRetireReply = fault == 2;
        if (fault is 5 or 6)
        {
            FieldInfo route = retainedLoanFact!.GetType().GetField("<Loan>k__BackingField", Hidden)!;
            route.SetValue(retainedLoanFact, fault == 5 ? retainedLoan! with { } : null);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); // A returned equal borrower wrapper is not original custody.
            Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Null(model.ResolveOriginalDeviceLoan(retainedLoan!.Request));
            route.SetValue(retainedLoanFact, retainedLoan);
        }
        if (fault == 4)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); // Incapable owner keeps the unknown original; no Acquire replay.
            Assert.Equal(1, loanSpy.Acquires); Assert.Equal(1, model.ActiveDeviceLeaseCount);
            Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x7F, x));
            return;
        }
        if (fault is 2 or 3)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(1024UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            loanSpy.LoseQuery = false;
        }
        KernelResult closed = kernel.TerminateProcess(owner); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(acquired ? 1 : 0, loanSpy.Acquires);
        Assert.Equal(0, spy.Calls.GetValueOrDefault("BindOriginalParentDevice"));
        Assert.Equal(0, spy.Calls.GetValueOrDefault("BindOriginalVirtualIo"));
        Assert.Equal(0, spy.Calls.GetValueOrDefault("ContainOriginalVirtualIoBacking"));
        Assert.Equal(0, model.ActiveDeviceLeaseCount);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x7F, x));
    }
}
