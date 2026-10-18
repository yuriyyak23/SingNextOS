using System.Collections.Immutable;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class SipJobProtectedTransportExecutorTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string C = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Evidence = "65e85181878847e622489506de5a939828a1235562da4e4d5abf26098f72760c";

    [Fact]
    public void OrdinaryTransportRevalidatesExactLabelsBeforeLiveOwnerCommit()
    {
        var (plan, catalog) = Contour();
        var transportCalls = 0;
        var ownerCalls = 0;

        var result = SipJobResourceTransportExecutor.ExecuteProtected(
            new VNextFeatureGateAuthority(), null, false, plan, catalog, [Sidecar(plan)],
            () => { transportCalls++; return KernelResult.Ok(); },
            () => { ownerCalls++; return KernelResult.Ok(); });

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(SipJobResourceTransportMode.OrdinarySip, result.Transport.Mode);
        Assert.Equal(1, transportCalls);
        Assert.Equal(1, ownerCalls);
        var binding = Assert.Single(result.LabelBindings);
        Assert.Equal("edge", binding.EdgeId);
        Assert.Equal(Label(), binding.Label);
        Assert.False(Sidecar(plan).AuthorizesAccess);
    }

    [Fact]
    public void FusedTransportPreservesSameProtectedBindingAndSkipsOnlyTransportHop()
    {
        var (plan, catalog) = Contour();
        var gates = EnabledGates();
        var lease = gates.TryAcquire(SipJobResourceTransportExecutor.GateName).Value!;
        var transportCalls = 0;
        var ownerCalls = 0;

        var result = SipJobResourceTransportExecutor.ExecuteProtected(
            gates, lease, false, plan, catalog, [Sidecar(plan)],
            () => { transportCalls++; return KernelResult.Ok(); },
            () => { ownerCalls++; return KernelResult.Ok(); });

        Assert.True(result.IsSuccess, result.Detail);
        Assert.Equal(SipJobResourceTransportMode.FusedTransport, result.Transport.Mode);
        Assert.Equal(0, transportCalls);
        Assert.Equal(1, ownerCalls);
        Assert.Equal(Label(), Assert.Single(result.LabelBindings).Label);
    }

    [Fact]
    public void MissingOrDuplicateLabelRejectsBeforeTransportAndOwnerCallbacks()
    {
        var (plan, catalog) = Contour();
        var callbacks = 0;
        var duplicate = Sidecar(plan);

        var missing = SipJobResourceTransportExecutor.ExecuteProtected(
            new VNextFeatureGateAuthority(), null, false, plan, catalog, [],
            Callback, Callback);
        var changed = SipJobResourceTransportExecutor.ExecuteProtected(
            new VNextFeatureGateAuthority(), null, false, plan, catalog, [duplicate, Sidecar(plan)],
            Callback, Callback);

        Assert.Equal(SipJobProtectedLabelError.MissingOrDuplicateSidecar, missing.LabelError);
        Assert.Equal(SipJobProtectedLabelError.MissingOrDuplicateSidecar, changed.LabelError);
        Assert.Equal(KernelError.ProjectionDenied, missing.Transport.Error);
        Assert.Equal(KernelError.ProjectionDenied, changed.Transport.Error);
        Assert.Equal(0, callbacks);

        KernelResult Callback() { callbacks++; return KernelResult.Ok(); }
    }

    [Fact]
    public void UnverifiedPlanRejectsBeforeLabelOrOwnerWork()
    {
        var (plan, catalog) = Contour();
        var callbacks = 0;
        var tampered = new SipJobPlanDescriptor(plan.FormatVersion, plan.Stages, plan.Edges,
            plan.DeclaredGateSet, new string('f', 64));

        var result = SipJobResourceTransportExecutor.ExecuteProtected(
            new VNextFeatureGateAuthority(), null, false, tampered, catalog, [Sidecar(tampered)],
            Callback, Callback);

        Assert.Equal(SipJobPlanError.DigestMismatch, result.PlanError);
        Assert.Equal(KernelError.InvalidManifest, result.Transport.Error);
        Assert.Equal(0, callbacks);

        KernelResult Callback() { callbacks++; return KernelResult.Ok(); }
    }

    [Fact]
    public void OwnerFailureIsPropagatedWithoutPromotingLabelToAuthority()
    {
        var (plan, catalog) = Contour();

        var result = SipJobResourceTransportExecutor.ExecuteProtected(
            new VNextFeatureGateAuthority(), null, false, plan, catalog, [Sidecar(plan)],
            KernelResult.Ok,
            () => KernelResult.Fail(KernelError.CapabilityRevoked, "live owner denied"));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, result.Transport.Error);
        Assert.Equal(Label(), Assert.Single(result.LabelBindings).Label);
    }

    private static (SipJobPlanDescriptor Plan, SipJobClosedCatalog Catalog) Contour()
    {
        var first = Stage("first", "contract-a", "operation-a", "value-a", A, "value-b", B,
            SipJobInvocationObservability.HiddenIntermediate);
        var final = Stage("final", "contract-b", "operation-b", "value-b", B, "value-c", C,
            SipJobInvocationObservability.FinalPublication);
        var edge = new SipJobEdgeDescriptor(1, "edge", "first", "final",
            SipJobEdgeKind.ClosedCopiedValue, "value-b", B, "None", "ClosedCopy",
            "InternalOnly", SipJobBarrierClass.None, "JobRun");
        var plan = new SipJobPlanDescriptor(1, [first, final], [edge],
            [SipJobPlanVerifier.LinearGate], string.Empty).WithDigest();
        var allowed = ImmutableHashSet<SipJobAuthoritySourceClass>.Empty;
        var catalog = new SipJobClosedCatalog([
            new(first.ContractId, first.ContractDigest, first.OperationId,
                first.RequestSchemaId, first.RequestSchemaDigest, first.ResponseSchemaId,
                first.ResponseSchemaDigest, first.GeneratedThunkId, first.ThunkDigest, allowed),
            new(final.ContractId, final.ContractDigest, final.OperationId,
                final.RequestSchemaId, final.RequestSchemaDigest, final.ResponseSchemaId,
                final.ResponseSchemaDigest, final.GeneratedThunkId, final.ThunkDigest, allowed),
        ]);
        return (plan, catalog);
    }

    private static SipJobStageDescriptor Stage(string id, string contract, string operation,
        string requestSchema, string requestDigest, string responseSchema, string responseDigest,
        SipJobInvocationObservability observability) =>
        new(1, id, contract, A, operation, requestSchema, requestDigest,
            responseSchema, responseDigest, "thunk-" + id, C, [], [],
            "protocol-none", observability, "cancel-job", SipJobEffectClass.None,
            SipJobExecutionClass.ManagedDefault, SipJobStageMode.Synchronous);

    private static ProtectedValueLabelSidecarV1 Sidecar(SipJobPlanDescriptor plan) =>
        new(1, plan.PlanDigest, "edge", B, Label(), 11);

    private static DataLabelV1 Label() =>
        new(1, ConfidentialityClassV1.Restricted, IntegrityClassV1.Validated);

    private static VNextFeatureGateAuthority EnabledGates()
    {
        var authority = new VNextFeatureGateAuthority();
        var configuration = new VNextFeatureGateConfiguration(
            VNextFeatureGateConfiguration.CurrentVersion, 2,
            VNextFeatureGateAuthority.QualifiedHostContour, "host-test",
            SingPlus.Platform.PlatformResourceContract.ContractVersion,
            "Windows-x64/.NET-11/JIT", Evidence,
            ["FG-VNX-HOST-RESOURCE-ADAPTER", SipJobResourceTransportExecutor.GateName]);
        Assert.True(authority.Apply(1, configuration).IsSuccess);
        return authority;
    }
}
