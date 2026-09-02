using SingPlus.Contracts;

namespace SingPlus.Sip;

public sealed class SingProcess
{
    public SingProcess(SingProcessManifest manifest)
    {
        Manifest = manifest;
    }

    public SingProcessManifest Manifest { get; }

    public CapabilitySet Capabilities { get; } = new();
}
