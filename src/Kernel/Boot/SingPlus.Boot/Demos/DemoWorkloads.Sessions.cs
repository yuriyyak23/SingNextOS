using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunSessions(EmulatorSession session)
    {
        if (session.Runtime is not { } runtime) return KernelBootStageResult.Failure("Runtime is unavailable.");
        var client = session.SpawnProcess("singplus.emulator.session.client", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!client.IsSuccess) return Failure("spawn client", client.Error, client.Message);
        var server = session.SpawnProcess("singplus.emulator.session.server", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!server.IsSuccess) return Failure("spawn server", server.Error, server.Message);
        var protocol = EmulatorSession.CreatePingPongProtocol();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var resource = $"singplus.emulator.session:{server.Value.ProcessId.Value}";
        var registered = runtime.RegisterService(server.Value, resource, contract, protocol,
            requiredCapabilities: [new CapabilityRequirementV1(ResourceKind.KernelService, resource, CapabilityRights.Read)]);
        if (!registered.IsSuccess) return Failure("register", registered.Error, registered.Message);
        var descriptor = registered.Value;
        var discovered = runtime.ResolveByServiceName(resource);
        if (!discovered.IsSuccess || discovered.Value != descriptor)
            return KernelBootStageResult.Failure("Service discovery did not return the registered descriptor.");
        var denied = runtime.OpenSession(client.Value, descriptor);
        if (denied.IsSuccess || denied.Error != KernelError.MissingCapability)
            return KernelBootStageResult.Failure($"Session without required authority returned {denied.Error}.");
        var principal = runtime.Processes.Resolve(client.Value);
        if (!principal.IsSuccess) return Failure("resolve client", principal.Error, principal.Message);
        var granted = runtime.MintCapability(principal.Value!.DomainId, client.Value, ResourceKind.KernelService, resource, CapabilityRights.Read);
        if (!granted.IsSuccess) return Failure("mint", granted.Error, granted.Message);
        var opened = runtime.OpenSession(client.Value, descriptor, [granted.Value!.CapabilityId]);
        if (!opened.IsSuccess) return Failure("open", opened.Error, opened.Message);
        EmulatorConsole.StageDetail($"service {descriptor.Service.Id.Value}:{descriptor.Generation.Value}; session {opened.Value.SessionId.Value}:{opened.Value.Generation.Value}; missing capability rejected ({denied.Error})");
        var timed = runtime.OpenSession(client.Value, descriptor, [granted.Value.CapabilityId], lifetime: TimeSpan.FromMilliseconds(1));
        if (!timed.IsSuccess) return Failure("open timed session", timed.Error, timed.Message);
        Thread.Sleep(20);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var transport = new RuntimeSipClientTransport(runtime, client.Value, timed.Value, timeout.Token);
        try
        {
            _ = transport.Invoke(1);
            return KernelBootStageResult.Failure("Expired session accepted invocation.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("SessionTimedOut", StringComparison.Ordinal))
        { EmulatorConsole.StageDetail("expired session invocation rejected before enqueue: SessionTimedOut"); }
        var foreignClose = runtime.CloseSession(server.Value, opened.Value);
        if (foreignClose.IsSuccess || foreignClose.Error != KernelError.WrongSessionOwner)
            return KernelBootStageResult.Failure($"Foreign session close returned {foreignClose.Error}.");
        var revoked = runtime.RevokeCapability(granted.Value.CapabilityId);
        if (!revoked.IsSuccess) return Failure("revoke admission capability", revoked.Error, revoked.Message);
        var revokedAdmission = runtime.OpenSession(client.Value, descriptor, [granted.Value.CapabilityId]);
        if (revokedAdmission.IsSuccess || revokedAdmission.Error != KernelError.MissingCapability)
            return KernelBootStageResult.Failure($"Revoked capability admitted a new session: {revokedAdmission.Error}.");
        EmulatorConsole.StageDetail($"revoked capability cannot admit another session ({revokedAdmission.Error}); existing close still requires its exact owner");
        var draining = runtime.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining);
        if (!draining.IsSuccess) return Failure("drain", draining.Error, draining.Message);
        var fresh = runtime.OpenSession(client.Value, descriptor, [granted.Value.CapabilityId]);
        if (fresh.IsSuccess || fresh.Error != KernelError.ServiceUnavailable)
            return KernelBootStageResult.Failure($"Draining service accepted a new session: {fresh.Error}.");
        var closed = runtime.CloseSession(client.Value, opened.Value);
        if (!closed.IsSuccess) return Failure("close existing session while draining", closed.Error, closed.Message);
        var stale = runtime.CloseSession(client.Value, opened.Value);
        if (stale.IsSuccess)
            return KernelBootStageResult.Failure("Closed session handle accepted a second explicit close.");
        EmulatorConsole.StageDetail($"foreign close refused ({foreignClose.Error}); draining refused new admission ({fresh.Error}); existing session closed; old handle rejected ({stale.Error})");
        return ReclaimPair(session, client.Value, server.Value);
    }
}
