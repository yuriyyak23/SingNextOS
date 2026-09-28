using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>Kernel-private compound identity for one virtualized compute submission.</summary>
internal readonly record struct VirtualComputeContext(
    VirtualDomainHandle VirtualDomain,
    GuestRegionMappingHandle Input,
    GuestRegionMappingHandle Output,
    VirtualIoBinding VirtualIo,
    SecureExecutionBinding? SecureExecution);

public sealed partial class RuntimeKernel
{
    private readonly object _virtualComputeGate = new();
    private readonly Dictionary<ExternalOperationId, (ProcessHandle Owner, VirtualComputeContext Context)> _virtualComputePins = [];

    internal void PinVirtualComputeContext(ProcessHandle owner, ExternalOperationHandle operation, VirtualComputeContext context)
    {
        lock (_virtualComputeGate) _virtualComputePins.Add(operation.OperationId, (owner, context));
    }

    internal void ReleaseVirtualComputeContext(ExternalOperationHandle operation)
    {
        lock (_virtualComputeGate) _virtualComputePins.Remove(operation.OperationId);
    }

    private bool HasVirtualComputePin(VirtualIoBinding binding)
    {
        lock (_virtualComputeGate) return _virtualComputePins.Values.Any(x => x.Context.VirtualIo == binding);
    }

    private bool HasVirtualComputePin(SecureExecutionBinding binding)
    {
        lock (_virtualComputeGate) return _virtualComputePins.Values.Any(x => x.Context.SecureExecution == binding);
    }

    private bool HasVirtualComputePin(GuestRegionMappingHandle mapping)
    {
        lock (_virtualComputeGate) return _virtualComputePins.Values.Any(x => x.Context.Input == mapping || x.Context.Output == mapping);
    }

    internal KernelResult<VirtualComputeContext> CreateVirtualComputeContext(ProcessHandle owner,
        VirtualDomainHandle domain, GuestRegionMappingHandle input, GuestRegionMappingHandle output,
        VirtualIoBinding virtualIo, SecureExecutionBinding? secureExecution = null)
    {
        var context = new VirtualComputeContext(domain, input, output, virtualIo, secureExecution);
        var validation = RevalidateVirtualComputeContext(owner, context, null);
        return validation.IsSuccess
            ? KernelResult<VirtualComputeContext>.Ok(context)
            : KernelResult<VirtualComputeContext>.Fail(validation.Error, validation.Message!);
    }

    internal KernelResult RevalidateVirtualComputeContext(ProcessHandle owner, VirtualComputeContext context, ComputePlan? plan)
    {
        var domain = _virtualDomains.Resolve(owner, context.VirtualDomain);
        if (!domain.IsSuccess) return KernelResult.Fail(domain.Error, domain.Message!);
        var input = _virtualDomains.ResolveMapping(domain.Value!, context.Input);
        var output = _virtualDomains.ResolveMapping(domain.Value!, context.Output);
        if (!input.IsSuccess) return KernelResult.Fail(input.Error, input.Message!);
        if (!output.IsSuccess) return KernelResult.Fail(output.Error, output.Message!);
        var io = RevalidateVirtualIo(owner, context.VirtualIo);
        if (!io.IsSuccess) return io;
        if (plan is not null && (plan.Intent.Input.Region != input.Value!.Region || plan.Intent.Output.Region != output.Value!.Region))
            return KernelResult.Fail(KernelError.WrongRegionOwner, "Compute plan operands do not match the exact guest mappings.");
        if (context.SecureExecution is { } secure)
        {
            var secureValidation = RevalidateSecureExecution(secure);
            if (!secureValidation.IsSuccess) return secureValidation;
            var execution = ResolveSecureGuestExecution(owner, secure);
            if (!execution.IsSuccess || execution.Value!.Request.Context.Virtual != context.VirtualDomain)
                return KernelResult.Fail(KernelError.WrongPlatformDomain, "Secure compute context belongs to another virtual domain.");
        }
        else if (plan?.Intent.RequiresSecureEvidence == true)
            return KernelResult.Fail(KernelError.PlatformDenied, "Secure compute plan requires an exact secure execution binding.");
        return KernelResult.Ok();
    }
}
