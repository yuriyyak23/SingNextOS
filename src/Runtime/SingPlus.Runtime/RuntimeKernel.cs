using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed class RuntimeKernel
{
    public SingProcess CreateProcess(SingProcessManifestV1 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return SingProcess.CreateForRuntime(manifest);
    }
}
