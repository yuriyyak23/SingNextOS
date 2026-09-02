using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed class RuntimeKernel
{
    public SingProcess CreateProcess(SingProcessManifest manifest)
    {
        if (manifest.ProcessId.Value == 0 || manifest.DomainId.Value == 0)
        {
            throw new ArgumentException("Process and domain identifiers must be non-zero.", nameof(manifest));
        }

        return new SingProcess(manifest);
    }
}
