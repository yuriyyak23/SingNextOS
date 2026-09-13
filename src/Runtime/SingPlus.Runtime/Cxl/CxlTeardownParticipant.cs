using SingPlus.Contracts;

namespace SingPlus.Runtime;

public interface ICxlTeardownParticipant
{
    KernelResult CloseForProcess(ProcessHandle process, RegionOwner owner);
}

public sealed partial class RuntimeKernel
{
    private readonly List<ICxlTeardownParticipant> _cxlTeardownParticipants = [];

    internal void RegisterCxlTeardownParticipant(ICxlTeardownParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!_cxlTeardownParticipants.Contains(participant))
            _cxlTeardownParticipants.Add(participant);
    }

    private KernelResult CloseCxlResourcesForProcess(ProcessHandle process, RegionOwner owner)
    {
        foreach (var participant in _cxlTeardownParticipants.AsEnumerable().Reverse().ToArray())
        {
            var closure = participant.CloseForProcess(process, owner);
            if (!closure.IsSuccess) return closure;
        }
        return KernelResult.Ok();
    }
}
