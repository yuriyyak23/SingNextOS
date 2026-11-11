namespace SingPlus.Kernel;

public readonly record struct KernelBootStageResult(bool IsSuccess, string? Error)
{
    public static KernelBootStageResult Success() => new(true, null);

    public static KernelBootStageResult Failure(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            throw new ArgumentException("A failed boot stage requires an error.", nameof(error));

        return new KernelBootStageResult(false, error);
    }
}

public interface IKernelBootStage
{
    string Name { get; }

    KernelBootStageResult Execute();
}
