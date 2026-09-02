using SingPlus.Contracts;

namespace SingPlus.Sip;

public sealed class CapabilityView
{
    private readonly CapabilityId[] _capabilities;

    internal CapabilityView(IEnumerable<CapabilityId> capabilities) => _capabilities = capabilities.OrderBy(static id => id.Value).ToArray();

    public IReadOnlyList<CapabilityId> Items => _capabilities;
    public bool Contains(CapabilityId capability) => Array.BinarySearch(_capabilities, capability, CapabilityIdComparer.Instance) >= 0;

    private sealed class CapabilityIdComparer : IComparer<CapabilityId>
    {
        internal static readonly CapabilityIdComparer Instance = new();
        public int Compare(CapabilityId x, CapabilityId y) => x.Value.CompareTo(y.Value);
    }
}
