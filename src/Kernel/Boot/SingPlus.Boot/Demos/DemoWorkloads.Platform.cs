using SingPlus.Contracts;
using SingPlus.Kernel;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Boot.Demos;

public static partial class DemoWorkloads
{
    public static KernelBootStageResult RunPlatform(EmulatorSession session)
    {
        if (!session.Options.HostModelPlatform || session.Runtime is not { } runtime)
            return KernelBootStageResult.Failure("Platform demo requires --platform host-model.");
        KernelResult<ProcessHandle> spawned = session.SpawnProcess("singplus.emulator.platform.subject", ExecutionRole.User, MemoryProfile.ManagedGc, start: false);
        if (!spawned.IsSuccess) return Failure("admit subject", spawned.Error, spawned.Message);
        ProcessHandle handle = spawned.Value;
        KernelResult<PlatformDomainBinding> bound = runtime.BindPlatformDomain(handle);
        if (!bound.IsSuccess) return Failure("bind domain", bound.Error, bound.Message);
        session.PlatformBindings[handle.ProcessId.Value] = bound.Value;
        PlatformExecutionPolicy policy = new PlatformExecutionPolicy(new ExecutionBudget(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)),
            PriorityClass.Background, LatencyHint.Balanced, ThroughputHint.PreferThroughput);
        KernelResult<PlatformExecutionPolicyRegistration> configured = runtime.ConfigurePlatformExecutionPolicy(handle, bound.Value, policy);
        if (!configured.IsSuccess) return Failure("configure policy", configured.Error, configured.Message);
        EmulatorConsole.StageDetail($"binding {bound.Value.BindingId.Value} and background policy admitted by existing Host model");
        KernelResult started = runtime.StartProcess(handle);
        if (started.IsSuccess || started.Error != KernelError.PlatformUnsupported)
            return KernelBootStageResult.Failure($"Host model execution classification differs from expectation: {started.Error}.");
        if (!CheckState(runtime, handle, ProcessState.Admitted, out string? stateError))
            return KernelBootStageResult.Failure(stateError!);
        EmulatorConsole.StageDetail("start refused (PlatformUnsupported); local state remains Admitted, no execution claimed");
        KernelResult reclaimed = session.TerminateAndReclaim(handle);
        if (!reclaimed.IsSuccess) return Failure("reclaim platform subject", reclaimed.Error, reclaimed.Message);
        EmulatorConsole.StageDetail("bound admitted subject terminated; provider closure and local teardown complete");
        return KernelBootStageResult.Success();
    }
}
