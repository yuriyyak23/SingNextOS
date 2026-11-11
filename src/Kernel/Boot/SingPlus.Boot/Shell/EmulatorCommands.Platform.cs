using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Boot.Shell;

public static partial class EmulatorCommands
{
    private static bool BindPlatform(EmulatorSession session, string[] args)
    {
        if (args.Length != 1 || !TryReadPid(args, 0, out ulong pid))
        {
            EmulatorConsole.Error("[emulator] usage: platform-bind <pid>\r\n");
            return false;
        }
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime unavailable.");
        KernelResult<ProcessHandle> owner = session.ResolvePid(pid);
        if (!owner.IsSuccess) return Fail(owner.Error, owner.Message);
        KernelResult<PlatformDomainBinding> bound = runtime.BindPlatformDomain(owner.Value);
        if (!bound.IsSuccess) return Fail(bound.Error, bound.Message);
        session.PlatformBindings[pid] = bound.Value;
        EmulatorConsole.Ok($"[kernel] process {pid} platform binding {bound.Value.BindingId.Value}/g{bound.Value.Generation.Value} admitted; no code execution has been granted\r\n");
        return true;
    }

    private static bool ConfigurePlatformPolicy(EmulatorSession session, string[] args)
    {
        if (args.Length != 1 || !TryReadPid(args, 0, out ulong pid))
        {
            EmulatorConsole.Error("[emulator] usage: platform-policy <pid>\r\n");
            return false;
        }
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime unavailable.");
        KernelResult<ProcessHandle> owner = session.ResolvePid(pid);
        if (!owner.IsSuccess) return Fail(owner.Error, owner.Message);
        if (!session.PlatformBindings.TryGetValue(pid, out PlatformDomainBinding binding))
            return Fail(KernelError.PlatformUnavailable, "No tracked binding; use platform-bind before start.");
        PlatformExecutionPolicy policy = new PlatformExecutionPolicy(new ExecutionBudget(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)),
            PriorityClass.Background, LatencyHint.Balanced, ThroughputHint.PreferThroughput);
        KernelResult<PlatformExecutionPolicyRegistration> configured = runtime.ConfigurePlatformExecutionPolicy(owner.Value, binding, policy);
        if (!configured.IsSuccess) return Fail(configured.Error, configured.Message);
        EmulatorConsole.Ok($"[kernel] process {pid} background execution policy admitted (20ms/100ms); provider model semantics\r\n");
        return true;
    }
}
