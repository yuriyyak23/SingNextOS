using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed class ProcessRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<ProcessId, SingProcess> _live = [];
    private readonly Dictionary<ProcessId, SingProcess> _ownedLive = [];
    private readonly Dictionary<ProcessId, ulong> _lastGeneration = [];
    private sealed record TerminalReceipt(SingProcess Original, ProcessState State);
    private readonly Dictionary<ProcessHandle, TerminalReceipt> _terminalStates = [];
    private readonly Dictionary<ProcessHandle, TerminalReceipt> _ownedTerminalStates = [];
    private readonly Dictionary<string, ProcessHandle> _activeIdentities = new(StringComparer.Ordinal);

    internal KernelResult<SingProcess> Create(SingProcessManifestV1 manifest)
    {
        lock (_gate)
        {
            var valid = ValidateCreation(manifest);
            if (!valid.IsSuccess) return KernelResult<SingProcess>.Fail(valid.Error, valid.Message!);

            var process = SingProcess.CreateForRuntime(manifest);
            _ownedLive.EnsureCapacity(_ownedLive.Count + 1);
            _live.EnsureCapacity(_live.Count + 1);
            _ownedLive.Add(process.ProcessId, process);
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
            if (_ownedLive.ContainsKey(manifest.ProcessId) || _live.ContainsKey(manifest.ProcessId))
                return KernelResult.Fail(KernelError.DuplicateProcessId, $"Process {manifest.ProcessId} is already live.");
            if (_activeIdentities.ContainsKey(manifest.EntryIdentity) ||
                _ownedLive.Values.Any(p => p.Manifest.EntryIdentity == manifest.EntryIdentity))
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
            if (!_ownedLive.TryGetValue(handle.ProcessId, out var original) || !ReferenceEquals(process, original))
                return KernelResult<SingProcess>.Fail(KernelError.StaleHandle, "Process route is not the original incarnation owner.");
            return KernelResult<SingProcess>.Ok(process);
        }
    }

    internal void Retire(SingProcess process)
    {
        lock (_gate)
        {
            var handle = new ProcessHandle(process.ProcessId, process.Generation);
            if (!_ownedLive.TryGetValue(process.ProcessId, out var original) || !ReferenceEquals(process, original) ||
                process.State is not (ProcessState.Exited or ProcessState.Faulted))
                return;
            var receipt = new TerminalReceipt(process, process.State);
            _ownedTerminalStates.EnsureCapacity(_ownedTerminalStates.Count + 1);
            _terminalStates.EnsureCapacity(_terminalStates.Count + 1);
            _ownedTerminalStates.Add(handle, receipt);
            _terminalStates[handle] = receipt;
            _ownedLive.Remove(process.ProcessId);
            if (_live.TryGetValue(process.ProcessId, out var routed) && ReferenceEquals(routed, original))
                _live.Remove(process.ProcessId);
            _activeIdentities.Remove(process.Manifest.EntryIdentity);
            _lastGeneration[process.ProcessId] = process.Generation;
        }
    }

    public KernelResult<ProcessState> ResolveTerminalState(ProcessHandle handle)
    {
        lock (_gate)
        {
            // Completed original lifetime fact needs no renewed active route.
            if (_ownedTerminalStates.TryGetValue(handle, out var receipt) &&
                receipt.Original.ProcessId == handle.ProcessId && receipt.Original.Generation == handle.Generation &&
                receipt.State is ProcessState.Exited or ProcessState.Faulted)
                return KernelResult<ProcessState>.Ok(receipt.State);
            if (_ownedLive.ContainsKey(handle.ProcessId))
                return KernelResult<ProcessState>.Fail(KernelError.InvalidTransition, "Process generation has not reached terminal closure.");
            return KernelResult<ProcessState>.Fail(
                _lastGeneration.TryGetValue(handle.ProcessId, out var last) && handle.Generation <= last ? KernelError.StaleHandle : KernelError.ProcessNotFound,
                "No terminal receipt exists for this exact process generation.");
        }
    }

    internal SingProcess? FindByDomainGeneration(DomainId domainId, ulong generation)
    {
        lock (_gate)
            return _ownedLive.Values.FirstOrDefault(p => p.DomainId == domainId && p.Generation == generation &&
                _live.TryGetValue(p.ProcessId, out var routed) && ReferenceEquals(routed, p));
    }

    public IReadOnlyList<SingProcess> Snapshot()
    {
        lock (_gate)
            return _ownedLive.Values.OrderBy(static p => p.ProcessId.Value).ThenBy(static p => p.Generation).ToArray();
    }
}
