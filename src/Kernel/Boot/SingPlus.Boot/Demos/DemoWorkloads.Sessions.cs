using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunSessions(EmulatorSession session)
    {
        if (session.Runtime is not { } runtime) return KernelBootStageResult.Failure("Runtime is unavailable.");
        KernelResult<ProcessHandle> client = session.SpawnProcess("singplus.emulator.session.client", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!client.IsSuccess) return Failure("spawn client", client.Error, client.Message);
        KernelResult<ProcessHandle> server = session.SpawnProcess("singplus.emulator.session.server", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!server.IsSuccess) return Failure("spawn server", server.Error, server.Message);
        ProtocolDefinitionV1 protocol = EmulatorSession.CreatePingPongProtocol();
        ServiceContractIdentity contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        string resource = $"singplus.emulator.session:{server.Value.ProcessId.Value}";
        KernelResult<ServiceEndpointDescriptor> registered = runtime.RegisterService(server.Value, resource, contract, protocol,
            requiredCapabilities: [new CapabilityRequirementV1(ResourceKind.KernelService, resource, CapabilityRights.Read)]);
        if (!registered.IsSuccess) return Failure("register", registered.Error, registered.Message);
        ServiceEndpointDescriptor descriptor = registered.Value;
        KernelResult<ServiceEndpointDescriptor> discovered = runtime.ResolveByServiceName(resource);
        if (!discovered.IsSuccess || discovered.Value != descriptor)
            return KernelBootStageResult.Failure("Service discovery did not return the registered descriptor.");
        KernelResult<EndpointSessionHandle> denied = runtime.OpenSession(client.Value, descriptor);
        if (denied.IsSuccess || denied.Error != KernelError.MissingCapability)
            return KernelBootStageResult.Failure($"Session without required authority returned {denied.Error}.");
        KernelResult<SingProcess> principal = runtime.Processes.Resolve(client.Value);
        if (!principal.IsSuccess) return Failure("resolve client", principal.Error, principal.Message);
        KernelResult<CapabilityDescriptorV1> granted = runtime.MintCapability(principal.Value!.DomainId, client.Value, ResourceKind.KernelService, resource, CapabilityRights.Read);
        if (!granted.IsSuccess) return Failure("mint", granted.Error, granted.Message);
        KernelResult<EndpointSessionHandle> opened = runtime.OpenSession(client.Value, descriptor, [granted.Value!.CapabilityId]);
        if (!opened.IsSuccess) return Failure("open", opened.Error, opened.Message);
        EmulatorConsole.StageDetail($"service {descriptor.Service.Id.Value}:{descriptor.Generation.Value}; session {opened.Value.SessionId.Value}:{opened.Value.Generation.Value}; missing capability rejected ({denied.Error})");
        KernelResult<EndpointSessionHandle> timed = runtime.OpenSession(client.Value, descriptor, [granted.Value.CapabilityId], lifetime: TimeSpan.FromMilliseconds(1));
        if (!timed.IsSuccess) return Failure("open timed session", timed.Error, timed.Message);
        Thread.Sleep(20);
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(runtime, client.Value, timed.Value, timeout.Token);
        try
        {
            _ = transport.Invoke(1);
            return KernelBootStageResult.Failure("Expired session accepted invocation.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("SessionTimedOut", StringComparison.Ordinal))
        { EmulatorConsole.StageDetail("expired session invocation rejected before enqueue: SessionTimedOut"); }
        KernelResult foreignClose = runtime.CloseSession(server.Value, opened.Value);
        if (foreignClose.IsSuccess || foreignClose.Error != KernelError.WrongSessionOwner)
            return KernelBootStageResult.Failure($"Foreign session close returned {foreignClose.Error}.");
        KernelResult revoked = runtime.RevokeCapability(granted.Value.CapabilityId);
        if (!revoked.IsSuccess) return Failure("revoke admission capability", revoked.Error, revoked.Message);
        KernelResult<EndpointSessionHandle> revokedAdmission = runtime.OpenSession(client.Value, descriptor, [granted.Value.CapabilityId]);
        if (revokedAdmission.IsSuccess || revokedAdmission.Error != KernelError.MissingCapability)
            return KernelBootStageResult.Failure($"Revoked capability admitted a new session: {revokedAdmission.Error}.");
        EmulatorConsole.StageDetail($"revoked capability cannot admit another session ({revokedAdmission.Error}); existing close still requires its exact owner");
        KernelResult draining = runtime.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining);
        if (!draining.IsSuccess) return Failure("drain", draining.Error, draining.Message);
        KernelResult<EndpointSessionHandle> fresh = runtime.OpenSession(client.Value, descriptor, [granted.Value.CapabilityId]);
        if (fresh.IsSuccess || fresh.Error != KernelError.ServiceUnavailable)
            return KernelBootStageResult.Failure($"Draining service accepted a new session: {fresh.Error}.");
        KernelResult closed = runtime.CloseSession(client.Value, opened.Value);
        if (!closed.IsSuccess) return Failure("close existing session while draining", closed.Error, closed.Message);
        KernelResult stale = runtime.CloseSession(client.Value, opened.Value);
        if (stale.IsSuccess)
            return KernelBootStageResult.Failure("Closed session handle accepted a second explicit close.");
        EmulatorConsole.StageDetail($"foreign close refused ({foreignClose.Error}); draining refused new admission ({fresh.Error}); existing session closed; old handle rejected ({stale.Error})");
        return ReclaimPair(session, client.Value, server.Value);
    }
}
