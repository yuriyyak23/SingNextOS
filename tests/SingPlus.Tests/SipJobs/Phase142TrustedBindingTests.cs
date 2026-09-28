using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip.FileSystem;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase142TrustedBindingTests
{
    [Fact]
    public void ExactFileOpenRouteResolvesWithoutReturningImplementationOrAuthority()
    {
        var scenario = CreateScenario();
        var trace = new TraceSink();
        var host = RuntimeFileServiceHost.CreateForSession(
            scenario.Kernel, scenario.Service, scenario.Session, trace).Value!;
        var table = new SipJobFileOpenBindingTable(scenario.Kernel);

        var registered = table.Register(scenario.Descriptor, scenario.Service, scenario.Session, host);
        Assert.True(registered.IsSuccess, registered.Message);
        var resolved = table.ValidateRoute(registered.Value!.Handle, registered.Value.Key);

        Assert.True(resolved.IsSuccess, resolved.Message);
        Assert.Equal("Generated:IFileService.OpenAsync", resolved.Value!.RouteKind);
        Assert.Empty(trace.Events);
        Assert.DoesNotContain(typeof(SipJobResolvedBindingMetadata).GetProperties(), property =>
            property.PropertyType == typeof(RuntimeFileServiceHost) ||
            property.PropertyType == typeof(IIFileServiceGeneratedSentryTarget_OpenAsync));
        Assert.DoesNotContain(typeof(SipJobFileOpenBindingTable).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic), method =>
            method.Name.Contains("Invoke", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryExactIdentityMutationFailsClosed()
    {
        var scenario = CreateScenario();
        var host = RuntimeFileServiceHost.CreateForSession(scenario.Kernel, scenario.Service, scenario.Session).Value!;
        var table = new SipJobFileOpenBindingTable(scenario.Kernel);
        var registered = table.Register(scenario.Descriptor, scenario.Service, scenario.Session, host).Value!;
        var key = registered.Key;
        SipJobFileOpenBindingKey[] mutations =
        [
            key with { Realm = new AuthorityRealmId(Guid.NewGuid()) },
            key with { Caller = key.Caller with { Generation = key.Caller.Generation + 1 } },
            key with { ServiceProcess = key.ServiceProcess with { Generation = key.ServiceProcess.Generation + 1 } },
            key with { Service = new(new ServiceId(key.Service.Id.Value + 1), key.Service.Name) },
            key with { ServiceGeneration = new(key.ServiceGeneration.Value + 1) },
            key with { Session = key.Session with { Generation = new(key.Session.Generation.Value + 1) } },
            key with { ContractName = key.ContractName + ".wrong" },
            key with { ContractVersion = key.ContractVersion + ".wrong" },
            key with { ContractDigest = Mutate(key.ContractDigest) },
            key with { ThunkId = key.ThunkId + ".wrong" },
            key with { ThunkDigest = Mutate(key.ThunkDigest) },
        ];

        foreach (var mutation in mutations)
            Assert.Equal(KernelError.ServiceContractMismatch, table.ValidateRoute(registered.Handle, mutation).Error);
    }

    [Fact]
    public void SessionCloseInvalidatesPreviouslyResolvedBinding()
    {
        var scenario = CreateScenario();
        var host = RuntimeFileServiceHost.CreateForSession(scenario.Kernel, scenario.Service, scenario.Session).Value!;
        var table = new SipJobFileOpenBindingTable(scenario.Kernel);
        var registered = table.Register(scenario.Descriptor, scenario.Service, scenario.Session, host).Value!;
        Assert.True(table.ValidateRoute(registered.Handle, registered.Key).IsSuccess);

        Assert.True(scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);

        Assert.Equal(KernelError.SessionClosed, table.ValidateRoute(registered.Handle, registered.Key).Error);
        Assert.Equal(0, table.Count);
        Assert.Equal(KernelError.StaleHandle, table.ValidateRoute(registered.Handle, registered.Key).Error);
    }

    [Fact]
    public void ServiceFaultInvalidatesBindingAndCannotReplayOldRoute()
    {
        var scenario = CreateScenario();
        var host = RuntimeFileServiceHost.CreateForSession(scenario.Kernel, scenario.Service, scenario.Session).Value!;
        var table = new SipJobFileOpenBindingTable(scenario.Kernel);
        var registered = table.Register(scenario.Descriptor, scenario.Service, scenario.Session, host).Value!;

        Assert.True(scenario.Kernel.FaultProcess(scenario.Service).IsSuccess);

        Assert.False(table.ValidateRoute(registered.Handle, registered.Key).IsSuccess);
        Assert.Equal(0, table.Count);
        Assert.Equal(KernelError.StaleHandle, table.ValidateRoute(registered.Handle, registered.Key).Error);
        Assert.Equal(KernelError.StaleHandle, table.Retire(registered.Handle).Error);
    }

    [Fact]
    public void ServiceReplacementWithReusedIdentityCannotAbaOldBinding()
    {
        var scenario = CreateScenario();
        var oldHost = RuntimeFileServiceHost.CreateForSession(scenario.Kernel, scenario.Service, scenario.Session).Value!;
        var table = new SipJobFileOpenBindingTable(scenario.Kernel);
        var oldBinding = table.Register(scenario.Descriptor, scenario.Service, scenario.Session, oldHost).Value!;
        Assert.True(scenario.Kernel.FaultProcess(scenario.Service).IsSuccess);

        var replacement = TestFixtures.Create(scenario.Kernel, 61_000, 610_000, generation: 2).Handle;
        var protocol = IFileServiceProtocol.CreateDefinition();
        var descriptor = scenario.Kernel.RegisterService(
            replacement,
            "p14-file-binding",
            new(protocol.ContractName, "1", protocol.ContractDigest),
            protocol,
            IFileServiceResponseProtocol.Definition,
            [new(ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write)]).Value!;
        var replacementSession = scenario.Kernel.OpenSession(scenario.Caller, descriptor, [scenario.Capability]).Value;
        var replacementHost = RuntimeFileServiceHost.CreateForSession(
            scenario.Kernel, replacement, replacementSession).Value!;
        var replacementBinding = table.Register(descriptor, replacement, replacementSession, replacementHost).Value!;

        Assert.Equal(KernelError.StaleHandle, table.ValidateRoute(oldBinding.Handle, oldBinding.Key).Error);
        Assert.True(table.ValidateRoute(replacementBinding.Handle, replacementBinding.Key).IsSuccess);
        Assert.NotEqual(oldBinding.Key.ServiceProcess, replacementBinding.Key.ServiceProcess);
        Assert.NotEqual(oldBinding.Key.ServiceGeneration, replacementBinding.Key.ServiceGeneration);
        Assert.NotEqual(oldBinding.Key.Session, replacementBinding.Key.Session);
    }

    [Fact]
    public void UnknownAndEmptyBindingHandlesFailClosed()
    {
        var scenario = CreateScenario();
        var table = new SipJobFileOpenBindingTable(scenario.Kernel);
        var key = new SipJobFileOpenBindingKey();

        Assert.Equal(KernelError.StaleHandle, table.ValidateRoute(default, key).Error);
        Assert.Equal(KernelError.StaleHandle, table.ValidateRoute(new(Guid.NewGuid()), key).Error);
    }

    private static Scenario CreateScenario()
    {
        var kernel = new RuntimeKernel();
        var service = TestFixtures.Create(kernel, 61_000, 610_000).Handle;
        var caller = TestFixtures.Create(kernel, 61_001, 610_010).Handle;
        var protocol = IFileServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var descriptor = kernel.RegisterService(
            service,
            "p14-file-binding",
            contract,
            protocol,
            IFileServiceResponseProtocol.Definition,
            [new(ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write)]).Value!;
        var capability = kernel.MintCapability(
            new DomainId(610_010), caller, ResourceKind.File, CapabilityResourceIds.FileNamespace,
            CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        return new(kernel, service, caller, capability, descriptor, session);
    }

    private static string Mutate(string digest) => (digest[0] == '0' ? "1" : "0") + digest[1..];

    private sealed record Scenario(
        RuntimeKernel Kernel,
        ProcessHandle Service,
        ProcessHandle Caller,
        CapabilityId Capability,
        ServiceEndpointDescriptor Descriptor,
        EndpointSessionHandle Session);

    private sealed class TraceSink : INativeSipCompletedEventSink
    {
        internal List<NativeSipCompletedEvent> Events { get; } = [];
        public void Record(NativeSipCompletedEvent completedEvent) => Events.Add(completedEvent);
    }
}
