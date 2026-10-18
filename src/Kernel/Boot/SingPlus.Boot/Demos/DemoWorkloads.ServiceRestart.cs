using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Runtime;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunServiceRestart(EmulatorSession session)
    {
        if (session.Runtime is not { } runtime) return KernelBootStageResult.Failure("Runtime is unavailable.");
        var caller = session.SpawnProcess("singplus.emulator.restart.client", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!caller.IsSuccess) return Failure("spawn caller", caller.Error, caller.Message);
        var first = session.SpawnProcess("singplus.emulator.restart.first", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!first.IsSuccess) return Failure("spawn first provider", first.Error, first.Message);
        var protocol = EmulatorSession.CreatePingPongProtocol();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var name = $"singplus.emulator.restart:{first.Value.ProcessId.Value}";
        var registered = runtime.RegisterService(first.Value, name, contract, protocol);
        if (!registered.IsSuccess) return Failure("register first", registered.Error, registered.Message);
        var oldDescriptor = registered.Value;
        var originalSession = runtime.OpenSession(caller.Value, oldDescriptor);
        if (!originalSession.IsSuccess) return Failure("open original session", originalSession.Error, originalSession.Message);
        var retired = session.TerminateAndReclaim(first.Value);
        if (!retired.IsSuccess) return Failure("retire provider", retired.Error, retired.Message);
        var unavailable = runtime.ResolveByServiceName(name);
        if (unavailable.IsSuccess || unavailable.Error != KernelError.ServiceNotFound)
            return KernelBootStageResult.Failure("Retired service remained discoverable as accepting.");
        var oldSession = runtime.CloseSession(caller.Value, originalSession.Value);
        if (oldSession.IsSuccess || oldSession.Error != KernelError.SessionClosed)
            return KernelBootStageResult.Failure($"Provider teardown did not close its old session: {oldSession.Error}.");
        var replacement = session.SpawnProcess("singplus.emulator.restart.replacement", ExecutionRole.User, MemoryProfile.ManagedGc);
        if (!replacement.IsSuccess) return Failure("spawn replacement", replacement.Error, replacement.Message);
        var registeredNext = runtime.RegisterService(replacement.Value, name, contract, protocol);
        if (!registeredNext.IsSuccess) return Failure("register replacement", registeredNext.Error, registeredNext.Message);
        var next = registeredNext.Value;
        if (next.Service != oldDescriptor.Service || next.Generation.Value != oldDescriptor.Generation.Value + 1)
            return KernelBootStageResult.Failure("Replacement did not retain service identity with an incremented generation.");
        var stale = runtime.OpenSession(caller.Value, oldDescriptor);
        if (stale.IsSuccess || stale.Error != KernelError.StaleGeneration)
            return KernelBootStageResult.Failure($"Old service descriptor admitted a replacement session: {stale.Error}.");
        var discovered = runtime.ResolveByServiceName(name);
        if (!discovered.IsSuccess || discovered.Value != next)
            return KernelBootStageResult.Failure("Discovery did not publish the exact replacement descriptor.");
        var openedNext = runtime.OpenSession(caller.Value, discovered.Value);
        if (!openedNext.IsSuccess) return Failure("open replacement", openedNext.Error, openedNext.Message);
        if (openedNext.Value == originalSession.Value)
            return KernelBootStageResult.Failure("Replacement resurrected the old session handle.");
        var closed = runtime.CloseSession(caller.Value, openedNext.Value);
        if (!closed.IsSuccess) return Failure("close replacement session", closed.Error, closed.Message);
        EmulatorConsole.StageDetail($"service {next.Service.Id.Value}: generation {oldDescriptor.Generation.Value} -> {next.Generation.Value}; old descriptor refused ({stale.Error}); old session closed ({oldSession.Error}); new session {openedNext.Value.SessionId.Value} admitted and closed");
        return ReclaimPair(session, caller.Value, replacement.Value);
    }
}
