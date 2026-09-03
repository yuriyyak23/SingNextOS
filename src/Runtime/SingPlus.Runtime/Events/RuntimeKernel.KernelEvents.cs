using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly KernelEventRegistry _kernelEvents = new();

    public KernelResult<KernelEventEndpoint> CreateKernelEventEndpoint(ProcessHandle subject)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<KernelEventEndpoint>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess)
        {
            return KernelResult<KernelEventEndpoint>.Fail(
                effect.Error,
                effect.Message!);
        }

        return _kernelEvents.Create(subject);
    }

    public KernelResult<KernelEvent> ConsumeKernelEvent(
        ProcessHandle subject,
        KernelEventEndpoint endpoint)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<KernelEvent>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        if (resolved.Value!.State == ProcessState.Exiting)
        {
            return KernelResult<KernelEvent>.Fail(
                KernelError.InvalidTransition,
                "An Exiting process cannot consume new kernel event delivery.");
        }

        return _kernelEvents.Consume(subject, endpoint);
    }

    public KernelResult CloseKernelEventEndpoint(
        ProcessHandle subject,
        KernelEventEndpoint endpoint)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult.Fail(resolved.Error, resolved.Message!);

        var validation = _kernelEvents.Validate(subject, endpoint);
        if (!validation.IsSuccess) return validation;

        var routes = AdvancePlatformIrqBindingsForEndpoint(endpoint);
        if (!routes.IsSuccess) return routes;

        return _kernelEvents.Close(subject, endpoint);
    }
}
