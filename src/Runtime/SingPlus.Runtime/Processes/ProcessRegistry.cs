using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed class ProcessRegistry
{
    private readonly Dictionary<ProcessId, SingProcess> _live = [];
    private readonly Dictionary<ProcessId, ulong> _lastGeneration = [];
    private readonly Dictionary<string, ProcessHandle> _activeIdentities = new(StringComparer.Ordinal);

    internal KernelResult<SingProcess> Create(SingProcessManifestV1 manifest)
    {
        if (_live.ContainsKey(manifest.ProcessId))
            return KernelResult<SingProcess>.Fail(KernelError.DuplicateProcessId, $"Process {manifest.ProcessId} is already live.");
        if (_activeIdentities.ContainsKey(manifest.EntryIdentity))
            return KernelResult<SingProcess>.Fail(KernelError.DuplicateIdentity, $"Entry identity '{manifest.EntryIdentity}' is already active.");
        if (_lastGeneration.TryGetValue(manifest.ProcessId, out var last) && manifest.Generation <= last)
            return KernelResult<SingProcess>.Fail(KernelError.StaleGeneration, $"Generation {manifest.Generation} is not newer than {last}.");

        var process = SingProcess.CreateForRuntime(manifest);
        _live.Add(process.ProcessId, process);
        _lastGeneration[process.ProcessId] = process.Generation;
        _activeIdentities.Add(manifest.EntryIdentity, new ProcessHandle(process.ProcessId, process.Generation));
        return KernelResult<SingProcess>.Ok(process);
    }

    public KernelResult<SingProcess> Resolve(ProcessHandle handle)
    {
        if (!_live.TryGetValue(handle.ProcessId, out var process))
        {
            if (_lastGeneration.TryGetValue(handle.ProcessId, out var last) && handle.Generation <= last)
                return KernelResult<SingProcess>.Fail(KernelError.StaleHandle, $"Process handle {handle.ProcessId}/{handle.Generation} is terminated or stale.");
            return KernelResult<SingProcess>.Fail(KernelError.ProcessNotFound, $"Process {handle.ProcessId} was not found.");
        }
        if (process.Generation != handle.Generation)
            return KernelResult<SingProcess>.Fail(KernelError.StaleHandle, $"Expected generation {process.Generation}, got {handle.Generation}.");
        return KernelResult<SingProcess>.Ok(process);
    }

    internal void Retire(SingProcess process)
    {
        _live.Remove(process.ProcessId);
        _activeIdentities.Remove(process.Manifest.EntryIdentity);
        _lastGeneration[process.ProcessId] = process.Generation;
    }

    internal SingProcess? FindByDomainGeneration(DomainId domainId, ulong generation) =>
        _live.Values.FirstOrDefault(p => p.DomainId == domainId && p.Generation == generation);

    public IReadOnlyList<SingProcess> Snapshot() => _live.Values.OrderBy(static p => p.ProcessId.Value).ThenBy(static p => p.Generation).ToArray();
}
