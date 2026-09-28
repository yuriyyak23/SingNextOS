using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase16SipJobResourceDifferentialTests
{
    private const string Evidence = "65e85181878847e622489506de5a939828a1235562da4e4d5abf26098f72760c";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryAndFusedTransportProduceEquivalentLiveOwnerTrace(bool providerAmbiguous)
    {
        var ordinary = Budget();
        var fused = Budget();
        var ordinaryTrace = new List<BudgetReservationState>();
        var fusedTrace = new List<BudgetReservationState>();
        var ordinaryTransportCalls = 0;
        var fusedTransportCalls = 0;

        var ordinaryResult = SipJobResourceTransportExecutor.Execute(
            new VNextFeatureGateAuthority(), null, possibleSubmit: false,
            () => { ordinaryTransportCalls++; return KernelResult.Ok(); },
            () => RunOwnerBoundary(ordinary, providerAmbiguous, ordinaryTrace));

        var gates = EnabledGates();
        var lease = gates.TryAcquire(SipJobResourceTransportExecutor.GateName).Value!;
        var fusedResult = SipJobResourceTransportExecutor.Execute(
            gates, lease, possibleSubmit: false,
            () => { fusedTransportCalls++; return KernelResult.Ok(); },
            () => RunOwnerBoundary(fused, providerAmbiguous, fusedTrace));

        Assert.Equal(ordinaryResult.Error, fusedResult.Error);
        Assert.Equal(SipJobResourceTransportMode.OrdinarySip, ordinaryResult.Mode);
        Assert.Equal(SipJobResourceTransportMode.FusedTransport, fusedResult.Mode);
        Assert.Equal(1, ordinaryTransportCalls);
        Assert.Equal(0, fusedTransportCalls);
        Assert.Equal(ordinaryTrace, fusedTrace);
        Assert.Equal(ordinary.Authority.Query(ordinary.ProcessAccount).Value!.Usage,
            fused.Authority.Query(fused.ProcessAccount).Value!.Usage);
    }

    [Fact]
    public void GateRollbackFallsBackBeforeSubmitButNeverResubmitsAfterPossibleSubmit()
    {
        var gates = EnabledGates();
        var lease = gates.TryAcquire(SipJobResourceTransportExecutor.GateName).Value!;
        Assert.True(gates.Apply(2, VNextFeatureGateConfiguration.DefaultOff(3)).IsSuccess);
        var transportCalls = 0;
        var ownerCalls = 0;

        var fallback = SipJobResourceTransportExecutor.Execute(gates, lease, possibleSubmit: false,
            () => { transportCalls++; return KernelResult.Ok(); },
            () => { ownerCalls++; return KernelResult.Ok(); });
        var quarantined = SipJobResourceTransportExecutor.Execute(gates, lease, possibleSubmit: true,
            () => { transportCalls++; return KernelResult.Ok(); },
            () => { ownerCalls++; return KernelResult.Ok(); });

        Assert.True(fallback.IsSuccess);
        Assert.Equal(SipJobResourceTransportMode.OrdinarySip, fallback.Mode);
        Assert.Equal(KernelError.Quarantined, quarantined.Error);
        Assert.Equal(SipJobResourceTransportMode.Quarantined, quarantined.Mode);
        Assert.Equal(1, transportCalls);
        Assert.Equal(1, ownerCalls);
    }

    [Fact]
    public async Task ParallelBranchesUseSplitLeasesAndNeverAmplifyCapacity()
    {
        var context = Budget();
        var parent = context.Authority.Reserve(context.Process, [Amount(10)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        var child = context.Authority.SplitLease(context.Process, parent, [Amount(4)]).Value!.Reservation;
        using var start = new ManualResetEventSlim(false);
        var branches = new[] { parent, child }.Select(lease => Task.Run(() =>
        {
            start.Wait();
            Assert.True(context.Authority.BindLease(context.Process, lease).IsSuccess);
            Assert.True(context.Authority.BeginConsumption(context.Process, lease).IsSuccess);
            return context.Authority.SettleLease(context.Process, lease,
                context.Authority.Query(lease).Value!.Amounts);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(branches);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Message));
        Assert.Equal(10UL, Used(context));
        Assert.All(results, result => Assert.Equal(BudgetReservationState.Released, result.Value!.State));
    }

    [Fact]
    public void PlannerAndExecutorRemainTransportPolicyWithoutAuthorityFields()
    {
        var fields = typeof(SipJobResourceTransportExecutor).GetFields(
            global::System.Reflection.BindingFlags.Static |
            global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic |
            global::System.Reflection.BindingFlags.Public);
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(ResourceBudgetAuthority) ||
            field.FieldType == typeof(CapabilityAuthority) ||
            field.FieldType == typeof(ExternalOperationAuthority));
        Assert.Equal(SipJobBarrierDisposition.MaterializeOrdinarySip,
            SipJobBarrierPlanner.Classify(1, SipJobBarrierClass.ResourceConsumption).Disposition);
    }

    private static KernelResult RunOwnerBoundary(
        BudgetContext context,
        bool providerAmbiguous,
        List<BudgetReservationState> trace)
    {
        var lease = context.Authority.Reserve(context.Process, [Amount(10)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        trace.Add(context.Authority.Query(lease).Value!.State);
        trace.Add(context.Authority.BindLease(context.Process, lease).Value!.State);
        trace.Add(context.Authority.BeginConsumption(context.Process, lease).Value!.State);
        if (providerAmbiguous)
        {
            trace.Add(context.Authority.QuarantineLease(context.Process, lease).Value!.State);
            trace.Add(context.Authority.ReconcileLease(context.Process, lease).Value!.State);
        }
        trace.Add(context.Authority.SettleLease(context.Process, lease, [Amount(6)]).Value!.State);
        return KernelResult.Ok();
    }

    private static VNextFeatureGateAuthority EnabledGates()
    {
        var authority = new VNextFeatureGateAuthority();
        var configuration = new VNextFeatureGateConfiguration(
            VNextFeatureGateConfiguration.CurrentVersion, 2,
            VNextFeatureGateAuthority.QualifiedHostContour, "host-test",
            SingPlus.Platform.PlatformResourceContract.ContractVersion,
            "Windows-x64/.NET-11/JIT", Evidence,
            ["FG-VNX-HOST-RESOURCE-ADAPTER", "FG-VNX-SIPJOB-RESOURCE"]);
        Assert.True(authority.Apply(1, configuration).IsSuccess);
        return authority;
    }

    private static BudgetContext Budget()
    {
        var authority = new ResourceBudgetAuthority();
        Assert.True(authority.ConfigureSystem([Amount(100)]).IsSuccess);
        var service = authority.CreateChild(authority.SystemBudget, BudgetAccountLevel.Service,
            "p16", [Amount(100)]).Value!.Account;
        var processAccount = authority.CreateChild(service, BudgetAccountLevel.ProcessDomain,
            "p16-process", [Amount(100)]).Value!.Account;
        var process = new ProcessHandle(new ProcessId(89), 7);
        Assert.True(authority.AttachProcess(process, processAccount).IsSuccess);
        return new BudgetContext(authority, process, processAccount);
    }

    private static BudgetAmount Amount(ulong value) =>
        new(ServiceBudgetDimension.ComputeTimeNanoseconds, value);

    private static ulong Used(BudgetContext context) => Assert.Single(
        context.Authority.Query(context.ProcessAccount).Value!.Usage,
        usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;

    private sealed record BudgetContext(
        ResourceBudgetAuthority Authority,
        ProcessHandle Process,
        BudgetAccountHandle ProcessAccount);
}
