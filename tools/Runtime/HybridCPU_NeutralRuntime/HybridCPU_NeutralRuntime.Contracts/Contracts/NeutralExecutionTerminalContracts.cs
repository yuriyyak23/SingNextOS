namespace YAKSys_Hybrid_CPU.Core;

public enum NeutralExecutionTerminalKind : byte
{
    Completed = 0, BudgetExhausted = 1, KernelRejected = 2, ExecutionFailure = 3,
}

/// <summary>Typed external terminal outcome; no resume, permission, containment or backing release.</summary>
public readonly record struct NeutralExecutionTerminalObservation(
    NeutralChildExecutionStartRequest Request, NeutralChildExecutionReceipt Receipt, NeutralExecutionTerminalKind Kind)
{
    public int? ProcessExitCode { get; init; }
}

/// <summary>Pure surviving-original lookup. A missing observation is unknown, never closure.</summary>
public interface INeutralExecutionTerminalProvider
{
    NeutralVirtualizationResult<NeutralExecutionTerminalObservation> ResolveExecutionTerminal(
        NeutralChildExecutionStartRequest originalRequest);
}
