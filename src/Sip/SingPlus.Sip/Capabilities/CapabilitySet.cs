using SingPlus.Contracts;

namespace SingPlus.Sip;

public sealed class CapabilitySet
{
    private readonly HashSet<CapabilityId> _capabilities = [];

    public bool Contains(CapabilityId capability) => _capabilities.Contains(capability);

    public void Grant(CapabilityId capability) => _capabilities.Add(capability);
}
