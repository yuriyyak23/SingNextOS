using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<PlatformExecutionPolicyRegistration> ConfigurePlatformExecutionPolicy(
        ProcessHandle subject,
        PlatformDomainBinding binding,
        PlatformExecutionPolicy policy)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformExecutionPolicyRegistration>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformExecutionPolicyRegistration>.Fail(
                effect.Error,
                effect.Message!);
        }

        if (!CanChangePlatformExecutionAttachment(process.State))
        {
            return KernelResult<PlatformExecutionPolicyRegistration>.Fail(
                KernelError.InvalidTransition,
                $"Execution policy v{PlatformExecutionPolicyContract.ContractVersion} is immutable and must be configured before execution starts; process state is {process.State}.");
        }

        return PlatformAuthority.ConfigureExecutionPolicy(
            binding,
            PlatformIdentity(process),
            policy, commit =>
            {
                lock (_platformMemoryUseGate)
                {
                    var current = Processes.Resolve(subject);
                    if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                    if (!ReferenceEquals(current.Value, process))
                        return KernelResult.Fail(KernelError.StaleGeneration, "Exact execution-policy process changed.");
                    var accepting = EnsureProcessAcceptsNewEffects(current.Value!);
                    if (!accepting.IsSuccess) return accepting;
                    if (!CanChangePlatformExecutionAttachment(current.Value!.State))
                        return KernelResult.Fail(KernelError.InvalidTransition, "Execution-policy attachment is no longer mutable.");
                    return commit();
                }
            });
    }
}
