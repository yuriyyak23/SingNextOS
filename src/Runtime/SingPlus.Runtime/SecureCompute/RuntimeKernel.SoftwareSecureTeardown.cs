using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private bool HasKernelInitializedLoadsForProcess(ProcessHandle owner) =>
        _originalKernelInitializedLoads.Any(record => record.Owner == owner);

    private KernelResult DrainKernelInitializedLoadsForProcess(ProcessHandle owner)
    {
        InitializedLoadRecord[] pending;
        lock (_platformMemoryUseGate)
        {
            if (!IntactKernelInitializedLoads())
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Original initialized-load inventory remains unknown.");
            pending = _originalKernelInitializedLoads.Where(record => record.Owner == owner && !record.Closed).ToArray();
            foreach (var record in pending) record.Cancelled = true;
            if (pending.Any(record => record.Active))
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Original initialized-load callback remains in flight.");
        }
        // Original query/closure only, outside process/source owner gates and after local revoke.
        foreach (var record in pending)
        {
            try
            {
                var resolved = ResolveSoftwareSecureInitializedLoad(owner, record.Operation);
                if (!resolved.IsSuccess || resolved.Value?.ResourcesClosed != true)
                    return KernelResult.Fail(KernelError.PlatformBindingDraining, "Original initialized-load cleanup remains pending.");
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Original initialized-load query did not prove closure.");
            }
        }
        lock (_platformMemoryUseGate)
            return IntactKernelInitializedLoads() && !_originalKernelInitializedLoads.Any(record => record.Owner == owner && (record.Active || !record.Closed))
                ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformBindingDraining, "Original initialized-load custody remains pending.");
    }
}
