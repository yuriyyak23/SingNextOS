using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<VirtualExecutionResult> ObserveVirtualExecutionResult(
        ProcessHandle owner, VirtualDomainHandle domain, CapabilityId readCapability)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess)
            return KernelResult<VirtualExecutionResult>.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var resolved = ResolveVirtualCapability(owner, domain, readCapability,
            VirtualizationResourceIds.Traps(domain.DomainId), CapabilityRights.Read);
        if (!resolved.IsSuccess) return KernelResult<VirtualExecutionResult>.Fail(resolved.Error, resolved.Message!);
        var record = resolved.Value!;
        var original = _virtualDomains.ValidateOriginalCompletedPublication(record);
        if (!original.IsSuccess) return KernelResult<VirtualExecutionResult>.Fail(original.Error, original.Message!);
        if (record.ChildBinding is not { } child)
            return KernelResult<VirtualExecutionResult>.Fail(KernelError.PlatformUnsupported, "Original execution result producer unavailable.");
        KernelResult<PlatformExecutionTerminalObservation> observed = default;
        var publication = UseVirtualDeliverySource(owner, domain, readCapability, record, child, CapabilityRights.Read,
            VirtualizationResourceIds.Traps(domain.DomainId), VirtualDomainState.Completed, (fresh, sourceCommit) =>
            {
                observed = PlatformAuthority.ReadCompletedExecution(child, fresh, sourceCommit, commit =>
                {
                    var composed = RevalidateComposedDomain(owner, virtualDomain: domain);
                    return composed.IsSuccess ? fresh(() => commit(() => sourceCommit(() =>
                        _virtualDomains.ValidateOriginalCompletedPublication(record)))) : composed;
                });
                return observed.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(observed.Error, observed.Message!);
            });
        if (!publication.IsSuccess)
            return KernelResult<VirtualExecutionResult>.Fail(publication.Error, publication.Message!);
        return KernelResult<VirtualExecutionResult>.Ok(new(domain, observed.Value.Receipt.RetiredSequence,
            observed.Value.ProcessExitCode!.Value));
    }
}
