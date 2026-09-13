namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    /// <summary>
    /// Trusted control-plane hook used when the platform backend is known to have
    /// reset or lost continuity. It invalidates Sing-local platform generations
    /// and quarantines local VM authority without treating a provider reset token
    /// as authority or reclaim evidence.
    /// </summary>
    internal KernelResult<PlatformBackendResetSnapshot> ObservePlatformBackendReset()
    {
        lock (_platformMemoryUseGate)
        {
            var reset = PlatformAuthority.ObserveBackendReset();
            if (!reset.IsSuccess)
            {
                return KernelResult<PlatformBackendResetSnapshot>.Fail(
                    reset.Error,
                    reset.Message!);
            }

            _virtualDomains.QuarantineForPlatformBackendReset();
            QuarantineSecureDomainsForPlatformBackendReset();
            return reset;
        }
    }
}
