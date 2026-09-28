using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class Phase02ServiceSupervisorTests
{
    [Fact]
    public void HardDependenciesStartFirstAndBindExactGeneration()
    {
        var scenario = CreateSupervisor();
        var provider = Definition("provider", 201, 2001, Contract("Provider"));
        var dependent = Definition("dependent", 202, 2002, Contract("Dependent"),
            dependencies: [new(provider.Admission.Manifest.ProvidedContracts[0].Contract, ServiceDependencyKind.Hard)]);
        Assert.True(scenario.Supervisor.Register(dependent).IsSuccess);
        Assert.True(scenario.Supervisor.Register(provider).IsSuccess);

        var started = scenario.Supervisor.StartAll();

        Assert.True(started.IsSuccess, started.Message);
        Assert.Equal(new[] { provider.Identity, dependent.Identity }, started.Value!.Select(receipt => receipt.Request.Identity));
        var dependentSnapshot = scenario.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, dependentSnapshot.State);
        var binding = Assert.Single(dependentSnapshot.Dependencies);
        Assert.Equal(scenario.Supervisor.Query(provider.Identity).Value!.Instance, binding.Provider);
        Assert.Equal(dependentSnapshot.Instance, binding.Consumer);
    }

    [Fact]
    public void OptionalDependencyAbsenceIsExplicitDegradation()
    {
        var scenario = CreateSupervisor();
        var service = Definition("optional", 203, 2003, Contract("OptionalConsumer"),
            dependencies: [new(Contract("Absent"), ServiceDependencyKind.Optional)]);
        Assert.True(scenario.Supervisor.Register(service).IsSuccess);

        var started = scenario.Supervisor.Start(service.Identity);

        Assert.True(started.IsSuccess, started.Message);
        Assert.Equal(ServiceLifecycleState.Degraded, started.Value!.Snapshot.State);
        Assert.Equal(ServiceHealthState.Degraded, started.Value.Snapshot.Health!.State);
        Assert.Empty(started.Value.Snapshot.Dependencies);
    }

    [Fact]
    public void OptionalDependencyLateArrivalUsesExplicitSessionRebind()
    {
        var scenario = CreateSupervisor();
        var providerContract = Contract("LateProvider");
        var dependent = Definition("late-dependent", 213, 2013, Contract("LateDependent"),
            dependencies: [new(providerContract, ServiceDependencyKind.Optional)]);
        Assert.True(scenario.Supervisor.Register(dependent).IsSuccess);
        Assert.True(scenario.Supervisor.Start(dependent.Identity).IsSuccess);
        Assert.Equal(ServiceLifecycleState.Degraded, scenario.Supervisor.Query(dependent.Identity).Value!.State);
        var provider = Definition("late-provider", 214, 2014, providerContract);

        Assert.True(scenario.Supervisor.Register(provider).IsSuccess);
        Assert.True(scenario.Supervisor.Start(provider.Identity).IsSuccess);

        var rebound = scenario.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, rebound.State);
        Assert.Single(rebound.Dependencies);
        Assert.Single(scenario.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
    }

    [Fact]
    public void DependencyCycleIsRejectedBeforeProcessEffects()
    {
        var scenario = CreateSupervisor();
        var contractA = Contract("CycleA");
        var contractB = Contract("CycleB");
        var a = Definition("cycle-a", 204, 2004, contractA, dependencies: [new(contractB, ServiceDependencyKind.Hard)]);
        var b = Definition("cycle-b", 205, 2005, contractB, dependencies: [new(contractA, ServiceDependencyKind.Hard)]);
        Assert.True(scenario.Supervisor.Register(a).IsSuccess);

        var rejected = scenario.Supervisor.Register(b);

        Assert.Equal(KernelError.InvalidManifest, rejected.Error);
        Assert.Equal(KernelError.ProcessNotFound, scenario.Kernel.Processes.Resolve(new(new(205), 1)).Error);
        Assert.Equal(KernelError.ComponentNotFound, scenario.Supervisor.Query(b.Identity).Error);
    }

    [Fact]
    public void PlannedReplacementUsesFreshProcessCapabilityAndStableServiceLineage()
    {
        var scenario = CreateSupervisor();
        var capability = new CapabilityRequirementV1(ResourceKind.KernelService, "managed-data", CapabilityRights.Read);
        var current = Definition("planned", 206, 2006, Contract("Planned"), capability: capability, issuer: scenario.IssuerDomain);
        Assert.True(scenario.Supervisor.Register(current).IsSuccess);
        var old = scenario.Supervisor.Start(current.Identity).Value!.Snapshot;
        var oldComponent = scenario.Kernel.QueryComponent(current.Identity).Value!;
        var oldCapability = Assert.Single(oldComponent.Capabilities);
        var next = Definition("planned", 206, 2006, Contract("Planned"), generation: 2, capability: capability, issuer: scenario.IssuerDomain);

        var replaced = scenario.Supervisor.Replace(old.Instance!.Value, next);

        Assert.True(replaced.IsSuccess, replaced.Message);
        Assert.True(replaced.Value!.FreshAdmission);
        var fresh = replaced.Value.Snapshot.Instance!.Value;
        Assert.Equal(old.Instance.Value.ServiceId, fresh.ServiceId);
        Assert.Equal(old.Instance.Value.Generation.Value + 1, fresh.Generation.Value);
        Assert.Equal(old.Instance.Value.Process.Generation + 1, fresh.Process.Generation);
        Assert.NotEqual(oldComponent.ManifestDigest, replaced.Value.Snapshot.ManifestDigest);
        Assert.False(scenario.Kernel.ValidateCapability(fresh.Process, oldCapability, CapabilityRights.Read).IsSuccess);
        var freshComponent = scenario.Kernel.QueryComponent(current.Identity).Value!;
        Assert.NotEqual(oldCapability, Assert.Single(freshComponent.Capabilities));
        Assert.NotEqual(oldComponent.ProcessBudget, freshComponent.ProcessBudget);
        Assert.NotEqual(oldComponent.ServiceBudget, freshComponent.ServiceBudget);
    }

    [Fact]
    public void DependencyReplacementRebindsDependentToFreshExactGeneration()
    {
        var clock = new SupervisorTimeProvider();
        var scenario = CreateSupervisor(clock);
        var providerContract = Contract("ReplaceableProvider");
        var provider = Definition("replaceable-provider", 207, 2007, providerContract,
            restart: new(ServiceRestartMode.OnFailure, 3, 10, 100, 1000),
            replacementFactory: generation => Plan("replaceable-provider", 207, 2007, providerContract, generation,
                restart: new(ServiceRestartMode.OnFailure, 3, 10, 100, 1000)));
        var dependent = Definition("rebound-dependent", 208, 2008, Contract("ReboundDependent"),
            dependencies: [new(providerContract, ServiceDependencyKind.Hard)]);
        Assert.True(scenario.Supervisor.Register(dependent).IsSuccess);
        Assert.True(scenario.Supervisor.Register(provider).IsSuccess);
        Assert.True(scenario.Supervisor.StartAll().IsSuccess);
        var oldBinding = Assert.Single(scenario.Supervisor.Query(dependent.Identity).Value!.Dependencies);

        Assert.True(scenario.Supervisor.ReportHealth(oldBinding.Provider, ServiceHealthState.Failed, "crash").IsSuccess);
        Assert.Equal(ServiceLifecycleState.Degraded, scenario.Supervisor.Query(dependent.Identity).Value!.State);
        clock.AdvanceMilliseconds(10);
        Assert.True(scenario.Supervisor.Observe(provider.Identity).IsSuccess);

        var rebound = scenario.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, rebound.State);
        var newBinding = Assert.Single(rebound.Dependencies);
        Assert.NotEqual(oldBinding.Provider, newBinding.Provider);
        Assert.Equal(oldBinding.Provider.ServiceId, newBinding.Provider.ServiceId);
        Assert.Equal(oldBinding.Provider.Generation.Value + 1, newBinding.Provider.Generation.Value);
        Assert.Equal(oldBinding.Provider.Process.Generation + 1, newBinding.Provider.Process.Generation);
    }

    [Fact]
    public void BoundedRestartPolicyEntersCrashLoop()
    {
        var clock = new SupervisorTimeProvider();
        var scenario = CreateSupervisor(clock);
        var contract = Contract("CrashLoop");
        var restart = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 1, 1, 1, 10_000);
        var definition = Definition("crash-loop", 209, 2009, contract, restart: restart,
            replacementFactory: generation => Plan("crash-loop", 209, 2009, contract, generation, restart: restart));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        var first = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(scenario.Supervisor.ReportHealth(first, ServiceHealthState.Failed, "first").IsSuccess);
        clock.AdvanceMilliseconds(1);
        var replacement = scenario.Supervisor.Observe(definition.Identity).Value!.Instance!.Value;

        Assert.True(scenario.Supervisor.ReportHealth(replacement, ServiceHealthState.Failed, "second").IsSuccess);

        var terminal = scenario.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.CrashLoop, terminal.State);
        Assert.Contains("Crash-loop", terminal.BlockingReason, StringComparison.Ordinal);
    }

    [Fact]
    public void SubmittedClosableOperationDrainsBeforeReplacement()
    {
        var clock = new SupervisorTimeProvider();
        var scenario = CreateSupervisor(clock);
        var contract = Contract("Closable");
        var restart = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        var definition = Definition("closable", 210, 2010, contract, restart: restart,
            replacementFactory: generation => Plan("closable", 210, 2010, contract, generation, restart: restart));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        var instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var operation = SubmitOperation(scenario.Kernel, instance.Process);

        Assert.True(scenario.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "crash with submitted work").IsSuccess);
        Assert.Equal(ServiceLifecycleState.Draining, scenario.Supervisor.Query(definition.Identity).Value!.State);
        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(instance.Process, new(operation.Binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        var closurePending = scenario.Supervisor.Observe(definition.Identity);
        Assert.True(closurePending.IsSuccess, closurePending.Message);
        Assert.Equal(ServiceLifecycleState.Draining, closurePending.Value!.State);
        Assert.True(scenario.Kernel.ReleaseExternalOperation(instance.Process, operation.Operation, new(true, false)).IsSuccess);
        var observed = scenario.Supervisor.Observe(definition.Identity);
        Assert.True(observed.IsSuccess, observed.Message);
        Assert.Equal(ServiceLifecycleState.RestartBackoff, observed.Value!.State);
        clock.AdvanceMilliseconds(1);
        var restarted = scenario.Supervisor.Observe(definition.Identity);
        Assert.True(restarted.IsSuccess, restarted.Message);
        Assert.Equal(ServiceLifecycleState.Ready, restarted.Value!.State);
        Assert.Equal(instance.Process.Generation + 1, restarted.Value.Instance!.Value.Process.Generation);
    }

    [Fact]
    public void AmbiguousExternalEffectQuarantinesAndBlocksReplacement()
    {
        var scenario = CreateSupervisor();
        var contract = Contract("Ambiguous");
        var definition = Definition("ambiguous", 211, 2011, contract,
            restart: new(ServiceRestartMode.OnFailure, 2, 1, 10, 1000),
            replacementFactory: generation => Plan("ambiguous", 211, 2011, contract, generation,
                restart: new(ServiceRestartMode.OnFailure, 2, 1, 10, 1000)));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        var instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var operation = SubmitOperation(scenario.Kernel, instance.Process);
        Assert.True(scenario.Kernel.RecordExternalOperationProviderLoss(instance.Process, operation.Operation).IsSuccess);

        var failure = scenario.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "provider response lost");

        Assert.True(failure.IsSuccess, failure.Message);
        var quarantined = scenario.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Quarantined, quarantined.State);
        Assert.Equal(ServiceHealthState.Quarantined, quarantined.Health!.State);
        var next = new ManagedServiceDefinition(Plan("ambiguous", 211, 2011, contract, 2), "ambiguous");
        var replacement = scenario.Supervisor.Replace(instance, next);
        Assert.False(replacement.IsSuccess);
        Assert.Equal(KernelError.ReplacementBlocked, replacement.Error);
        Assert.Equal(instance, scenario.Supervisor.Query(definition.Identity).Value!.Instance);
    }

    [Fact]
    public void DrainDeadlineWithAmbiguousExternalEffectQuarantinesWithoutReclaim()
    {
        var clock = new SupervisorTimeProvider();
        var scenario = CreateSupervisor(clock);
        var contract = Contract("DeadlineAmbiguous");
        var definition = Definition("deadline-ambiguous", 215, 2015, contract);
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        var instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var operation = SubmitOperation(scenario.Kernel, instance.Process);
        var scope = scenario.Kernel.CreateCancellationScope(instance.Process, new(1)).Value!;
        clock.AdvanceMilliseconds(1);

        var drained = scenario.Supervisor.Drain(
            instance, scope.Scope, ServiceDrainTimeoutPolicy.Quarantine);

        Assert.True(drained.IsSuccess, drained.Message);
        Assert.False(drained.Value!.ClosureProven);
        Assert.Equal(ServiceLifecycleState.Quarantined, drained.Value.Snapshot.State);
        Assert.Equal(ExternalOperationState.Submitted,
            scenario.Kernel.QueryExternalOperation(instance.Process, operation.Operation).Value!.State);
        Assert.Contains(scenario.Kernel.Regions.SnapshotUses(), use => use.State == RegionUseState.Active);
    }

    [Fact]
    public void StaleHandleAndRevokedSupervisorCapabilityFailClosed()
    {
        var scenario = CreateSupervisor();
        var definition = Definition("stale", 212, 2012, Contract("Stale"));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        var instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var stale = instance with { Generation = new(instance.Generation.Value + 1) };

        Assert.Equal(KernelError.StaleGeneration, scenario.Supervisor.ReportHealth(stale, ServiceHealthState.Ready, "forged").Error);
        Assert.True(scenario.Kernel.RevokeCapability(scenario.ControlCapability).IsSuccess);
        Assert.Equal(KernelError.SupervisorDenied, scenario.Supervisor.Drain(instance).Error);
        Assert.Equal(ComponentLifecycleState.Running, scenario.Kernel.QueryComponent(definition.Identity).Value!.State);
    }

    [Fact]
    public void OrdinaryKernelServiceCapabilityCannotCreateSupervisor()
    {
        var kernel = new RuntimeKernel();
        var (_, principal) = TestFixtures.Create(kernel, 901, 9001);
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        var ordinary = kernel.MintCapability(new DomainId(9001), principal, ResourceKind.KernelService,
            "ordinary-service", CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;

        var denied = kernel.CreateServiceSupervisor(principal, ordinary);

        Assert.Equal(KernelError.SupervisorDenied, denied.Error);
    }

    [Fact]
    public void HealthAndSupervisorDtosContainNoRootOrProviderAuthority()
    {
        var propertyTypes = typeof(ServiceHealthSnapshot).GetProperties().Select(property => property.PropertyType)
            .Concat(typeof(ServiceSupervisorSnapshot).GetProperties().Select(property => property.PropertyType))
            .ToArray();
        Assert.DoesNotContain(typeof(CapabilityId), propertyTypes);
        string[] forbiddenVocabulary =
        [
            "ProviderLease",
            "PlatformDomainBinding",
            "Cxl",
            "Bdf",
            "Hdm",
            "Dpa",
            "FabricManager",
            "HybridCpuLane",
            "Opcode",
            "ReplayCertificate"
        ];
        Assert.DoesNotContain(propertyTypes, type => forbiddenVocabulary.Any(forbidden =>
            (type.FullName ?? type.Name).Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
        Assert.False(new ServiceHealthSnapshot(default, ServiceHealthState.Ready, 1, "observation").AuthorizesMutation);
    }

    private static SupervisorScenario CreateSupervisor(TimeProvider? clock = null)
    {
        var kernel = new RuntimeKernel(null, clock);
        var (_, principal) = TestFixtures.Create(kernel, 900, 9000, identity: "supervisor-control");
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        var capability = kernel.MintCapability(new DomainId(9000), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        var supervisor = kernel.CreateServiceSupervisor(principal, capability, clock).Value!;
        return new(kernel, supervisor, principal, new DomainId(9000), capability);
    }

    private static ManagedServiceDefinition Definition(
        string name,
        ulong processId,
        ulong domainId,
        ServiceContractIdentity contract,
        ulong generation = 1,
        IEnumerable<ServiceDependencyRequirementV1>? dependencies = null,
        ServiceRestartPolicyV1? restart = null,
        Func<ulong, ComponentAdmissionPlan>? replacementFactory = null,
        CapabilityRequirementV1? capability = null,
        DomainId? issuer = null) =>
        new(Plan(name, processId, domainId, contract, generation, dependencies, restart, capability, issuer), name,
            ServiceReadinessPolicy.HardDependenciesBound, ServiceFailurePropagationPolicy.DegradeDependent, replacementFactory);

    private static ComponentAdmissionPlan Plan(
        string name,
        ulong processId,
        ulong domainId,
        ServiceContractIdentity contract,
        ulong generation = 1,
        IEnumerable<ServiceDependencyRequirementV1>? dependencies = null,
        ServiceRestartPolicyV1? restart = null,
        CapabilityRequirementV1? capability = null,
        DomainId? issuer = null)
    {
        var image = new byte[] { (byte)(processId & 0xff), (byte)generation };
        var provided = new ProvidedServiceManifestV1(name, contract);
        var capabilities = capability is { } requirement ? new[] { requirement } : [];
        var process = TestFixtures.Manifest(processId, domainId, generation, $"{name}-entry", capabilities);
        var manifest = new ServiceManifestV1(new(name), new($"1.{generation}"), Digest(image), process, [provided],
            dependencies: dependencies,
            restartPolicy: restart ?? ServiceRestartPolicyV1.Never);
        var grants = capability is { } grant
            ? new[] { new ComponentCapabilityGrant(issuer ?? throw new ArgumentNullException(nameof(issuer)), grant) }
            : [];
        return new(manifest, image, grants, [new(provided, Protocol(contract))]);
    }

    private static SubmittedOperation SubmitOperation(RuntimeKernel kernel, ProcessHandle process)
    {
        var input = kernel.AllocateBuffer<byte>(process, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(process, 8).Value!;
        var prepared = kernel.PrepareExternalOperation(process,
        [
            new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
            new(output.Handle, RegionUseMode.StagedOutput, new(0, 8)),
        ], ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        var dependencies = new OperationDependencySnapshot(7, 11);
        Assert.True(kernel.AdmitExternalOperation(process, prepared.Operation, dependencies).IsSuccess);
        var binding = kernel.RecordExternalOperationSubmission(process, prepared.Operation, dependencies).Value!;
        return new(prepared.Operation, binding, input, output);
    }

    private static ServiceContractIdentity Contract(string name) => new(name, "1", $"{name.ToLowerInvariant()}-digest");
    private static ProtocolDefinitionV1 Protocol(ServiceContractIdentity contract) =>
        new(contract.Name, contract.Digest, "Idle", ["Done"], [new(1, "Invoke")], [new(1, "Idle", "Done")]);
    private static string Digest(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed record SupervisorScenario(RuntimeKernel Kernel, CapabilityAwareServiceSupervisor Supervisor, ProcessHandle Principal, DomainId IssuerDomain, CapabilityId ControlCapability);
    private sealed record SubmittedOperation(ExternalOperationHandle Operation, OperationBinding Binding, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output);

    private sealed class SupervisorTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void AdvanceMilliseconds(long milliseconds) => _timestamp += milliseconds;
    }
}
