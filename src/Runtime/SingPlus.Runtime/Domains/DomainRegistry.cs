using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed class DomainRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<DomainId, HashSet<ProcessHandle>> _domains = [];
    // Routing references the same membership fact held by the existing owner.
    private readonly Dictionary<DomainId, HashSet<ProcessHandle>> _ownedDomains = [];

    internal void Add(SingProcess process)
    {
        lock (_gate)
        {
            if (!_ownedDomains.TryGetValue(process.DomainId, out var handles))
            {
                handles = [];
                _ownedDomains.EnsureCapacity(_ownedDomains.Count + 1);
                _domains.EnsureCapacity(_domains.Count + 1);
                _ownedDomains.Add(process.DomainId, handles);
                _domains[process.DomainId] = handles;
            }
            handles.Add(new ProcessHandle(process.ProcessId, process.Generation));
        }
    }

    internal bool Remove(SingProcess process)
    {
        lock (_gate)
        {
            if (!_ownedDomains.TryGetValue(process.DomainId, out var handles)) return false;
            handles.Remove(new ProcessHandle(process.ProcessId, process.Generation));
            if (handles.Count != 0) return false;
            _ownedDomains.Remove(process.DomainId);
            if (_domains.TryGetValue(process.DomainId, out var routed) && ReferenceEquals(routed, handles))
                _domains.Remove(process.DomainId);
            return true;
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
