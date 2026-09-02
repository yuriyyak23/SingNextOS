using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed class DomainRegistry
{
    private readonly Dictionary<DomainId, HashSet<ProcessHandle>> _domains = [];

    internal void Add(SingProcess process)
    {
        if (!_domains.TryGetValue(process.DomainId, out var handles))
        {
            handles = [];
            _domains.Add(process.DomainId, handles);
        }
        handles.Add(new ProcessHandle(process.ProcessId, process.Generation));
    }

    internal bool Remove(SingProcess process)
    {
        if (!_domains.TryGetValue(process.DomainId, out var handles)) return true;
        handles.Remove(new ProcessHandle(process.ProcessId, process.Generation));
        if (handles.Count != 0) return false;
        _domains.Remove(process.DomainId);
        return true;
    }

    public bool Contains(DomainId domainId) => _domains.ContainsKey(domainId);

    public IReadOnlyList<DomainId> Snapshot() => _domains.Keys.OrderBy(static id => id.Value).ToArray();
}
