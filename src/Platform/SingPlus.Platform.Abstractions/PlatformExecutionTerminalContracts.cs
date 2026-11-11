namespace SingPlus.Platform;

public enum PlatformExecutionTerminalKind : byte
{
    Completed = 0, BudgetExhausted = 1, KernelRejected = 2, ExecutionFailure = 3,
}

/// <summary>Typed original terminal work observation. No resume or backing release authority.</summary>
public readonly record struct PlatformExecutionTerminalObservation(
    PlatformChildExecutionReceipt Receipt, PlatformExecutionTerminalKind Kind)
{
    public int? ProcessExitCode { get; init; }
}

/// <summary>Pure lookup of the surviving original published Start, with no native delivery/ack effect.</summary>
public interface IPlatformExecutionTerminalProvider
{
    PlatformAuthorityResult<PlatformExecutionTerminalObservation> ResolveExecutionTerminal(
        PlatformProviderChildDomainLease childLease);
}
