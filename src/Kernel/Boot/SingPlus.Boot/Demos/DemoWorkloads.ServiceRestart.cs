using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunServiceRestart(EmulatorSession session)
    {
        if (session.Runtime is not { } runtime) return KernelBootStageResult.Failure("Runtime is unavailable.");
        KernelResult<ProcessHandle> caller = session.SpawnProcess("singplus.emulator.restart.client", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!caller.IsSuccess) return Failure("spawn caller", caller.Error, caller.Message);
        KernelResult<ProcessHandle> first = session.SpawnProcess("singplus.emulator.restart.first", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!first.IsSuccess) return Failure("spawn first provider", first.Error, first.Message);
        ProtocolDefinitionV1 protocol = EmulatorSession.CreatePingPongProtocol();
        ServiceContractIdentity contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        string name = $"singplus.emulator.restart:{first.Value.ProcessId.Value}";
        KernelResult<ServiceEndpointDescriptor> registered = runtime.RegisterService(first.Value, name, contract, protocol);
        if (!registered.IsSuccess) return Failure("register first", registered.Error, registered.Message);
        ServiceEndpointDescriptor oldDescriptor = registered.Value;
        KernelResult<EndpointSessionHandle> originalSession = runtime.OpenSession(caller.Value, oldDescriptor);
        if (!originalSession.IsSuccess) return Failure("open original session", originalSession.Error, originalSession.Message);
        KernelResult retired = session.TerminateAndReclaim(first.Value);
        if (!retired.IsSuccess) return Failure("retire provider", retired.Error, retired.Message);
        KernelResult<ServiceEndpointDescriptor> unavailable = runtime.ResolveByServiceName(name);
        if (unavailable.IsSuccess || unavailable.Error != KernelError.ServiceNotFound)
            return KernelBootStageResult.Failure("Retired service remained discoverable as accepting.");
        KernelResult oldSession = runtime.CloseSession(caller.Value, originalSession.Value);
        if (oldSession.IsSuccess || oldSession.Error != KernelError.SessionClosed)
            return KernelBootStageResult.Failure($"Provider teardown did not close its old session: {oldSession.Error}.");
        KernelResult<ProcessHandle> replacement = session.SpawnProcess("singplus.emulator.restart.replacement", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!replacement.IsSuccess) return Failure("spawn replacement", replacement.Error, replacement.Message);
        KernelResult<ServiceEndpointDescriptor> registeredNext = runtime.RegisterService(replacement.Value, name, contract, protocol);
        if (!registeredNext.IsSuccess) return Failure("register replacement", registeredNext.Error, registeredNext.Message);
        ServiceEndpointDescriptor next = registeredNext.Value;
        if (next.Service != oldDescriptor.Service || next.Generation.Value != oldDescriptor.Generation.Value + 1)
            return KernelBootStageResult.Failure("Replacement did not retain service identity with an incremented generation.");
        KernelResult<EndpointSessionHandle> stale = runtime.OpenSession(caller.Value, oldDescriptor);
        if (stale.IsSuccess || stale.Error != KernelError.StaleGeneration)
            return KernelBootStageResult.Failure($"Old service descriptor admitted a replacement session: {stale.Error}.");
        KernelResult<ServiceEndpointDescriptor> discovered = runtime.ResolveByServiceName(name);
        if (!discovered.IsSuccess || discovered.Value != next)
            return KernelBootStageResult.Failure("Discovery did not publish the exact replacement descriptor.");
        KernelResult<EndpointSessionHandle> openedNext = runtime.OpenSession(caller.Value, discovered.Value);
        if (!openedNext.IsSuccess) return Failure("open replacement", openedNext.Error, openedNext.Message);
        if (openedNext.Value == originalSession.Value)
            return KernelBootStageResult.Failure("Replacement resurrected the old session handle.");
        KernelResult closed = runtime.CloseSession(caller.Value, openedNext.Value);
        if (!closed.IsSuccess) return Failure("close replacement session", closed.Error, closed.Message);
        EmulatorConsole.StageDetail($"service {next.Service.Id.Value}: generation {oldDescriptor.Generation.Value} -> {next.Generation.Value}; old descriptor refused ({stale.Error}); old session closed ({oldSession.Error}); new session {openedNext.Value.SessionId.Value} admitted and closed");
        return ReclaimPair(session, caller.Value, replacement.Value);
    }
}
