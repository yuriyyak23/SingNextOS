using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed class DomainRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<DomainId, HashSet<ProcessHandle>> _domains = [];
    // Routing references the same membership fact held by the existing owner.
    private readonly Dictionary<DomainId, HashSet<ProcessHandle>> _ownedDomains = [];

    private sealed class Membership : HashSet<ProcessHandle>
    {
        internal Membership(DomainRegistry registry, DomainId domain)
        { Self = this; Registry = registry; Domain = domain; }
        internal readonly Membership Self;
        internal readonly DomainRegistry Registry;
        internal readonly DomainId Domain;
        // The original last departure seals this lifetime even if its mutable
        // handle set is later reseeded to equal a new cohort. Retain the fact,
        // rather than inferring continuity from provenance/count/handles.
        internal DepartureOutcome? ClosedBy;
    }

    private bool IsOriginalMembership(DomainId domain, HashSet<ProcessHandle> members) =>
        members is Membership original && ReferenceEquals(original.Self, original) &&
        ReferenceEquals(original.Registry, this) && original.Domain == domain;

    private bool IsCurrentMembership(DomainId domain, HashSet<ProcessHandle> members) =>
        IsOriginalMembership(domain, members) && ((Membership)members).ClosedBy is null;

    internal KernelResult ValidateAdmissionSource(DomainId domain)
    {
        lock (_gate)
            return !_ownedDomains.TryGetValue(domain, out var members) || IsCurrentMembership(domain, members)
                ? KernelResult.Ok() : KernelResult.Fail(KernelError.StaleGeneration,
                    "Membership source is copied or foreign; new membership remains forbidden.");
    }

    internal KernelResult ValidateAdmissionSource(DomainId domain, IReadOnlyCollection<ProcessHandle> liveHandles)
    {
        lock (_gate)
        {
            if (!_ownedDomains.TryGetValue(domain, out var members))
                return KernelResult.Ok();
            if (!IsCurrentMembership(domain, members))
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Membership source is copied or foreign; new membership remains forbidden.");
            if (members.Count != liveHandles.Count || liveHandles.Any(handle => !members.Contains(handle)))
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Membership source does not match the current process lifetime cohort.");
            return KernelResult.Ok();
        }
    }

    internal void Add(SingProcess process)
    {
        lock (_gate)
        {
            if (!_ownedDomains.TryGetValue(process.DomainId, out HashSet<ProcessHandle>? handles))
            {
                handles = new Membership(this, process.DomainId);
                _ownedDomains.EnsureCapacity(_ownedDomains.Count + 1);
                _domains.EnsureCapacity(_domains.Count + 1);
                _ownedDomains.Add(process.DomainId, handles);
                _domains[process.DomainId] = handles;
            }
            handles.Add(new ProcessHandle(process.ProcessId, process.Generation));
        }
    }

    internal sealed class Departure
    {
        internal Departure(DomainRegistry registry, SingProcess process, HashSet<ProcessHandle> members)
        {
            Self = this; Registry = registry; Process = process; Members = members; MemberCount = members.Count;
        }
        internal readonly Departure Self;
        internal readonly DomainRegistry Registry;
        internal readonly SingProcess Process;
        internal readonly HashSet<ProcessHandle> Members;
        internal readonly int MemberCount;
        internal DepartureOutcome? Outcome;
    }

    internal sealed class DepartureOutcome
    {
        internal DepartureOutcome(Departure original, bool lastMember)
        { Self = this; Original = original; LastMember = lastMember; }
        internal readonly DepartureOutcome Self;
        internal readonly Departure Original;
        internal readonly bool LastMember;
    }

    internal KernelResult<Departure> PrepareDeparture(SingProcess process, IReadOnlyCollection<ProcessHandle> currentHandles)
    {
        lock (_gate)
        {
            if (!_ownedDomains.TryGetValue(process.DomainId, out var members) || !IsCurrentMembership(process.DomainId, members) ||
                !members.Contains(new(process.ProcessId, process.Generation)) ||
                members.Count != currentHandles.Count || currentHandles.Any(handle => !members.Contains(handle)))
                return KernelResult<Departure>.Fail(KernelError.PlatformBindingDraining,
                    "Original membership unavailable; absence cannot prove a prior domain departure.");
            return KernelResult<Departure>.Ok(new(this, process, members));
        }
    }

    private bool IsOriginalDeparture(SingProcess process, Departure original) =>
        ReferenceEquals(original.Self, original) && ReferenceEquals(original.Registry, this) &&
        ReferenceEquals(original.Process, process) && original.MemberCount > 0 &&
        IsOriginalMembership(process.DomainId, original.Members);

    internal KernelResult<bool> Remove(SingProcess process, Departure original)
    {
        lock (_gate)
        {
            if (!IsOriginalDeparture(process, original))
                return KernelResult<bool>.Fail(KernelError.StaleGeneration, "Original domain departure required.");
            if (original.Outcome is not null) return ResolveDeparture(process, original);
            if (!_ownedDomains.TryGetValue(process.DomainId, out var handles) || !IsCurrentMembership(process.DomainId, handles) || !ReferenceEquals(handles, original.Members) ||
                handles.Count != original.MemberCount || !handles.Contains(new(process.ProcessId, process.Generation)))
                return KernelResult<bool>.Fail(KernelError.StaleGeneration, "Original membership changed before departure.");
            // Allocate the canonical outcome before effect; publish it only after
            // the owner commit, with no external callback between these writes.
            DepartureOutcome outcome = new(original, handles.Count == 1);
            handles.Remove(new ProcessHandle(process.ProcessId, process.Generation));
            if (handles.Count == 0)
            {
                ((Membership)handles).ClosedBy = outcome;
                _ownedDomains.Remove(process.DomainId);
                if (_domains.TryGetValue(process.DomainId, out var routed) && ReferenceEquals(routed, handles))
                    _domains.Remove(process.DomainId);
            }
            original.Outcome = outcome;
            return ResolveDeparture(process, original);
        }
    }

    // Pure same-original observation. Never reconstruct completion from a missing
    // route, a projection flag, an equal token or a foreign registry's receipt.
    internal KernelResult<bool> ResolveDeparture(SingProcess process, Departure original)
    {
        lock (_gate)
        {
            if (!IsOriginalDeparture(process, original) || original.Outcome is not { } outcome ||
                !ReferenceEquals(outcome.Self, outcome) || !ReferenceEquals(outcome.Original, original) ||
                outcome.LastMember != (original.MemberCount == 1) ||
                original.Members.Contains(new(process.ProcessId, process.Generation)))
                return KernelResult<bool>.Fail(KernelError.PlatformBindingDraining, "Original departure outcome remains unknown.");
            if (outcome.LastMember && (!ReferenceEquals(((Membership)original.Members).ClosedBy, outcome) ||
                original.Members.Count != 0 || _ownedDomains.ContainsKey(process.DomainId)))
                return KernelResult<bool>.Fail(KernelError.StaleGeneration, "Last-member source no longer matches original departure.");
            return KernelResult<bool>.Ok(outcome.LastMember);
        }
    }

    public bool Contains(DomainId domainId)
    {
        lock (_gate) return _ownedDomains.ContainsKey(domainId);
    }

    public IReadOnlyList<DomainId> Snapshot()
    {
        lock (_gate) return _ownedDomains.Keys.OrderBy(static id => id.Value).ToArray();
    }
}
