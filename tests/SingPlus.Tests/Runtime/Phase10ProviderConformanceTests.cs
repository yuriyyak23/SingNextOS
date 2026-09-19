using System.Security.Cryptography;
using System.Diagnostics;
using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Tests.Conformance;
using Xunit.Abstractions;

namespace SingPlus.Tests.Runtime;

public sealed class Phase10ProviderConformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void NativeExternalOperationModelPassesReusableFaultMatrix()
    {
        var timer = Stopwatch.StartNew();
        var results = new ProviderConformanceSuite(() => new NativeModelDriver()).RunAll();
        timer.Stop();
        output.WriteLine("Provider conformance baseline; family=generic-external-operation-model; scenarios={0}; repeated-runs=2; elapsed-ticks={1}; frequency={2}",
            results.Count, timer.ElapsedTicks, Stopwatch.Frequency);
        Assert.Equal(Enum.GetValues<ProviderFaultMode>().Length, results.Count);
        Assert.All(results, result => Assert.Equal("generic-external-operation-model", result.Family));
    }

    [Fact]
    public void HybridCpuExecutableAdapterBoundaryPassesSameFaultMatrix()
    {
        var timer = Stopwatch.StartNew();
        var results = new ProviderConformanceSuite(() => new HybridAdapterDriver()).RunAll();
        timer.Stop();
        output.WriteLine("Provider conformance baseline; family=external-runtime-executable-adapter; scenarios={0}; repeated-runs=2; elapsed-ticks={1}; frequency={2}",
            results.Count, timer.ElapsedTicks, Stopwatch.Frequency);
        Assert.Equal(Enum.GetValues<ProviderFaultMode>().Length, results.Count);
        Assert.All(results, result => Assert.Equal("external-runtime-executable-adapter", result.Family));
    }

    [Fact]
    public void FaultPlanTypesAreTestOnlyAndCannotBecomeRuntimeAuthority()
    {
        Assert.Equal("SingPlus.Tests", typeof(ProviderFaultPlan).Assembly.GetName().Name);
        Assert.DoesNotContain(typeof(RuntimeKernel).Assembly.GetTypes(), type =>
            type.Name.Contains("FaultPlan", StringComparison.Ordinal));
    }

    private abstract class DriverBase : IProviderConformanceDriver
    {
        private static readonly byte[] Image = [0x10, 0xAA];
        public abstract string Family { get; }
        public abstract ProviderConformanceObservation Execute(ProviderScenario scenario);

        protected static DriverContext Context(ProviderScenario scenario, ulong familyOffset)
        {
            var kernel = new RuntimeKernel();
            var ordinal = (ulong)scenario.Fault.Mode + familyOffset;
            var processId = 1000UL + ordinal;
            var domainId = 10000UL + ordinal;
            var process = TestFixtures.Manifest(processId, domainId, 1, $"conformance-{familyOffset}-{scenario.Fault.Mode}");
            var manifest = new ServiceManifestV1(new($"conformance-{familyOffset}-{scenario.Fault.Mode}"), new("1"), Digest(Image), process,
                budgetRequests:
                [
                    new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                    new(ServiceBudgetDimension.ExternalOperations, 1),
                    new(ServiceBudgetDimension.CheckpointStorageBytes, 4096),
                ],
                checkpointPolicy: new(ServiceCheckpointMode.OperatorRequested),
                telemetryPolicy: new(ServiceTelemetryVisibility.Self, 4096));
            var component = kernel.AdmitComponent(new(manifest, Image)).Value!;
            var input = kernel.AllocateBuffer<byte>(component.Process, 8).Value!;
            var output = kernel.AllocateBuffer<byte>(component.Process, 8).Value!;
            input.Span.Fill(0x5A);
            var admin = TestFixtures.Create(kernel, 5000 + ordinal, 15000 + ordinal, identity: $"checkpoint-admin-{familyOffset}-{scenario.Fault.Mode}").Handle;
            var checkpointCapability = kernel.MintCapability(new(15000 + ordinal), admin, ResourceKind.KernelService,
                CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
            return new(kernel, component, input, output, admin, checkpointCapability);
        }

        protected static ProviderConformanceObservation BeforeEffect(
            string family,
            ProviderScenario scenario,
            DriverContext context,
            bool rejected)
        {
            var uses = context.Kernel.Regions.SnapshotUses().Count(use => use.State == RegionUseState.Active);
            var budget = Used(context, ServiceBudgetDimension.ExternalOperations);
            return new(family, scenario.Fault.Mode,
                new(rejected, false, true, uses != 0, budget != 0),
                new(false, false, false, uses == 0, budget == 0, true),
                new(false, true));
        }

        protected static ProviderConformanceObservation AfterEffect(
            string family,
            ProviderScenario scenario,
            DriverContext context,
            ExternalOperationHandle operation,
            bool staleRejected)
        {
            var pinsRetained = context.Kernel.Regions.SnapshotUses().Any(use => use.State != RegionUseState.Released);
            var charged = Used(context, ServiceBudgetDimension.ExternalOperations) == 1;
            var publicationSuppressed = context.Output.Span.ToArray().All(value => value == 0);
            var current = context.Kernel.QueryExternalOperation(context.Component.Process, operation).Value!;
            var quarantined = current.Disposition is ExternalOperationDisposition.ProviderLost or
                ExternalOperationDisposition.Discarded or ExternalOperationDisposition.Faulted;
            var checkpoint = context.Kernel.CreateOrdinaryCheckpoint(context.Admin, context.CheckpointCapability,
                context.Component.Identity, new byte[] { 1 }, [new(context.Input), new(context.Output)]);
            var drained = context.Kernel.DrainComponent(context.Component.Identity);
            var blocked = !drained.IsSuccess || !drained.Value!.Reclaimable;
            var contained = context.Kernel.ReleaseExternalOperation(context.Component.Process, operation,
                new(false, true, true));
            Assert.True(contained.IsSuccess, $"{family}/{scenario.Fault.Mode}: {contained.Error}: {contained.Message}");
            var reclaimed = context.Kernel.ObserveComponentTeardown(context.Component.Identity);
            var activePins = context.Kernel.Regions.SnapshotUses().Count(use => use.State != RegionUseState.Released);
            var remainingBudget = Used(context, ServiceBudgetDimension.ExternalOperations);
            return new(family, scenario.Fault.Mode,
                new(false, true, publicationSuppressed, pinsRetained, charged),
                new(blocked, quarantined, checkpoint.Error == KernelError.CheckpointBlocked,
                    activePins == 0, remainingBudget == 0, reclaimed.IsSuccess && reclaimed.Value!.Reclaimable),
                new(staleRejected, true));
        }

        protected static ulong Used(DriverContext context, ServiceBudgetDimension dimension) =>
            Assert.Single(context.Kernel.QueryBudget(context.Component.ProcessBudget).Value!.Usage,
                usage => usage.Dimension == dimension).Used;

        protected static string Digest(ReadOnlySpan<byte> bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        protected static bool RejectMalformedReceipt(
            Hc.ExternalOperationRequest expected,
            Hc.ExternalOperationReceipt receipt) =>
            receipt.Request != expected ||
            receipt.Request.ContractVersion != Hc.ExternalOperationContract.Version ||
            !Enum.IsDefined(receipt.Outcome) ||
            !Enum.IsDefined(receipt.Stage);
    }

    private sealed class NativeModelDriver : DriverBase
    {
        public override string Family => "generic-external-operation-model";

        public override ProviderConformanceObservation Execute(ProviderScenario scenario)
        {
            var context = Context(scenario, 100);
            var requests = new[]
            {
                new OperationRegionUseRequest(
                    scenario.Fault.Mode == ProviderFaultMode.FailBeforeEffect
                        ? new(new(ulong.MaxValue), new(1))
                        : context.Input.Handle,
                    RegionUseMode.ReadOnly, new(0, 8)),
                new OperationRegionUseRequest(context.Output.Handle, RegionUseMode.StagedOutput, new(0, 8)),
            };
            var prepared = context.Kernel.PrepareExternalOperation(context.Component.Process, requests,
                ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged);
            if (scenario.Fault.Mode == ProviderFaultMode.FailBeforeEffect)
            {
                var rejected = !prepared.IsSuccess || !context.Kernel.AdmitExternalOperation(
                    context.Component.Process, prepared.Value!.Operation, new(1, 1, 1, 1)).IsSuccess;
                return BeforeEffect(Family, scenario, context, rejected);
            }
            Assert.True(prepared.IsSuccess, prepared.Message);
            var operation = prepared.Value!.Operation;
            var dependencies = new OperationDependencySnapshot(1, 1, 1, 1);
            Assert.True(context.Kernel.AdmitExternalOperation(context.Component.Process, operation, dependencies).IsSuccess);
            var binding = context.Kernel.RecordExternalOperationSubmission(context.Component.Process, operation, dependencies).Value!;
            var staleRejected = false;

            if (scenario.Fault.Mode == ProviderFaultMode.ResetDuringVisible)
            {
                Assert.True(context.Kernel.RecordExternalOperationCompletion(context.Component.Process,
                    new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
                Assert.True(context.Kernel.RecordExternalOperationVisibility(context.Component.Process,
                    new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
            }
            if (scenario.Fault.Mode == ProviderFaultMode.ReturnMalformedReceipt)
            {
                var expected = new Hc.ExternalOperationRequest(new(new(Guid.NewGuid()), new(1)),
                    new(new(Guid.NewGuid()), new(1)), Hc.ExternalOperationContract.Version,
                    new(Hc.ExternalOperationContract.Version, [Guid.NewGuid()]), new(Guid.NewGuid()),
                    Hc.ExternalEffectClass.NonIdempotent, Hc.ExternalVisibilityRequirement.StagedOutput,
                    Hc.ExternalCancellationMode.ExactAcknowledgement);
                var malformedRequest = new Hc.ExternalOperationRequest(
                    new(new(Guid.NewGuid()), expected.Operation.Generation), expected.Scope,
                    expected.ContractVersion, expected.Generations, expected.Correlation,
                    expected.EffectClass, expected.VisibilityRequirement, expected.CancellationMode);
                staleRejected = RejectMalformedReceipt(expected,
                    new Hc.ExternalOperationReleaseReceipt(malformedRequest, Hc.ExternalRuntimeOutcome.Closed));
            }
            if (scenario.Fault.Mode is ProviderFaultMode.ReturnStaleClosureReceipt or ProviderFaultMode.ChangeGeneration)
            {
                var stale = operation with { Generation = new(operation.Generation.Value + 1) };
                staleRejected = context.Kernel.ReleaseExternalOperation(context.Component.Process, stale, new(true, false)).Error == KernelError.StaleGeneration;
            }
            if (scenario.Fault.Mode is ProviderFaultMode.DelayClosure or ProviderFaultMode.FailClosure)
                Assert.False(context.Kernel.ReleaseExternalOperation(context.Component.Process, operation, new(false, false)).IsSuccess);
            if (scenario.Fault.Mode == ProviderFaultMode.ThrowAfterAcceptance)
            {
                try { throw new InvalidOperationException("deterministic injected post-acceptance failure"); }
                catch (InvalidOperationException) { }
            }
            Assert.True(context.Kernel.RecordExternalOperationProviderLoss(context.Component.Process, operation).IsSuccess);
            return AfterEffect(Family, scenario, context, operation, staleRejected);
        }
    }

    private sealed class HybridAdapterDriver : DriverBase
    {
        public override string Family => "external-runtime-executable-adapter";

        public override ProviderConformanceObservation Execute(ProviderScenario scenario)
        {
            var context = Context(scenario, 200);
            var generations = new Hc.ExternalGenerationSet(Hc.ExternalOperationContract.Version,
                [Guid.Parse("5aa6b3e1-1865-4f5a-a498-e401616d8f20")]);
            var provider = new HybridCpuExternalOperationProvider(context.Kernel, context.Component.Process,
            [
                new(scenario.Fault.Mode == ProviderFaultMode.FailBeforeEffect
                        ? new(new(ulong.MaxValue), new(1))
                        : context.Input.Handle,
                    RegionUseMode.ReadOnly, new(0, 8)),
                new(context.Output.Handle, RegionUseMode.StagedOutput, new(0, 8)),
            ], new(1, 1, 1, 1), new("conformance"),
                new(new(Guid.Parse("229b207b-feef-450d-b2fc-e498bfdb76df")), new(1)), generations);
            var semantic = new Hc.ExternalOperationSemanticRequest(
                Hc.ExternalOperationContract.Version,
                new(Guid.Parse("4bcf3f23-e366-4849-a5d6-48dd062ad932")), Hc.ExternalEffectClass.NonIdempotent,
                Hc.ExternalVisibilityRequirement.StagedOutput, Hc.ExternalCancellationMode.ExactAcknowledgement,
                Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);
            var admitted = provider.Admit(semantic);
            if (scenario.Fault.Mode == ProviderFaultMode.FailBeforeEffect)
                return BeforeEffect(Family, scenario, context, admitted.Status == Hc.ExternalOperationProviderPollStatus.Faulted);
            var request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(admitted.Receipt).Request;
            Assert.Equal(Hc.ExternalOperationStage.Submitted, provider.Submit(request).Receipt!.Stage);
            var operation = Assert.Single(context.Kernel.ExternalOperations.InspectionSnapshot()).Operation;
            var staleRejected = false;

            if (scenario.Fault.Mode == ProviderFaultMode.ResetDuringVisible)
            {
                Assert.True(provider.RecordDeviceCompletion(request).IsSuccess);
                Assert.True(provider.RecordVisibility(request).IsSuccess);
            }
            if (scenario.Fault.Mode == ProviderFaultMode.ChangeGeneration)
            {
                provider.Reconfigure(new(Hc.ExternalOperationContract.Version, [Guid.Parse("65d1f667-60b5-4ed2-a21c-39cd1b57ff80")]));
                staleRejected = provider.Poll(request).Status == Hc.ExternalOperationProviderPollStatus.Stale;
            }
            if (scenario.Fault.Mode == ProviderFaultMode.ReturnMalformedReceipt)
            {
                var malformedRequest = new Hc.ExternalOperationRequest(new(new(Guid.NewGuid()), request.Operation.Generation), request.Scope,
                    request.ContractVersion, request.Generations, request.Correlation,
                    request.EffectClass, request.VisibilityRequirement, request.CancellationMode);
                var malformedReceipt = new Hc.ExternalOperationReleaseReceipt(malformedRequest, Hc.ExternalRuntimeOutcome.Closed);
                staleRejected = RejectMalformedReceipt(request, malformedReceipt);
            }
            if (scenario.Fault.Mode == ProviderFaultMode.ReturnStaleClosureReceipt)
            {
                var stale = new Hc.ExternalOperationRequest(
                    new(new(Guid.NewGuid()), new(request.Operation.Generation.Value + 1)), request.Scope,
                    request.ContractVersion, request.Generations, request.Correlation,
                    request.EffectClass, request.VisibilityRequirement, request.CancellationMode);
                staleRejected = provider.Submit(stale).Status == Hc.ExternalOperationProviderPollStatus.Stale;
            }
            if (scenario.Fault.Mode is ProviderFaultMode.DelayClosure or ProviderFaultMode.FailClosure)
                Assert.False(provider.Release(request, providerResourcesClosed: false).IsSuccess);
            if (scenario.Fault.Mode == ProviderFaultMode.ThrowAfterAcceptance)
            {
                try { throw new InvalidOperationException("deterministic executable-adapter fault"); }
                catch (InvalidOperationException) { }
            }
            Assert.True(context.Kernel.RecordExternalOperationProviderLoss(context.Component.Process, operation).IsSuccess);
            return AfterEffect(Family, scenario, context, operation, staleRejected);
        }
    }

    private sealed record DriverContext(
        RuntimeKernel Kernel,
        ComponentLifecycleSnapshot Component,
        OwnedBuffer<byte> Input,
        OwnedBuffer<byte> Output,
        ProcessHandle Admin,
        CapabilityId CheckpointCapability);
}
