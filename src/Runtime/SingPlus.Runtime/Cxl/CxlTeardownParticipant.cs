using SingPlus.Contracts;

namespace SingPlus.Runtime;

public interface ICxlTeardownParticipant
{
    KernelResult CloseForProcess(ProcessHandle process, RegionOwner owner);
}

internal interface IComposedWorkTeardownParticipant
{
    KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner);
}

// Kernel-private: Type-3 retains authority over whether its current backing
// generations can support a new secure guest effect. It deliberately exposes
// neither endpoint identity nor provider handles to the secure-compute model.
internal interface ISecureGuestBackingAuthority
{
    KernelResult RevalidateForSecureGuest(RegionOwner owner, RegionHandle region);
}

public sealed partial class RuntimeKernel
{
    private readonly List<ICxlTeardownParticipant> _cxlTeardownParticipants = [];
    private readonly List<IComposedWorkTeardownParticipant> _composedWorkTeardownParticipants = [];
    private readonly List<ISecureGuestBackingAuthority> _secureGuestBackingAuthorities = [];

    internal void RegisterCxlTeardownParticipant(ICxlTeardownParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!_cxlTeardownParticipants.Contains(participant))
            _cxlTeardownParticipants.Add(participant);
    }

    internal void RegisterComposedWorkTeardownParticipant(IComposedWorkTeardownParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!_composedWorkTeardownParticipants.Contains(participant))
            _composedWorkTeardownParticipants.Add(participant);
    }

    private KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner)
    {
        foreach (var participant in _composedWorkTeardownParticipants.AsEnumerable().Reverse().ToArray())
        {
            var closure = participant.CloseComposedWorkForProcess(process, owner);
            if (!closure.IsSuccess) return closure;
        }
        return KernelResult.Ok();
    }

    internal void RegisterSecureGuestBackingAuthority(ISecureGuestBackingAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!_secureGuestBackingAuthorities.Contains(authority))
            _secureGuestBackingAuthorities.Add(authority);
    }

    private KernelResult RevalidateSecureGuestBackings(RegionOwner owner, RegionHandle region)
    {
        foreach (var authority in _secureGuestBackingAuthorities.ToArray())
        {
            var validation = authority.RevalidateForSecureGuest(owner, region);
            if (!validation.IsSuccess) return validation;
        }
        return KernelResult.Ok();
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
