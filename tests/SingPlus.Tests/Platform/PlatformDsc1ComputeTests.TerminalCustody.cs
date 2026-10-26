using System.Collections;
using System.Reflection;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed partial class PlatformDsc1ComputeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopiedBackingClosureDuringSourceCallbackCannotConsumeOriginalPayload(bool lateRelease)
    {
        IOriginalDsc1Runtime runtime = DispatchProxy.Create<IOriginalDsc1Runtime, Dsc1NativeSpy>();
        Dsc1NativeSpy spy = (Dsc1NativeSpy)(object)runtime; spy.Native = new();
        IUninvokedDsc1Provider provider = DispatchProxy.Create<IUninvokedDsc1Provider, UninvokedDsc1ProviderSpy>();
        UninvokedDsc1ProviderSpy providerSpy = (UninvokedDsc1ProviderSpy)(object)provider;
        providerSpy.Inner = new HybridCpuPlatformAuthorityProvider(runtime);
        BudgetAccountHandle budget = default;
        Scenario scenario = CreateScenario(provider, 2691, 26910, 32, (kernel, subject) =>
        {
            ProcessHandle admin = TestFixtures.Create(kernel, 2692, 26920).Handle;
            CapabilityId cap = kernel.MintCapability(new(26920), admin, ResourceKind.KernelService,
                CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
            budget = kernel.AdmitProcessBudget(admin, cap, subject, "terminal-custody",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value!.ProcessBudget;
        }, originalSlices: true);
        ProcessHandle foreignOwner = TestFixtures.Create(scenario.Kernel, 2693, 26930).Handle;
        OwnedBuffer<byte> foreign = scenario.Kernel.AllocateBuffer<byte>(foreignOwner, 8).Value!;
        foreign.Span.Fill(0x4C);
        ulong Used() => scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        scenario.Source.Span.Fill(0x6B);
        KernelResult<PlatformDsc1CopySubmission> submission = SubmitWhole(scenario); Assert.True(submission.IsSuccess, submission.Message);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary routes = (IDictionary)scenario.Kernel.PlatformAuthority.GetType().GetField("_dsc1Operations", flags)!.GetValue(scenario.Kernel.PlatformAuthority)!;
        object owner = routes[submission.Value.SubmissionId]!;
        PropertyInfo property = owner.GetType().GetProperty("OriginalBackingObservation")!;
        PlatformOriginalDsc1CopyBackingObservation original = (PlatformOriginalDsc1CopyBackingObservation)property.GetValue(owner)!;
        PlatformOriginalDsc1CopyBackingObservation copy = original with { };
        int reads = 0;
        providerSpy.BeforeSource = () => { if (++reads >= (lateRelease ? 2 : 1)) property.SetValue(owner, copy); };
        KernelResult<PlatformDsc1CopyReceipt> refused = scenario.Kernel.ObservePlatformDsc1Copy(scenario.Subject, submission.Value);
        Assert.False(refused.IsSuccess);
        Assert.Equal(1, spy.Copies);
        Assert.Single(routes);
        Assert.Equal(128UL, Used());
        Assert.All(foreign.Span.ToArray(), b => Assert.Equal((byte)0x4C, b));
        Assert.Throws<InvalidOperationException>(() => { _ = scenario.Destination.Span.Length; });
        providerSpy.BeforeSource = null; property.SetValue(owner, original);
        KernelResult<PlatformDsc1CopyReceipt> recovered = scenario.Kernel.ObservePlatformDsc1Copy(scenario.Subject, submission.Value);
        Assert.True(recovered.IsSuccess, recovered.Message);
        Assert.Equal(1, spy.Copies);
        Assert.Equal(64UL, Used());
        Assert.All(scenario.Destination.Span.ToArray(), b => Assert.Equal(recovered.Value.OutputPublished ? (byte)0x6B : (byte)0, b));
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        Assert.Equal(0UL, Used());
        Assert.All(scenario.Kernel.Regions.Snapshot().Where(r => r.Handle == scenario.Source.Handle || r.Handle == scenario.Destination.Handle), r => Assert.Equal(RegionState.Released, r.State));
        Assert.All(foreign.Span.ToArray(), b => Assert.Equal((byte)0x4C, b));
    }
}
