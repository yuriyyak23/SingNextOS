using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed class ProcessRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<ProcessId, SingProcess> _live = [];
    private readonly Dictionary<ProcessId, ulong> _lastGeneration = [];
    private readonly Dictionary<ProcessHandle, ProcessState> _terminalStates = [];
    private readonly Dictionary<string, ProcessHandle> _activeIdentities = new(StringComparer.Ordinal);

    internal KernelResult<SingProcess> Create(SingProcessManifestV1 manifest)
    {
        lock (_gate)
        {
            var valid = ValidateCreation(manifest);
            if (!valid.IsSuccess) return KernelResult<SingProcess>.Fail(valid.Error, valid.Message!);

            var process = SingProcess.CreateForRuntime(manifest);
            _live.Add(process.ProcessId, process);
            _lastGeneration[process.ProcessId] = process.Generation;
            _activeIdentities.Add(manifest.EntryIdentity, new ProcessHandle(process.ProcessId, process.Generation));
            return KernelResult<SingProcess>.Ok(process);
        }
    }

    internal KernelResult ValidateCreation(SingProcessManifestV1 manifest)
    {
        lock (_gate)
        {
            if (_live.ContainsKey(manifest.ProcessId))
                return KernelResult.Fail(KernelError.DuplicateProcessId, $"Process {manifest.ProcessId} is already live.");
            if (_activeIdentities.ContainsKey(manifest.EntryIdentity))
                return KernelResult.Fail(KernelError.DuplicateIdentity, $"Entry identity '{manifest.EntryIdentity}' is already active.");
            if (_lastGeneration.TryGetValue(manifest.ProcessId, out var last) && manifest.Generation <= last)
                return KernelResult.Fail(KernelError.StaleGeneration, $"Generation {manifest.Generation} is not newer than {last}.");
            return KernelResult.Ok();
        }
    }

    public KernelResult<SingProcess> Resolve(ProcessHandle handle)
    {
        lock (_gate)
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
    }

    internal void Retire(SingProcess process)
    {
        lock (_gate)
        {
            var handle = new ProcessHandle(process.ProcessId, process.Generation);
            if (process.State is ProcessState.Exited or ProcessState.Faulted)
                _terminalStates[handle] = process.State;
            _live.Remove(process.ProcessId);
            _activeIdentities.Remove(process.Manifest.EntryIdentity);
            _lastGeneration[process.ProcessId] = process.Generation;
        }
    }

    public KernelResult<ProcessState> ResolveTerminalState(ProcessHandle handle)
    {
        lock (_gate)
        {
            if (_terminalStates.TryGetValue(handle, out var state))
                return KernelResult<ProcessState>.Ok(state);
            if (_live.ContainsKey(handle.ProcessId))
                return KernelResult<ProcessState>.Fail(KernelError.InvalidTransition, "Process generation has not reached terminal closure.");
            return KernelResult<ProcessState>.Fail(
                _lastGeneration.TryGetValue(handle.ProcessId, out var last) && handle.Generation <= last ? KernelError.StaleHandle : KernelError.ProcessNotFound,
                "No terminal receipt exists for this exact process generation.");
        }
    }

    internal SingProcess? FindByDomainGeneration(DomainId domainId, ulong generation)
    {
        lock (_gate)
            return _live.Values.FirstOrDefault(p => p.DomainId == domainId && p.Generation == generation);
    }

    public IReadOnlyList<SingProcess> Snapshot()
    {
        lock (_gate)
            return _live.Values.OrderBy(static p => p.ProcessId.Value).ThenBy(static p => p.Generation).ToArray();
    }
}
