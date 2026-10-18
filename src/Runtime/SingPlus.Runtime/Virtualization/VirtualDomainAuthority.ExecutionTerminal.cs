using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

internal sealed partial class VirtualDomainAuthority
{
    internal KernelResult CommitExecutionPublication(Record record, PlatformExecutionTerminalKind? terminal)
    {
        lock (_gate)
        {
            if (terminal is { } kind && !Enum.IsDefined(kind))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Unknown original execution outcome.");
            var target = terminal is null ? VirtualDomainState.Running : terminal == PlatformExecutionTerminalKind.Completed
                ? VirtualDomainState.Completed : VirtualDomainState.Faulted;
            var result = CommitStateTransition(record, VirtualDomainState.Configured, target);
            if (!result.IsSuccess) return result;
            record.TerminalPublicationKind = terminal;
            record.TerminalPublicationRevision = terminal is null ? null : record.SourceRevision;
            return result;
        }
    }

    internal KernelResult ValidateOriginalTerminalFailurePublication(Record record)
    {
        lock (_gate)
            return HasCurrentOriginalRecord(record) && record.AuthorityPublished && !record.SourceRevisionExhausted &&
                record.State == VirtualDomainState.Faulted && record.TerminalPublicationRevision == record.SourceRevision &&
                record.TerminalPublicationKind is { } kind && Enum.IsDefined(kind) && kind != PlatformExecutionTerminalKind.Completed
                    ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "Original terminal state publication is unavailable or changed.");
    }

    internal KernelResult ValidateOriginalCompletedPublication(Record record)
    {
        lock (_gate)
            return HasCurrentOriginalRecord(record) && record.AuthorityPublished && !record.SourceRevisionExhausted &&
                record.State == VirtualDomainState.Completed && record.TerminalPublicationRevision == record.SourceRevision &&
                record.TerminalPublicationKind == PlatformExecutionTerminalKind.Completed
                    ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted, "Original completed publication is unavailable or changed.");
    }
}
