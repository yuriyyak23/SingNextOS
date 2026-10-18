using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed partial class PlatformDsc1ComputeTests
{
    public interface IUninvokedDsc1Provider : IPlatformOriginalAdmissionResolutionProvider,
        IPlatformFeatureProvider, IPlatformProviderIncarnationSource, IPlatformOriginalDsc1CopyBackingProvider,
        IPlatformOriginalDomainClosureProvider, IPlatformRegionRevocationResolutionProvider,
        IPlatformRegionRevocationNoEffectProvider { }

    public class UninvokedDsc1ProviderSpy : DispatchProxy
    {
        public HybridCpuPlatformAuthorityProvider Inner = null!;
        public Action? BeforeSource;
        public bool SourceUnavailable;
        public int MappingCloseBegins;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_CurrentIncarnation")
            {
                BeforeSource?.Invoke();
                if (SourceUnavailable) throw new InvalidOperationException("Pre-provider source unavailable");
            }
            if (method.Name == "BeginRegionMappingRevocation") MappingCloseBegins++;
            return method.Invoke(Inner, args);
        }
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void UninvokedDsc1SourceFailureRetainsExactViewsAndChargeUntilCleanup(bool sourceFault, bool foreignRoute)
    {
        (Scenario Scenario, BudgetAccountHandle Budget, Dsc1NativeSpy Spy, UninvokedDsc1ProviderSpy Provider) Build(ulong id)
        {
            var proxy = DispatchProxy.Create<IOriginalDsc1Runtime, Dsc1NativeSpy>();
            var spy = (Dsc1NativeSpy)(object)proxy;
            spy.Native = new();
            var provider = DispatchProxy.Create<IUninvokedDsc1Provider, UninvokedDsc1ProviderSpy>();
            var providerSpy = (UninvokedDsc1ProviderSpy)(object)provider;
            providerSpy.Inner = new HybridCpuPlatformAuthorityProvider(proxy);
            BudgetAccountHandle budget = default;
            var scenario = CreateScenario(provider, id, id * 10, 32,
                (kernel, subject) =>
                {
                    var admin = TestFixtures.Create(kernel, id + 1, id * 10 + 1).Handle;
                    var cap = kernel.MintCapability(new(id * 10 + 1), admin, ResourceKind.KernelService,
                        CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
                    budget = kernel.AdmitProcessBudget(admin, cap, subject, "dsc1-uninvoked",
                        [new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value!.ProcessBudget;
                }, originalSlices: true);
            return (scenario, budget, spy, providerSpy);
        }
        var x = Build(2601); var foreign = Build(2605);
        x.Scenario.Destination.Span.Fill(0x35);
        foreign.Scenario.Source.Span.Fill(0x7D);
        Assert.True(SubmitWhole(foreign.Scenario).IsSuccess);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        IDictionary Payloads(RuntimeKernel kernel) => (IDictionary)kernel.GetType().GetField("_dsc1PayloadOperations", flags)!.GetValue(kernel)!;
        var foreignPayload = Assert.Single(Payloads(foreign.Scenario.Kernel).Values.Cast<object>());
        var foreignLease = foreignPayload.GetType().GetProperty("SourceLease")!.GetValue(foreignPayload)!;
        object? storage = null, original = null; FieldInfo? route = null; byte[]? output = null;
        x.Provider.BeforeSource = () =>
        {
            if (storage is not null || Payloads(x.Scenario.Kernel).Count != 1) return;
            var payload = Assert.Single(Payloads(x.Scenario.Kernel).Values.Cast<object>());
            var outputLease = payload.GetType().GetProperty("DestinationLease")!.GetValue(payload)!;
            var outputStorage = outputLease.GetType().GetField("_storage", flags)!.GetValue(outputLease)!;
            output = (byte[])outputStorage.GetType().GetProperty("Data")!.GetValue(outputStorage)!;
            var lease = payload.GetType().GetProperty(sourceFault ? "SourceLease" : "DestinationLease")!.GetValue(payload)!;
            storage = lease.GetType().GetField("_storage", flags)!.GetValue(lease)!;
            route = storage.GetType().GetField("_runtimeReservation", flags)!;
            original = route.GetValue(storage);
            route.SetValue(storage, foreignRoute ? foreignLease.GetType().GetField("_lifetime", flags)!.GetValue(foreignLease) : null);
            x.Provider.SourceUnavailable = true;
        };
        KernelResult<PlatformDsc1CopySubmission> submitted = default;
        var escaped = Record.Exception(() => submitted = SubmitWhole(x.Scenario));
        Assert.NotNull(storage);
        Assert.Null(escaped);
        Assert.False(submitted.IsSuccess);
        Assert.Equal(0, x.Spy.Copies); Assert.Equal(0, x.Spy.Prepares);
        ulong Used((Scenario Scenario, BudgetAccountHandle Budget, Dsc1NativeSpy Spy, UninvokedDsc1ProviderSpy Provider) item) => item.Scenario.Kernel.QueryBudget(item.Budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        Assert.Equal(128UL, Used(x)); Assert.Equal(128UL, Used(foreign));
        var retained = Assert.Single(Payloads(x.Scenario.Kernel).Values.Cast<object>());
        var identity = (PlatformDsc1CopySubmission)retained.GetType().GetProperty("Submission")!.GetValue(retained)!;
        Assert.True(x.Scenario.Kernel.PlatformAuthority.HasActiveDsc1Operations(x.Scenario.Binding));
        Assert.False(x.Scenario.Kernel.TerminateProcess(x.Scenario.Subject).IsSuccess);
        Assert.Equal(128UL, Used(x));
        route!.SetValue(storage, original);
        // Cleanup of a proven uninvoked request needs no active source/permission.
        Assert.True(x.Scenario.Kernel.CancelPlatformDsc1Copy(x.Scenario.Subject, identity).IsSuccess);
        Assert.Equal(64UL, Used(x));
        Assert.All(output!, b => Assert.Equal((byte)0x35, b));
        x.Provider.SourceUnavailable = false;
        Assert.True(x.Scenario.Kernel.TerminateProcess(x.Scenario.Subject).IsSuccess);
        Assert.Empty(Payloads(x.Scenario.Kernel));
        Assert.Equal(0UL, Used(x));
        Assert.All(x.Scenario.Kernel.Regions.Snapshot().Where(r => r.Handle == x.Scenario.Source.Handle || r.Handle == x.Scenario.Destination.Handle), r => Assert.Equal(RegionState.Released, r.State));
        Assert.Equal(0, x.Spy.Copies); Assert.Equal(0, x.Spy.Prepares);
        Assert.Equal(128UL, Used(foreign)); Assert.Equal(1, foreign.Spy.Copies);
        Assert.True((bool)foreignLease.GetType().GetProperty("IsActive", flags)!.GetValue(foreignLease)!);
        Assert.Throws<InvalidOperationException>(() => foreign.Scenario.Destination.Span.ToArray());
    }
}
