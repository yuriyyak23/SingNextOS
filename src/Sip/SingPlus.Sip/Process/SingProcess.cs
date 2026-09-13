using SingPlus.Contracts;

namespace SingPlus.Sip;

public sealed class SingProcess
{
    private readonly HashSet<CapabilityId> _capabilities = [];
    private readonly HashSet<RegionHandle> _regions = [];
    private readonly HashSet<ChannelEndpointHandle> _channels = [];

    private SingProcess(SingProcessManifestV1 manifest)
    {
        Manifest = manifest;
        State = ProcessState.Created;
    }

    public SingProcessManifestV1 Manifest { get; }
    public ProcessId ProcessId => Manifest.ProcessId;
    public DomainId DomainId => Manifest.DomainId;
    public ulong Generation => Manifest.Generation;
    public MemoryProfile MemoryProfile => Manifest.MemoryProfile;
    public ExecutionRole ExecutionRole => Manifest.ExecutionRole;
    public ProcessState State { get; private set; }
    public CapabilityView Capabilities => new(_capabilities);
    public IReadOnlyList<RegionHandle> Regions => _regions.OrderBy(static x => x.RegionId.Value).ThenBy(static x => x.Generation.Value).ToArray();
    public IReadOnlyList<ChannelEndpointHandle> Channels => _channels.OrderBy(static x => x.ChannelId.Value).ThenBy(static x => x.EndpointId.Value).ToArray();

    internal static SingProcess CreateForRuntime(SingProcessManifestV1 manifest) => new(manifest);
    internal void SetState(ProcessState state) => State = state;
    internal void AddCapability(CapabilityId id) => _capabilities.Add(id);
    internal void RemoveCapability(CapabilityId id) => _capabilities.Remove(id);
    internal void ClearCapabilities() => _capabilities.Clear();
    internal void AddRegion(RegionHandle handle) => _regions.Add(handle);
    internal void RemoveRegion(RegionHandle handle) => _regions.Remove(handle);
    internal void ReplaceRegion(RegionHandle oldHandle, RegionHandle newHandle)
    {
        _regions.Remove(oldHandle);
        _regions.Add(newHandle);
    }
    internal void AddChannel(ChannelEndpointHandle handle) => _channels.Add(handle);
    internal void RemoveChannel(ChannelEndpointHandle handle) => _channels.Remove(handle);
    internal void ClearRuntimeResources()
    {
        _capabilities.Clear();
        _regions.Clear();
        _channels.Clear();
    }
}
