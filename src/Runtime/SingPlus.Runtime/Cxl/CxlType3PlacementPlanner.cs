using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed class CxlType3PlacementPlanner(ICxlDiscoveryProvider discovery, ICxlMemoryProvider memory)
{
    public KernelResult<CxlMemoryPlacementDecision> Plan(
        CxlMemoryPlacementIntent intent,
        IReadOnlyList<CxlEndpointId> candidates)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(candidates);
        if (intent.CapacityBytes <= 0 || !Enum.IsDefined(intent.Persistence) || !Enum.IsDefined(intent.Sharing) ||
            !Enum.IsDefined(intent.Preference) || intent.MaximumLatencyClass < 0 || intent.MinimumBandwidthClass < 0)
            return KernelResult<CxlMemoryPlacementDecision>.Fail(KernelError.PlatformDenied, "Memory placement intent is invalid.");
        if (intent.Preference == CxlMemoryPlacementPreference.LocalRequired)
            return KernelResult<CxlMemoryPlacementDecision>.Ok(new(CxlMemoryPlacementKind.Local, null, null));

        var feasible = new List<(CxlEndpointSnapshot Endpoint, CxlMemoryCapacitySnapshot Capacity)>();
        foreach (var id in candidates.Distinct().OrderBy(static id => id.Value, StringComparer.Ordinal))
        {
            var endpoint = discovery.QueryEndpoint(id);
            if (!endpoint.IsSuccess || !endpoint.Value!.Available || (endpoint.Value.Features & CxlEndpointFeatures.Memory) == 0) continue;
            var capacity = memory.QueryCapacity(id);
            if (!capacity.IsSuccess || capacity.Value!.DeviceGeneration != endpoint.Value.DeviceGeneration) continue;
            if (capacity.Value.AvailableBytes < intent.CapacityBytes || capacity.Value.Persistence != intent.Persistence ||
                capacity.Value.LatencyClass > intent.MaximumLatencyClass || capacity.Value.BandwidthClass < intent.MinimumBandwidthClass) continue;
            feasible.Add((endpoint.Value, capacity.Value));
        }
        if (feasible.Count == 0)
            return intent.Preference == CxlMemoryPlacementPreference.CxlPreferred
                ? KernelResult<CxlMemoryPlacementDecision>.Ok(new(CxlMemoryPlacementKind.Local, null, null))
                : KernelResult<CxlMemoryPlacementDecision>.Fail(KernelError.PlatformUnavailable, "No Type-3 endpoint satisfies the placement policy.");

        var selected = feasible.OrderBy(static item => item.Capacity.LatencyClass)
            .ThenByDescending(static item => item.Capacity.BandwidthClass)
            .ThenBy(static item => item.Capacity.ProximityClass)
            .ThenBy(static item => item.Endpoint.EndpointId.Value, StringComparer.Ordinal).First();
        return KernelResult<CxlMemoryPlacementDecision>.Ok(new(CxlMemoryPlacementKind.CxlType3, selected.Endpoint, selected.Capacity));
    }
}
