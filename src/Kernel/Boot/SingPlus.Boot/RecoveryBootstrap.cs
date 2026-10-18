using System.Security.Cryptography;
using SingPlus.Kernel;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Boot;

internal static class RecoveryBootstrap
{
    internal static KernelBootStageResult Initialize(EmulatorSession session)
    {
        byte[]? key = null;
        try
        {
            var options = session.Options;
            RuntimeKernelRecoveryOptions? recovery = null;
            if (options.RecoveryJournalPath is { } journal)
            {
                using var stream = new FileStream(options.RecoveryKeyPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != 32)
                    return KernelBootStageResult.Failure("Recovery key must contain exactly 32 binary bytes.");
                key = new byte[32];
                stream.ReadExactly(key);
                recovery = new RuntimeKernelRecoveryOptions(journal, key);
            }
            IPlatformAuthorityProvider? provider = options.HostModelPlatform
                ? new HostPlatformAuthorityProvider(deferDsc1Completion: options.DeferComputeCompletion)
                : options.HybridCpuChildPlatform
                    ? new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), new HybridCpuExecutableChildAdapter())
                    : options.HybridCpuModelPlatform ? new HybridCpuPlatformAuthorityProvider() : null;
            session.HostModelProvider = provider as HostPlatformAuthorityProvider;
            session.Runtime = new RuntimeKernel(provider, session.RuntimeClock, recovery);
            if (recovery is not null)
            {
                session.EnvironmentFacts.Add($"Recovery: authenticated runtime replay enabled; journal={recovery.ResourceBudgetJournalPath}");
                EmulatorConsole.StageDetail("runtime journal replay accepted; conservative charge import occurs at system budget configuration; no stale authority restored");
            }
            return KernelBootStageResult.Success();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException or OverflowException or InvalidOperationException)
        {
            return KernelBootStageResult.Failure($"Recovery/runtime initialization failed: {exception.Message}");
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }
}
