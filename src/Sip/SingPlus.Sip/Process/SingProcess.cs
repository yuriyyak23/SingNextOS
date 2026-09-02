using SingPlus.Contracts;

namespace SingPlus.Sip;

public sealed class SingProcess
{
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

    public static SingProcess CreateForRuntime(SingProcessManifestV1 manifest) => new(manifest);

    internal void SetState(ProcessState state) => State = state;
}
